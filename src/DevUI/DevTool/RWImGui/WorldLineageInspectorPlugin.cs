using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Map;
using DryCycle.DevUI.DevTool.World;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldCreatureSpawnInspectorPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldLineageInspectorPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMap.Lineages";
    public const string PluginName = "DryCycle DevTool World Lineages";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldLineageInspector.Enable(Logger);
    private void OnDisable() => WorldLineageInspector.Disable();
}

internal static class WorldLineageInspector
{
    private enum TimelineMode
    {
        All,
        Only,
        Exclude
    }

    private sealed class StageState
    {
        internal string Creature = "NONE";
        internal float Chance;
        internal string SpawnData = string.Empty;
    }

    private delegate void OrigDraw(EditorMapPresentationSnapshot snapshot, EditorMapRoomSnapshot room, float restoreScale);
    private delegate void HookDraw(OrigDraw orig, EditorMapPresentationSnapshot snapshot, EditorMapRoomSnapshot room, float restoreScale);

    private static readonly HookDraw DrawHookDelegate = DrawHook;
    private static readonly string[] SpawnTagPresets =
    {
        "Night", "PreCycle", "Winter", "Ignorecycle", "AlternateForm", "Lavasafe",
        "TentacleImmune", "Voidsea", "Ripple", "Slayer", "Seed:0", "RotType:0", "NamedAttr:"
    };

    private static IDisposable hook;
    private static ManualLogSource log;
    private static int stateRoom = -1;
    private static int editingId = -1;
    private static int selectedDen = -1;
    private static bool nightCreature;
    private static TimelineMode timelineMode;
    private static string timelineFilter = string.Empty;
    private static string status = string.Empty;
    private static bool statusSuccess = true;
    private static readonly List<StageState> stages = new();
    private static readonly List<string> timelines = new();
    private static int timelineFingerprint = -1;

    internal static void Enable(ManualLogSource logger)
    {
        if (hook != null) return;
        log = logger;
        try
        {
            Type owner = typeof(WorldCreatureSpawnInspectorPlugin).Assembly.GetType(
                "DryCycle.DevUI.DevTool.RWImGui.WorldCreatureSpawnInspector",
                throwOnError: true);
            MethodInfo draw = owner.GetMethod(
                "Draw",
                BindingFlags.Static | BindingFlags.NonPublic,
                null,
                new[] { typeof(EditorMapPresentationSnapshot), typeof(EditorMapRoomSnapshot), typeof(float) },
                null);
            if (draw == null) throw new MissingMethodException("WorldCreatureSpawnInspector.Draw was not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: true);
            ConstructorInfo constructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null) throw new MissingMethodException("RuntimeDetour Hook(MethodBase, Delegate) is unavailable.");
            hook = constructor.Invoke(new object[] { draw, DrawHookDelegate }) as IDisposable;
            if (hook == null) throw new InvalidOperationException("Lineage inspector hook was not created.");
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("World lineage inspector could not attach: " + error.Message);
        }
    }

    internal static void Disable()
    {
        try { hook?.Dispose(); } catch { }
        hook = null;
        stages.Clear();
        timelines.Clear();
        stateRoom = -1;
        editingId = -1;
        timelineFingerprint = -1;
        log = null;
    }

    private static void DrawHook(
        OrigDraw orig,
        EditorMapPresentationSnapshot snapshot,
        EditorMapRoomSnapshot room,
        float restoreScale)
    {
        orig(snapshot, room, restoreScale);
        DrawLineages(snapshot, room, restoreScale);
    }

