using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Map;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Replaces the room inspector's free-form subregion text field with a constrained selector.
/// Existing subregions are derived from the current region snapshot; assigning a new name to the
/// current room creates that subregion in the same data model used by Rain World's MapPage.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(BridgePlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldSubregionSelectorPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldSubregionSelector";
    public const string PluginName = "DryCycle DevTool World Subregion Selector";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldSubregionSelector.Enable(Logger);

    private void OnDisable() => WorldSubregionSelector.Disable();
}

internal static class WorldSubregionSelector
{
    private delegate void OrigDrawRoomInspector(EditorMapPresentationSnapshot snapshot, EditorMapRoomSnapshot room);
    private delegate void HookDrawRoomInspector(
        OrigDrawRoomInspector orig,
        EditorMapPresentationSnapshot snapshot,
        EditorMapRoomSnapshot room);

    private static readonly HookDrawRoomInspector DrawRoomInspectorHookDelegate = DrawRoomInspectorHook;

    private static ManualLogSource log;
    private static object hook;
    private static bool enabled;

    private static int inspectorRoom = -1;
    private static Num.Vector2 inspectorPosition;
    private static string inspectorSubregion = string.Empty;

    private static string newSubregionName = string.Empty;
    private static int newSubregionRoom = -1;
    private static bool focusNewSubregionInput;

    private static MethodInfo selectConnectionMethod;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;

        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            Type workspaceType = typeof(WorldWorkspaceView);
            MethodInfo drawRoomInspector = workspaceType.GetMethod(
                "DrawRoomInspector",
                flags,
                null,
                new[] { typeof(EditorMapPresentationSnapshot), typeof(EditorMapRoomSnapshot) },
                null);
            selectConnectionMethod = workspaceType.GetMethod(
                "SelectConnection",
                flags,
                null,
                new[] { typeof(EditorMapConnectionSnapshot) },
                null);

