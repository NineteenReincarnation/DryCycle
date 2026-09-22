using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Map;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Owns only the room-inspector subregion field. The rest of the room inspector stays in
/// WorldWorkspaceView so map controls cannot drift into a second duplicated implementation.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(BridgePlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldSubregionSelectorPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldSubregionSelector";
    public const string PluginName = "DryCycle DevTool World Subregion Selector";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() =>
        global::DryCycle.AuxiliaryPluginStartupGuard.Enable(
            PluginName + ".OnEnable",
            () => WorldSubregionSelector.Enable(Logger),
            WorldSubregionSelector.Disable);

    private void OnDisable() =>
        global::DryCycle.AuxiliaryPluginStartupGuard.Disable(
            PluginName + ".OnDisable",
            WorldSubregionSelector.Disable);
}

internal static class WorldSubregionSelector
{
    private const string DeletePopupId = "###WorldDeleteSubregionConfirm";

    private static ManualLogSource log;
    private static bool enabled;

    private static int creatingRoom = -1;
    private static string newSubregionName = string.Empty;
    private static bool focusNewSubregionInput;

    private static string pendingDeleteSubregion = string.Empty;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        log = logger;
        logger?.LogInfo(
            "World Map subregion selector enabled as a focused inspector field; " +
            "the duplicated room inspector has been retired.");
    }

    internal static void Disable()
    {
        CancelCreate();
        pendingDeleteSubregion = string.Empty;
        enabled = false;
        log = null;
    }

    /// <summary>
    /// Draws the complete subregion field. Returns false only when the optional selector plugin is
    /// unavailable, allowing WorldWorkspaceView to fall back to its simple text editor.
    /// </summary>
    internal static bool DrawField(
        EditorMapPresentationSnapshot snapshot,
        EditorMapRoomSnapshot room)
    {
        if (!enabled || snapshot == null || room == null)
            return false;

        if (creatingRoom >= 0 && creatingRoom != room.RoomIndex)
            CancelCreate();

        if (creatingRoom == room.RoomIndex)
            DrawInlineCreate(snapshot, room);
        else
            DrawSelector(snapshot, room);

        DrawDeleteConfirmation();
        return true;
    }

    private static void DrawSelector(
        EditorMapPresentationSnapshot snapshot,
        EditorMapRoomSnapshot room)
    {
        List<string> subregions = CollectSubregions(snapshot);
        string current = Normalize(room.Subregion);
        string preview = current.Length == 0
            ? DevToolUiSettings.T("None（无子区域）", "None")
            : current;

        DevToolWidgets.MutedText(
            DevToolUiSettings.T("子区域", "Subregion"),
            true);

        string deleteLabel =
            DevToolUiSettings.T("删除", "Delete");
        float spacing =
            Math.Max(
                4f,
                ImGui.GetStyle().ItemSpacing.X);
        float deleteWidth =
            DevToolWidgets.ButtonWidth(deleteLabel);
        float available =
            Math.Max(
                1f,
                ImGui.GetContentRegionAvail().X);
        float selectorWidth =
            Math.Max(
                96f,
                available - deleteWidth - spacing);

        ImGui.SetNextItemWidth(selectorWidth);
        if (ImGui.BeginCombo(
                "##WorldRoomSubregionSelector",
                preview))
        {
            if (ImGui.Selectable(
                    DevToolUiSettings.T(
                        "新建子区域",
                        "Create subregion") +
                    "##WorldSubregionCreate"))
            {
                BeginCreate(room.RoomIndex);
                ImGui.CloseCurrentPopup();
            }

            ImGui.Separator();

            bool noneSelected = current.Length == 0;
            if (ImGui.Selectable(
                    DevToolUiSettings.T(
                        "None（无子区域）",
                        "None") +
                    "##WorldSubregionNone",
                    noneSelected))
            {
                AssignSubregion(
                    room.RoomIndex,
                    string.Empty);
            }
            if (noneSelected)
                ImGui.SetItemDefaultFocus();

            for (int i = 0; i < subregions.Count; i++)
            {
                string name = subregions[i];
                bool selected =
                    string.Equals(
                        current,
                        name,
                        StringComparison.Ordinal);

                if (ImGui.Selectable(
                        name +
                        "##WorldSubregionChoice" +
                        i,
                        selected))
                {
                    AssignSubregion(
                        room.RoomIndex,
                        name);
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine(0f, spacing);

        bool canDelete = current.Length > 0;
        if (!canDelete)
            ImGui.BeginDisabled();

        if (DevToolWidgets.ActionButton(
                deleteLabel,
                "WorldDeleteSubregion",
                DevToolButtonTone.Danger))
        {
            pendingDeleteSubregion = current;
            ImGui.OpenPopup(DeletePopupId);
        }

        if (!canDelete)
            ImGui.EndDisabled();
    }

    private static void DrawInlineCreate(
        EditorMapPresentationSnapshot snapshot,
        EditorMapRoomSnapshot room)
    {
        DevToolWidgets.MutedText(
            DevToolUiSettings.T("子区域", "Subregion"),
            true);

        string cancelLabel =
            DevToolUiSettings.T("取消", "Cancel");
        float spacing =
            Math.Max(
                4f,
                ImGui.GetStyle().ItemSpacing.X);
        float cancelWidth =
            DevToolWidgets.ButtonWidth(cancelLabel);
        float available =
            Math.Max(
                1f,
                ImGui.GetContentRegionAvail().X);
        float inputWidth =
            Math.Max(
                96f,
                available - cancelWidth - spacing);

        ImGui.SetNextItemWidth(inputWidth);
        if (focusNewSubregionInput)
        {
            ImGui.SetKeyboardFocusHere();
            focusNewSubregionInput = false;
        }

        bool submitted =
            ImGui.InputText(
                "##WorldNewSubregionInline",
                ref newSubregionName,
                128,
                ImGuiInputTextFlags.EnterReturnsTrue);

        ImGui.SameLine(0f, spacing);
        if (DevToolWidgets.ActionButton(
                cancelLabel,
                "WorldCancelCreateSubregion",
                DevToolButtonTone.Subtle))
        {
            CancelCreate();
            return;
        }

        string normalized =
            Normalize(newSubregionName);
        bool duplicate =
            normalized.Length > 0 &&
            ContainsSubregion(
                snapshot,
                normalized);

        if (duplicate)
        {
            DevToolWidgets.MutedText(
                DevToolUiSettings.T(
                    "该子区域已经存在，请从列表中选择。",
                    "This subregion already exists; select it from the list."),
                true);
        }
        else
        {
            DevToolWidgets.MutedText(
                DevToolUiSettings.T(
                    "输入名称后按 Enter 创建并应用。",
                    "Type a name and press Enter to create and apply."),
                true);
        }

        if (!submitted ||
            normalized.Length == 0 ||
            duplicate)
            return;

        AssignSubregion(
            room.RoomIndex,
            normalized);
        CancelCreate();
    }

    private static void DrawDeleteConfirmation()
    {
        ImGuiIOPtr io = ImGui.GetIO();
        ImGui.SetNextWindowPos(
            io.DisplaySize * 0.5f,
            ImGuiCond.Appearing,
            new Num.Vector2(0.5f, 0.5f));

        bool open = true;
        string title =
            DevToolUiSettings.T(
                "删除子区域",
                "Delete Subregion") +
            DeletePopupId;

        if (!ImGui.BeginPopupModal(
                title,
                ref open,
                ImGuiWindowFlags.AlwaysAutoResize))
            return;

        ImGui.TextUnformatted(
            DevToolUiSettings.T(
                "你确定吗？",
                "Are you sure?"));

        ImGui.Spacing();

        float spacing =
            Math.Max(
                6f,
                ImGui.GetStyle().ItemSpacing.X);
        float width =
            Math.Max(
                86f,
                (ImGui.GetContentRegionAvail().X - spacing) *
                0.5f);

        if (ImGui.Button(
                DevToolUiSettings.T("是", "Yes") +
                "##ConfirmDeleteSubregion",
                new Num.Vector2(width, 0f)))
        {
            if (!string.IsNullOrWhiteSpace(
                    pendingDeleteSubregion))
            {
                MapEditorCommandQueue.Enqueue(
                    new MapEditorCommand(
                        MapEditorCommandKind.DeleteSubregion,
                        text: pendingDeleteSubregion));
            }

            pendingDeleteSubregion = string.Empty;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine(0f, spacing);

        if (ImGui.Button(
                DevToolUiSettings.T("否", "No") +
                "##CancelDeleteSubregion",
                new Num.Vector2(width, 0f)))
        {
            pendingDeleteSubregion = string.Empty;
            ImGui.CloseCurrentPopup();
        }

        if (!open)
            pendingDeleteSubregion = string.Empty;

        ImGui.EndPopup();
    }

    private static void BeginCreate(int roomIndex)
    {
        creatingRoom = roomIndex;
        newSubregionName = string.Empty;
        focusNewSubregionInput = true;
    }

    private static void CancelCreate()
    {
        creatingRoom = -1;
        newSubregionName = string.Empty;
        focusNewSubregionInput = false;
    }

    private static List<string> CollectSubregions(
        EditorMapPresentationSnapshot snapshot)
    {
        HashSet<string> unique =
            new(StringComparer.Ordinal);
        EditorMapRoomSnapshot[] rooms =
            snapshot?.Rooms ??
            Array.Empty<EditorMapRoomSnapshot>();

        for (int i = 0; i < rooms.Length; i++)
        {
            string name =
                Normalize(
                    rooms[i]?.Subregion);
            if (name.Length > 0)
                unique.Add(name);
        }

        List<string> result =
            new(unique);
        result.Sort(
            StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private static bool ContainsSubregion(
        EditorMapPresentationSnapshot snapshot,
        string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        EditorMapRoomSnapshot[] rooms =
            snapshot?.Rooms ??
            Array.Empty<EditorMapRoomSnapshot>();

        for (int i = 0; i < rooms.Length; i++)
        {
            if (string.Equals(
                    Normalize(
                        rooms[i]?.Subregion),
                    name.Trim(),
                    StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static void AssignSubregion(
        int roomIndex,
        string name)
    {
        if (roomIndex < 0)
            return;

        MapEditorCommandQueue.Enqueue(
            new MapEditorCommand(
                MapEditorCommandKind.SetRoomSubregion,
                roomIndex: roomIndex,
                text: Normalize(name)));
    }

    private static string Normalize(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim();
}
