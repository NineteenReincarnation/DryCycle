using System;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

/// <summary>
/// Rejects no-op SetLayer commands before PlayerMapWorkspaceRuntime marks its authoring state dirty.
/// Invoked directly from the authoritative Execute boundary.
/// </summary>
internal static class PlayerMapLayerMutationFilter
{
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        logger?.LogInfo("Player Map layer no-op filter enabled through direct Execute call; no self-detour attached.");
    }

    internal static void Disable() => enabled = false;

    internal static bool ShouldSkip(EditorSession session, PlayerMapCommand command)
    {
        if (!enabled || command.Kind != PlayerMapCommandKind.SetLayer || session == null)
            return false;

        int target = Mathf.Clamp(command.Integer, 0, PlayerMapCoordinateSystem.LayerCount - 1);
        PlayerMapPresentationSnapshot snapshot = PlayerMapWorkspaceRuntime.GetPresentation(session);
        PlayerMapRoomSnapshot[] rooms = snapshot?.Rooms ?? Array.Empty<PlayerMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
        {
            PlayerMapRoomSnapshot room = rooms[i];
            if (room == null || room.RoomIndex != command.RoomIndex) continue;
            return room.Layer == target;
        }
        return false;
    }
}
