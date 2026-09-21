using System;
using System.Collections.Generic;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Map.PlayerMap;
using ImGuiNET;
using UnityEngine;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Group layer controls for the Player Map selection model. The UI only collects the current
/// selection and enqueues one backend PlayerMapGroupLayerCommand; layer mutation/history remains
/// authoritative in PlayerMapWorkspaceRuntime + PlayerMapGroupCommandRuntime.
/// </summary>
internal static class PlayerMapGroupLayerControls
{
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = PlayerMapSelectionAccess.Available;
        if (enabled)
            logger?.LogInfo("Player Map grouped layer controls enabled through direct view calls; no self-detour attached.");
        else
            logger?.LogWarning("Player Map grouped layer controls disabled because selection adapter is unavailable.");
    }

    internal static void Disable() => enabled = false;

    internal static void DrawToolbar(PlayerMapPresentationSnapshot snapshot)
    {
        if (!enabled || snapshot?.Available != true) return;

        List<PlayerMapRoomSnapshot> selected = PlayerMapSelectionAccess.Collect(snapshot);
        if (selected.Count == 0) return;

        ImGui.SameLine(0f, 12f);
        ImGui.TextDisabled("Layer");
        for (int layer = 0; layer < PlayerMapCoordinateSystem.LayerCount; layer++)
        {
            ImGui.SameLine(0f, layer == 0 ? 5f : 3f);
            bool allOnLayer = true;
            for (int i = 0; i < selected.Count; i++)
            {
                if (selected[i].Layer == layer) continue;
                allOnLayer = false;
                break;
            }

            if (DevToolWidgets.ActionButton(
                    "L" + layer,
                    "PlayerMapGroupLayer" + layer,
                    allOnLayer ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
                QueueLayerChange(selected, layer);
        }
    }

    internal static void HandleShortcuts(
        PlayerMapPresentationSnapshot snapshot,
        bool canvasHovered,
        ImGuiIOPtr io)
    {
        if (!enabled || snapshot?.Available != true || !canvasHovered ||
            ImGui.IsAnyItemActive() || io.WantTextInput || io.KeyCtrl || io.KeyAlt ||
            ImGui.IsMouseDown(ImGuiMouseButton.Left))
            return;

        int target = -1;
        if (UnityEngine.Input.GetKeyDown(KeyCode.Alpha1) || UnityEngine.Input.GetKeyDown(KeyCode.Keypad1)) target = 0;
        else if (UnityEngine.Input.GetKeyDown(KeyCode.Alpha2) || UnityEngine.Input.GetKeyDown(KeyCode.Keypad2)) target = 1;
        else if (UnityEngine.Input.GetKeyDown(KeyCode.Alpha3) || UnityEngine.Input.GetKeyDown(KeyCode.Keypad3)) target = 2;
        if (target < 0) return;

        QueueLayerChange(PlayerMapSelectionAccess.Collect(snapshot), target);
    }

    private static void QueueLayerChange(List<PlayerMapRoomSnapshot> selected, int targetLayer)
    {
        if (selected == null || selected.Count == 0) return;
        targetLayer = Math.Max(0, Math.Min(PlayerMapCoordinateSystem.LayerCount - 1, targetLayer));

        List<int> changed = new(selected.Count);
        for (int i = 0; i < selected.Count; i++)
        {
            PlayerMapRoomSnapshot room = selected[i];
            if (room != null && !room.Disabled && room.Layer != targetLayer)
                changed.Add(room.RoomIndex);
        }
        if (changed.Count == 0) return;

        PlayerMapGroupCommandQueue.Enqueue(new PlayerMapGroupLayerCommand(
            changed.ToArray(),
            targetLayer,
            changed.Count == 1 ? "Change player-map room layer" : "Change player-map room layers"));
    }

}
