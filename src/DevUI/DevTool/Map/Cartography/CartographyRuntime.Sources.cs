using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.History;

namespace DryCycle.DevUI.DevTool.Map.Cartography;

internal sealed class CartographySourcePicker
{
    internal CartographyRegionOption[] Regions = Array.Empty<CartographyRegionOption>();
    internal string Region = "", Campaign = "", PlayerRegion = "", Status = "";
    internal bool Loading, Failed;
}

internal static partial class CartographyRuntime
{
    private sealed class SourceRequest
    {
        internal long Generation;
        internal string Region, Campaign, Installation, Identity, ImportPath;
        internal CartographyRegionFiles Files;
        internal bool Refresh;
    }

    private sealed class SourceResult
    {
        internal SourceRequest Request;
        internal CartographySource Source;
        internal CartographyDocument Initial;
        internal string[] Warnings = Array.Empty<string>();
    }

    private sealed class PreparedSource
    {
        internal SourceResult Loaded;
        internal Workspace Workspace;
        internal long Revision;
        internal bool New;
        internal CartographyDocument Document;
        internal string AuthorText, SavedText, DiskHash;
        internal CartographyScene Scene;
        internal CartographySceneCache Cache;
    }

    private static readonly CartographySourceCache SourceCache = new(4);
    private static Task<SourceResult> loadingSource;
    private static Task<PreparedSource> preparingSource;
    private static SourceRequest pendingSource;
    private static long sourceGeneration, workspaceClock;
    private static long loadingGeneration, preparingGeneration;
    private static int followPlayerRequested;
    private static string[] fonts = Array.Empty<string>(), icons = Array.Empty<string>();
    private static volatile CartographySourcePicker picker = new();
    internal static CartographySourcePicker SourcePicker => picker;
    internal static string[] Fonts => fonts;
    internal static string[] Icons => icons;
    internal static bool LoadingSource => picker.Loading;

    private static void ProcessSources(EditorSession session)
    {
        currentSession = session;
        bool worldChanged = !ReferenceEquals(observedWorld, session.World);
        observedWorld = session.World;
        if (worldChanged || Interlocked.Exchange(ref followPlayerRequested, 0) != 0)
        {
            // Entering Cartography starts at the player's current region/campaign.
            // Retained author documents are selected again, never replaced with defaults.
            Interlocked.Exchange(ref followPlayerRequested, 0);
            try { RequestSource(null, null, "", false, false); }
            catch (Exception error) { SourceFailure(error); }
        }

        if (loadingSource?.IsCompleted == true)
        {
            Task<SourceResult> completed = loadingSource;
            loadingSource = null;
            try
            {
                SourceResult loaded = completed.GetAwaiter().GetResult();
                if (loaded.Request.Generation == sourceGeneration)
                {
                    // Only atlas readback touches Unity. Parsing and raster generation run on
                    // the worker while the previous region remains usable on screen.
                    CartographyGameAssets.Load(loaded.Source);
                    if (fonts.Length == 0 || loaded.Request.ImportPath != null) fonts = CartographyAssets.FontNames;
                    icons = CartographyAssets.IconNames;
                    BeginPrepare(loaded);
                }
            }
            catch (Exception error) { SourceFailure(error, loadingGeneration != sourceGeneration); }
        }
        if (preparingSource?.IsCompleted == true)
        {
            Task<PreparedSource> completed = preparingSource;
            preparingSource = null;
            try
            {
                PreparedSource prepared = completed.GetAwaiter().GetResult();
                if (prepared.Loaded.Request.Generation == sourceGeneration)
                {
                    if (!prepared.New && prepared.Workspace.Revision != prepared.Revision)
                        BeginPrepare(prepared.Loaded); // Retain edits made while the preview was building.
                    else
                        ApplyPrepared(prepared, session);
                }
            }
            catch (Exception error) { SourceFailure(error, preparingGeneration != sourceGeneration); }
        }
        if (loadingSource == null && preparingSource == null && pendingSource != null)
        {
            SourceRequest request = pendingSource;
            pendingSource = null;
            loadingGeneration = request.Generation;
            // One worker and one latest pending request bound rapid region switching.
            loadingSource = Task.Run(() =>
            {
                if (request.ImportPath != null)
                {
                    CartographyAssets.LoadDirectory(Path.GetDirectoryName(request.ImportPath));
                    var imported = CartographyCorniferImport.Load(request.ImportPath);
                    request.Identity = imported.Document.Identity;
                    request.Region = imported.Document.Region;
                    request.Campaign = imported.Document.Options.Campaign;
                    return new SourceResult { Request = request, Source = imported.Source, Initial = imported.Document, Warnings = imported.Warnings };
                }
                CartographySource source = SourceCache.Load(request.Identity, request.Files, request.Campaign, request.Refresh, out _);
                return new SourceResult { Request = request, Source = source };
            });
        }
    }

