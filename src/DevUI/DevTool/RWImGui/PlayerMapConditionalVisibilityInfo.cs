using System;
using System.Reflection;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Map.PlayerMap;
using ImGuiNET;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Makes World.DisabledMapRooms explicit in the Player Map inspector. This is intentionally read-only:
/// DisabledMapRooms is produced by world/timeline conditional rules (for example EXCLUSIVEROOM and
/// HIDEROOM), not by a persistent map_XX.txt authoring flag. Offering a toggle here would create a
/// session-only edit that looks saveable but is not.
/// </summary>
internal static class PlayerMapConditionalVisibilityInfo
{
    private delegate void OrigDrawRoomInspector(PlayerMapRoomSnapshot room);
    private delegate void HookDrawRoomInspector(OrigDrawRoomInspector orig, PlayerMapRoomSnapshot room);

    private static readonly HookDrawRoomInspector DrawRoomInspectorHookDelegate = DrawRoomInspectorHook;
    private static IDisposable inspectorHook;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            MethodInfo target = typeof(PlayerMapWorkspaceView).GetMethod(
                "DrawRoomInspector",
                flags,
                null,
                new[] { typeof(PlayerMapRoomSnapshot) },
                null);
            if (target == null)
                throw new MissingMethodException("PlayerMapWorkspaceView.DrawRoomInspector was not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            ConstructorInfo constructor = hookType?.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            inspectorHook = constructor.Invoke(new object[] { target, DrawRoomInspectorHookDelegate }) as IDisposable;
            if (inspectorHook == null)
                throw new InvalidOperationException("Player Map conditional-visibility inspector hook was not created.");

            enabled = true;
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("Player Map conditional-visibility inspector could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        try { inspectorHook?.Dispose(); }
        catch { }
        inspectorHook = null;
        enabled = false;
    }

    private static void DrawRoomInspectorHook(OrigDrawRoomInspector orig, PlayerMapRoomSnapshot room)
    {
        orig(room);
        if (!enabled || room == null || !room.Disabled) return;

        ImGui.Separator();
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("地图可见性", "MAP VISIBILITY"));
        ImGui.TextUnformatted(DevToolUiSettings.T("条件隐藏", "Conditionally hidden"));
        ImGui.TextWrapped(DevToolUiSettings.T(
            "该房间当前位于 World.DisabledMapRooms 中，因此不会进入玩家地图、连接线和 Render 输出。这个状态由当前 World/Timeline 的条件规则产生；请在 World 数据中编辑 EXCLUSIVEROOM / HIDEROOM 等条件，而不是在 Player Map 中修改。",
            "This room is currently in World.DisabledMapRooms, so it is excluded from the player map, connections and Render output. The state comes from the active World/Timeline conditional rules; edit EXCLUSIVEROOM / HIDEROOM conditions in World data rather than Player Map."));
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
