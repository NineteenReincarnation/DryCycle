using System;
using System.Collections.Generic;
using System.Reflection;
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
    private delegate void OrigDrawToolbar(PlayerMapPresentationSnapshot snapshot);
    private delegate void HookDrawToolbar(OrigDrawToolbar orig, PlayerMapPresentationSnapshot snapshot);
    private delegate void OrigHandleRoomInteraction(
        PlayerMapPresentationSnapshot snapshot,
        bool canvasHovered,
        PlayerMapRoomSnapshot hoveredRoom,
        Num.Vector2 canvasMin,
        ImGuiIOPtr io);
    private delegate void HookHandleRoomInteraction(
        OrigHandleRoomInteraction orig,
        PlayerMapPresentationSnapshot snapshot,
        bool canvasHovered,
        PlayerMapRoomSnapshot hoveredRoom,
        Num.Vector2 canvasMin,
        ImGuiIOPtr io);

    private static readonly HookDrawToolbar DrawToolbarHookDelegate = DrawToolbarHook;
    private static readonly HookHandleRoomInteraction HandleRoomInteractionHookDelegate = HandleRoomInteractionHook;

    private static IDisposable toolbarHook;
    private static IDisposable interactionHook;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        try
        {
            if (!PlayerMapSelectionAccess.Available)
                throw new InvalidOperationException("Player Map selection adapter is unavailable.");

            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            Type view = typeof(PlayerMapWorkspaceView);
            MethodInfo toolbar = view.GetMethod(
                "DrawToolbar", flags, null, new[] { typeof(PlayerMapPresentationSnapshot) }, null);
            MethodInfo interaction = view.GetMethod(
                "HandleRoomInteraction", flags, null,
                new[]
                {
                    typeof(PlayerMapPresentationSnapshot), typeof(bool), typeof(PlayerMapRoomSnapshot),
                    typeof(Num.Vector2), typeof(ImGuiIOPtr)
                }, null);
            if (toolbar == null || interaction == null)
                throw new MissingMemberException("Player Map grouped layer control targets were not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            ConstructorInfo constructor = hookType?.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            toolbarHook = constructor.Invoke(new object[] { toolbar, DrawToolbarHookDelegate }) as IDisposable;
            interactionHook = constructor.Invoke(new object[] { interaction, HandleRoomInteractionHookDelegate }) as IDisposable;
            if (toolbarHook == null || interactionHook == null)
                throw new InvalidOperationException("Player Map grouped layer control hooks were not created.");

            enabled = true;
            logger?.LogInfo("Player Map grouped layer controls enabled.");
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("Player Map grouped layer controls could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        Dispose(ref interactionHook);
        Dispose(ref toolbarHook);
        enabled = false;
    }

    private static void DrawToolbarHook(OrigDrawToolbar orig, PlayerMapPresentationSnapshot snapshot)
    {
        orig(snapshot);
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

    private static void HandleRoomInteractionHook(
        OrigHandleRoomInteraction orig,
        PlayerMapPresentationSnapshot snapshot,
        bool canvasHovered,
        PlayerMapRoomSnapshot hoveredRoom,
        Num.Vector2 canvasMin,
        ImGuiIOPtr io)
    {
        orig(snapshot, canvasHovered, hoveredRoom, canvasMin, io);
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

    private static void Dispose(ref IDisposable hook)
    {
        try { hook?.Dispose(); }
        catch { }
        hook = null;
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