    private static void BeginPrepare(SourceResult loaded)
    {
        preparingGeneration = loaded.Request.Generation;
        bool exists = Documents.TryGetValue(loaded.Request.Identity, out Workspace workspace);
        workspace ??= new Workspace { Identity = loaded.Request.Identity, Path = ProjectPath(loaded.Request.Identity, loaded.Request.Region) };
        PreparedSource prepared = new()
        {
            Loaded = loaded, Workspace = workspace, New = !exists, Revision = workspace.Revision,
            Document = workspace.Document?.Clone(), SavedText = workspace.SavedText, DiskHash = workspace.DiskHash
        };
        // Warm revisits reuse the complete scene, including terrain and label rasterizations.
        if (exists && ReferenceEquals(workspace.Source, loaded.Source) && workspace.Scene != null)
        {
            prepared.AuthorText = workspace.AuthorText;
            prepared.Scene = workspace.Scene;
            prepared.Cache = workspace.SceneCache;
            preparingSource = Task.FromResult(prepared);
            return;
        }
        preparingSource = Task.Run(() =>
        {
            if (prepared.New)
            {
                if (File.Exists(workspace.Path))
                {
                    byte[] bytes = File.ReadAllBytes(workspace.Path);
                    prepared.Document = CartographyStorage.Deserialize(System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'), workspace.Identity);
                    prepared.DiskHash = CartographyStorage.HashBytes(bytes);
                    prepared.SavedText = CartographyStorage.Serialize(prepared.Document);
                }
                else
                {
                    prepared.Document = loaded.Initial ?? loaded.Source.CreateDocument(workspace.Identity, loaded.Request.Region);
                    prepared.Document.Options.Campaign = loaded.Request.Campaign;
                    prepared.Document.Options.Installation = loaded.Request.Installation;
                }
            }
            prepared.AuthorText = CartographyStorage.Serialize(prepared.Document);
            prepared.Cache = new CartographySceneCache();
            prepared.Scene = CartographySceneBuilder.Build(prepared.Document, loaded.Source, prepared.Cache);
            return prepared;
        });
    }

    private static void ApplyPrepared(PreparedSource prepared, EditorSession session)
    {
        Workspace workspace = prepared.Workspace;
        if (prepared.New)
        {
            workspace.History.ActivateDocument(new EditorDocumentKey(EditorDocumentKind.RegionMap, "Cartography:" + workspace.Identity));
            workspace.Document = prepared.Document;
            workspace.AuthorText = prepared.AuthorText;
            workspace.SavedText = prepared.SavedText;
            workspace.DiskHash = prepared.DiskHash;
            Documents.Add(workspace.Identity, workspace);
        }
        // Publish only a fully prepared document/source/scene. Any failure retains the previous
        // workspace and all unsaved author data.
        // An existing author model has not changed. Preserve its revision and save baseline,
        // including a successful save that happened while the new preview was being built.
        workspace.Source = prepared.Loaded.Source;
        workspace.Scene = prepared.Scene;
        workspace.SceneCache = prepared.Cache;
        workspace.LastUsed = ++workspaceClock;
        workspace.Status = string.Join("\n", prepared.Loaded.Warnings);
        current = workspace;
        SourceRequest request = prepared.Loaded.Request;
        picker = new CartographySourcePicker { Regions = picker.Regions, Region = request.Region, Campaign = request.Campaign, PlayerRegion = session.World.name };
        Publish(workspace);
        EditorRevisionHub.Mark(session, EditorRevisionKind.Shell);

        // Evict only derived previews. Documents and undo histories remain available.
        foreach (Workspace old in Documents.Values.Where(w => w.Source != null).OrderByDescending(w => w.LastUsed).Skip(4))
        { old.Source = null; old.Scene = null; old.SceneCache = new CartographySceneCache(); }
    }

    private static void SourceFailure(Exception error, bool superseded = false)
    {
        Plugin.Logger?.LogError("Cartography region load failed: " + error);
        if (superseded) return;
        picker = new CartographySourcePicker { Regions = picker.Regions, Region = picker.Region, Campaign = picker.Campaign,
            PlayerRegion = picker.PlayerRegion, Status = error.Message, Failed = true };
        if (current == null) presentation = new CartographyPresentation { Status = error.Message };
    }

    private static void RequestSource(string region, string selectedCampaign, string path, bool cornifer, bool refresh)
    {
        if (currentSession?.World == null) throw new InvalidOperationException("No active world / 当前没有载入区域。");
        long generation = ++sourceGeneration;
        pendingSource = null;
        string playerRegion = currentSession.World.name;
        region = (string.IsNullOrWhiteSpace(region) ? playerRegion : region).Trim().ToUpperInvariant();
        selectedCampaign = string.IsNullOrWhiteSpace(selectedCampaign) ? currentSession.World.game?.StoryCharacter?.value ?? "White" : selectedCampaign;
        string defaultRoot = CartographyRegionFiles.StreamingAssetsRoot(RWCustom.Custom.RootFolderDirectory());
        string root = string.IsNullOrWhiteSpace(path) || cornifer ? defaultRoot : CartographyRegionFiles.StreamingAssetsRoot(path);
        bool live = string.Equals(root, defaultRoot, StringComparison.OrdinalIgnoreCase);
        string[] externalRoots = { Path.Combine(root, "mergedmods"), root };
        string[] List(string relative, bool directories)
        {
            if (live) return AssetManager.ListDirectory(relative, directories);
            return externalRoots.Select(folder => Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar)))
                .Where(Directory.Exists).SelectMany(folder => directories ? Directory.GetDirectories(folder) : Directory.GetFiles(folder))
                .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();
        }
        string Resolve(string relative) => live ? AssetManager.ResolveFilePath(relative) : externalRoots
            .Select(folder => Path.Combine(folder, relative.ToLowerInvariant().Replace('/', Path.DirectorySeparatorChar))).FirstOrDefault(File.Exists);
        SlugcatStats.Name character = new(selectedCampaign, false);
        string FullName(string code)
        {
            if (live) return Region.GetRegionFullName(code, character);
            string file = Resolve("world/" + code + "/displayname-" + selectedCampaign + ".txt") ?? Resolve("world/" + code + "/displayname.txt");
            return file == null ? code : File.ReadLines(file).FirstOrDefault() ?? code;
        }
        string Translate(string name) => string.IsNullOrWhiteSpace(name) ? name : currentSession.World.game?.rainWorld?.inGameTranslator?.Translate(name) ?? name;
        CartographyRegionOption[] regions = CartographyRegionFiles.Catalog(List("world", true), Resolve, FullName, Translate);
        picker = new CartographySourcePicker { Regions = regions, Region = region, Campaign = selectedCampaign, PlayerRegion = playerRegion, Loading = true };
        CartographyRegionFiles files = cornifer ? null : CartographyRegionFiles.Capture(region, folder => List(folder, false));
        string worldPath = files?.Resolve("world/" + region + "/world_" + region + ".txt");
        if (!cornifer && worldPath == null) throw new FileNotFoundException("Region world file not found / 找不到区域文件: " + region);
        string timeline = currentSession.World.game?.TimelinePoint?.value ?? "default";
        pendingSource = new SourceRequest
        {
            Generation = generation, Region = region, Campaign = selectedCampaign, Installation = root, Files = files, Refresh = refresh,
            ImportPath = cornifer ? Path.GetFullPath(path) : null,
            Identity = cornifer ? "" : "region:" + worldPath.ToLowerInvariant() + "|" + region + "|" + selectedCampaign + "|" + timeline
        };
    }
}
