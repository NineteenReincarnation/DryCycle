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

    private void OnEnable() => WorldCreatureSpawnInspector.Enable(Logger);
    private void OnDisable() => WorldCreatureSpawnInspector.Disable();
}

internal static class WorldCreatureSpawnInspector
{
    private enum TimelineMode
    {
        All,
        Only,
        Exclude
    }

    private static readonly string[] KnownSpawnTags =
    {
        "Night", "PreCycle", "Winter", "Ignorecycle", "AlternateForm", "Lavasafe",
        "TentacleImmune", "Voidsea", "Ripple", "Slayer", "Seed:0", "RotType:0", "NamedAttr:"
    };

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
    private static bool lastStatusSuccess = true;

    private static readonly List<string> timelineCatalog = new();
    private static int timelineCatalogFingerprint = -1;
    private static WorldCreatureSpawnRecord[] presentedSpawns = Array.Empty<WorldCreatureSpawnRecord>();
    private static string[] presentedLabels = Array.Empty<string>();
    private static string[] presentedSpawnData = Array.Empty<string>();

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
        presentedSpawns = Array.Empty<WorldCreatureSpawnRecord>();
        presentedLabels = Array.Empty<string>();
        presentedSpawnData = Array.Empty<string>();
        log = null;
    }

    internal static void DrawIntegrated(EditorMapPresentationSnapshot snapshot, EditorMapRoomSnapshot room)
    {
        // This panel is composed directly by WorldWorkspaceView. Rendering must therefore not be
        // gated by a separate BepInEx plugin lifecycle flag; otherwise the whole section can vanish
        // even though the workspace itself is alive.
        if (snapshot?.Available != true || room == null) return;
        if (stateRoom != room.RoomIndex)
            ResetForRoom(room);

        RefreshTimelineCatalog();

        DevToolWidgets.SectionHeader(DevToolUiSettings.T("放置生物", "CREATURE SPAWNS"));
        WorldCreatureSpawnRecord[] existing = WorldTextRegistry.GetCreatureSpawns(snapshot.RegionName, room.Name);
        DrawExistingSpawns(snapshot, room, existing);
        ImGui.Spacing();
        DrawEditor(snapshot, room);

        WorldLineageInspector.DrawIntegrated(snapshot, room);
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
        DevToolWidgets.MutedText(
            DevToolUiSettings.T("已放置 ", "Placed ") + existing.Length +
            DevToolUiSettings.T(" 个生成项", " spawn entrie(s)"));

        float deleteWidth = ImGui.CalcTextSize(DevToolUiSettings.T("删除", "Delete")).X + 22f;
        for (int i = 0; i < existing.Length; i++)
        {
            WorldCreatureSpawnRecord spawn = existing[i];
            ImGui.PushID(spawn.Id);

            bool editing = editingSpawnId == spawn.Id;
            if (ImGui.Selectable(presentedLabels[i], editing))
                LoadSpawn(spawn);

            if (presentedSpawnData[i].Length > 0)
            {
                ImGui.Indent();
                DevToolWidgets.MutedText(presentedSpawnData[i], true);
                ImGui.Unindent();
            }

            ImGui.SameLine();
            float targetX = ImGui.GetCursorPosX() + Math.Max(0f, ImGui.GetContentRegionAvail().X - deleteWidth);
            ImGui.SetCursorPosX(targetX);
            if (DevToolWidgets.ActionButton(
                    DevToolUiSettings.T("删除", "Delete"),
                    "DeleteCreatureSpawn",
                    DevToolButtonTone.Danger))
            {
                if (WorldTextRegistry.TryDeleteCreatureSpawn(snapshot.RegionName, spawn.Id, out string error))
                {
                    if (editingSpawnId == spawn.Id) ResetForm(room);
                    SetStatus(DevToolUiSettings.T(
                        "已删除生成项并刷新实时预览。",
                        "Spawner deleted and live preview refreshed."), true);
                }
                else
                {
                    SetStatus(error, false);
                }
            }
            ImGui.PopID();
        }
    }

    private static void EnsurePresentation(WorldCreatureSpawnRecord[] existing)
    {
        if (ReferenceEquals(existing, presentedSpawns) && presentedLabels.Length == existing.Length) return;
        presentedSpawns = existing;
        presentedLabels = new string[existing.Length];
        presentedSpawnData = new string[existing.Length];
        for (int i = 0; i < existing.Length; i++)
        {
            WorldCreatureSpawnRecord spawn = existing[i];
            string scope = SpawnScope(spawn);
            string label = "#" + spawn.DenNode + "  " + spawn.Creature;
            if (spawn.Amount > 1) label += " ×" + spawn.Amount;
            if (scope.Length > 0) label += "  ·  " + scope;
            presentedLabels[i] = label + "##CreatureSpawn";
            presentedSpawnData[i] = string.IsNullOrEmpty(spawn.SpawnData)
                ? string.Empty
                : "{" + spawn.SpawnData + "}";
        }
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

        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo(
                DevToolUiSettings.T("生物管道##CreatureSpawnDen", "Creature pipe##CreatureSpawnDen"),
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

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputInt(DevToolUiSettings.T("数量##CreatureSpawnAmount", "Amount##CreatureSpawnAmount"), ref amount);
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
            if (lastStatusSuccess) DevToolWidgets.MutedText(lastStatus, true);
            else ImGui.TextColored(new Num.Vector4(0.92f, 0.42f, 0.42f, 1f), lastStatus);
        }
    }

    private static void DrawSpawnTags()
    {
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText(
            DevToolUiSettings.T("Spawn 标签##CreatureSpawnTags", "Spawn tags##CreatureSpawnTags"),
            ref spawnTags,
            512);
        if (ImGui.IsItemHovered())
            DevToolTooltip.Show(DevToolUiSettings.T(
                "花括号内部内容，例如 Night,PreCycle,Seed:12。未知标签原样保留给 Mod。",
                "Contents inside {...}, e.g. Night,PreCycle,Seed:12. Unknown tags are preserved for mods."));

        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo(
                DevToolUiSettings.T("快速添加标签##CreatureSpawnTagPreset", "Add tag preset##CreatureSpawnTagPreset"),
                DevToolUiSettings.T("选择…", "Select…")))
        {
            for (int i = 0; i < KnownSpawnTags.Length; i++)
            {
                string tag = KnownSpawnTags[i];
                if (ImGui.Selectable(tag + "##SpawnTagPreset" + i)) AddTag(tag);
            }
            ImGui.EndCombo();
        }
    }

    private static void DrawTimelineEditor()
    {
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo(
                DevToolUiSettings.T("时间线范围##CreatureTimelineMode", "Timeline scope##CreatureTimelineMode"),
                TimelineModeText(timelineMode)))
        {
            DrawTimelineModeOption(TimelineMode.All, DevToolUiSettings.T("全部时间线", "All timelines"));
            DrawTimelineModeOption(TimelineMode.Only, DevToolUiSettings.T("仅这些标签", "Only these tags"));
            DrawTimelineModeOption(TimelineMode.Exclude, DevToolUiSettings.T("排除这些标签", "Exclude these tags"));
            ImGui.EndCombo();
        }

        if (timelineMode == TimelineMode.All) return;

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText(
            DevToolUiSettings.T("时间线 / 角色标签##CreatureTimelineFilter", "Timeline / character tags##CreatureTimelineFilter"),
            ref timelineFilter,
            256);
        if (ImGui.IsItemHovered())
            DevToolTooltip.Show(DevToolUiSettings.T(
                "world.txt 行首条件；逗号分隔，Mod 自定义时间线标签也会原样保留。",
                "world.txt line-prefix condition; comma-separated mod timeline tags are preserved."));

        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo(
                DevToolUiSettings.T("添加已注册标签##CreatureTimelinePreset", "Add registered tag##CreatureTimelinePreset"),
                DevToolUiSettings.T("选择…", "Select…")))
        {
            for (int i = 0; i < timelineCatalog.Count; i++)
            {
                string value = timelineCatalog[i];
                if (ImGui.Selectable(value + "##TimelinePreset" + i)) AddTimeline(value);
            }
            ImGui.EndCombo();
        }
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
        editingSpawnId = -1;
        List<WorldCreaturePipeCatalog.Entry> dens = WorldCreaturePipeCatalog.Get(room);
        selectedDen = dens.Count > 0 ? dens[0].NodeIndex : -1;
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
        {
            timelineMode = mode;
            if (mode == TimelineMode.All) timelineFilter = string.Empty;
        }
        if (selected) ImGui.SetItemDefaultFocus();
    }
    private static string TimelineModeText(TimelineMode mode) => mode switch
    {
        TimelineMode.Only => DevToolUiSettings.T("仅指定", "Only selected"),
        TimelineMode.Exclude => DevToolUiSettings.T("排除指定", "Exclude selected"),
        _ => DevToolUiSettings.T("全部", "All")
    };

    private static void AddTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || CsvContains(spawnTags, tag)) return;
        spawnTags = string.IsNullOrWhiteSpace(spawnTags) ? tag : spawnTags.Trim().TrimEnd(',') + "," + tag;
    }

    private static void AddTimeline(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || CsvContains(timelineFilter, value)) return;
        timelineFilter = string.IsNullOrWhiteSpace(timelineFilter)
            ? value
            : timelineFilter.Trim().TrimEnd(',') + "," + value;
    }

    private static bool CsvContains(string csv, string value)
    {
        if (string.IsNullOrWhiteSpace(csv)) return false;
        string[] parts = csv.Split(',');
        for (int i = 0; i < parts.Length; i++)
            if (string.Equals(parts[i].Trim(), value, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
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
        lastStatus = message ?? string.Empty;
        lastStatusSuccess = success;
    }
}