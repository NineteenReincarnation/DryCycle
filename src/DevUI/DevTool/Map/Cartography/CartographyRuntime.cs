using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.History;
using DryCycle.DevUI.DevTool.Map.PlayerMap;

namespace DryCycle.DevUI.DevTool.Map.Cartography;

internal sealed class CartographyPresentation
{
    internal static readonly CartographyPresentation Empty = new();
    internal string Identity = string.Empty, ProjectPath = string.Empty, ExportPath = string.Empty, Status = string.Empty;
    internal long Revision;
    internal bool Dirty, Exporting;
    internal CartographyDocument Document;
    internal CartographyScene Scene;
}

internal static class CartographyRuntime
{
    private sealed class Workspace
    {
        internal string Identity, Path, DiskHash, SavedText, AuthorText;
        internal CartographyDocument Document;
        internal CartographySource Source;
        internal CartographyScene Scene;
        internal readonly CartographySceneCache SceneCache = new();
        internal readonly Dictionary<string, RoomMapBakeSnapshot> Bakes = new(StringComparer.OrdinalIgnoreCase);
        internal long Revision = 1;
        internal string Status = string.Empty, ExportPath = string.Empty;
        internal readonly EditorHistoryService History = new(64);
        internal Task<CartographyExportResult> Export;
        internal PlayerMapPresentationSnapshot ObservedPlayer;
        internal EditorMapPresentationSnapshot ObservedWorld;
        internal bool LoadFailed;
        internal bool Dirty => Document != null && AuthorText != SavedText;
    }

    // Retained author documents/history contain no live Room/World references. Closing DevUI or
    // switching regions does not discard edits, and a returning region gets its exact history back.
    private static readonly Dictionary<string, Workspace> Documents = new(StringComparer.Ordinal);
    private static readonly ConcurrentQueue<CartographyCommand> Commands = new();
    private static readonly ConcurrentDictionary<string, CartographyCommand> Drafts = new(StringComparer.Ordinal);
    private static Workspace current;
    private static EditorSession currentSession;
    private static global::World observedWorld;
    private static string observedIdentity;
    private static volatile bool active;
    private static volatile CartographyPresentation presentation = CartographyPresentation.Empty;

    internal static CartographyPresentation Presentation => presentation;
    internal static bool ActiveFor(EditorSession session) => active && session != null && ReferenceEquals(currentSession, session) &&
        session.ToolMode == EditorToolMode.Map && ReferenceEquals(observedWorld, session.World) &&
        !EditorUiModeState.UseVanilla && !EditorUiModeState.OverlayHidden;
    internal static EditorHistoryService HistoryFor(EditorSession session) => ActiveFor(session) && current != null ? current.History : session?.History;

    internal static void SetActive(bool value) => active = value;

    internal static void Enqueue(CartographyCommand command)
    {
        if (command == null) return;
        if (command.Kind == CartographyCommandKind.Save || command.Kind == CartographyCommandKind.Export || command.Kind == CartographyCommandKind.Open)
            CommitDrafts();
        // The caller transfers values, never mutable view drafts, to the backend queue.
        command.Ids = command.Ids?.ToArray() ?? Array.Empty<string>();
        command.Item = command.Item?.Clone(); command.Layer = command.Layer?.Clone(); command.Style = command.Style?.Clone();
        Commands.Enqueue(command);
    }

    internal static void StageDraft(string key, CartographyCommand command)
    {
        command.Item = command.Item?.Clone(); command.Layer = command.Layer?.Clone(); command.Style = command.Style?.Clone();
        Drafts[key] = command;
    }

    internal static void CommitDraft(string key)
    {
        if (Drafts.TryRemove(key, out CartographyCommand command)) Enqueue(command);
    }

    private static void CommitDrafts()
    {
        foreach (string key in Drafts.Keys) CommitDraft(key);
    }

