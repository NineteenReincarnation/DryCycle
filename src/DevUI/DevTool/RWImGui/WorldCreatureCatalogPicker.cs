using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using ImGuiNET;
using UnityEngine;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Main-thread runtime for the visual creature catalog. Expensive Unity work never runs inside the
/// RWImGui DX11 render callback. A persistent cache makes subsequent game sessions immediately
/// usable and only changed plugin sources are rescanned.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(BridgePlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldCreatureCatalogRuntimePlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMap.CreatureCatalogRuntime";
    public const string PluginName = "DryCycle DevTool Creature Catalog Runtime";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldCreatureCatalogPicker.Initialize(Logger);
    private void Update() => WorldCreatureCatalogPicker.PumpMainThread();
    private void OnDisable() => WorldCreatureCatalogPicker.Shutdown();
}

internal static class WorldCreatureCatalogPicker
{
    private enum IconState
    {
        Pending,
        Ready,
        Failed
    }

    private enum IconBuildResult
    {
        Ready,
        Failed,
        Deferred
    }

    private sealed class CreatureEntry
    {
        internal string Id = string.Empty;
        internal CreatureTemplate.Type Type;
        internal string SourceKey = string.Empty;
        internal string SourceLabel = string.Empty;
        internal string SourceFingerprint = string.Empty;
    }

    private sealed class SourceGroup
    {
        internal string Key = string.Empty;
        internal string Label = string.Empty;
        internal CreatureEntry[] Entries = Array.Empty<CreatureEntry>();
    }

    private sealed class CatalogSnapshot
    {
        internal static readonly CatalogSnapshot Empty = new();
        internal SourceGroup[] Groups = Array.Empty<SourceGroup>();
        internal Dictionary<string, CreatureEntry> ById = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class PluginSource
    {
        internal Assembly Assembly;
        internal string Key = string.Empty;
        internal string Label = string.Empty;
        internal string Fingerprint = string.Empty;
        internal bool NeedsScan;
        internal readonly List<string> CreatureIds = new();
    }

    private sealed class PersistentSource
    {
        internal string Key = string.Empty;
        internal string Label = string.Empty;
        internal string Fingerprint = string.Empty;
        internal string[] CreatureIds = Array.Empty<string>();
    }

    private sealed class IconSlot
    {
        internal IconState State;
        internal IconRaster Raster = new();
        internal string SourceFingerprint = string.Empty;
        internal bool Validated;
        internal bool ValidationQueued;
    }

    private sealed class AtlasPixels
    {
        internal int Width;
        internal int Height;
        internal Color32[] Pixels = Array.Empty<Color32>();
        internal int LastUseFrame;
        internal int Bytes;
    }

    private readonly struct PixelRun
    {
        internal PixelRun(int x, int y, int width, Color32 color)
        {
            X = x;
            Y = y;
            Width = width;
            Color = color;
        }

        internal int X { get; }
        internal int Y { get; }
        internal int Width { get; }
        internal Color32 Color { get; }
    }

    private sealed class IconRaster
    {
        internal string SpriteName = string.Empty;
        internal string AtlasSignature = string.Empty;
        internal int Width = 1;
        internal int Height = 1;
        internal Color Tint = Color.white;
        internal PixelRun[] Runs = Array.Empty<PixelRun>();
        internal bool Available;
    }

    private sealed class PersistentIcon
    {
        internal string CreatureId = string.Empty;
        internal string SourceFingerprint = string.Empty;
        internal IconState State;
        internal IconRaster Raster = new();
    }

    private sealed class PersistentSnapshot
    {
        internal readonly List<PersistentSource> Sources = new();
        internal readonly List<PersistentIcon> Icons = new();
    }

    private const int CacheMagic = 0x44434343; // DCCC
    private const int CacheVersion = 4;
    private const int MaxPreparedIconsPerFrame = 4;
    private const int MaxGpuReadbacksPerFrame = 1;
    private const int MaxSandboxSymbolsPerFrame = 48;
    private const double MainThreadBudgetMs = 1.75;
    private const int MaxIconRasterDimension = 40;
    private const int MaxAtlasCacheBytes = 32 * 1024 * 1024;
    private const float SaveDebounceSeconds = 2.0f;

    private static readonly object iconSync = new();
    private static readonly Dictionary<string, IconSlot> iconSlots = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> iconRequests = new();
    private static readonly HashSet<string> queuedIcons = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, AtlasPixels> atlasPixels = new();
    private static readonly HashSet<int> unreadableAtlases = new();
    private static readonly Dictionary<string, string> searchByPicker = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, string> sourceByCreature = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> sourceLabelByKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> sourceFingerprintByKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, IconSymbol.IconSymbolData> sandboxSymbols = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, PersistentSource> persistentSources = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> registeredIds = new();
    private static readonly HashSet<string> registeredIdSet = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<PluginSource> pluginSources = new();
    private static readonly List<PluginSource> pluginScanQueue = new();
    private static readonly List<string> sandboxUnlockIds = new();

    private static volatile CatalogSnapshot catalog = CatalogSnapshot.Empty;
    private static volatile bool catalogRequested;
    private static ManualLogSource log;
    private static bool initialized;
    private static bool currentCatalogValidated;
    private static int observedCreatureCount = -1;
    private static int observedPluginCount = -1;
    private static int pluginScanIndex;
    private static int sandboxScanIndex;
    private static bool sandboxScanComplete;
    private static int atlasCacheBytes;
    private static bool gpuFallbackLogged;

    private static string cacheFilePath = string.Empty;
    private static bool cacheDirty;
    private static float cacheDirtyAt;
    private static volatile bool cacheSaveInFlight;
    private static volatile string backgroundSaveError;

    internal static void Initialize(ManualLogSource logger)
    {
        log = logger;
        initialized = true;
        cacheFilePath = ResolveCachePath();
        LoadPersistentCache();
    }

    internal static void Shutdown()
    {
        if (cacheDirty && !cacheSaveInFlight)
        {
            try
            {
                WritePersistentCache(CapturePersistentSnapshot());
            }
            catch
            {
            }
        }

        initialized = false;
        catalogRequested = false;
        catalog = CatalogSnapshot.Empty;
        currentCatalogValidated = false;
        observedCreatureCount = -1;
        observedPluginCount = -1;
        pluginScanIndex = 0;
        sandboxScanIndex = 0;
        sandboxScanComplete = false;
        registeredIds.Clear();
        registeredIdSet.Clear();
        pluginSources.Clear();
        pluginScanQueue.Clear();
        sandboxUnlockIds.Clear();
        persistentSources.Clear();
        sourceByCreature.Clear();
        sourceLabelByKey.Clear();
        sourceFingerprintByKey.Clear();
        sandboxSymbols.Clear();
        searchByPicker.Clear();
        atlasPixels.Clear();
        unreadableAtlases.Clear();
        atlasCacheBytes = 0;
        gpuFallbackLogged = false;
        cacheDirty = false;
        cacheSaveInFlight = false;
        backgroundSaveError = null;
        lock (iconSync)
        {
            iconSlots.Clear();
            iconRequests.Clear();
            queuedIcons.Clear();
        }
        log = null;
    }

