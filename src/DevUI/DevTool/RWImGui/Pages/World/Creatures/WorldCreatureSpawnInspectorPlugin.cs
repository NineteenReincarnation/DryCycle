using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Map;
using DryCycle.DevUI.DevTool.World;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Ordinary world creature-spawner authoring. WorldWorkspaceView calls this panel directly, so the
/// creature authoring path owns no RuntimeDetour hooks and cannot affect unrelated DevTool widgets.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(BridgePlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldCreatureSpawnInspectorPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMap.CreatureSpawns";
    public const string PluginName = "DryCycle DevTool World Creature Spawns";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() =>
        global::DryCycle.AuxiliaryPluginStartupGuard.Enable(
            PluginName + ".OnEnable",
            () =>
            {
                WorldCreatureSpawnInspector.Enable(Logger);
                WorldTimelineSlugcatIconCatalog.Initialize(Logger);
            },
            () =>
            {
                WorldTimelineSlugcatIconCatalog.Shutdown();
                WorldCreatureSpawnInspector.Disable();
            });

    private void Update() =>
        WorldTimelineSlugcatIconCatalog.PumpMainThread();

    private void OnDisable() =>
        global::DryCycle.AuxiliaryPluginStartupGuard.Disable(
            PluginName + ".OnDisable",
            () =>
            {
                WorldTimelineSlugcatIconCatalog.Shutdown();
                WorldCreatureSpawnInspector.Disable();
            });
}

internal static class WorldCreatureSpawnInspector
{
    private enum TimelineMode
    {
        All,
        Only,
        Exclude
    }

    private static readonly string[] ToggleSpawnTags =
    {
        "Night", "PreCycle", "Winter", "Ignorecycle", "AlternateForm", "Lavasafe",
        "TentacleImmune", "Voidsea", "Ripple", "Slayer"
    };

    private const string SeedTagPrefix = "Seed:";
    private const string RotTypeTagPrefix = "RotType:";
    private const string NamedAttrTagPrefix = "NamedAttr:";

    private static ManualLogSource log;
    private static bool enabled;

    private static int stateRoom = -1;
    private static int editingSpawnId = -1;
    private static int selectedDen = -1;
    private static string creatureId = string.Empty;
    private static int amount = 1;
    private static string spawnTags = string.Empty;
    private static TimelineMode timelineMode = TimelineMode.All;
    private static string timelineFilter = string.Empty;
    private static string lastStatus = string.Empty;