    internal static void Process(EditorSession session)
    {
        foreach (Workspace workspace in Documents.Values)
        {
            if (workspace.Export?.IsCompleted != true) continue;
            try
            {
                CartographyExportResult result = workspace.Export.GetAwaiter().GetResult();
                workspace.ExportPath = result.Path;
                workspace.Status = "Exported / 已导出 " + result.Width + " × " + result.Height + " · " + result.Path + " " + result.Note;
            }
            catch (Exception error) { Report(workspace, "Export failed / 导出失败", error); }
            workspace.Export = null;
            if (ReferenceEquals(current, workspace)) Publish(workspace);
        }

        if (active && session?.ToolMode == EditorToolMode.Map && session.World != null)
        {
            PlayerMapPresentationSnapshot player = PlayerMapWorkspaceRuntime.GetPresentation(session);
            EditorMapPresentationSnapshot world = MapEditorPresentationHub.Current;
            if (player.Available && world.Available && player.RegionName == session.World.name && world.RegionName == player.RegionName)
            {
                try
                {
                    string identity = Identity(session);
                    if (!Documents.TryGetValue(identity, out Workspace workspace))
                    {
                        workspace = new Workspace { Identity = identity, Path = ProjectPath(identity, player.RegionName) };
                        workspace.History.ActivateDocument(new EditorDocumentKey(EditorDocumentKind.RegionMap, "Cartography:" + identity));
                        Documents.Add(identity, workspace);
                        workspace.Source = Source(player, world, workspace);
                        try
                        {
                            if (File.Exists(workspace.Path))
                            {
                                byte[] bytes = File.ReadAllBytes(workspace.Path);
                                string text = System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
                                workspace.Document = CartographyStorage.Deserialize(text, identity);
                                workspace.DiskHash = CartographyStorage.HashBytes(bytes);
                                workspace.SavedText = CartographyStorage.Serialize(workspace.Document);
                            }
                            else workspace.Document = workspace.Source.CreateDocument(identity, player.RegionName);
                            workspace.AuthorText = CartographyStorage.Serialize(workspace.Document);
                        }
                        catch (Exception error) { workspace.LoadFailed = true; Report(workspace, "Project load failed; original file retained / 项目读取失败，已保留原文件", error); }
                    }
                    bool switched = !ReferenceEquals(current, workspace) || !ReferenceEquals(currentSession, session);
                    current = workspace; currentSession = session;
                    if (switched) EditorRevisionHub.Mark(session, EditorRevisionKind.Shell);
                    if (!ReferenceEquals(workspace.ObservedPlayer, player) || !ReferenceEquals(workspace.ObservedWorld, world))
                    {
                        workspace.Source = Source(player, world, workspace);
                        workspace.ObservedPlayer = player; workspace.ObservedWorld = world;
                        if (workspace.Document != null) workspace.Scene = CartographySceneBuilder.Build(workspace.Document, workspace.Source, workspace.SceneCache);
                        Publish(workspace);
                    }
                    else if (switched) Publish(workspace);
                }
                catch (Exception error)
                {
                    Plugin.Logger?.LogError("Cartography initialization failed: " + error);
                    presentation = new CartographyPresentation { Status = error.Message };
                }
            }
            else presentation = new CartographyPresentation { Status = "Waiting for current region geometry / 正在准备当前区域地形" };
        }
        FlushCommands();
    }

    internal static bool SaveActive(EditorSession session)
    {
        if (!ActiveFor(session) || current?.Document == null) return false;
        CommitDrafts();
        if (!FlushCommands()) return false;
        return Save(current, current.Path);
    }

    internal static void ResetView()
    {
        // Already released drag/inspector commands belong to named retained documents; commit them
        // before closing the frontend. No dirty author state is reset here.
        CommitDrafts(); FlushCommands();
        active = false; currentSession = null; observedWorld = null; observedIdentity = null;
        presentation = CartographyPresentation.Empty;
    }