    /// <summary>
    /// Unity-main-thread work pump. The render callback only consumes already prepared data.
    /// </summary>
    internal static void PumpMainThread()
    {
        if (!initialized) return;

        if (!string.IsNullOrEmpty(backgroundSaveError))
        {
            string error = backgroundSaveError;
            backgroundSaveError = null;
            log?.LogWarning("Creature catalog cache save failed: " + error);
        }

        if (!catalogRequested)
        {
            TrySchedulePersistentSave();
            return;
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        EnsureCatalogMainThread();

        while (!sandboxScanComplete && stopwatch.Elapsed.TotalMilliseconds < MainThreadBudgetMs)
        {
            int processed = PumpSandboxSymbols(MaxSandboxSymbolsPerFrame);
            if (processed == 0) break;
        }

        if (pluginScanIndex < pluginScanQueue.Count && stopwatch.Elapsed.TotalMilliseconds < MainThreadBudgetMs)
            ScanNextPluginSource();

        int prepared = 0;
        int gpuReadbacks = 0;
        while (prepared < MaxPreparedIconsPerFrame && stopwatch.Elapsed.TotalMilliseconds < MainThreadBudgetMs)
        {
            if (!sandboxScanComplete) break;
            if (!TryDequeueIcon(out string creatureId)) break;
            CatalogSnapshot snapshot = catalog;
            if (!snapshot.ById.TryGetValue(creatureId, out CreatureEntry entry) || entry.Type == null)
            {
                SetIconFailed(creatureId, string.Empty);
                continue;
            }

            bool allowGpu = gpuReadbacks < MaxGpuReadbacksPerFrame;
            IconBuildResult result = BuildIconMainThread(entry, allowGpu, out IconRaster raster, out bool usedGpu);
            if (result == IconBuildResult.Deferred)
            {
                RequeueIcon(creatureId);
                break;
            }

            if (usedGpu) gpuReadbacks++;
            prepared++;
            if (result == IconBuildResult.Ready) SetIconReady(creatureId, entry.SourceFingerprint, raster);
            else SetIconFailed(creatureId, entry.SourceFingerprint);
        }

        if (currentCatalogValidated && pluginScanIndex >= pluginScanQueue.Count)
            FinalizePersistentSources();

        TrySchedulePersistentSave();
    }

    internal static bool DrawSelector(
        string widgetId,
        ref string creatureId,
        bool allowNone = false,
        string label = null)
    {
        catalogRequested = true;
        CatalogSnapshot snapshot = catalog;

        string popupId = "##CreatureCatalogPicker_" + widgetId;
        string current = string.IsNullOrWhiteSpace(creatureId)
            ? DevToolUiSettings.T("选择生物…", "Choose creature…")
            : creatureId;
        string buttonLabel = string.IsNullOrWhiteSpace(label)
            ? DevToolUiSettings.T("选择生物", "Choose Creature") + "  ·  " + current
            : label + "  ·  " + current;

        bool changed = false;
        if (DevToolWidgets.ActionButton(buttonLabel, "CreatureCatalogOpen_" + widgetId, DevToolButtonTone.Normal, true))
        {
            searchByPicker[popupId] = string.Empty;
            ImGui.OpenPopup(popupId);
        }

        ImGuiIOPtr io = ImGui.GetIO();
        Num.Vector2 desired = new(
            Math.Min(900f, Math.Max(560f, io.DisplaySize.X * 0.58f)),
            Math.Min(720f, Math.Max(430f, io.DisplaySize.Y * 0.72f)));
        ImGui.SetNextWindowSize(desired, ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(Math.Min(520f, Math.Max(280f, io.DisplaySize.X - 24f)), Math.Min(360f, Math.Max(220f, io.DisplaySize.Y - 24f))),
            new Num.Vector2(Math.Max(520f, io.DisplaySize.X - 20f), Math.Max(360f, io.DisplaySize.Y - 20f)));

        if (!ImGui.BeginPopup(popupId)) return false;

        DevToolWidgets.PaneTitle(DevToolUiSettings.T("生物图鉴", "CREATURE CATALOG"));
        DevToolWidgets.MutedText(
            DevToolUiSettings.T(
                "已缓存内容立即显示；只有新增或变化的 Mod / 图标会在后台增量刷新。",
                "Cached content is immediate; only new or changed mods/icons are refreshed incrementally."),
            true);

        if (!searchByPicker.TryGetValue(popupId, out string search)) search = string.Empty;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputTextWithHint(
                "##CreatureCatalogSearch_" + widgetId,
                DevToolUiSettings.T("搜索生物 ID 或 Mod…", "Search creature ID or mod…"),
                ref search,
                160))
            searchByPicker[popupId] = search;

        ImGui.Spacing();
        if (ImGui.BeginChild("##CreatureCatalogScroll_" + widgetId, new Num.Vector2(0f, 0f), ImGuiChildFlags.None))
        {
            if (allowNone)
            {
                DevToolSourceMark special = DevToolSourcePresentation.FromLabel(DevToolUiSettings.T("Lineage 特殊项", "LINEAGE SPECIAL"));
                DevToolSourcePresentation.DrawHeader(special, 1.24f, 1f);
                bool selectedNone = string.Equals(creatureId, "NONE", StringComparison.OrdinalIgnoreCase);
                if (DrawNoneCard("None_" + widgetId, selectedNone))
                {
                    creatureId = "NONE";
                    changed = true;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.Spacing();
            }

            if (snapshot.Groups.Length == 0)
            {
                DevToolWidgets.MutedText(
                    DevToolUiSettings.T("首次建立生物目录缓存…", "Building the creature catalog cache for the first time…"),
                    true);
            }
            else
            {
                int visibleCount = 0;
                for (int groupIndex = 0; groupIndex < snapshot.Groups.Length; groupIndex++)
                {
                    SourceGroup group = snapshot.Groups[groupIndex];
                    int matching = CountMatching(group, search);
                    if (matching == 0) continue;
                    visibleCount += matching;

                    DevToolSourceMark mark = DevToolSourcePresentation.FromLabel(group.Label);
                    DevToolSourcePresentation.DrawHeader(mark, 1.36f, 1f);
                    if (DrawGroupGrid(group, search, widgetId, creatureId, out string selected))
                    {
                        creatureId = selected;
                        changed = true;
                        ImGui.CloseCurrentPopup();
                        break;
                    }
                    ImGui.Spacing();
                }

                if (visibleCount == 0)
                    DevToolWidgets.MutedText(DevToolUiSettings.T("没有匹配的生物。", "No matching creatures."), true);
            }

            if (!string.IsNullOrWhiteSpace(creatureId) &&
                !string.Equals(creatureId, "NONE", StringComparison.OrdinalIgnoreCase) &&
                !snapshot.ById.ContainsKey(creatureId))
            {
                DevToolSourcePresentation.DrawHeader(
                    DevToolSourcePresentation.FromLabel(DevToolUiSettings.T("缺失 Mod", "MISSING MOD")),
                    1.28f,
                    1f);
                DevToolWidgets.MutedText(
                    DevToolUiSettings.T(
                        "当前 world.txt 使用的生物未在本次运行注册；原值会保持不变。",
                        "The creature stored in world.txt is not registered in this run; its value is preserved."),
                    true);
                DrawMissingCard(creatureId);
            }
        }
        ImGui.EndChild();
        ImGui.EndPopup();
        return changed;
    }

    private static bool DrawGroupGrid(
        SourceGroup group,
        string search,
        string widgetId,
        string current,
        out string selected)
    {
        selected = null;
        const float cardWidth = 112f;
        const float cardHeight = 102f;
        float spacing = Math.Max(6f, ImGui.GetStyle().ItemSpacing.X);
        float available = Math.Max(cardWidth, ImGui.GetContentRegionAvail().X);
        int columns = Math.Max(1, (int)Math.Floor((available + spacing) / (cardWidth + spacing)));

        List<CreatureEntry> filtered = new();
        for (int i = 0; i < group.Entries.Length; i++)
            if (Matches(group.Entries[i], group, search)) filtered.Add(group.Entries[i]);
        if (filtered.Count == 0) return false;

        int rows = (filtered.Count + columns - 1) / columns;
        float clipTop = ImGui.GetWindowPos().Y - cardHeight;
        float clipBottom = ImGui.GetWindowPos().Y + ImGui.GetWindowHeight() + cardHeight;

        for (int row = 0; row < rows; row++)
        {
            float rowScreenY = ImGui.GetCursorScreenPos().Y;
            if (rowScreenY + cardHeight < clipTop || rowScreenY > clipBottom)
            {
                ImGui.Dummy(new Num.Vector2(1f, cardHeight));
                continue;
            }

            int start = row * columns;
            int end = Math.Min(filtered.Count, start + columns);
            for (int i = start; i < end; i++)
            {
                if (i > start) ImGui.SameLine(0f, spacing);
                CreatureEntry entry = filtered[i];
                bool isSelected = string.Equals(current, entry.Id, StringComparison.Ordinal);
                if (DrawCreatureCard(entry, widgetId + "_" + group.Key + "_" + i, cardWidth, cardHeight, isSelected))
                {
                    selected = entry.Id;
                    return true;
                }
            }
        }
        return false;
    }

    private static bool DrawCreatureCard(
        CreatureEntry entry,
        string cardId,
        float width,
        float height,
        bool selected)
    {
        Num.Vector2 pos = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##CreatureCard_" + cardId, new Num.Vector2(width, height));
        bool hovered = ImGui.IsItemHovered();
        bool clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        Num.Vector2 max = pos + new Num.Vector2(width, height);
        ImDrawListPtr draw = ImGui.GetWindowDrawList();

        Num.Vector4 bg = selected
            ? new Num.Vector4(0.16f, 0.32f, 0.52f, 0.92f)
            : hovered
                ? new Num.Vector4(0.18f, 0.21f, 0.27f, 0.94f)
                : new Num.Vector4(0.10f, 0.12f, 0.16f, 0.82f);
        Num.Vector4 border = selected
            ? new Num.Vector4(0.34f, 0.66f, 1f, 1f)
            : hovered
                ? new Num.Vector4(0.48f, 0.55f, 0.66f, 0.95f)
                : new Num.Vector4(0.28f, 0.31f, 0.37f, 0.80f);
        draw.AddRectFilled(pos, max, ImGui.GetColorU32(bg), 5f);
        draw.AddRect(pos, max, ImGui.GetColorU32(border), 5f, ImDrawFlags.None, selected ? 2.0f : 1f);

        TryGetIconForRender(entry.Id, out IconRaster icon, out IconState state);
        DrawIcon(draw, icon, state, pos + new Num.Vector2(8f, 7f), new Num.Vector2(width - 16f, 65f));
        DrawCenteredId(draw, entry.Id, pos, width, height - 26f);

        if (hovered)
        {
            string iconState = state == IconState.Ready && icon.Available
                ? icon.SpriteName
                : state == IconState.Pending
                    ? DevToolUiSettings.T("图标载入中", "Icon pending")
                    : DevToolUiSettings.T("Sandbox 图标不可用", "Sandbox icon unavailable");
            DevToolTooltip.Show(entry.SourceLabel + "\n" + entry.Id + "\n" + iconState);
        }
        return clicked;
    }

    private static bool DrawNoneCard(string id, bool selected)
    {
        const float width = 112f;
        const float height = 92f;
        Num.Vector2 pos = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##LineageNoneCard_" + id, new Num.Vector2(width, height));
        bool hovered = ImGui.IsItemHovered();
        bool clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        Num.Vector2 max = pos + new Num.Vector2(width, height);
        draw.AddRectFilled(pos, max, ImGui.GetColorU32(selected
            ? new Num.Vector4(0.16f, 0.32f, 0.52f, 0.92f)
            : new Num.Vector4(0.10f, 0.12f, 0.16f, hovered ? 0.96f : 0.82f)), 5f);
        draw.AddRect(pos, max, ImGui.GetColorU32(selected
            ? new Num.Vector4(0.34f, 0.66f, 1f, 1f)
            : new Num.Vector4(0.34f, 0.38f, 0.45f, 0.86f)), 5f, ImDrawFlags.None, selected ? 2f : 1f);
        Num.Vector2 center = pos + new Num.Vector2(width * 0.5f, 35f);
        uint muted = ImGui.GetColorU32(new Num.Vector4(0.56f, 0.60f, 0.68f, 1f));
        draw.AddCircle(center, 18f, muted, 28, 2f);
        draw.AddLine(center + new Num.Vector2(-13f, 13f), center + new Num.Vector2(13f, -13f), muted, 2f);
        DrawCenteredId(draw, "NONE", pos, width, height - 23f);
        return clicked;
    }

    private static void DrawMissingCard(string id)
    {
        const float width = 180f;
        const float height = 76f;
        Num.Vector2 pos = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Num.Vector2(width, height));
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        Num.Vector2 max = pos + new Num.Vector2(width, height);
        draw.AddRectFilled(pos, max, ImGui.GetColorU32(new Num.Vector4(0.30f, 0.14f, 0.08f, 0.78f)), 5f);
        draw.AddRect(pos, max, ImGui.GetColorU32(new Num.Vector4(0.94f, 0.45f, 0.22f, 0.95f)), 5f);
        DrawCenteredId(draw, id, pos, width, height - 27f);
    }

