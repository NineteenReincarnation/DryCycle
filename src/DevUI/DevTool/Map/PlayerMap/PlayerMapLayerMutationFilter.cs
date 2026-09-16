using System;
using System.Reflection;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

/// <summary>
/// Rejects no-op SetLayer commands before PlayerMapWorkspaceRuntime marks its authoring state dirty.
/// MapEditorActions already rejects an unchanged RoomPanel layer, but the Player Map wrapper used to
/// Touch its own revision unconditionally. Keeping the filter at the command boundary fixes both
/// single-room controls and grouped layer changes without introducing another layer mutation path.
/// </summary>
internal static class PlayerMapLayerMutationFilter
{
    private delegate void OrigExecute(EditorSession session, PlayerMapCommand command);
    private delegate void HookExecute(OrigExecute orig, EditorSession session, PlayerMapCommand command);

    private static readonly HookExecute ExecuteHookDelegate = ExecuteHook;
    private static IDisposable executeHook;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            MethodInfo execute = typeof(PlayerMapWorkspaceRuntime).GetMethod(
                "Execute", flags, null, new[] { typeof(EditorSession), typeof(PlayerMapCommand) }, null);
            if (execute == null)
                throw new MissingMethodException("PlayerMapWorkspaceRuntime.Execute was not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            ConstructorInfo constructor = hookType?.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            executeHook = constructor.Invoke(new object[] { execute, ExecuteHookDelegate }) as IDisposable;
            if (executeHook == null)
                throw new InvalidOperationException("Player Map layer mutation filter hook was not created.");

            enabled = true;
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("Player Map layer mutation filter could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        try { executeHook?.Dispose(); }
        catch { }
        executeHook = null;
        enabled = false;
    }

    private static void ExecuteHook(OrigExecute orig, EditorSession session, PlayerMapCommand command)
    {
        if (!enabled || command.Kind != PlayerMapCommandKind.SetLayer || session == null)
        {
            orig(session, command);
            return;
        }

        int target = Mathf.Clamp(command.Integer, 0, PlayerMapCoordinateSystem.LayerCount - 1);
        PlayerMapPresentationSnapshot snapshot = PlayerMapWorkspaceRuntime.GetPresentation(session);
        PlayerMapRoomSnapshot[] rooms = snapshot?.Rooms ?? Array.Empty<PlayerMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
        {
            PlayerMapRoomSnapshot room = rooms[i];
            if (room == null || room.RoomIndex != command.RoomIndex) continue;
            if (room.Layer == target) return;
            break;
        }

        orig(session, command);
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