    private static bool FlushCommands()
    {
        Dictionary<Workspace, long> batchRevisions = new();
        HashSet<Workspace> failed = new();
        bool success = true;
        while (Commands.TryDequeue(out CartographyCommand command))
        {
            if (command.DocumentId == null || !Documents.TryGetValue(command.DocumentId, out Workspace workspace))
            { Plugin.Logger?.LogWarning("Cartography command has no loaded target document."); success = false; continue; }
            // In particular, a failed staged edit must not be followed by "Saved" or a successful
            // export of older data from the same UI action. Keep the original diagnostic visible.
            if (failed.Contains(workspace)) continue;
            try
            {
                // Ctrl+S may have committed a still-focused widget's staged value. Its later focus
                // loss is idempotent, not a stale-edit failure and not a second history entry.
                if (workspace.Document != null && command.Kind == CartographyCommandKind.UpdateItem && command.Item != null &&
                    CartographyEditing.SameItem(workspace.Document.Items.Find(item => item.Id == command.Item.Id), command.Item)) continue;
                if (workspace.Document != null && command.Kind == CartographyCommandKind.UpdateLayer && command.Layer != null &&
                    CartographyEditing.SameLayer(workspace.Document.Layer(command.Layer.Id), command.Layer)) continue;
                if (!batchRevisions.TryGetValue(workspace, out long batchRevision)) batchRevisions[workspace] = batchRevision = workspace.Revision;
                if (command.Revision != batchRevision) throw new InvalidOperationException("The map changed while this edit was in progress. Please repeat the edit / 编辑期间地图已变更，请重试。");
                if (command.Kind == CartographyCommandKind.Open) { Open(workspace, command.Path); batchRevisions[workspace] = workspace.Revision; continue; }
                if (workspace.LoadFailed || workspace.Document == null) throw new InvalidOperationException("Repair the project file before editing; it has not been overwritten.");
                if (command.Kind == CartographyCommandKind.Save)
                {
                    if (!Save(workspace, string.IsNullOrWhiteSpace(command.Path) ? workspace.Path : command.Path)) { failed.Add(workspace); success = false; }
                    continue;
                }
                if (command.Kind == CartographyCommandKind.Export) { Export(workspace, command); continue; }
                CartographyDocument before = workspace.Document;
                CartographyDocument after = CartographyEditing.Apply(before, workspace.Source, command);
                if (CartographyStorage.Serialize(before) == CartographyStorage.Serialize(after)) continue;
                workspace.Status = string.Empty;
                Restore(workspace, after);
                workspace.History.Push(new DelegateHistoryEntry("Cartography · " + command.Kind,
                    _ => Restore(workspace, before), _ => Restore(workspace, after)));
            }
            catch (Exception error)
            { failed.Add(workspace); success = false; Report(workspace, "Cartography edit failed / 制图操作失败", error); PublishIfCurrent(workspace); }
        }
        return success;
    }

    private static bool Restore(Workspace workspace, CartographyDocument document)
    {
        // Prepare everything before swapping the author model. Failed Undo or scene preparation
        // cannot leave the history stack describing a different document from the visible one.
        string text = CartographyStorage.Serialize(document);
        CartographyScene scene = CartographySceneBuilder.Build(document, workspace.Source, workspace.SceneCache);
        workspace.Document = document;
        workspace.Revision++;
        workspace.AuthorText = text;
        workspace.Scene = scene;
        workspace.Status = string.Empty;
        PublishIfCurrent(workspace);
        return true;
    }