    private static void DrawIcon(
        ImDrawListPtr draw,
        IconRaster icon,
        IconState state,
        Num.Vector2 areaPos,
        Num.Vector2 areaSize)
    {
        if (state != IconState.Ready || icon == null || !icon.Available || icon.Runs.Length == 0)
        {
            Num.Vector2 c = areaPos + areaSize * 0.5f;
            uint color = ImGui.GetColorU32(state == IconState.Pending
                ? new Num.Vector4(0.46f, 0.64f, 0.82f, 0.90f)
                : new Num.Vector4(0.55f, 0.58f, 0.64f, 0.85f));
            draw.AddCircle(c, Math.Min(areaSize.X, areaSize.Y) * 0.24f, color, 24, 1.6f);
            if (state == IconState.Pending)
            {
                draw.AddCircleFilled(c + new Num.Vector2(-8f, 0f), 2f, color);
                draw.AddCircleFilled(c, 2f, color);
                draw.AddCircleFilled(c + new Num.Vector2(8f, 0f), 2f, color);
            }
            else
            {
                draw.AddText(c - new Num.Vector2(3.5f, 8f), color, "?");
            }
            return;
        }

        float scale = Math.Min(areaSize.X / Math.Max(1, icon.Width), areaSize.Y / Math.Max(1, icon.Height));
        scale = Math.Max(1f, Math.Min(scale, 4f));
        Num.Vector2 origin = areaPos + new Num.Vector2(
            (areaSize.X - icon.Width * scale) * 0.5f,
            (areaSize.Y - icon.Height * scale) * 0.5f);

        for (int i = 0; i < icon.Runs.Length; i++)
        {
            PixelRun run = icon.Runs[i];
            float alpha = run.Color.a / 255f;
            if (alpha <= 0.02f) continue;
            Num.Vector4 color = new(
                icon.Tint.r * run.Color.r / 255f,
                icon.Tint.g * run.Color.g / 255f,
                icon.Tint.b * run.Color.b / 255f,
                icon.Tint.a * alpha);
            float y = icon.Height - 1 - run.Y;
            Num.Vector2 a = origin + new Num.Vector2(run.X * scale, y * scale);
            Num.Vector2 b = a + new Num.Vector2(run.Width * scale, scale);
            draw.AddRectFilled(a, b, ImGui.GetColorU32(color));
        }
    }

