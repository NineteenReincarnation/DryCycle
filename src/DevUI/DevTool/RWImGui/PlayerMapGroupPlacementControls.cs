using System;
using System.Collections.Generic;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Map.PlayerMap;
using ImGuiNET;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Batch placement controls for the current Player Map multi-selection. These controls are shown only
/// for two or more rooms so the existing single-room inspector remains the focused editing surface.
/// </summary>
internal static class PlayerMapGroupPlacementControls
{
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = PlayerMapSelectionAccess.Available;
        if (enabled)
            logger?.LogInfo("Player Map grouped placement controls enabled through direct inspector calls; no self-detour attached.");
        else
            logger?.LogWarning("Player Map grouped placement controls disabled because selection adapter is unavailable.");
    }

    internal static void Disable() => enabled = false;

    internal static void Draw(PlayerMapPresentationSnapshot snapshot)
    {
        if (!enabled || snapshot?.Available != true) return;

        List<PlayerMapRoomSnapshot> selected = PlayerMapSelectionAccess.Collect(snapshot);
        if (selected.Count < 2) return;

        ImGui.Separator();
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("多选放置", "MULTI PLACEMENT"));
        ImGui.TextDisabled(selected.Count + " rooms");

        bool allDerived = true;
        bool allAbsolute = true;
        for (int i = 0; i < selected.Count; i++)
        {
            allDerived &= selected[i].Mode == PlayerMapPlacementMode.Derived;
            allAbsolute &= selected[i].Mode == PlayerMapPlacementMode.Absolute;
        }

        if (DevToolWidgets.ActionButton(
                "Derived",
                "PlayerMapGroupDerived",
                allDerived ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
            Queue(selected, PlayerMapGroupPlacementOperation.SetDerived, "Set player-map rooms to derived placement");

        ImGui.SameLine();
        if (DevToolWidgets.ActionButton(
                "Absolute",
                "PlayerMapGroupAbsolute",
                allAbsolute ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
            Queue(selected, PlayerMapGroupPlacementOperation.SetAbsolute, "Set player-map rooms to absolute placement");

        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("重置到 World Layout", "Reset to World Layout"),
                "PlayerMapGroupResetOffset",
                DevToolButtonTone.Subtle))
            Queue(selected, PlayerMapGroupPlacementOperation.ResetOffset, "Reset player-map rooms to world layout");

        ImGui.TextWrapped(DevToolUiSettings.T(
            "切换 Derived/Absolute 会保持当前画面位置；重置到 World Layout 会将整组切回 Derived，并清零 Offset。",
            "Derived/Absolute preserves current visual positions. Reset to World Layout switches the group to Derived and clears offsets."));
    }

    private static void Queue(
        List<PlayerMapRoomSnapshot> selected,
        PlayerMapGroupPlacementOperation operation,
        string label)
    {
        if (selected == null || selected.Count == 0) return;
        int[] rooms = new int[selected.Count];
        for (int i = 0; i < selected.Count; i++) rooms[i] = selected[i].RoomIndex;
        PlayerMapGroupCommandQueue.Enqueue(new PlayerMapGroupPlacementCommand(rooms, operation, label));
    }

}
