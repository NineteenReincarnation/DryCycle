using System;
using System.Collections.Generic;
using System.Reflection;
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
    private delegate void OrigDrawInspector(PlayerMapPresentationSnapshot snapshot);
    private delegate void HookDrawInspector(OrigDrawInspector orig, PlayerMapPresentationSnapshot snapshot);

    private static readonly HookDrawInspector DrawInspectorHookDelegate = DrawInspectorHook;
    private static IDisposable inspectorHook;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        try
        {
            if (!PlayerMapSelectionAccess.Available)
                throw new InvalidOperationException("Player Map selection adapter is unavailable.");

            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            MethodInfo inspector = typeof(PlayerMapWorkspaceView).GetMethod(
                "DrawInspector", flags, null, new[] { typeof(PlayerMapPresentationSnapshot) }, null);
            if (inspector == null)
                throw new MissingMemberException("Player Map grouped placement control target was not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            ConstructorInfo constructor = hookType?.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            inspectorHook = constructor.Invoke(new object[] { inspector, DrawInspectorHookDelegate }) as IDisposable;
            if (inspectorHook == null)
                throw new InvalidOperationException("Player Map grouped placement inspector hook was not created.");

            enabled = true;
            logger?.LogInfo("Player Map grouped placement controls enabled.");
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("Player Map grouped placement controls could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        try { inspectorHook?.Dispose(); }
        catch { }
        inspectorHook = null;
        enabled = false;
    }

    private static void DrawInspectorHook(OrigDrawInspector orig, PlayerMapPresentationSnapshot snapshot)
    {
        orig(snapshot);
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

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