    private static void DrawCenteredId(ImDrawListPtr draw, string id, Num.Vector2 cardPos, float cardWidth, float y)
    {
        string shown = FitText(id ?? string.Empty, cardWidth - 10f);
        Num.Vector2 size = ImGui.CalcTextSize(shown);
        draw.AddText(
            new Num.Vector2(cardPos.X + Math.Max(5f, (cardWidth - size.X) * 0.5f), cardPos.Y + y),
            ImGui.GetColorU32(ImGuiCol.Text),
            shown);
    }

    private static string FitText(string value, float maxWidth)
    {
        if (ImGui.CalcTextSize(value).X <= maxWidth) return value;
        const string ellipsis = "…";
        int length = value.Length;
        while (length > 2)
        {
            string candidate = value.Substring(0, --length) + ellipsis;
            if (ImGui.CalcTextSize(candidate).X <= maxWidth) return candidate;
        }
        return ellipsis;
    }

    private static void TryGetIconForRender(string creatureId, out IconRaster raster, out IconState state)
    {
        lock (iconSync)
        {
            if (iconSlots.TryGetValue(creatureId, out IconSlot slot))
            {
                raster = slot.Raster;
                state = slot.State;
                if (slot.State == IconState.Ready && !slot.Validated && !slot.ValidationQueued && currentCatalogValidated)
                {
                    slot.ValidationQueued = true;
                    if (queuedIcons.Add(creatureId)) iconRequests.Enqueue(creatureId);
                }
                return;
            }

            iconSlots[creatureId] = new IconSlot
            {
                State = IconState.Pending,
                ValidationQueued = true
            };
            if (queuedIcons.Add(creatureId)) iconRequests.Enqueue(creatureId);
            raster = new IconRaster();
            state = IconState.Pending;
        }
    }

    private static bool TryDequeueIcon(out string creatureId)
    {
        lock (iconSync)
        {
            if (iconRequests.Count == 0)
            {
                creatureId = null;
                return false;
            }
            creatureId = iconRequests.Dequeue();
            queuedIcons.Remove(creatureId);
            if (iconSlots.TryGetValue(creatureId, out IconSlot slot)) slot.ValidationQueued = false;
            return true;
        }
    }

    private static void RequeueIcon(string creatureId)
    {
        lock (iconSync)
        {
            if (iconSlots.TryGetValue(creatureId, out IconSlot slot)) slot.ValidationQueued = true;
            if (queuedIcons.Add(creatureId)) iconRequests.Enqueue(creatureId);
        }
    }

    private static void SetIconReady(string creatureId, string sourceFingerprint, IconRaster raster)
    {
        lock (iconSync)
        {
            iconSlots[creatureId] = new IconSlot
            {
                State = IconState.Ready,
                Raster = raster ?? new IconRaster(),
                SourceFingerprint = sourceFingerprint ?? string.Empty,
                Validated = true
            };
        }
        MarkCacheDirty();
    }

    private static void SetIconFailed(string creatureId, string sourceFingerprint)
    {
        lock (iconSync)
        {
            iconSlots[creatureId] = new IconSlot
            {
                State = IconState.Failed,
                Raster = new IconRaster(),
                SourceFingerprint = sourceFingerprint ?? string.Empty,
                Validated = true
            };
        }
        MarkCacheDirty();
    }

    private static IconBuildResult BuildIconMainThread(
        CreatureEntry entry,
        bool allowGpuReadback,
        out IconRaster raster,
        out bool usedGpuReadback)
    {
        raster = new IconRaster();
        usedGpuReadback = false;
        if (entry?.Type == null) return IconBuildResult.Failed;

        try
        {
            IconSymbol.IconSymbolData symbol = sandboxSymbols.TryGetValue(entry.Id, out IconSymbol.IconSymbolData mapped)
                ? mapped
                : new IconSymbol.IconSymbolData(entry.Type, AbstractPhysicalObject.AbstractObjectType.Creature, 0);
            string spriteName = CreatureSymbol.SpriteNameOfCreature(symbol) ?? string.Empty;
            Color tint = CreatureSymbol.ColorOfCreature(symbol);

            FAtlasElement element = null;
            Texture2D texture = null;
            string atlasSignature = string.Empty;
            if (!string.IsNullOrEmpty(spriteName) &&
                !string.Equals(spriteName, "Futile_White", StringComparison.Ordinal) &&
                Futile.atlasManager != null &&
                Futile.atlasManager.DoesContainElementWithName(spriteName))
            {
                element = Futile.atlasManager.GetElementWithName(spriteName);
                texture = element?.atlas?.texture as Texture2D;
                if (texture != null)
                    atlasSignature = BuildAtlasSignature(texture, element.uvRect);
            }

            lock (iconSync)
            {
                if (iconSlots.TryGetValue(entry.Id, out IconSlot cached) &&
                    cached.State == IconState.Ready &&
                    !cached.Validated &&
                    string.Equals(cached.SourceFingerprint, entry.SourceFingerprint, StringComparison.Ordinal) &&
                    string.Equals(cached.Raster.SpriteName, spriteName, StringComparison.Ordinal) &&
                    string.Equals(cached.Raster.AtlasSignature, atlasSignature, StringComparison.Ordinal) &&
                    SimilarTint(cached.Raster.Tint, tint))
                {
                    cached.Validated = true;
                    cached.ValidationQueued = false;
                    raster = cached.Raster;
                    return IconBuildResult.Ready;
                }
            }

            raster.SpriteName = spriteName;
            raster.AtlasSignature = atlasSignature;
            raster.Tint = tint;
            if (texture == null || element == null)
                return IconBuildResult.Ready;

            Rect uv = element.uvRect;
            int x = Mathf.Clamp(Mathf.RoundToInt(uv.x * texture.width), 0, Math.Max(0, texture.width - 1));
            int y = Mathf.Clamp(Mathf.RoundToInt(uv.y * texture.height), 0, Math.Max(0, texture.height - 1));
            int width = Mathf.Clamp(Mathf.RoundToInt(Mathf.Abs(uv.width) * texture.width), 1, Math.Max(1, texture.width - x));
            int height = Mathf.Clamp(Mathf.RoundToInt(Mathf.Abs(uv.height) * texture.height), 1, Math.Max(1, texture.height - y));

            Color32[] pixels;
            if (!TryReadCpuRegion(texture, x, y, width, height, out pixels))
            {
                if (!allowGpuReadback) return IconBuildResult.Deferred;
                if (!TryReadGpuRegion(texture, x, y, width, height, out pixels)) return IconBuildResult.Failed;
                usedGpuReadback = true;
                if (!gpuFallbackLogged)
                {
                    gpuFallbackLogged = true;
                    log?.LogInfo("Creature catalog uses budgeted main-thread GPU readback only for changed non-readable atlas entries.");
                }
            }

            Downsample(ref pixels, ref width, ref height);
            raster.Width = width;
            raster.Height = height;
            raster.Runs = BuildRuns(pixels, width, height);
            raster.Available = raster.Runs.Length > 0;
            return IconBuildResult.Ready;
        }
        catch (Exception error)
        {
            log?.LogWarning("Creature catalog icon failed for '" + entry.Id + "': " + error.Message);
            return IconBuildResult.Failed;
        }
    }

    private static string BuildAtlasSignature(Texture2D texture, Rect uv)
    {
        if (texture == null) return string.Empty;
        return (texture.name ?? string.Empty) + "|" + texture.width + "x" + texture.height + "|" +
               uv.x.ToString("R") + "," + uv.y.ToString("R") + "," + uv.width.ToString("R") + "," + uv.height.ToString("R");
    }