    private static void DrawLineages(EditorMapPresentationSnapshot snapshot, EditorMapRoomSnapshot room, float restoreScale)
    {
        if (snapshot?.Available != true || room == null) return;
        if (stateRoom != room.RoomIndex) ResetRoom(room);
        RefreshTimelines();
        WorldLineageRegistry.EnsureLoaded(snapshot.RegionName);

        DevToolWidgets.SectionHeader(DevToolUiSettings.T("族谱 / Lineage", "LINEAGE"), restoreScale);
        WorldLineageRecord[] existing = WorldLineageRegistry.GetLineages(snapshot.RegionName, room.Name);
        DrawExisting(snapshot, room, existing);
        ImGui.Spacing();
        DrawEditor(snapshot, room);

        if (!string.IsNullOrEmpty(WorldCreatureLiveReload.LastStatus))
        {
            ImGui.Spacing();
            if (WorldCreatureLiveReload.LastSucceeded)
                DevToolWidgets.MutedText(WorldCreatureLiveReload.LastStatus, true);
            else
                ImGui.TextColored(new Num.Vector4(0.92f, 0.42f, 0.42f, 1f), WorldCreatureLiveReload.LastStatus);
        }
    }

    private static void DrawExisting(
        EditorMapPresentationSnapshot snapshot,
        EditorMapRoomSnapshot room,
        WorldLineageRecord[] existing)
    {
        if (existing.Length == 0)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T("这个房间还没有 Lineage。", "No lineage entries in this room."), true);
            return;
        }

        for (int i = 0; i < existing.Length; i++)
        {
            WorldLineageRecord lineage = existing[i];
            ImGui.PushID(lineage.Id);
            string label = "#" + lineage.DenNode + "  " + StageSummary(lineage);
            if (!string.IsNullOrEmpty(lineage.TimelineFilter))
                label += "  ·  " + (lineage.ExcludeTimeline ? "X-" : string.Empty) + lineage.TimelineFilter;
            if (lineage.NightCreature) label += "  ·  Night";

            if (ImGui.Selectable(label + "##LineageEntry", editingId == lineage.Id)) Load(lineage);
            ImGui.SameLine();
            if (DevToolWidgets.ActionButton(
                    DevToolUiSettings.T("删除", "Delete"),
                    "DeleteLineage",
                    DevToolButtonTone.Danger))
            {
                if (WorldLineageRegistry.TryDelete(snapshot.RegionName, lineage.Id, out string error))
                {
                    if (editingId == lineage.Id) ResetForm(room);
                    SetStatus(DevToolUiSettings.T("Lineage 已删除并实时重载。", "Lineage deleted and live-reloaded."), true);
                }
                else SetStatus(error, false);
            }
            ImGui.PopID();
        }
    }

    private static void DrawEditor(EditorMapPresentationSnapshot snapshot, EditorMapRoomSnapshot room)
    {
        List<EditorMapRoomNodeSnapshot> dens = DenNodes(room);
        if (dens.Count == 0)
        {
            ImGui.TextColored(
                new Num.Vector4(0.92f, 0.62f, 0.30f, 1f),
                DevToolUiSettings.T("这个房间没有 Den / 生物管道节点。", "This room has no Den / creature-pipe nodes."));
            return;
        }
        if (!ContainsDen(dens, selectedDen)) selectedDen = dens[0].NodeIndex;

        DevToolWidgets.MutedText(editingId >= 0
            ? DevToolUiSettings.T("编辑 Lineage", "Edit lineage")
            : DevToolUiSettings.T("新建 Lineage", "New lineage"));

        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo(
                DevToolUiSettings.T("生物管道##LineageDen", "Creature pipe##LineageDen"),
                DenLabel(dens, selectedDen)))
        {
            for (int i = 0; i < dens.Count; i++)
            {
                EditorMapRoomNodeSnapshot node = dens[i];
                bool selected = selectedDen == node.NodeIndex;
                if (ImGui.Selectable("#" + node.NodeIndex + " · " + node.Type + "##LineageDen" + node.NodeIndex, selected))
                    selectedDen = node.NodeIndex;
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        bool night = nightCreature;
        if (ImGui.Checkbox(DevToolUiSettings.T("夜间 Lineage##LineageNight", "Night lineage##LineageNight"), ref night))
            nightCreature = night;

        DrawTimelineEditor();
        ImGui.Spacing();
        DevToolWidgets.MutedText(DevToolUiSettings.T(
            "阶段顺序就是 Lineage 的进化顺序；概率为进入下一阶段的 ChanceToProgress 概率。",
            "Stage order is lineage progression order; chance is the ChanceToProgress probability for advancing."), true);

        for (int i = 0; i < stages.Count; i++) DrawStage(i);

        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("+ 添加阶段", "+ Add Stage"),
                "AddLineageStage",
                DevToolButtonTone.Subtle,
                true))
            stages.Add(new StageState { Creature = "NONE", Chance = 0f });

        ImGui.Spacing();
        string action = editingId >= 0
            ? DevToolUiSettings.T("应用 Lineage 修改", "Apply Lineage Changes")
            : DevToolUiSettings.T("创建 Lineage", "Create Lineage");
        if (DevToolWidgets.ActionButton(action, "ApplyLineage", DevToolButtonTone.Primary, true))
            Apply(snapshot, room);

        if (editingId >= 0 && DevToolWidgets.ActionButton(
                DevToolUiSettings.T("取消编辑", "Cancel Edit"),
                "CancelLineageEdit",
                DevToolButtonTone.Subtle,
                true))
            ResetForm(room);

        if (!string.IsNullOrEmpty(status))
        {
            ImGui.Spacing();
            if (statusSuccess) DevToolWidgets.MutedText(status, true);
            else ImGui.TextColored(new Num.Vector4(0.92f, 0.42f, 0.42f, 1f), status);
        }
    }

    private static void DrawStage(int index)
    {
        StageState stage = stages[index];
        ImGui.PushID(index);
        ImGui.Separator();
        ImGui.TextUnformatted(DevToolUiSettings.T("阶段 ", "Stage ") + (index + 1));
        ImGui.SameLine();
        if (index > 0 && DevToolWidgets.ActionButton("↑", "LineageStageUp", DevToolButtonTone.Subtle))
        {
            (stages[index - 1], stages[index]) = (stages[index], stages[index - 1]);
            ImGui.PopID();
            return;
        }
        ImGui.SameLine();
        if (index + 1 < stages.Count && DevToolWidgets.ActionButton("↓", "LineageStageDown", DevToolButtonTone.Subtle))
        {
            (stages[index + 1], stages[index]) = (stages[index], stages[index + 1]);
            ImGui.PopID();
            return;
        }
        ImGui.SameLine();
        if (stages.Count > 1 && DevToolWidgets.ActionButton(
                DevToolUiSettings.T("删除", "Delete"),
                "DeleteLineageStage",
                DevToolButtonTone.Danger))
        {
            stages.RemoveAt(index);
            ImGui.PopID();
            return;
        }

        // Lineage uses the exact same catalog as ordinary spawners. NONE is an explicit special
        // tile at the top of the picker instead of a magic text ID field.
        string selectedCreature = stage.Creature;
        if (WorldCreatureCatalogPicker.DrawSelector(
                "LineageStage_" + index,
                ref selectedCreature,
                allowNone: true,
                label: DevToolUiSettings.T("生物", "Creature")))
            stage.Creature = selectedCreature;

        float chance = stage.Chance;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputFloat(
                DevToolUiSettings.T("进化概率##LineageChance", "Progress chance##LineageChance"),
                ref chance,
                0.05f,
                0.1f,
                "%.3f"))
            stage.Chance = Math.Max(0f, Math.Min(1f, chance));

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText(
            DevToolUiSettings.T("Spawn 标签##LineageSpawnData", "Spawn tags##LineageSpawnData"),
            ref stage.SpawnData,
            512);
        if (ImGui.BeginCombo(
                DevToolUiSettings.T("快速添加标签##LineageTagPreset", "Add tag preset##LineageTagPreset"),
                DevToolUiSettings.T("选择…", "Select…")))
        {
            for (int i = 0; i < SpawnTagPresets.Length; i++)
            {
                string tag = SpawnTagPresets[i];
                if (ImGui.Selectable(tag + "##LineageTag" + i)) stage.SpawnData = AddCsv(stage.SpawnData, tag);
            }
            ImGui.EndCombo();
        }
        ImGui.PopID();
    }

    private static void DrawTimelineEditor()
    {
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo(
                DevToolUiSettings.T("时间线范围##LineageTimelineMode", "Timeline scope##LineageTimelineMode"),
                TimelineModeText()))
        {
            DrawTimelineMode(TimelineMode.All, DevToolUiSettings.T("全部时间线", "All timelines"));
            DrawTimelineMode(TimelineMode.Only, DevToolUiSettings.T("仅这些标签", "Only these tags"));
            DrawTimelineMode(TimelineMode.Exclude, DevToolUiSettings.T("排除这些标签", "Exclude these tags"));
            ImGui.EndCombo();
        }
        if (timelineMode == TimelineMode.All) return;

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText(
            DevToolUiSettings.T("时间线 / 角色标签##LineageTimeline", "Timeline / character tags##LineageTimeline"),
            ref timelineFilter,
            256);
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo(
                DevToolUiSettings.T("添加已注册标签##LineageTimelinePreset", "Add registered tag##LineageTimelinePreset"),
                DevToolUiSettings.T("选择…", "Select…")))
        {
            for (int i = 0; i < timelines.Count; i++)
            {
                string value = timelines[i];
                if (ImGui.Selectable(value + "##LineageTimeline" + i)) timelineFilter = AddCsv(timelineFilter, value);
            }
            ImGui.EndCombo();
        }
    }

    private static void Apply(EditorMapPresentationSnapshot snapshot, EditorMapRoomSnapshot room)
    {
        if (stages.Count == 0)
        {
            SetStatus(DevToolUiSettings.T("至少需要一个 Lineage 阶段。", "At least one lineage stage is required."), false);
            return;
        }
        if (timelineMode != TimelineMode.All && string.IsNullOrWhiteSpace(timelineFilter))
        {
            SetStatus(DevToolUiSettings.T("时间线范围需要至少一个标签。", "Timeline scope needs at least one tag."), false);
            return;
        }

        List<WorldLineageStageRecord> data = new();
        for (int i = 0; i < stages.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(stages[i].Creature))
            {
                SetStatus(DevToolUiSettings.T("每个阶段都要选择生物或 NONE。", "Every stage needs a creature or NONE."), false);
                return;
            }
            data.Add(new WorldLineageStageRecord
            {
                Creature = stages[i].Creature.Trim(),
                Chance = stages[i].Chance,
                SpawnData = stages[i].SpawnData
            });
        }

        string scope = timelineMode == TimelineMode.All ? string.Empty : timelineFilter;
        bool exclude = timelineMode == TimelineMode.Exclude;
        bool ok;
        string error;
        if (editingId >= 0)
            ok = WorldLineageRegistry.TryUpdate(snapshot.RegionName, editingId, selectedDen, data, scope, exclude, nightCreature, out error);
        else
            ok = WorldLineageRegistry.TryAdd(snapshot.RegionName, room.Name, selectedDen, data, scope, exclude, nightCreature, out editingId, out error);

        if (!ok)
        {
            SetStatus(error, false);
            return;
        }
        SetStatus(DevToolUiSettings.T(
            "Lineage 已更新并实时重载；保存世界以写入 world.txt。",
            "Lineage updated and live-reloaded; save the world to write world.txt."), true);
    }

    private static void Load(WorldLineageRecord lineage)
    {
        editingId = lineage.Id;
        selectedDen = lineage.DenNode;
        nightCreature = lineage.NightCreature;
        timelineFilter = lineage.TimelineFilter ?? string.Empty;
        timelineMode = string.IsNullOrEmpty(timelineFilter)
            ? TimelineMode.All
            : lineage.ExcludeTimeline ? TimelineMode.Exclude : TimelineMode.Only;
        stages.Clear();
        for (int i = 0; i < lineage.Stages.Count; i++)
        {
            WorldLineageStageRecord stage = lineage.Stages[i];
            stages.Add(new StageState
            {
                Creature = stage.Creature,
                Chance = stage.Chance,
                SpawnData = stage.SpawnData
            });
        }
        status = string.Empty;
    }

    private static void ResetRoom(EditorMapRoomSnapshot room)
    {
        stateRoom = room?.RoomIndex ?? -1;
        ResetForm(room);
    }

    private static void ResetForm(EditorMapRoomSnapshot room)
    {
        editingId = -1;
        List<EditorMapRoomNodeSnapshot> dens = DenNodes(room);
        selectedDen = dens.Count > 0 ? dens[0].NodeIndex : -1;
        nightCreature = false;
        timelineMode = TimelineMode.All;
        timelineFilter = string.Empty;
        stages.Clear();
        stages.Add(new StageState { Creature = "NONE", Chance = 0f });
        status = string.Empty;
    }

    private static void RefreshTimelines()
    {
        int fingerprint = ExtEnum<SlugcatStats.Timeline>.values.entries.Count * 397 ^
                          ExtEnum<SlugcatStats.Name>.values.entries.Count;
        if (fingerprint == timelineFingerprint && timelines.Count > 0) return;
        timelineFingerprint = fingerprint;
        timelines.Clear();
        for (int i = 0; i < ExtEnum<SlugcatStats.Timeline>.values.entries.Count; i++)
            AddUnique(timelines, ExtEnum<SlugcatStats.Timeline>.values.entries[i]);
        for (int i = 0; i < ExtEnum<SlugcatStats.Name>.values.entries.Count; i++)
            AddUnique(timelines, ExtEnum<SlugcatStats.Name>.values.entries[i]);
        timelines.Sort(StringComparer.OrdinalIgnoreCase);
    }

    private static List<EditorMapRoomNodeSnapshot> DenNodes(EditorMapRoomSnapshot room)
    {
        List<EditorMapRoomNodeSnapshot> result = new();
        EditorMapRoomNodeSnapshot[] nodes = room?.Nodes ?? Array.Empty<EditorMapRoomNodeSnapshot>();
        for (int i = 0; i < nodes.Length; i++)
        {
            EditorMapRoomNodeSnapshot node = nodes[i];
            if (node == null || node.Exit) continue;
            if (string.Equals(node.Type, "Den", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(node.Type, "GarbageHoles", StringComparison.OrdinalIgnoreCase))
                result.Add(node);
        }
        return result;
    }

    private static bool ContainsDen(List<EditorMapRoomNodeSnapshot> dens, int node)
    {
        for (int i = 0; i < dens.Count; i++) if (dens[i].NodeIndex == node) return true;
        return false;
    }

    private static string DenLabel(List<EditorMapRoomNodeSnapshot> dens, int node)
    {
        for (int i = 0; i < dens.Count; i++)
            if (dens[i].NodeIndex == node) return "#" + node + " · " + dens[i].Type;
        return "#" + node;
    }

    private static string StageSummary(WorldLineageRecord lineage)
    {
        List<string> names = new();
        for (int i = 0; i < lineage.Stages.Count; i++) names.Add(lineage.Stages[i].Creature);
        return string.Join(" → ", names);
    }

    private static void DrawTimelineMode(TimelineMode mode, string label)
    {
        bool selected = timelineMode == mode;
        if (ImGui.Selectable(label + "##LineageTimelineMode" + mode, selected))
        {
            timelineMode = mode;
            if (mode == TimelineMode.All) timelineFilter = string.Empty;
        }
        if (selected) ImGui.SetItemDefaultFocus();
    }

    private static string TimelineModeText() => timelineMode switch
    {
        TimelineMode.Only => DevToolUiSettings.T("仅指定", "Only selected"),
        TimelineMode.Exclude => DevToolUiSettings.T("排除指定", "Exclude selected"),
        _ => DevToolUiSettings.T("全部", "All")
    };

    private static string AddCsv(string csv, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return csv ?? string.Empty;
        string[] parts = (csv ?? string.Empty).Split(',');
        for (int i = 0; i < parts.Length; i++)
            if (string.Equals(parts[i].Trim(), value, StringComparison.OrdinalIgnoreCase)) return csv;
        return string.IsNullOrWhiteSpace(csv) ? value : csv.Trim().TrimEnd(',') + "," + value;
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
        status = message ?? string.Empty;
        statusSuccess = success;
    }
}
