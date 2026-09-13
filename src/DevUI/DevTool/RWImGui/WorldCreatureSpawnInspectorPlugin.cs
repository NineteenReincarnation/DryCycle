using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Map;
using DryCycle.DevUI.DevTool.World;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Inserts world creature-spawner authoring into the selected-room inspector immediately before
/// WORLD LINKS. Creature ids and timeline ids come from live ExtEnum registries so mod-added values
/// appear without DryCycle maintaining a hard-coded compatibility list.
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

    private delegate void OrigSectionHeader(string text, float restoreScale);
    private delegate void HookSectionHeader(OrigSectionHeader orig, string text, float restoreScale);

    private static readonly HookSectionHeader SectionHeaderHookDelegate = SectionHeaderHook;
    private static readonly string[] KnownSpawnTags =
    {
        "Night",
        "PreCycle",
        "Winter",
        "Ignorecycle",
        "AlternateForm",
        "Lavasafe",
        "TentacleImmune",
        "Voidsea",
        "Ripple",
        "Seed:0",
        "RotType:0",
        "NamedAttr:"
    };

    private static ManualLogSource log;
    private static IDisposable sectionHeaderHook;
    private static bool enabled;
    private static bool injecting;

    private static int stateRoom = -1;
    private static int editingSpawnId = -1;
    private static int selectedDen = -1;
    private static string creatureId = string.Empty;
    private static string creatureSearch = string.Empty;
    private static int amount = 1;
    private static string spawnTags = string.Empty;
    private static TimelineMode timelineMode = TimelineMode.All;
    private static string timelineFilter = string.Empty;
    private static string lastStatus = string.Empty;
    private static bool lastStatusSuccess = true;

    private static readonly List<string> creatureCatalog = new();
    private static readonly List<string> timelineCatalog = new();
    private static int creatureCatalogCount = -1;
    private static int timelineCatalogFingerprint = -1;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;
        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            MethodInfo sectionHeader = typeof(DevToolWidgets).GetMethod(
                "SectionHeader",
                flags,
                null,
                new[] { typeof(string), typeof(float) },
                null);
            if (sectionHeader == null)
                throw new MissingMethodException("DevToolWidgets.SectionHeader was not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            if (hookType == null)
                throw new TypeLoadException("MonoMod.RuntimeDetour.Hook is unavailable.");
            ConstructorInfo constructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            sectionHeaderHook = constructor.Invoke(new object[] { sectionHeader, SectionHeaderHookDelegate }) as IDisposable;
            if (sectionHeaderHook == null)
                throw new InvalidOperationException("Creature-spawn inspector hook was not created.");

            enabled = true;
            log?.LogInfo("World creature-spawn inspector enabled.");
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("World creature-spawn inspector could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        try { sectionHeaderHook?.Dispose(); }
        catch { }
        sectionHeaderHook = null;
        enabled = false;
        injecting = false;
        stateRoom = -1;
        editingSpawnId = -1;
        creatureCatalog.Clear();
        timelineCatalog.Clear();
        creatureCatalogCount = -1;
        timelineCatalogFingerprint = -1;
        log = null;
    }

    private static void SectionHeaderHook(OrigSectionHeader orig, string text, float restoreScale)
    {
        if (enabled && !injecting && IsWorldLinksHeader(text))
        {
            EditorSession session = DevToolRuntime.ActiveSession;
            EditorMapPresentationSnapshot snapshot = MapEditorPresentationHub.Current;
            if (session?.ToolMode == EditorToolMode.Map && snapshot?.Available == true)
            {
                EditorMapRoomSnapshot room = FindRoom(snapshot, snapshot.SelectedRoomIndex);
                if (room != null)
                {
                    injecting = true;
                    try
                    {
                        Draw(snapshot, room, restoreScale);
                    }
                    finally
                    {
                        injecting = false;
                    }
                }
            }
        }

        orig(text, restoreScale);
    }

    private static bool IsWorldLinksHeader(string text) =>
        string.Equals(text, "世界连接", StringComparison.Ordinal) ||
        string.Equals(text, "WORLD LINKS", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(text, DevToolUiSettings.T("世界连接", "WORLD LINKS"), StringComparison.Ordinal);

    private static void Draw(EditorMapPresentationSnapshot snapshot, EditorMapRoomSnapshot room, float restoreScale)
    {
        if (stateRoom != room.RoomIndex)
            ResetForRoom(room);

        RefreshCreatureCatalog();
        RefreshTimelineCatalog();

        DevToolWidgets.SectionHeader(DevToolUiSettings.T("放置生物", "CREATURE SPAWNS"), restoreScale);
        WorldCreatureSpawnRecord[] existing = WorldTextRegistry.GetCreatureSpawns(snapshot.RegionName, room.Name);
        DrawExistingSpawns(snapshot, room, existing);
        ImGui.Spacing();
        DrawEditor(snapshot, room);
    }

    private static void DrawExistingSpawns(
        EditorMapPresentationSnapshot snapshot,
        EditorMapRoomSnapshot room,
        WorldCreatureSpawnRecord[] existing)
    {
        if (existing == null || existing.Length == 0)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T("这个房间还没有普通生物生成器。", "No ordinary creature spawners in this room."), true);
            return;
        }

        DevToolWidgets.MutedText(
            DevToolUiSettings.T("已放置 ", "Placed ") + existing.Length + DevToolUiSettings.T(" 个生成项", " spawn entrie(s)"));

        for (int i = 0; i < existing.Length; i++)
        {
            WorldCreatureSpawnRecord spawn = existing[i];
            ImGui.PushID(spawn.Id);
            string scope = SpawnScope(spawn);
            string label = "#" + spawn.DenNode + "  " + spawn.Creature;
            if (spawn.Amount > 1) label += " ×" + spawn.Amount;
            if (scope.Length > 0) label += "  ·  " + scope;

            bool editing = editingSpawnId == spawn.Id;
            if (ImGui.Selectable(label + "##CreatureSpawn", editing))
                LoadSpawn(spawn);

            if (!string.IsNullOrEmpty(spawn.SpawnData))
            {
                ImGui.Indent();
                DevToolWidgets.MutedText("{" + spawn.SpawnData + "}", true);
                ImGui.Unindent();
            }

            ImGui.SameLine();
            float deleteWidth = ImGui.CalcTextSize(DevToolUiSettings.T("删除", "Delete")).X + 22f;
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
                    SetStatus(DevToolUiSettings.T("已删除生成项。", "Spawner deleted."), true);
                }
                else
                {
                    SetStatus(error, false);
                }
            }
            ImGui.PopID();
        }
    }

    private static void DrawEditor(EditorMapPresentationSnapshot snapshot, EditorMapRoomSnapshot room)
    {
        List<EditorMapRoomNodeSnapshot> dens = CreatureDenNodes(room);
        if (dens.Count == 0)
        {
            ImGui.TextColored(
                new Num.Vector4(0.92f, 0.62f, 0.30f, 1f),
                DevToolUiSettings.T("这个房间没有 Den / 生物管道节点。", "This room has no Den / creature-pipe nodes."));
            return;
        }
        if (!ContainsDen(dens, selectedDen)) selectedDen = dens[0].NodeIndex;

        string editorTitle = editingSpawnId >= 0
            ? DevToolUiSettings.T("编辑生成项", "Edit spawn")
            : DevToolUiSettings.T("新建生成项", "New spawn");
        DevToolWidgets.MutedText(editorTitle);

        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo(
                DevToolUiSettings.T("生物管道##CreatureSpawnDen", "Creature pipe##CreatureSpawnDen"),
                DenLabel(dens, selectedDen)))
        {
            for (int i = 0; i < dens.Count; i++)
            {
                EditorMapRoomNodeSnapshot node = dens[i];
                bool selected = selectedDen == node.NodeIndex;
                if (ImGui.Selectable("#" + node.NodeIndex + " · " + node.Type + "##CreatureDen" + node.NodeIndex, selected))
                    selectedDen = node.NodeIndex;
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText(
            DevToolUiSettings.T("生物 ID##CreatureSpawnId", "Creature ID##CreatureSpawnId"),
            ref creatureId,
            128);
        if (ImGui.IsItemHovered())
            DevToolTooltip.Show(DevToolUiSettings.T(
                "可以直接输入 Mod 注册的 CreatureTemplate.Type 名称。",
                "You can type any mod-registered CreatureTemplate.Type id directly."));

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText(
            DevToolUiSettings.T("筛选生物列表##CreatureSpawnSearch", "Filter creatures##CreatureSpawnSearch"),
            ref creatureSearch,
            128);

        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo(
                DevToolUiSettings.T("已注册生物##CreatureSpawnCatalog", "Registered creatures##CreatureSpawnCatalog"),
                string.IsNullOrEmpty(creatureId) ? DevToolUiSettings.T("选择…", "Select…") : creatureId))
        {
            int visible = 0;
            for (int i = 0; i < creatureCatalog.Count; i++)
            {
                string id = creatureCatalog[i];
                if (!Matches(id, creatureSearch)) continue;
                visible++;
                bool selected = string.Equals(creatureId, id, StringComparison.Ordinal);
                if (ImGui.Selectable(id + "##CreatureType" + i, selected)) creatureId = id;
                if (selected) ImGui.SetItemDefaultFocus();
            }
            if (visible == 0)
                DevToolWidgets.MutedText(DevToolUiSettings.T("没有匹配项；仍可直接输入 ID。", "No matches; a raw id can still be used."));
            ImGui.EndCombo();
        }

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputInt(DevToolUiSettings.T("数量##CreatureSpawnAmount", "Amount##CreatureSpawnAmount"), ref amount);
        if (amount < 1) amount = 1;

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText(
            DevToolUiSettings.T("Spawn 标签##CreatureSpawnTags", "Spawn tags##CreatureSpawnTags"),
            ref spawnTags,
            512);
        if (ImGui.IsItemHovered())
            DevToolTooltip.Show(DevToolUiSettings.T(
                "写花括号内部内容，例如 Night,PreCycle,Seed:12。未知标签会原样保留给 Mod。",
                "Enter the contents inside {...}, e.g. Night,PreCycle,Seed:12. Unknown tags are preserved for mods."));

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

        if (timelineMode != TimelineMode.All)
        {
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputText(
                DevToolUiSettings.T("时间线 / 角色标签##CreatureTimelineFilter", "Timeline / character tags##CreatureTimelineFilter"),
                ref timelineFilter,
                256);
            if (ImGui.IsItemHovered())
                DevToolTooltip.Show(DevToolUiSettings.T(
                    "world.txt 行首条件；支持逗号分隔，也允许输入 Mod 自定义标签。",
                    "world.txt line-prefix condition; comma-separated and mod-defined tags are accepted."));

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

        ImGui.Spacing();
        string action = editingSpawnId >= 0
            ? DevToolUiSettings.T("应用修改", "Apply Changes")
            : DevToolUiSettings.T("放置生物", "Add Creature");
        if (DevToolWidgets.ActionButton(action, "ApplyCreatureSpawn", DevToolButtonTone.Primary, true))
            Apply(snapshot, room);

        if (editingSpawnId >= 0)
        {
            if (DevToolWidgets.ActionButton(
                    DevToolUiSettings.T("取消编辑", "Cancel Edit"),
                    "CancelCreatureSpawnEdit",
                    DevToolButtonTone.Subtle,
                    true))
                ResetForm(room);
        }

        if (!string.IsNullOrEmpty(lastStatus))
        {
            ImGui.Spacing();
            if (lastStatusSuccess) DevToolWidgets.MutedText(lastStatus, true);
            else ImGui.TextColored(new Num.Vector4(0.92f, 0.42f, 0.42f, 1f), lastStatus);
        }
    }

    private static void Apply(EditorMapPresentationSnapshot snapshot, EditorMapRoomSnapshot room)
    {
        string effectiveTimeline = timelineMode == TimelineMode.All ? string.Empty : timelineFilter;
        bool exclude = timelineMode == TimelineMode.Exclude;
        if (string.IsNullOrWhiteSpace(creatureId))
        {
            SetStatus(DevToolUiSettings.T("请选择或输入生物 ID。", "Choose or enter a creature id."), false);
            return;
        }
        if (timelineMode != TimelineMode.All && string.IsNullOrWhiteSpace(effectiveTimeline))
        {
            SetStatus(DevToolUiSettings.T("时间线范围需要至少一个标签。", "Timeline scope needs at least one tag."), false);
            return;
        }

        if (editingSpawnId >= 0)
        {
            if (WorldTextRegistry.TryUpdateCreatureSpawn(
                    snapshot.RegionName,
                    editingSpawnId,
                    selectedDen,
                    creatureId,
                    amount,
                    spawnTags,
                    effectiveTimeline,
                    exclude,
                    out string updateError))
            {
                SetStatus(DevToolUiSettings.T("生成项已更新；保存世界以写入 world.txt。", "Spawner updated; save the world to write world.txt."), true);
                editingSpawnId = -1;
            }
            else
            {
                SetStatus(updateError, false);
            }
            return;
        }

        if (WorldTextRegistry.TryAddCreatureSpawn(
                snapshot.RegionName,
                room.Name,
                selectedDen,
                creatureId,
                amount,
                spawnTags,
                effectiveTimeline,
                exclude,
                out _,
                out string addError))
        {
            SetStatus(DevToolUiSettings.T("已添加生成项；保存世界以写入 world.txt。", "Spawner added; save the world to write world.txt."), true);
        }
        else
        {
            SetStatus(addError, false);
        }
    }

    private static void ResetForRoom(EditorMapRoomSnapshot room)
    {
        stateRoom = room?.RoomIndex ?? -1;
        editingSpawnId = -1;
        creatureSearch = string.Empty;
        lastStatus = string.Empty;
        ResetForm(room);
    }

    private static void ResetForm(EditorMapRoomSnapshot room)
    {
        editingSpawnId = -1;
        List<EditorMapRoomNodeSnapshot> dens = CreatureDenNodes(room);
        selectedDen = dens.Count > 0 ? dens[0].NodeIndex : -1;
        if (string.IsNullOrEmpty(creatureId))
        {
            RefreshCreatureCatalog();
            creatureId = creatureCatalog.Count > 0 ? creatureCatalog[0] : string.Empty;
        }
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

    private static List<EditorMapRoomNodeSnapshot> CreatureDenNodes(EditorMapRoomSnapshot room)
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

    private static bool ContainsDen(List<EditorMapRoomNodeSnapshot> dens, int nodeIndex)
    {
        for (int i = 0; i < dens.Count; i++)
            if (dens[i].NodeIndex == nodeIndex) return true;
        return false;
    }

    private static string DenLabel(List<EditorMapRoomNodeSnapshot> dens, int nodeIndex)
    {
        for (int i = 0; i < dens.Count; i++)
            if (dens[i].NodeIndex == nodeIndex) return "#" + nodeIndex + " · " + dens[i].Type;
        return "#" + nodeIndex;
    }

    private static string SpawnScope(WorldCreatureSpawnRecord spawn)
    {
        if (spawn == null || string.IsNullOrEmpty(spawn.TimelineFilter)) return string.Empty;
        return (spawn.ExcludeTimeline ? "X-" : string.Empty) + spawn.TimelineFilter;
    }

    private static void RefreshCreatureCatalog()
    {
        int count = ExtEnum<CreatureTemplate.Type>.values.entries.Count;
        if (count == creatureCatalogCount && creatureCatalog.Count > 0) return;
        creatureCatalogCount = count;
        creatureCatalog.Clear();
        for (int i = 0; i < ExtEnum<CreatureTemplate.Type>.values.entries.Count; i++)
        {
            string value = ExtEnum<CreatureTemplate.Type>.values.entries[i];
            if (!string.IsNullOrWhiteSpace(value) && !ContainsExact(creatureCatalog, value))
                creatureCatalog.Add(value);
        }
        creatureCatalog.Sort(StringComparer.OrdinalIgnoreCase);
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

    private static string TimelineModeText(TimelineMode mode)
    {
        return mode switch
        {
            TimelineMode.Only => DevToolUiSettings.T("仅指定", "Only selected"),
            TimelineMode.Exclude => DevToolUiSettings.T("排除指定", "Exclude selected"),
            _ => DevToolUiSettings.T("全部", "All")
        };
    }

    private static void AddTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;
        if (CsvContains(spawnTags, tag)) return;
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

    private static bool Matches(string value, string query) =>
        string.IsNullOrWhiteSpace(query) ||
        (!string.IsNullOrEmpty(value) && value.IndexOf(query.Trim(), StringComparison.OrdinalIgnoreCase) >= 0) ||
        Fuzzy(value, query.Trim());

    private static bool Fuzzy(string value, string query)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(query)) return false;
        int q = 0;
        for (int i = 0; i < value.Length && q < query.Length; i++)
            if (char.ToUpperInvariant(value[i]) == char.ToUpperInvariant(query[q])) q++;
        return q == query.Length;
    }

    private static bool ContainsExact(List<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static void AddUnique(List<string> list, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || ContainsExact(list, value)) return;
        list.Add(value);
    }

    private static EditorMapRoomSnapshot FindRoom(EditorMapPresentationSnapshot snapshot, int roomIndex)
    {
        EditorMapRoomSnapshot[] rooms = snapshot?.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
            if (rooms[i]?.RoomIndex == roomIndex) return rooms[i];
        return null;
    }

    private static void SetStatus(string message, bool success)
    {
        lastStatus = message ?? string.Empty;
        lastStatusSuccess = success;
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