    private static readonly List<string> timelineCatalog = new();
    private static int timelineCatalogFingerprint = -1;
    private static WorldCreatureSpawnRecord[] presentedSpawnSource = Array.Empty<WorldCreatureSpawnRecord>();
    private static WorldCreatureSpawnRecord[] presentedSpawns = Array.Empty<WorldCreatureSpawnRecord>();
    private static string[] presentedCreatureText = Array.Empty<string>();
    private static string[] presentedSecondaryText = Array.Empty<string>();

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        log = logger;
        log?.LogInfo("World creature-spawn inspector enabled with direct WorldWorkspace integration.");
    }

    internal static void Disable()
    {
        enabled = false;
        stateRoom = -1;
        editingSpawnId = -1;
        timelineCatalog.Clear();
        timelineCatalogFingerprint = -1;
        presentedSpawnSource = Array.Empty<WorldCreatureSpawnRecord>();
        presentedSpawns = Array.Empty<WorldCreatureSpawnRecord>();
        presentedCreatureText = Array.Empty<string>();
        presentedSecondaryText = Array.Empty<string>();
        log = null;
    }

    internal static void DrawIntegrated(EditorMapPresentationSnapshot snapshot, EditorMapRoomSnapshot room)
    {
        // This panel is composed directly by WorldWorkspaceView. Its heading is intentionally drawn
        // before touching any runtime catalogs: one malformed Mod registration or cache must never
        // make the whole authoring section silently disappear from the inspector.
        if (snapshot?.Available != true || room == null) return;

        DevToolWidgets.SectionHeader(DevToolUiSettings.T("放置生物", "CREATURE SPAWNS"));
        try
        {
            if (stateRoom != room.RoomIndex)
                ResetForRoom(room);

            RefreshTimelineCatalog();

            WorldCreatureSpawnRecord[] existing = WorldTextRegistry.GetCreatureSpawns(snapshot.RegionName, room.Name);
            DrawExistingSpawns(snapshot, room, existing);
            ImGui.Spacing();
            DrawEditor(snapshot, room);
        }
        catch (Exception error)
        {
            ImGui.TextColored(
                new Num.Vector4(0.92f, 0.42f, 0.42f, 1f),
                DevToolUiSettings.T("生物编辑器初始化失败：", "Creature editor failed: ") + error.Message);
            log?.LogError("World creature-spawn inspector failed for '" + room.Name + "': " + error);
        }

        // Lineage is useful but must not be able to take ordinary spawner authoring (or the rest of
        // the room inspector) down with it. Keep its failure boundary independent.
        try
        {
            WorldLineageInspector.DrawIntegrated(snapshot, room);
        }
        catch (Exception error)
        {
            DevToolWidgets.SectionHeader(DevToolUiSettings.T("族谱 / Lineage", "LINEAGE"));
            ImGui.TextColored(
                new Num.Vector4(0.92f, 0.42f, 0.42f, 1f),
                DevToolUiSettings.T("Lineage 编辑器初始化失败：", "Lineage editor failed: ") + error.Message);
            log?.LogError("World lineage inspector failed for '" + room.Name + "': " + error);
        }
    }

    private static void DrawExistingSpawns(
        EditorMapPresentationSnapshot snapshot,
        EditorMapRoomSnapshot room,
        WorldCreatureSpawnRecord[] existing)
    {
        if (existing == null || existing.Length == 0)
        {
            DevToolWidgets.MutedText(
                DevToolUiSettings.T("这个房间还没有普通生物生成器。", "No ordinary creature spawners in this room."),
                true);
            return;
        }

        EnsurePresentation(existing);
        WorldCreatureSpawnRecord[] displaySpawns = presentedSpawns;
        DevToolWidgets.MutedText(
            DevToolUiSettings.T("已放置 ", "Placed ") + existing.Length +
            DevToolUiSettings.T(" 个生成项", " spawn entrie(s)"));

        string deleteLabel =
            DevToolUiSettings.T("删除", "Delete");
        float deleteWidth =
            ImGui.CalcTextSize(deleteLabel).X +
            ImGui.GetStyle().FramePadding.X * 2f +
            8f;
        float spacing =
            Math.Max(
                6f,
                ImGui.GetStyle().ItemSpacing.X);

        for (int i = 0; i < displaySpawns.Length; i++)
        {
            WorldCreatureSpawnRecord spawn = displaySpawns[i];
            ImGui.PushID(spawn.Id);

            float available =
                Math.Max(
                    1f,
                    ImGui.GetContentRegionAvail().X);
            float rowWidth =
                Math.Max(
                    92f,
                    available - deleteWidth - spacing);
            bool hasSecondary =
                !string.IsNullOrEmpty(
                    presentedSecondaryText[i]);
            float lineHeight =
                ImGui.GetTextLineHeight();
            float iconSize =
                Math.Max(
                    20f,
                    Math.Min(
                        30f,
                        lineHeight * 0.92f));
            float rowHeight =
                Math.Max(
                    iconSize + 8f,
                    hasSecondary
                        ? lineHeight * 2f + 10f
                        : lineHeight + 10f);

            bool editing =
                editingSpawnId == spawn.Id;
            if (ImGui.Selectable(
                    "##CreatureSpawnRow",
                    editing,
                    ImGuiSelectableFlags.None,
                    new Num.Vector2(
                        rowWidth,
                        rowHeight)))
                LoadSpawn(spawn);

            Num.Vector2 rowMin =
                ImGui.GetItemRectMin();
            Num.Vector2 rowMax =
                ImGui.GetItemRectMax();
            DrawSpawnRow(
                spawn,
                presentedCreatureText[i],
                presentedSecondaryText[i],
                rowMin,
                rowMax,
                iconSize);

            ImGui.SameLine(0f, spacing);
            if (DevToolWidgets.ActionButton(
                    deleteLabel,
                    "DeleteCreatureSpawn",
                    DevToolButtonTone.Danger))
            {
                if (WorldTextRegistry.TryDeleteCreatureSpawn(
                        snapshot.RegionName,
                        spawn.Id,
                        out string error))
                {
                    if (editingSpawnId == spawn.Id)
                        ResetForm(room);
                    SetStatus(
                        DevToolUiSettings.T(
                            "已删除生成项并刷新实时预览。",
                            "Spawner deleted and live preview refreshed."),
                        true);
                }
                else
                {
                    SetStatus(error, false);
                }
            }

            ImGui.PopID();
        }
    }

    private static void EnsurePresentation(
        WorldCreatureSpawnRecord[] existing)
    {
        if (ReferenceEquals(existing, presentedSpawnSource) &&
            presentedCreatureText.Length == existing.Length)
            return;

        presentedSpawnSource = existing;
        presentedSpawns = (WorldCreatureSpawnRecord[])existing.Clone();
        Array.Sort(
            presentedSpawns,
            static (a, b) =>
            {
                if (ReferenceEquals(a, b)) return 0;
                if (a == null) return 1;
                if (b == null) return -1;

                int denOrder = a.DenNode.CompareTo(b.DenNode);
                if (denOrder != 0) return denOrder;

                // Keep multiple spawns in the same pipe deterministic without changing registry data.
                return a.Id.CompareTo(b.Id);
            });

        presentedCreatureText =
            new string[presentedSpawns.Length];
        presentedSecondaryText =
            new string[presentedSpawns.Length];

        for (int i = 0; i < presentedSpawns.Length; i++)
        {
            WorldCreatureSpawnRecord spawn = presentedSpawns[i];
            string creature =
                string.IsNullOrWhiteSpace(spawn.Creature)
                    ? "?"
                    : spawn.Creature;
            if (spawn.Amount > 1)
                creature += " x" + spawn.Amount;
            presentedCreatureText[i] = creature;

            string scope = SpawnScope(spawn);
            string data =
                string.IsNullOrEmpty(spawn.SpawnData)
                    ? string.Empty
                    : "{" + spawn.SpawnData + "}";
            presentedSecondaryText[i] =
                scope.Length > 0 && data.Length > 0
                    ? scope + "  |  " + data
                    : scope.Length > 0
                        ? scope
                        : data;
        }
    }

    private static void DrawSpawnRow(
        WorldCreatureSpawnRecord spawn,
        string creatureText,
        string secondaryText,
        Num.Vector2 rowMin,
        Num.Vector2 rowMax,
        float iconSize)
    {
        ImDrawListPtr draw =
            ImGui.GetWindowDrawList();
        ImGuiStylePtr style =
            ImGui.GetStyle();
        float padX =
            Math.Max(
                5f,
                style.FramePadding.X);
        float centerY =
            (rowMin.Y + rowMax.Y) * 0.5f;
        float primaryHeight =
            ImGui.GetTextLineHeight();

        Num.Vector2 pipePos =
            new(
                rowMin.X + padX,
                centerY - iconSize * 0.5f);
        DrawCreaturePipeIcon(
            draw,
            pipePos,
            iconSize);

        string denText =
            "#" + spawn.DenNode;
        float denX =
            pipePos.X +
            iconSize +
            5f;
        Num.Vector2 denSize =
            ImGui.CalcTextSize(denText);
        float primaryY =
            secondaryText.Length > 0
                ? rowMin.Y + 4f
                : centerY - primaryHeight * 0.5f;
        draw.AddText(
            new Num.Vector2(
                denX,
                primaryY),
            ImGui.GetColorU32(ImGuiCol.Text),
            denText);

        float creatureIconX =
            denX +
            denSize.X +
            10f;
        Num.Vector2 creatureIconPos =
            new(
                creatureIconX,
                centerY - iconSize * 0.5f);
        WorldCreatureCatalogPicker.DrawInlineIcon(
            draw,
            spawn.Creature,
            creatureIconPos,
            new Num.Vector2(
                iconSize,
                iconSize));

        float creatureTextX =
            creatureIconX +
            iconSize +
            5f;
        float maxTextRight =
            rowMax.X - padX;
        string shown =
            FitSpawnRowText(
                creatureText ?? string.Empty,
                Math.Max(
                    12f,
                    maxTextRight - creatureTextX));
        draw.AddText(
            new Num.Vector2(
                creatureTextX,
                primaryY),
            ImGui.GetColorU32(ImGuiCol.Text),
            shown);

        if (!string.IsNullOrEmpty(secondaryText))
        {
            float secondaryY =
                primaryY +
                primaryHeight +
                2f;
            string secondaryShown =
                FitSpawnRowText(
                    secondaryText,
                    Math.Max(
                        12f,
                        maxTextRight - denX));
            draw.AddText(
                new Num.Vector2(
                    denX,
                    secondaryY),
                ImGui.GetColorU32(
                    ImGuiCol.TextDisabled),
                secondaryShown);
        }
    }

    private static void DrawCreaturePipeIcon(
        ImDrawListPtr draw,
        Num.Vector2 pos,
        float size)
    {
        // Compact visual language matching the map's creature-pipe meaning: bright green casing
        // around a dark opening. It is procedural so it never depends on another texture/atlas.
        uint green =
            ImGui.GetColorU32(
                new Num.Vector4(
                    0.20f,
                    0.88f,
                    0.33f,
                    1f));
        uint dark =
            ImGui.GetColorU32(
                new Num.Vector4(
                    0.035f,
                    0.07f,
                    0.045f,
                    0.96f));

        float radius =
            Math.Max(
                3f,
                size * 0.18f);
        Num.Vector2 max =
            pos + new Num.Vector2(size, size);
        draw.AddRectFilled(
            pos,
            max,
            green,
            radius);
        float inset =
            Math.Max(
                3f,
                size * 0.20f);
        draw.AddRectFilled(
            pos + new Num.Vector2(inset, inset),
            max - new Num.Vector2(inset, inset),
            dark,
            Math.Max(2f, radius * 0.62f));

        // Small mouth marks make the symbol read as a pipe rather than a generic green square.
        float mark =
            Math.Max(
                1.5f,
                size * 0.075f);
        float cy =
            pos.Y + size * 0.5f;
        draw.AddCircleFilled(
            new Num.Vector2(
                pos.X + inset * 0.52f,
                cy),
            mark,
            dark);
        draw.AddCircleFilled(
            new Num.Vector2(
                max.X - inset * 0.52f,
                cy),
            mark,
            dark);
    }

    private static string FitSpawnRowText(
        string value,
        float maxWidth)
    {
        value ??= string.Empty;
        if (maxWidth <= 0f ||
            ImGui.CalcTextSize(value).X <= maxWidth)
            return value;

        const string ellipsis = "...";
        int length = value.Length;
        while (length > 1)
        {
            string candidate =
                value.Substring(0, --length) +
                ellipsis;
            if (ImGui.CalcTextSize(candidate).X <= maxWidth)
                return candidate;
        }

        return ellipsis;
    }

    private static void DrawEditor(EditorMapPresentationSnapshot snapshot, EditorMapRoomSnapshot room)
    {
        List<WorldCreaturePipeCatalog.Entry> dens = WorldCreaturePipeCatalog.Get(room, selectedDen);
        if (dens.Count == 0)
        {
            ImGui.TextColored(
                new Num.Vector4(0.92f, 0.62f, 0.30f, 1f),
                DevToolUiSettings.T("这个房间没有可用的生物管道。", "This room has no available creature pipes."));
            return;
        }
        if (!WorldCreaturePipeCatalog.Contains(dens, selectedDen)) selectedDen = dens[0].NodeIndex;

        DevToolWidgets.MutedText(editingSpawnId >= 0
            ? DevToolUiSettings.T("编辑生成项", "Edit spawn")
            : DevToolUiSettings.T("新建生成项", "New spawn"));

        string denLabel =
            WorldInspectorReadability.PrepareField(
                DevToolUiSettings.T("生物管道", "Creature pipe"),
                "CreatureSpawnDen");
        if (ImGui.BeginCombo(
                denLabel,
                WorldCreaturePipeCatalog.Label(dens, selectedDen)))
        {
            for (int i = 0; i < dens.Count; i++)
            {
                WorldCreaturePipeCatalog.Entry pipe = dens[i];
                bool selected = selectedDen == pipe.NodeIndex;
                if (ImGui.Selectable(
                        WorldCreaturePipeCatalog.Label(pipe) + "##CreatureDen" + pipe.NodeIndex,
                        selected))
                    selectedDen = pipe.NodeIndex;
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        WorldCreatureCatalogPicker.DrawSelector(
            "OrdinarySpawn",
            ref creatureId,
            allowNone: false,
            label: DevToolUiSettings.T("生物", "Creature"));

        string amountLabel =
            WorldInspectorReadability.PrepareField(
                DevToolUiSettings.T("数量", "Amount"),
                "CreatureSpawnAmount");
        ImGui.InputInt(amountLabel, ref amount);
        if (amount < 1) amount = 1;

        DrawSpawnTags();
        DrawTimelineEditor();

        ImGui.Spacing();
        string action = editingSpawnId >= 0
            ? DevToolUiSettings.T("应用修改", "Apply Changes")
            : DevToolUiSettings.T("放置生物", "Add Creature");
        if (DevToolWidgets.ActionButton(action, "ApplyCreatureSpawn", DevToolButtonTone.Primary, true))
            Apply(snapshot, room);

        if (editingSpawnId >= 0 && DevToolWidgets.ActionButton(
                DevToolUiSettings.T("取消编辑", "Cancel Edit"),
                "CancelCreatureSpawnEdit",
                DevToolButtonTone.Subtle,
                true))
            ResetForm(room);

        if (!string.IsNullOrEmpty(lastStatus))
        {
            ImGui.Spacing();
            ImGui.TextColored(new Num.Vector4(0.92f, 0.42f, 0.42f, 1f), lastStatus);
        }
    }

    private static void DrawSpawnTags()
    {
        string spawnTagsLabel =
            WorldInspectorReadability.PrepareField(
                DevToolUiSettings.T("Spawn 标签", "Spawn tags"),
                "CreatureSpawnTags");

        if (ImGui.BeginCombo(
                spawnTagsLabel,
                SpawnTagSummary()))
        {
            for (int i = 0; i < ToggleSpawnTags.Length; i++)
            {
                string tag = ToggleSpawnTags[i];
                bool enabled = CsvContains(spawnTags, tag);
                if (ImGui.Checkbox(tag + "##SpawnTagToggle" + i, ref enabled))
                    SetCsvToken(ref spawnTags, tag, enabled);
            }

            ImGui.Separator();

            DrawIntegerSpawnTag(
                DevToolUiSettings.T("随机种子", "Seed"),
                SeedTagPrefix,
                "SpawnTagSeed");
            DrawIntegerSpawnTag(
                DevToolUiSettings.T("腐化类型", "Rot type"),
                RotTypeTagPrefix,
                "SpawnTagRotType");
            DrawNamedSpawnTag();

            string custom = UnknownSpawnTags();
            if (!string.IsNullOrEmpty(custom))
            {
                ImGui.Separator();
                DevToolWidgets.MutedText(
                    DevToolUiSettings.T("保留的 Mod 标签", "Preserved mod tags"),
                    true);
                ImGui.TextWrapped(custom);
            }

            ImGui.EndCombo();
        }

        if (ImGui.IsItemHovered())
            DevToolTooltip.Show(DevToolUiSettings.T(
                "直接勾选生成标签。未识别的 Mod 自定义标签会在编辑时原样保留。",
                "Select spawn tags directly. Unrecognized mod-defined tags are preserved while editing."));
    }

    private static void DrawTimelineEditor()
    {
        string timelineModeLabel =
            WorldInspectorReadability.PrepareField(
                DevToolUiSettings.T("时间线范围", "Timeline scope"),
                "CreatureTimelineMode");
        if (ImGui.BeginCombo(
                timelineModeLabel,
                TimelineModeText(timelineMode)))
        {
            DrawTimelineModeOption(TimelineMode.All, DevToolUiSettings.T("全部时间线", "All timelines"));
            DrawTimelineModeOption(TimelineMode.Only, DevToolUiSettings.T("仅这些标签", "Only these tags"));
            DrawTimelineModeOption(TimelineMode.Exclude, DevToolUiSettings.T("排除这些标签", "Exclude these tags"));
            ImGui.EndCombo();
        }

        if (timelineMode == TimelineMode.All)
            return;

        string timelineFilterLabel =
            WorldInspectorReadability.PrepareField(
                DevToolUiSettings.T("时间线 / 角色标签", "Timeline / character tags"),
                "CreatureTimelineFilter");

        if (ImGui.BeginCombo(
                timelineFilterLabel,
                TimelineTagSummary()))
        {
            if (timelineCatalog.Count == 0)
            {
                DevToolWidgets.MutedText(
                    DevToolUiSettings.T("没有已注册的时间线标签。", "No registered timeline tags."),
                    true);
            }
            else
            {
                for (int i = 0; i < timelineCatalog.Count; i++)
                {
                    string value = timelineCatalog[i];
                    bool selected = CsvContains(timelineFilter, value);
                    if (DrawTimelineTagOption(value, i, ref selected))
                        SetCsvToken(ref timelineFilter, value, selected);
                }
            }

            string custom = UnknownTimelineTags();
            if (!string.IsNullOrEmpty(custom))
            {
                ImGui.Separator();
                DevToolWidgets.MutedText(
                    DevToolUiSettings.T("保留的未注册标签", "Preserved unregistered tags"),
                    true);
                ImGui.TextWrapped(custom);
            }

            ImGui.EndCombo();
        }

        if (ImGui.IsItemHovered())
            DevToolTooltip.Show(DevToolUiSettings.T(
                "从当前已注册的 Timeline / Slugcat 标签中多选。未注册的 Mod 标签会保留。",
                "Select from currently registered Timeline / Slugcat tags. Unregistered mod tags are preserved."));
    }

    private static bool DrawTimelineTagOption(
        string value,
        int index,
        ref bool selected)
    {
        bool changed = false;
        float frameHeight = ImGui.GetFrameHeight();
        float iconSize =
            Math.Max(
                16f,
                Math.Min(
                    21f,
                    frameHeight - 3f));

        ImGui.PushID("TimelineTagRow" + index);

        if (ImGui.Checkbox("##Toggle", ref selected))
            changed = true;

        ImGui.SameLine(0f, 7f);
        Num.Vector2 iconSlot =
            ImGui.GetCursorScreenPos();
        Num.Vector2 iconPos =
            iconSlot +
            new Num.Vector2(
                0f,
                Math.Max(
                    0f,
                    (frameHeight - iconSize) * 0.5f));
        WorldTimelineSlugcatIconCatalog.Draw(
            ImGui.GetWindowDrawList(),
            value,
            iconPos,
            iconSize);
        ImGui.Dummy(
            new Num.Vector2(
                iconSize,
                frameHeight));

        ImGui.SameLine(0f, 7f);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(value ?? string.Empty);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            selected = !selected;
            changed = true;
        }

        ImGui.PopID();
        return changed;
    }

    private static void Apply(EditorMapPresentationSnapshot snapshot, EditorMapRoomSnapshot room)
    {
        string effectiveTimeline = timelineMode == TimelineMode.All ? string.Empty : timelineFilter;
        bool exclude = timelineMode == TimelineMode.Exclude;
        if (string.IsNullOrWhiteSpace(creatureId))
        {
            SetStatus(DevToolUiSettings.T("请从生物图鉴选择一个生物。", "Choose a creature from the catalog."), false);
            return;
        }
        if (timelineMode != TimelineMode.All && string.IsNullOrWhiteSpace(effectiveTimeline))
        {
            SetStatus(DevToolUiSettings.T("时间线范围需要至少一个标签。", "Timeline scope needs at least one tag."), false);
            return;
        }

        bool ok;
        string error;
        if (editingSpawnId >= 0)
        {
            ok = WorldTextRegistry.TryUpdateCreatureSpawn(
                snapshot.RegionName,
                editingSpawnId,
                selectedDen,
                creatureId,
                amount,
                spawnTags,
                effectiveTimeline,
                exclude,
                out error);
            if (ok) editingSpawnId = -1;
        }
        else
        {
            ok = WorldTextRegistry.TryAddCreatureSpawn(
                snapshot.RegionName,
                room.Name,
                selectedDen,
                creatureId,
                amount,
                spawnTags,
                effectiveTimeline,
                exclude,
                out _,
                out error);
        }

        if (!ok)
        {
            SetStatus(error, false);
            return;
        }

        SetStatus(
            DevToolUiSettings.T(
                "生成项已更新并刷新实时预览；保存世界以写入 world.txt。",
                "Spawner updated and live preview refreshed; save the world to write world.txt."),
            true);
    }

    private static void ResetForRoom(EditorMapRoomSnapshot room)
    {
        stateRoom = room?.RoomIndex ?? -1;
        editingSpawnId = -1;
        lastStatus = string.Empty;
        ResetForm(room);
    }

    private static void ResetForm(EditorMapRoomSnapshot room)
    {
        // Do not touch the pipe catalog while merely switching inspector selection. Pipe discovery
        // can be incrementally populated; DrawEditor is the single safe place that resolves the
        // current set and repairs selectedDen when necessary.
        editingSpawnId = -1;
        selectedDen = -1;
        creatureId = string.Empty;
        amount = 1;
        spawnTags = string.Empty;
        timelineMode = TimelineMode.All;
        timelineFilter = string.Empty;
    }

    private static void LoadSpawn(WorldCreatureSpawnRecord spawn)
    {
        if (spawn == null) return;
        editingSpawnId = spawn.Id;
        selectedDen = spawn.DenNode;
        creatureId = spawn.Creature ?? string.Empty;
        amount = Math.Max(1, spawn.Amount);
        spawnTags = spawn.SpawnData ?? string.Empty;
        timelineFilter = spawn.TimelineFilter ?? string.Empty;
        timelineMode = string.IsNullOrEmpty(timelineFilter)
            ? TimelineMode.All
            : spawn.ExcludeTimeline ? TimelineMode.Exclude : TimelineMode.Only;
        lastStatus = string.Empty;
    }

    private static string SpawnScope(WorldCreatureSpawnRecord spawn)
    {
        if (spawn == null || string.IsNullOrEmpty(spawn.TimelineFilter)) return string.Empty;
        return (spawn.ExcludeTimeline ? "X-" : string.Empty) + spawn.TimelineFilter;
    }

    private static void RefreshTimelineCatalog()
    {
        int timelineCount = ExtEnum<SlugcatStats.Timeline>.values.entries.Count;
        int slugcatCount = ExtEnum<SlugcatStats.Name>.values.entries.Count;
        int fingerprint = timelineCount * 397 ^ slugcatCount;
        if (fingerprint == timelineCatalogFingerprint && timelineCatalog.Count > 0) return;
        timelineCatalogFingerprint = fingerprint;
        timelineCatalog.Clear();

        for (int i = 0; i < ExtEnum<SlugcatStats.Timeline>.values.entries.Count; i++)
            AddUnique(timelineCatalog, ExtEnum<SlugcatStats.Timeline>.values.entries[i]);
        for (int i = 0; i < ExtEnum<SlugcatStats.Name>.values.entries.Count; i++)
            AddUnique(timelineCatalog, ExtEnum<SlugcatStats.Name>.values.entries[i]);
        timelineCatalog.Sort(StringComparer.OrdinalIgnoreCase);
    }

    private static void DrawTimelineModeOption(TimelineMode mode, string label)
    {
        bool selected = timelineMode == mode;
        if (ImGui.Selectable(label + "##TimelineMode" + mode, selected))
            timelineMode = mode;
        if (selected) ImGui.SetItemDefaultFocus();
    }
    private static string TimelineModeText(TimelineMode mode) => mode switch
    {
        TimelineMode.Only => DevToolUiSettings.T("仅指定", "Only selected"),
        TimelineMode.Exclude => DevToolUiSettings.T("排除指定", "Exclude selected"),
        _ => DevToolUiSettings.T("全部", "All")
    };

    private static string SpawnTagSummary()
    {
        int count = CsvCount(spawnTags);
        return count == 0
            ? DevToolUiSettings.T("无", "None")
            : DevToolUiSettings.T("已选择 ", "Selected ") + count;
    }

    private static string TimelineTagSummary()
    {
        int count = CsvCount(timelineFilter);
        return count == 0
            ? DevToolUiSettings.T("未选择", "None selected")
            : DevToolUiSettings.T("已选择 ", "Selected ") + count;
    }

    private static void DrawIntegerSpawnTag(string label, string prefix, string id)
    {
        bool enabled = TryGetPrefixedTag(spawnTags, prefix, out string rawValue);
        bool nextEnabled = enabled;
        if (ImGui.Checkbox(label + "##" + id + "Enabled", ref nextEnabled))
        {
            if (nextEnabled)
                SetPrefixedTag(ref spawnTags, prefix, enabled ? rawValue : "0");
            else
                RemovePrefixedTag(ref spawnTags, prefix);
            enabled = nextEnabled;
        }

        if (!enabled)
            return;

        int value = 0;
        int.TryParse(rawValue, out value);
        ImGui.Indent();
        if (ImGui.InputInt(DevToolUiSettings.T("值", "Value") + "##" + id + "Value", ref value))
            SetPrefixedTag(ref spawnTags, prefix, value.ToString());
        ImGui.Unindent();
    }

    private static void DrawNamedSpawnTag()
    {
        bool enabled = TryGetPrefixedTag(spawnTags, NamedAttrTagPrefix, out string value);
        bool nextEnabled = enabled;
        if (ImGui.Checkbox(DevToolUiSettings.T("命名属性", "Named attribute") + "##NamedAttrEnabled", ref nextEnabled))
        {
            if (nextEnabled)
                SetPrefixedTag(ref spawnTags, NamedAttrTagPrefix, value);
            else
                RemovePrefixedTag(ref spawnTags, NamedAttrTagPrefix);
            enabled = nextEnabled;
        }

        if (!enabled)
            return;

        ImGui.Indent();
        string edited = value ?? string.Empty;
        if (ImGui.InputText(
                DevToolUiSettings.T("名称", "Name") + "##NamedAttrValue",
                ref edited,
                128))
            SetPrefixedTag(ref spawnTags, NamedAttrTagPrefix, edited);
        ImGui.Unindent();
    }

    private static string UnknownSpawnTags()
    {
        if (string.IsNullOrWhiteSpace(spawnTags))
            return string.Empty;

        List<string> unknown = new();
        string[] parts = spawnTags.Split(',');
        for (int i = 0; i < parts.Length; i++)
        {
            string token = parts[i].Trim();
            if (token.Length == 0 || IsKnownSpawnTag(token))
                continue;
            unknown.Add(token);
        }
        return string.Join(", ", unknown);
    }

    private static string UnknownTimelineTags()
    {
        if (string.IsNullOrWhiteSpace(timelineFilter))
            return string.Empty;

        List<string> unknown = new();
        string[] parts = timelineFilter.Split(',');
        for (int i = 0; i < parts.Length; i++)
        {
            string token = parts[i].Trim();
            if (token.Length == 0 || TimelineCatalogContains(token))
                continue;
            unknown.Add(token);
        }
        return string.Join(", ", unknown);
    }

    private static bool IsKnownSpawnTag(string token)
    {
        for (int i = 0; i < ToggleSpawnTags.Length; i++)
            if (string.Equals(token, ToggleSpawnTags[i], StringComparison.OrdinalIgnoreCase))
                return true;

        return token.StartsWith(SeedTagPrefix, StringComparison.OrdinalIgnoreCase) ||
               token.StartsWith(RotTypeTagPrefix, StringComparison.OrdinalIgnoreCase) ||
               token.StartsWith(NamedAttrTagPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TimelineCatalogContains(string token)
    {
        for (int i = 0; i < timelineCatalog.Count; i++)
            if (string.Equals(token, timelineCatalog[i], StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static int CsvCount(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
            return 0;

        int count = 0;
        string[] parts = csv.Split(',');
        for (int i = 0; i < parts.Length; i++)
            if (!string.IsNullOrWhiteSpace(parts[i]))
                count++;
        return count;
    }

    private static bool CsvContains(string csv, string value)
    {
        if (string.IsNullOrWhiteSpace(csv) || string.IsNullOrWhiteSpace(value))
            return false;

        string[] parts = csv.Split(',');
        for (int i = 0; i < parts.Length; i++)
            if (string.Equals(parts[i].Trim(), value, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static void SetCsvToken(ref string csv, string value, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        List<string> tokens = ParseCsv(csv);
        int found = -1;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (!string.Equals(tokens[i], value, StringComparison.OrdinalIgnoreCase))
                continue;
            found = i;
            break;
        }

        if (enabled)
        {
            if (found < 0)
                tokens.Add(value);
        }
        else if (found >= 0)
        {
            tokens.RemoveAt(found);
        }

        csv = string.Join(",", tokens);
    }

    private static bool TryGetPrefixedTag(string csv, string prefix, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(csv))
            return false;

        string[] parts = csv.Split(',');
        for (int i = 0; i < parts.Length; i++)
        {
            string token = parts[i].Trim();
            if (!token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            value = token.Substring(prefix.Length);
            return true;
        }

        return false;
    }

    private static void SetPrefixedTag(ref string csv, string prefix, string value)
    {
        List<string> tokens = ParseCsv(csv);
        string replacement = prefix + (value ?? string.Empty);

        for (int i = 0; i < tokens.Count; i++)
        {
            if (!tokens[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            tokens[i] = replacement;
            csv = string.Join(",", tokens);
            return;
        }

        tokens.Add(replacement);
        csv = string.Join(",", tokens);
    }

    private static void RemovePrefixedTag(ref string csv, string prefix)
    {
        List<string> tokens = ParseCsv(csv);
        for (int i = tokens.Count - 1; i >= 0; i--)
            if (tokens[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                tokens.RemoveAt(i);
        csv = string.Join(",", tokens);
    }

    private static List<string> ParseCsv(string csv)
    {
        List<string> tokens = new();
        if (string.IsNullOrWhiteSpace(csv))
            return tokens;

        string[] parts = csv.Split(',');
        for (int i = 0; i < parts.Length; i++)
        {
            string token = parts[i].Trim();
            if (token.Length > 0)
                tokens.Add(token);
        }
        return tokens;
    }

    private static void AddUnique(List<string> list, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase)) return;
        list.Add(value);
    }

    private static void SetStatus(string message, bool success)
    {
        // Successful edits already update the live preview immediately; a persistent success
        // paragraph only adds noise to the inspector. Keep failures visible because they are
        // actionable and otherwise easy to miss.
        lastStatus = success ? string.Empty : message ?? string.Empty;
    }
}