    private static bool SimilarTint(Color a, Color b) =>
        Math.Abs(a.r - b.r) < 0.001f &&
        Math.Abs(a.g - b.g) < 0.001f &&
        Math.Abs(a.b - b.b) < 0.001f &&
        Math.Abs(a.a - b.a) < 0.001f;

    private static bool TryReadCpuRegion(
        Texture2D texture,
        int x,
        int y,
        int width,
        int height,
        out Color32[] region)
    {
        region = null;
        if (texture == null) return false;
        int key = texture.GetInstanceID();
        if (unreadableAtlases.Contains(key)) return false;

        if (!atlasPixels.TryGetValue(key, out AtlasPixels snapshot) ||
            snapshot.Width != texture.width || snapshot.Height != texture.height)
        {
            Color32[] all;
            try
            {
                all = texture.GetPixels32();
            }
            catch
            {
                unreadableAtlases.Add(key);
                return false;
            }

            if (all == null || all.Length != texture.width * texture.height)
            {
                unreadableAtlases.Add(key);
                return false;
            }

            snapshot = new AtlasPixels
            {
                Width = texture.width,
                Height = texture.height,
                Pixels = all,
                LastUseFrame = Time.frameCount,
                Bytes = all.Length * 4
            };
            CacheAtlas(key, snapshot);
        }
        else
        {
            snapshot.LastUseFrame = Time.frameCount;
        }

        if (snapshot.Pixels == null || snapshot.Pixels.Length != snapshot.Width * snapshot.Height) return false;
        region = CopyRegion(snapshot.Pixels, snapshot.Width, snapshot.Height, x, y, width, height);
        return region != null;
    }

    private static void CacheAtlas(int key, AtlasPixels snapshot)
    {
        if (snapshot.Bytes > MaxAtlasCacheBytes) return;
        while (atlasCacheBytes + snapshot.Bytes > MaxAtlasCacheBytes && atlasPixels.Count > 0)
        {
            int oldestKey = 0;
            int oldestFrame = int.MaxValue;
            foreach (KeyValuePair<int, AtlasPixels> pair in atlasPixels)
            {
                if (pair.Value.LastUseFrame >= oldestFrame) continue;
                oldestFrame = pair.Value.LastUseFrame;
                oldestKey = pair.Key;
            }
            if (!atlasPixels.TryGetValue(oldestKey, out AtlasPixels oldest)) break;
            atlasCacheBytes -= oldest.Bytes;
            atlasPixels.Remove(oldestKey);
        }

        if (atlasPixels.TryGetValue(key, out AtlasPixels previous)) atlasCacheBytes -= previous.Bytes;
        atlasPixels[key] = snapshot;
        atlasCacheBytes += snapshot.Bytes;
    }

    private static Color32[] CopyRegion(
        Color32[] source,
        int sourceWidth,
        int sourceHeight,
        int x,
        int y,
        int width,
        int height)
    {
        if (source == null || sourceWidth <= 0 || sourceHeight <= 0 || width <= 0 || height <= 0) return null;
        if (x < 0 || y < 0 || x + width > sourceWidth || y + height > sourceHeight) return null;
        Color32[] result = new Color32[width * height];
        for (int row = 0; row < height; row++)
            Array.Copy(source, (y + row) * sourceWidth + x, result, row * width, width);
        return result;
    }

    private static bool TryReadGpuRegion(
        Texture2D texture,
        int x,
        int y,
        int width,
        int height,
        out Color32[] pixels)
    {
        pixels = null;
        RenderTexture previous = RenderTexture.active;
        RenderTexture target = null;
        Texture2D readable = null;
        try
        {
            target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            target.filterMode = FilterMode.Point;
            Vector2 scale = new((float)width / texture.width, (float)height / texture.height);
            Vector2 offset = new((float)x / texture.width, (float)y / texture.height);
            Graphics.Blit(texture, target, scale, offset);
            RenderTexture.active = target;
            readable = new Texture2D(width, height, TextureFormat.RGBA32, false);
            readable.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);
            readable.Apply(false, false);
            pixels = readable.GetPixels32();
            return pixels != null && pixels.Length == width * height;
        }
        catch
        {
            pixels = null;
            return false;
        }
        finally
        {
            RenderTexture.active = previous;
            if (readable != null) UnityEngine.Object.Destroy(readable);
            if (target != null) RenderTexture.ReleaseTemporary(target);
        }
    }

    private static void Downsample(ref Color32[] pixels, ref int width, ref int height)
    {
        int max = Math.Max(width, height);
        if (pixels == null || max <= MaxIconRasterDimension) return;

        float scale = (float)MaxIconRasterDimension / max;
        int nextWidth = Math.Max(1, Mathf.RoundToInt(width * scale));
        int nextHeight = Math.Max(1, Mathf.RoundToInt(height * scale));
        Color32[] next = new Color32[nextWidth * nextHeight];
        for (int yy = 0; yy < nextHeight; yy++)
        {
            int sourceY = Math.Min(height - 1, (int)((long)yy * height / nextHeight));
            for (int xx = 0; xx < nextWidth; xx++)
            {
                int sourceX = Math.Min(width - 1, (int)((long)xx * width / nextWidth));
                next[yy * nextWidth + xx] = pixels[sourceY * width + sourceX];
            }
        }
        pixels = next;
        width = nextWidth;
        height = nextHeight;
    }

    private static PixelRun[] BuildRuns(Color32[] pixels, int width, int height)
    {
        if (pixels == null || pixels.Length != width * height) return Array.Empty<PixelRun>();
        List<PixelRun> runs = new();
        for (int y = 0; y < height; y++)
        {
            int x = 0;
            while (x < width)
            {
                Color32 color = pixels[y * width + x];
                if (color.a < 12)
                {
                    x++;
                    continue;
                }

                int start = x++;
                while (x < width)
                {
                    Color32 next = pixels[y * width + x];
                    if (next.a < 12 || !Similar(color, next)) break;
                    x++;
                }
                runs.Add(new PixelRun(start, y, x - start, color));
            }
        }
        return runs.ToArray();
    }

    private static bool Similar(Color32 a, Color32 b) =>
        Math.Abs(a.r - b.r) <= 10 &&
        Math.Abs(a.g - b.g) <= 10 &&
        Math.Abs(a.b - b.b) <= 10 &&
        Math.Abs(a.a - b.a) <= 14;

    private static void EnsureCatalogMainThread()
    {
        int creatureCount = ExtEnum<CreatureTemplate.Type>.values.entries.Count;
        int pluginCount = Chainloader.PluginInfos.Count;
        if (currentCatalogValidated && creatureCount == observedCreatureCount && pluginCount == observedPluginCount)
            return;

        observedCreatureCount = creatureCount;
        observedPluginCount = pluginCount;
        BeginCatalogRebuild();
    }