            if (drawRoomInspector == null)
                throw new MissingMethodException("WorldWorkspaceView.DrawRoomInspector was not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            if (hookType == null)
                throw new TypeLoadException("MonoMod.RuntimeDetour.Hook is unavailable.");

            ConstructorInfo constructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            hook = constructor.Invoke(new object[] { drawRoomInspector, DrawRoomInspectorHookDelegate });
            enabled = true;
            log?.LogInfo("World Map subregion selector enabled.");
        }
        catch (Exception error)
        {
            DisposeHook();
            enabled = false;
            log?.LogWarning("World Map subregion selector could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        DisposeHook();
        selectConnectionMethod = null;
        inspectorRoom = -1;
        inspectorPosition = Num.Vector2.Zero;
        inspectorSubregion = string.Empty;
        newSubregionName = string.Empty;
        newSubregionRoom = -1;
        focusNewSubregionInput = false;
        enabled = false;
        log = null;
    }

    private static void DrawRoomInspectorHook(
        OrigDrawRoomInspector orig,
        EditorMapPresentationSnapshot snapshot,
        EditorMapRoomSnapshot room)
    {
        // This is intentionally a full room-inspector replacement rather than an overlay on top of
        // the old InputText. Keeping two widgets bound to the same property would make the free-form
        // editor look authoritative even though the selector is the intended workflow.
        if (snapshot == null || room == null)
        {
            orig(snapshot, room);
            return;
        }

        DrawRoomInspector(snapshot, room);
    }

    private static void DrawRoomInspector(EditorMapPresentationSnapshot snapshot, EditorMapRoomSnapshot room)
    {
        if (inspectorRoom != room.RoomIndex)
        {
            inspectorRoom = room.RoomIndex;
            inspectorPosition = new Num.Vector2(room.X, room.Y);
            inspectorSubregion = room.Subregion ?? string.Empty;
        }
        else if (!ImGui.IsAnyItemActive())
        {
            inspectorPosition = new Num.Vector2(room.X, room.Y);
            inspectorSubregion = room.Subregion ?? string.Empty;
        }

        ImGui.TextUnformatted(room.Name);
        ImGui.SameLine();
        ImGui.TextDisabled("L" + room.Layer);
        if (room.CurrentRoom) DevToolWidgets.MutedText(DevToolUiSettings.T("当前镜头房间", "Current camera room"));
        if (room.OffScreenDen) DevToolWidgets.MutedText(DevToolUiSettings.T("屏幕外巢穴", "Off-screen den"));
        if (room.Disabled) DevToolWidgets.MutedText(DevToolUiSettings.T("地图输出隐藏", "Hidden from map output"));
        ImGui.Separator();

        DevToolWidgets.SectionHeader(DevToolUiSettings.T("地图", "MAP"));
        Num.Vector2 position = inspectorPosition;
        bool positionChanged = ImGui.InputFloat2(
            DevToolUiSettings.T("位置##WorldRoomPosition", "Position##WorldRoomPosition"),
            ref position,
            "%.1f");
        inspectorPosition = position;
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            MapEditorCommandQueue.Enqueue(new MapEditorCommand(
                MapEditorCommandKind.SetRoomPosition,
                roomIndex: room.RoomIndex,
                value: new EditorPropertyValue(EditorPropertyKind.Vector2, x: position.X, y: position.Y)));
        }
        else if (!positionChanged && !ImGui.IsItemActive())
        {
            inspectorPosition = new Num.Vector2(room.X, room.Y);
        }

        int layer = room.Layer;
        if (ImGui.BeginCombo(DevToolUiSettings.T("图层##WorldRoomLayer", "Layer##WorldRoomLayer"), "L" + layer))
        {
            for (int i = 0; i < 3; i++)
            {
                bool selected = layer == i;
                if (ImGui.Selectable("L" + i + "##WorldRoomLayer" + i, selected))
                {
                    MapEditorCommandQueue.Enqueue(new MapEditorCommand(
                        MapEditorCommandKind.SetRoomLayer,
                        roomIndex: room.RoomIndex,
                        value: new EditorPropertyValue(EditorPropertyKind.Integer, integer: i)));
                }
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        DrawSubregionSelector(snapshot, room);

        EditorMapRoomVisualSnapshot visual = MapRoomGeometryPresentationHub.Get(room.RoomIndex);
        DevToolWidgets.MutedText(
            DevToolUiSettings.T("缩略图：", "Thumbnail: ") +
            (visual.DetailedRasterAvailable
                ? DevToolUiSettings.T("详细", "detailed")
                : DevToolUiSettings.T("等待房间数据", "waiting for room data")) +
            " · " + (visual.Curves?.Length ?? 0) + DevToolUiSettings.T(" 条曲面层", " curve layer(s)"),
            true);

        DevToolWidgets.SectionHeader(DevToolUiSettings.T("世界连接", "WORLD LINKS"));
        DrawRoomConnections(snapshot, room.RoomIndex);
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("节点", "NODES"));
        DrawRoomNodes(snapshot, room);
    }

    private static void DrawSubregionSelector(EditorMapPresentationSnapshot snapshot, EditorMapRoomSnapshot room)
    {
        List<string> subregions = CollectSubregions(snapshot);
        string current = inspectorSubregion ?? string.Empty;
        string preview = string.IsNullOrWhiteSpace(current)
            ? DevToolUiSettings.T("None（无子区域）", "None")
            : current;

        ImGui.TextDisabled(DevToolUiSettings.T("子区域", "Subregion"));

        string newLabel = DevToolUiSettings.T("+ 新建", "+ New");
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        float buttonWidth = DevToolWidgets.ButtonWidth(newLabel);
        float available = ImGui.GetContentRegionAvail().X;
        float comboWidth = Math.Max(112f, available - buttonWidth - spacing);

        ImGui.SetNextItemWidth(comboWidth);
        if (ImGui.BeginCombo("##WorldRoomSubregionSelector", preview))
        {
            bool noneSelected = string.IsNullOrWhiteSpace(current);
            if (ImGui.Selectable(DevToolUiSettings.T("None（无子区域）", "None") + "##WorldSubregionNone", noneSelected))
                AssignSubregion(room.RoomIndex, string.Empty);
            if (noneSelected) ImGui.SetItemDefaultFocus();

            if (subregions.Count > 0) ImGui.Separator();
            for (int i = 0; i < subregions.Count; i++)
            {
                string name = subregions[i];
                bool selected = string.Equals(current, name, StringComparison.Ordinal);
                if (ImGui.Selectable(name + "##WorldSubregionChoice" + i, selected))
                    AssignSubregion(room.RoomIndex, name);
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (DevToolWidgets.ActionButton(newLabel, "WorldCreateSubregion", DevToolButtonTone.Subtle))
        {
            newSubregionRoom = room.RoomIndex;
            newSubregionName = string.Empty;
            focusNewSubregionInput = true;
            ImGui.OpenPopup("CreateSubregionPopup");
        }

        DrawCreateSubregionPopup(snapshot);
    }

    private static void DrawCreateSubregionPopup(EditorMapPresentationSnapshot snapshot)
    {
        bool open = true;
        string title = DevToolUiSettings.T("新建子区域###CreateSubregionPopup", "Create Subregion###CreateSubregionPopup");
        if (!ImGui.BeginPopupModal(title, ref open, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        ImGui.TextUnformatted(DevToolUiSettings.T("子区域名称", "Subregion name"));
        ImGui.SetNextItemWidth(300f);
        if (focusNewSubregionInput)
        {
            ImGui.SetKeyboardFocusHere();
            focusNewSubregionInput = false;
        }
        ImGui.InputText("##NewWorldSubregionName", ref newSubregionName, 128);

        string normalized = (newSubregionName ?? string.Empty).Trim();
        bool empty = normalized.Length == 0;
        bool duplicate = ContainsSubregion(snapshot, normalized);

        if (duplicate)
        {
            DevToolWidgets.MutedText(
                DevToolUiSettings.T("该子区域已经存在，请直接从下拉列表选择。", "This subregion already exists; select it from the list."),
                true);
        }
        else
        {
            DevToolWidgets.MutedText(
                DevToolUiSettings.T("创建后会立即应用到当前房间。", "The new subregion will be applied to the current room immediately."),
                true);
        }

        ImGui.Spacing();
        if (empty || duplicate) ImGui.BeginDisabled();
        if (DevToolWidgets.ActionButton(DevToolUiSettings.T("创建并应用", "Create & Apply"), "CreateWorldSubregionConfirm", DevToolButtonTone.Primary))
        {
            AssignSubregion(newSubregionRoom, normalized);
            ImGui.CloseCurrentPopup();
            newSubregionName = string.Empty;
            newSubregionRoom = -1;
        }
        if (empty || duplicate) ImGui.EndDisabled();

        ImGui.SameLine();
        if (DevToolWidgets.ActionButton(DevToolUiSettings.T("取消", "Cancel"), "CreateWorldSubregionCancel", DevToolButtonTone.Subtle))
        {
            ImGui.CloseCurrentPopup();
            newSubregionName = string.Empty;
            newSubregionRoom = -1;
        }

        if (!open)
        {
            newSubregionName = string.Empty;
            newSubregionRoom = -1;
        }

        ImGui.EndPopup();
    }

    private static List<string> CollectSubregions(EditorMapPresentationSnapshot snapshot)
    {
        HashSet<string> unique = new(StringComparer.Ordinal);
        EditorMapRoomSnapshot[] rooms = snapshot?.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
        {
            string name = rooms[i]?.Subregion?.Trim();
            if (!string.IsNullOrEmpty(name)) unique.Add(name);
        }

        List<string> result = new(unique);
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private static bool ContainsSubregion(EditorMapPresentationSnapshot snapshot, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        EditorMapRoomSnapshot[] rooms = snapshot?.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
        {
            if (string.Equals(rooms[i]?.Subregion?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static void AssignSubregion(int roomIndex, string name)
    {
        if (roomIndex < 0) return;
        inspectorSubregion = string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim();
        MapEditorCommandQueue.Enqueue(new MapEditorCommand(
            MapEditorCommandKind.SetRoomSubregion,
            roomIndex: roomIndex,
            text: inspectorSubregion));
    }

    private static void DrawRoomConnections(EditorMapPresentationSnapshot snapshot, int roomIndex)
    {
        EditorMapConnectionSnapshot[] connections = snapshot.Connections ?? Array.Empty<EditorMapConnectionSnapshot>();
        int count = 0;
        for (int i = 0; i < connections.Length; i++)
        {
            EditorMapConnectionSnapshot connection = connections[i];
            if (connection.FromRoomIndex != roomIndex && connection.ToRoomIndex != roomIndex) continue;
            count++;
            if (!ImGui.Selectable(ConnectionLabel(snapshot, connection) + "##RoomConnection" + i)) continue;

            try
            {
                selectConnectionMethod?.Invoke(null, new object[] { connection });
            }
            catch (Exception error)
            {
                log?.LogDebug("Could not select World Map connection: " + Unwrap(error).Message);
                WorldMapView.SelectConnection(connection.ConnectionId);
            }
        }

        if (count == 0)
            DevToolWidgets.MutedText(DevToolUiSettings.T("没有区域内连接。", "No in-region links."));
    }

    private static void DrawRoomNodes(EditorMapPresentationSnapshot snapshot, EditorMapRoomSnapshot room)
    {
        EditorMapRoomNodeSnapshot[] nodes = room.Nodes ?? Array.Empty<EditorMapRoomNodeSnapshot>();
        if (nodes.Length == 0)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T("没有节点数据。", "No node data."));
            return;
        }

        for (int i = 0; i < nodes.Length; i++)
        {
            EditorMapRoomNodeSnapshot node = nodes[i];
            string target = node.ConnectedRoomIndex >= 0
                ? FindRoom(snapshot, node.ConnectedRoomIndex)?.Name ?? node.ConnectedRoomIndex.ToString()
                : DevToolUiSettings.T("未连接", "Disconnected");
            string line = node.NodeIndex + " · " + node.Type;
            if (node.Exit) line += "  ->  " + target;
            ImGui.TextUnformatted(line);
        }
    }

    private static string ConnectionLabel(EditorMapPresentationSnapshot snapshot, EditorMapConnectionSnapshot connection)
    {
        EditorMapRoomSnapshot a = FindRoom(snapshot, connection.FromRoomIndex);
        EditorMapRoomSnapshot b = FindRoom(snapshot, connection.ToRoomIndex);
        string left = (a?.Name ?? connection.FromRoomIndex.ToString()) + ":" + connection.FromNodeIndex;
        string right = (b?.Name ?? connection.ToRoomIndex.ToString()) + ":" +
                       (connection.ToNodeIndex >= 0 ? connection.ToNodeIndex.ToString() : "?");
        return left + " " + DirectionLabel(connection.Direction) + " " + right;
    }

    private static string DirectionLabel(WorldConnectionDirection direction) => direction switch
    {
        WorldConnectionDirection.AToB => "->",
        WorldConnectionDirection.BToA => "<-",
        _ => "<->"
    };

    private static EditorMapRoomSnapshot FindRoom(EditorMapPresentationSnapshot snapshot, int roomIndex)
    {
        EditorMapRoomSnapshot[] rooms = snapshot?.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
            if (rooms[i].RoomIndex == roomIndex) return rooms[i];
        return null;
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }

    private static void DisposeHook()
    {
        try
        {
            (hook as IDisposable)?.Dispose();
        }
        catch
        {
        }
        finally
        {
            hook = null;
        }
    }
}