    private static bool Save(Workspace workspace, string path)
    {
        try
        {
            path = Path.GetFullPath(path);
            if (!path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Use an .xml project path.");
            bool same = string.Equals(path, Path.GetFullPath(workspace.Path), StringComparison.OrdinalIgnoreCase);
            // Save As never replaces an unrelated author document, even if the user typed its path.
            string hash = CartographyStorage.Save(path, workspace.Document, same ? workspace.DiskHash : null, Log);
            if (same) { workspace.DiskHash = hash; workspace.SavedText = workspace.AuthorText; }
            workspace.Status = (same ? "Saved / 已保存 " : "Project copy saved / 已另存项目副本 ") + path;
            PublishIfCurrent(workspace);
            return true;
        }
        catch (Exception error) { Report(workspace, "Save failed; edits retained / 保存失败，修改已保留", error); PublishIfCurrent(workspace); return false; }
    }

    private static void Export(Workspace workspace, CartographyCommand command)
    {
        if (workspace.Export != null) throw new InvalidOperationException("An export is already running / 导出正在进行。");
        CartographyDocument frozen = workspace.Document.Clone();
        CartographyScene scene = workspace.Scene;
        if (scene.Errors.Length != 0) throw new InvalidOperationException("Wait for terrain or hide unavailable rooms before export / 请等待地形就绪，或隐藏无法读取的房间。\n" + string.Join("; ", scene.Errors.Take(5)));
        CartographyExporter.Dimensions(frozen, scene, out _, out _);
        string path = Path.GetFullPath(command.Path);
        string expectedHash = CartographyStorage.HashFile(path);
        workspace.Export = Task.Run(() => CartographyExporter.Export(frozen, scene, path, (CartographyExportFormat)command.Integer, expectedHash, Log));
        workspace.Status = "Exporting frozen map snapshot / 正在导出地图快照…";
        PublishIfCurrent(workspace);
    }

    private static void Open(Workspace workspace, string path)
    {
        if (workspace.Dirty) throw new InvalidOperationException("Save the current map before opening another project / 请先保存当前制图，修改不会被丢弃。");
        path = Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? workspace.Path : path);
        byte[] bytes = File.ReadAllBytes(path);
        CartographyDocument document = CartographyStorage.Deserialize(System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'), workspace.Identity);
        // Commit only after the complete replacement document passed identity/schema validation.
        Restore(workspace, document);
        workspace.Path = path; workspace.DiskHash = CartographyStorage.HashBytes(bytes);
        workspace.AuthorText = workspace.SavedText = CartographyStorage.Serialize(document);
        workspace.LoadFailed = false; workspace.History.ClearActive();
        workspace.Status = "Opened / 已打开 " + path;
        PublishIfCurrent(workspace);
    }

    private static void PublishIfCurrent(Workspace workspace) { if (ReferenceEquals(current, workspace)) Publish(workspace); }

    private static void Publish(Workspace workspace) => presentation = new CartographyPresentation
    {
        Identity = workspace.Identity, Revision = workspace.Revision, Document = workspace.Document?.Clone(), Scene = workspace.Scene,
        ProjectPath = workspace.Path, ExportPath = workspace.ExportPath, Dirty = workspace.Dirty, Exporting = workspace.Export != null, Status = workspace.Status
    };

    private static void Report(Workspace workspace, string message, Exception error)
    { workspace.Status = message + ": " + error.Message; Plugin.Logger?.LogError(message + ": " + error); }
    private static void Log(string message) => Plugin.Logger?.LogWarning(message);

    private static string Identity(EditorSession session)
    {
        if (ReferenceEquals(observedWorld, session.World) && observedIdentity != null) return observedIdentity;
        string source = AssetManager.ResolveFilePath("World" + Path.DirectorySeparatorChar + session.World.name + Path.DirectorySeparatorChar + "world_" + session.World.name + ".txt");
        observedWorld = session.World;
        return observedIdentity = Path.GetFullPath(source).ToLowerInvariant() + "|" + session.World.name + "|" +
            (session.World.game?.StoryCharacter?.value ?? "default") + "|" + (session.World.game?.TimelinePoint?.value ?? "default");
    }

    private static string ProjectPath(string identity, string region) => Path.Combine(BepInEx.Paths.ConfigPath, "DryCycle", "Cartography",
        new string(region.Where(char.IsLetterOrDigit).ToArray()) + "-" + CartographyStorage.HashText(identity).Substring(0, 16) + ".xml");

    private static CartographySource Source(PlayerMapPresentationSnapshot player, EditorMapPresentationSnapshot world, Workspace workspace)
    {
        CartographySource source = new();
        Dictionary<int, string> names = new();
        foreach (PlayerMapRoomSnapshot room in player.Rooms)
        {
            RoomMapBakeSnapshot bake = room.Bake;
            names[room.RoomIndex] = room.Name;
            if (workspace.Source != null && workspace.Source.Rooms.TryGetValue(room.Name, out CartographyRoomSource previous) &&
                workspace.Bakes.TryGetValue(room.Name, out RoomMapBakeSnapshot oldBake) &&
                ReferenceEquals(oldBake.Runs, bake.Runs) && ReferenceEquals(oldBake.NodeAnchors, bake.NodeAnchors) && oldBake.Status == bake.Status &&
                oldBake.Error == bake.Error && oldBake.Width == bake.Width && oldBake.Height == bake.Height &&
                previous.Layer == room.Layer && previous.Disabled == room.Disabled && previous.X == room.EffectivePosition.x && previous.Y == room.EffectivePosition.y)
            { source.Rooms[room.Name] = previous; continue; }
            CartographyRoomSource entry = new()
            {
                Name = room.Name, Layer = room.Layer, X = room.EffectivePosition.x, Y = room.EffectivePosition.y,
                Width = bake.Width, Height = bake.Height, Ready = bake.Status == RoomMapBakeStatus.Ready, Disabled = room.Disabled, Error = bake.Error,
                Runs = bake.Runs.Select(run => new CartographyTileRun(run.X, run.Y, run.Length, (int)run.Kind, run.Water)).ToArray()
            };
            foreach (RoomMapNodeAnchorSnapshot port in bake.NodeAnchors) entry.Ports[port.NodeIndex] = new CartographyRect(port.EntranceX, port.EntranceY, 0, 0);
            source.Rooms[room.Name] = entry; workspace.Bakes[room.Name] = bake;
        }
        foreach (EditorMapConnectionSnapshot connection in world.Connections)
        {
            if (!names.TryGetValue(connection.FromRoomIndex, out string from) || !names.TryGetValue(connection.ToRoomIndex, out string to)) continue;
            source.Connections.Add(new CartographyConnectionSource { From = from, To = to, FromPort = connection.FromNodeIndex, ToPort = connection.ToNodeIndex, Ambiguous = connection.Ambiguous });
        }
        foreach (string name in workspace.Bakes.Keys.Where(name => !source.Rooms.ContainsKey(name)).ToArray()) workspace.Bakes.Remove(name);
        return source;
    }
}