    private static void BeginCatalogRebuild()
    {
        sourceByCreature.Clear();
        sourceLabelByKey.Clear();
        sourceFingerprintByKey.Clear();
        sandboxSymbols.Clear();
        registeredIds.Clear();
        registeredIdSet.Clear();
        pluginSources.Clear();
        pluginScanQueue.Clear();
        sandboxUnlockIds.Clear();
        pluginScanIndex = 0;
        sandboxScanIndex = 0;
        sandboxScanComplete = false;

        List<string> ids = ExtEnum<CreatureTemplate.Type>.values.entries;
        for (int i = 0; i < ids.Count; i++)
        {
            string id = ids[i];
            if (string.IsNullOrWhiteSpace(id)) continue;
            registeredIds.Add(id);
            registeredIdSet.Add(id);
        }

        CollectOfficialSources();
        string officialFingerprint = AssemblyFingerprint(typeof(CreatureTemplate.Type).Assembly, "rainworld");
        sourceFingerprintByKey["official:vanilla"] = officialFingerprint;
        sourceFingerprintByKey["official:moreslugcats"] = officialFingerprint;
        sourceFingerprintByKey["official:watcher"] = officialFingerprint;
        sourceFingerprintByKey["internal:advanced"] = officialFingerprint;

        sandboxUnlockIds.AddRange(ExtEnum<MultiplayerUnlocks.SandboxUnlockID>.values.entries);
        sandboxScanComplete = sandboxUnlockIds.Count == 0;

        HashSet<Assembly> seenAssemblies = new();
        foreach (var pair in Chainloader.PluginInfos)
        {
            try
            {
                var info = pair.Value;
                Assembly assembly = info?.Instance?.GetType().Assembly;
                if (assembly == null || !seenAssemblies.Add(assembly)) continue;
                string label = info.Metadata?.Name;
                if (string.IsNullOrWhiteSpace(label)) label = info.Metadata?.GUID;
                if (string.IsNullOrWhiteSpace(label)) label = assembly.GetName().Name;
                string key = "mod:" + (info.Metadata?.GUID ?? assembly.GetName().Name ?? label).Trim().ToLowerInvariant();
                string fingerprint = PluginFingerprint(assembly, info.Metadata?.Version?.ToString() ?? string.Empty);
                PluginSource source = new()
                {
                    Assembly = assembly,
                    Key = key,
                    Label = label,
                    Fingerprint = fingerprint
                };
                pluginSources.Add(source);
                sourceFingerprintByKey[key] = fingerprint;

                if (persistentSources.TryGetValue(key, out PersistentSource cached) &&
                    string.Equals(cached.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    source.NeedsScan = false;
                    source.CreatureIds.AddRange(cached.CreatureIds ?? Array.Empty<string>());
                    for (int i = 0; i < source.CreatureIds.Count; i++)
                    {
                        string creature = source.CreatureIds[i];
                        if (registeredIdSet.Contains(creature)) AssignSource(creature, key, label, overwrite: false);
                    }
                }
                else
                {
                    source.NeedsScan = true;
                    pluginScanQueue.Add(source);
                    if (cached != null) InvalidateSourceIcons(cached.CreatureIds);
                }
            }
            catch
            {
            }
        }

        currentCatalogValidated = true;
        PublishCatalogSnapshot();
        if (pluginScanQueue.Count == 0) FinalizePersistentSources();
    }

    private static int PumpSandboxSymbols(int maxItems)
    {
        if (sandboxScanComplete) return 0;
        int processed = 0;
        while (sandboxScanIndex < sandboxUnlockIds.Count && processed < maxItems)
        {
            string id = sandboxUnlockIds[sandboxScanIndex++];
            processed++;
            try
            {
                MultiplayerUnlocks.SandboxUnlockID unlock = new(id);
                IconSymbol.IconSymbolData data = MultiplayerUnlocks.SymbolDataForSandboxUnlock(unlock);
                if (data.itemType == AbstractPhysicalObject.AbstractObjectType.Creature &&
                    data.critType != null &&
                    !string.IsNullOrWhiteSpace(data.critType.value) &&
                    !sandboxSymbols.ContainsKey(data.critType.value))
                    sandboxSymbols[data.critType.value] = data;
            }
            catch
            {
            }
        }

        if (sandboxScanIndex >= sandboxUnlockIds.Count) sandboxScanComplete = true;
        return processed;
    }

    private static void ScanNextPluginSource()
    {
        if (pluginScanIndex >= pluginScanQueue.Count) return;
        PluginSource source = pluginScanQueue[pluginScanIndex++];
        source.CreatureIds.Clear();
        if (source?.Assembly != null)
            CollectAssemblyCreatureIds(source.Assembly, source.Key, source.Label, source.CreatureIds);
        PublishCatalogSnapshot();
        if (pluginScanIndex >= pluginScanQueue.Count) FinalizePersistentSources();
    }

    private static void PublishCatalogSnapshot()
    {
        Dictionary<string, List<CreatureEntry>> grouped = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> groupLabels = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, CreatureEntry> byId = new(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < registeredIds.Count; i++)
        {
            string id = registeredIds[i];
            CreatureTemplate.Type type = new(id);
            ResolveSource(id, out string sourceKey, out string sourceLabel);
            if (IsInternalCreatureId(id))
            {
                sourceKey = "internal:advanced";
                sourceLabel = DevToolUiSettings.T("内部 / 高级", "INTERNAL / ADVANCED");
            }

            string fingerprint = sourceFingerprintByKey.TryGetValue(sourceKey, out string knownFingerprint)
                ? knownFingerprint
                : "unknown:" + observedCreatureCount;
            CreatureEntry entry = new()
            {
                Id = id,
                Type = type,
                SourceKey = sourceKey,
                SourceLabel = sourceLabel,
                SourceFingerprint = fingerprint
            };
            byId[id] = entry;
            if (!grouped.TryGetValue(sourceKey, out List<CreatureEntry> list))
            {
                list = new List<CreatureEntry>();
                grouped[sourceKey] = list;
                groupLabels[sourceKey] = sourceLabel;
            }
            list.Add(entry);
        }

        List<SourceGroup> result = new();
        foreach (KeyValuePair<string, List<CreatureEntry>> pair in grouped)
        {
            pair.Value.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
            result.Add(new SourceGroup
            {
                Key = pair.Key,
                Label = groupLabels.TryGetValue(pair.Key, out string label) ? label : pair.Key,
                Entries = pair.Value.ToArray()
            });
        }
        result.Sort(CompareGroups);
        catalog = new CatalogSnapshot { Groups = result.ToArray(), ById = byId };
    }

    private static void PublishCachedCatalogSnapshot()
    {
        Dictionary<string, CreatureEntry> byId = new(StringComparer.OrdinalIgnoreCase);
        List<SourceGroup> groups = new();
        foreach (PersistentSource source in persistentSources.Values)
        {
            List<CreatureEntry> list = new();
            string[] ids = source.CreatureIds ?? Array.Empty<string>();
            for (int i = 0; i < ids.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(ids[i]) || byId.ContainsKey(ids[i])) continue;
                CreatureEntry entry = new()
                {
                    Id = ids[i],
                    Type = null,
                    SourceKey = source.Key,
                    SourceLabel = source.Label,
                    SourceFingerprint = source.Fingerprint
                };
                list.Add(entry);
                byId[entry.Id] = entry;
            }
            if (list.Count == 0) continue;
            list.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
            groups.Add(new SourceGroup { Key = source.Key, Label = source.Label, Entries = list.ToArray() });
        }
        groups.Sort(CompareGroups);
        catalog = new CatalogSnapshot { Groups = groups.ToArray(), ById = byId };
    }

    private static void FinalizePersistentSources()
    {
        Dictionary<string, List<string>> grouped = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> labels = new(StringComparer.OrdinalIgnoreCase);
        CatalogSnapshot snapshot = catalog;
        for (int i = 0; i < snapshot.Groups.Length; i++)
        {
            SourceGroup group = snapshot.Groups[i];
            List<string> ids = new();
            for (int j = 0; j < group.Entries.Length; j++) ids.Add(group.Entries[j].Id);
            grouped[group.Key] = ids;
            labels[group.Key] = group.Label;
        }

        persistentSources.Clear();
        foreach (KeyValuePair<string, List<string>> pair in grouped)
        {
            string fingerprint = sourceFingerprintByKey.TryGetValue(pair.Key, out string value)
                ? value
                : "unknown:" + observedCreatureCount;
            persistentSources[pair.Key] = new PersistentSource
            {
                Key = pair.Key,
                Label = labels.TryGetValue(pair.Key, out string label) ? label : pair.Key,
                Fingerprint = fingerprint,
                CreatureIds = pair.Value.ToArray()
            };
        }
        MarkCacheDirty();
    }

    private static void InvalidateSourceIcons(IEnumerable<string> ids)
    {
        if (ids == null) return;
        lock (iconSync)
        {
            foreach (string id in ids)
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                iconSlots.Remove(id);
                queuedIcons.Remove(id);
            }
        }
        MarkCacheDirty();
    }

    private static void CollectOfficialSources()
    {
        CollectStaticCreatureIds(typeof(CreatureTemplate.Type), "official:vanilla", "Vanilla", overwrite: true, null);
        CollectStaticCreatureIds(FindLoadedType("MoreSlugcats.MoreSlugcatsEnums+CreatureTemplateType"), "official:moreslugcats", "Downpour", overwrite: true, null);
        CollectStaticCreatureIds(FindLoadedType("DLCSharedEnums+CreatureTemplateType"), "official:moreslugcats", "Downpour", overwrite: true, null);
        CollectStaticCreatureIds(FindLoadedType("Watcher.WatcherEnums+CreatureTemplateType"), "official:watcher", "Watcher", overwrite: true, null);
    }

    private static void CollectAssemblyCreatureIds(
        Assembly assembly,
        string sourceKey,
        string sourceLabel,
        List<string> collected)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException error)
        {
            types = error.Types ?? Array.Empty<Type>();
        }
        catch
        {
            return;
        }

        for (int i = 0; i < types.Length; i++)
        {
            Type type = types[i];
            if (type == null) continue;
            CollectStaticCreatureIds(type, sourceKey, sourceLabel, overwrite: false, collected);
        }
    }

    /// <summary>
    /// Static properties are intentionally not invoked: a getter is arbitrary third-party code and
    /// opening an editor must not execute it simply to classify a catalog item.
    /// </summary>
    private static void CollectStaticCreatureIds(
        Type owner,
        string sourceKey,
        string sourceLabel,
        bool overwrite,
        List<string> collected)
    {
        if (owner == null) return;
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
        try
        {
            FieldInfo[] fields = owner.GetFields(flags);
            for (int i = 0; i < fields.Length; i++)
            {
                if (!typeof(CreatureTemplate.Type).IsAssignableFrom(fields[i].FieldType)) continue;
                if (fields[i].GetValue(null) is not CreatureTemplate.Type creature || string.IsNullOrWhiteSpace(creature.value)) continue;
                if (AssignSource(creature.value, sourceKey, sourceLabel, overwrite) && collected != null)
                    AddUnique(collected, creature.value);
                else if (collected != null && sourceByCreature.TryGetValue(creature.value, out string assigned) &&
                         string.Equals(assigned, sourceKey, StringComparison.OrdinalIgnoreCase))
                    AddUnique(collected, creature.value);
            }
        }
        catch
        {
        }
    }

    private static bool AssignSource(string creatureId, string key, string label, bool overwrite)
    {
        if (string.IsNullOrWhiteSpace(creatureId)) return false;
        if (!overwrite && sourceByCreature.ContainsKey(creatureId)) return false;
        bool changed = !sourceByCreature.TryGetValue(creatureId, out string oldKey) ||
                       !string.Equals(oldKey, key, StringComparison.OrdinalIgnoreCase);
        sourceByCreature[creatureId] = key;
        sourceLabelByKey[key] = label;
        return changed;
    }

    private static void ResolveSource(string creatureId, out string key, out string label)
    {
        if (sourceByCreature.TryGetValue(creatureId, out key))
        {
            label = sourceLabelByKey.TryGetValue(key, out string stored) ? stored : key;
            return;
        }
        key = "mod:unknown";
        label = DevToolUiSettings.T("其他 Mod", "OTHER MODS");
    }

    private static bool IsInternalCreatureId(string id) =>
        string.Equals(id, "StandardGroundCreature", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(id, "StandardFly", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(id, "StandardPather", StringComparison.OrdinalIgnoreCase);

    private static int CompareGroups(SourceGroup a, SourceGroup b)
    {
        int ra = GroupRank(a.Key);
        int rb = GroupRank(b.Key);
        if (ra != rb) return ra.CompareTo(rb);
        return string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
    }

    private static int GroupRank(string key)
    {
        if (string.Equals(key, "official:vanilla", StringComparison.OrdinalIgnoreCase)) return 0;
        if (string.Equals(key, "official:moreslugcats", StringComparison.OrdinalIgnoreCase)) return 1;
        if (string.Equals(key, "official:watcher", StringComparison.OrdinalIgnoreCase)) return 2;
        if (string.Equals(key, "mod:unknown", StringComparison.OrdinalIgnoreCase)) return 90;
        if (string.Equals(key, "internal:advanced", StringComparison.OrdinalIgnoreCase)) return 100;
        return 10;
    }

    private static Type FindLoadedType(string fullName)
    {
        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
        for (int i = 0; i < assemblies.Length; i++)
        {
            try
            {
                Type type = assemblies[i].GetType(fullName, throwOnError: false, ignoreCase: false);
                if (type != null) return type;
            }
            catch
            {
            }
        }
        return null;
    }

    private static string PluginFingerprint(Assembly assembly, string version)
    {
        string baseFingerprint = AssemblyFingerprint(assembly, version);
        try
        {
            string location = assembly?.Location;
            if (!string.IsNullOrEmpty(location) && File.Exists(location))
            {
                FileInfo info = new(location);
                return baseFingerprint + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
            }
        }
        catch
        {
        }
        return baseFingerprint;
    }

    private static string AssemblyFingerprint(Assembly assembly, string suffix)
    {
        try
        {
            return (assembly?.GetName().Name ?? string.Empty) + "|" +
                   assembly?.ManifestModule?.ModuleVersionId.ToString("N") + "|" + suffix;
        }
        catch
        {
            return (assembly?.FullName ?? string.Empty) + "|" + suffix;
        }
    }

    private static void AddUnique(List<string> list, string value)
    {
        if (list == null || string.IsNullOrWhiteSpace(value)) return;
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase)) return;
        list.Add(value);
    }

    private static int CountMatching(SourceGroup group, string search)
    {
        int count = 0;
        for (int i = 0; i < group.Entries.Length; i++)
            if (Matches(group.Entries[i], group, search)) count++;
        return count;
    }

    private static bool Matches(CreatureEntry entry, SourceGroup group, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        string needle = query.Trim();
        return Contains(entry.Id, needle) || Contains(group.Label, needle) || Fuzzy(entry.Id, needle);
    }

    private static bool Contains(string value, string query) =>
        !string.IsNullOrEmpty(value) && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool Fuzzy(string value, string query)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(query)) return false;
        int q = 0;
        for (int i = 0; i < value.Length && q < query.Length; i++)
            if (char.ToUpperInvariant(value[i]) == char.ToUpperInvariant(query[q])) q++;
        return q == query.Length;
    }

    private static string ResolveCachePath()
    {
        try
        {
            return Path.Combine(Paths.CachePath, "DryCycle", "DevTool", "creature-catalog-v" + CacheVersion + ".bin");
        }
        catch
        {
            return Path.Combine(Application.persistentDataPath, "DryCycle", "creature-catalog-v" + CacheVersion + ".bin");
        }
    }

    private static void LoadPersistentCache()
    {
        if (string.IsNullOrEmpty(cacheFilePath) || !File.Exists(cacheFilePath)) return;
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            using FileStream stream = new(cacheFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using BinaryReader reader = new(stream);
            if (reader.ReadInt32() != CacheMagic || reader.ReadInt32() != CacheVersion) return;

            int sourceCount = ReadSafeCount(reader, 4096);
            for (int i = 0; i < sourceCount; i++)
            {
                PersistentSource source = new()
                {
                    Key = reader.ReadString(),
                    Label = reader.ReadString(),
                    Fingerprint = reader.ReadString()
                };
                int idCount = ReadSafeCount(reader, 20000);
                string[] ids = new string[idCount];
                for (int j = 0; j < idCount; j++) ids[j] = reader.ReadString();
                source.CreatureIds = ids;
                if (!string.IsNullOrWhiteSpace(source.Key)) persistentSources[source.Key] = source;
            }

            int iconCount = ReadSafeCount(reader, 50000);
            lock (iconSync)
            {
                for (int i = 0; i < iconCount; i++)
                {
                    string id = reader.ReadString();
                    string sourceFingerprint = reader.ReadString();
                    IconState state = (IconState)reader.ReadByte();
                    IconRaster raster = ReadRaster(reader);
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    iconSlots[id] = new IconSlot
                    {
                        State = state,
                        Raster = raster,
                        SourceFingerprint = sourceFingerprint,
                        Validated = false,
                        ValidationQueued = false
                    };
                }
            }

            PublishCachedCatalogSnapshot();
            log?.LogInfo("Creature catalog persistent cache loaded in " + stopwatch.Elapsed.TotalMilliseconds.ToString("0.0") + " ms (" +
                         persistentSources.Count + " source groups, " + iconSlots.Count + " icons).");
        }
        catch (Exception error)
        {
            persistentSources.Clear();
            lock (iconSync) iconSlots.Clear();
            catalog = CatalogSnapshot.Empty;
            log?.LogWarning("Creature catalog cache ignored because it is invalid: " + error.Message);
        }
    }

    private static int ReadSafeCount(BinaryReader reader, int max)
    {
        int value = reader.ReadInt32();
        if (value < 0 || value > max) throw new InvalidDataException("Invalid cache item count: " + value);
        return value;
    }

    private static IconRaster ReadRaster(BinaryReader reader)
    {
        IconRaster raster = new()
        {
            SpriteName = reader.ReadString(),
            AtlasSignature = reader.ReadString(),
            Width = reader.ReadInt32(),
            Height = reader.ReadInt32(),
            Tint = new Color(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
            Available = reader.ReadBoolean()
        };
        int runCount = ReadSafeCount(reader, 200000);
        PixelRun[] runs = new PixelRun[runCount];
        for (int i = 0; i < runCount; i++)
        {
            int x = reader.ReadInt32();
            int y = reader.ReadInt32();
            int width = reader.ReadInt32();
            Color32 color = new(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
            runs[i] = new PixelRun(x, y, width, color);
        }
        raster.Runs = runs;
        return raster;
    }

    private static void MarkCacheDirty()
    {
        cacheDirty = true;
        cacheDirtyAt = Time.realtimeSinceStartup;
    }

    private static void TrySchedulePersistentSave()
    {
        if (!cacheDirty || cacheSaveInFlight || string.IsNullOrEmpty(cacheFilePath)) return;
        if (Time.realtimeSinceStartup - cacheDirtyAt < SaveDebounceSeconds) return;

        PersistentSnapshot snapshot = CapturePersistentSnapshot();
        cacheDirty = false;
        cacheSaveInFlight = true;
        Task.Run(() =>
        {
            try
            {
                WritePersistentCache(snapshot);
            }
            catch (Exception error)
            {
                backgroundSaveError = error.Message;
            }
            finally
            {
                cacheSaveInFlight = false;
            }
        });
    }

    private static PersistentSnapshot CapturePersistentSnapshot()
    {
        PersistentSnapshot snapshot = new();
        foreach (PersistentSource source in persistentSources.Values)
        {
            snapshot.Sources.Add(new PersistentSource
            {
                Key = source.Key,
                Label = source.Label,
                Fingerprint = source.Fingerprint,
                CreatureIds = (string[])(source.CreatureIds ?? Array.Empty<string>()).Clone()
            });
        }

        lock (iconSync)
        {
            foreach (KeyValuePair<string, IconSlot> pair in iconSlots)
            {
                IconSlot slot = pair.Value;
                if (slot == null || slot.State == IconState.Pending) continue;
                snapshot.Icons.Add(new PersistentIcon
                {
                    CreatureId = pair.Key,
                    SourceFingerprint = slot.SourceFingerprint,
                    State = slot.State,
                    Raster = CloneRaster(slot.Raster)
                });
            }
        }
        return snapshot;
    }

    private static IconRaster CloneRaster(IconRaster source)
    {
        if (source == null) return new IconRaster();
        return new IconRaster
        {
            SpriteName = source.SpriteName,
            AtlasSignature = source.AtlasSignature,
            Width = source.Width,
            Height = source.Height,
            Tint = source.Tint,
            Runs = (PixelRun[])(source.Runs ?? Array.Empty<PixelRun>()).Clone(),
            Available = source.Available
        };
    }

    private static void WritePersistentCache(PersistentSnapshot snapshot)
    {
        string directory = Path.GetDirectoryName(cacheFilePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temp = cacheFilePath + ".tmp";

        using (FileStream stream = new(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (BinaryWriter writer = new(stream))
        {
            writer.Write(CacheMagic);
            writer.Write(CacheVersion);
            writer.Write(snapshot.Sources.Count);
            for (int i = 0; i < snapshot.Sources.Count; i++)
            {
                PersistentSource source = snapshot.Sources[i];
                writer.Write(source.Key ?? string.Empty);
                writer.Write(source.Label ?? string.Empty);
                writer.Write(source.Fingerprint ?? string.Empty);
                string[] ids = source.CreatureIds ?? Array.Empty<string>();
                writer.Write(ids.Length);
                for (int j = 0; j < ids.Length; j++) writer.Write(ids[j] ?? string.Empty);
            }

            writer.Write(snapshot.Icons.Count);
            for (int i = 0; i < snapshot.Icons.Count; i++)
            {
                PersistentIcon icon = snapshot.Icons[i];
                writer.Write(icon.CreatureId ?? string.Empty);
                writer.Write(icon.SourceFingerprint ?? string.Empty);
                writer.Write((byte)icon.State);
                WriteRaster(writer, icon.Raster);
            }
            writer.Flush();
        }

        try
        {
            if (File.Exists(cacheFilePath)) File.Replace(temp, cacheFilePath, null);
            else File.Move(temp, cacheFilePath);
        }
        catch
        {
            File.Copy(temp, cacheFilePath, true);
            File.Delete(temp);
        }
    }

    private static void WriteRaster(BinaryWriter writer, IconRaster raster)
    {
        raster ??= new IconRaster();
        writer.Write(raster.SpriteName ?? string.Empty);
        writer.Write(raster.AtlasSignature ?? string.Empty);
        writer.Write(raster.Width);
        writer.Write(raster.Height);
        writer.Write(raster.Tint.r);
        writer.Write(raster.Tint.g);
        writer.Write(raster.Tint.b);
        writer.Write(raster.Tint.a);
        writer.Write(raster.Available);
        PixelRun[] runs = raster.Runs ?? Array.Empty<PixelRun>();
        writer.Write(runs.Length);
        for (int i = 0; i < runs.Length; i++)
        {
            PixelRun run = runs[i];
            writer.Write(run.X);
            writer.Write(run.Y);
            writer.Write(run.Width);
            writer.Write(run.Color.r);
            writer.Write(run.Color.g);
            writer.Write(run.Color.b);
            writer.Write(run.Color.a);
        }
    }
}
