using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Map.PlayerMap;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Narrow adapter around PlayerMapMultiSelection's retained selection set. Auxiliary Player Map UI
/// modules use this adapter instead of each reflecting private selection state independently. The
/// multi-selection controller remains the owner; this type only exposes a filtered snapshot view.
/// </summary>
internal static class PlayerMapSelectionAccess
{
    private static HashSet<int> selection;
    private static string region = string.Empty;

    internal static bool Available => selection != null;

    internal static void Enable(ManualLogSource logger)
    {
        if (selection != null) return;
        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            FieldInfo field = typeof(PlayerMapMultiSelection).GetField("Selection", flags);
            selection = field?.GetValue(null) as HashSet<int>;
            if (selection == null)
                throw new MissingMemberException("PlayerMapMultiSelection.Selection was not found.");
        }
        catch (Exception error)
        {
            selection = null;
            region = string.Empty;
            logger?.LogWarning("Player Map selection adapter could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        selection = null;
        region = string.Empty;
    }

    internal static List<PlayerMapRoomSnapshot> Collect(PlayerMapPresentationSnapshot snapshot)
    {
        List<PlayerMapRoomSnapshot> result = new();
        if (selection == null || snapshot?.Available != true) return result;

        NormalizeRegion(snapshot);
        PlayerMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<PlayerMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
        {
            PlayerMapRoomSnapshot room = rooms[i];
            if (room == null || room.Disabled) continue;
            bool selectedByGroup = selection.Contains(room.RoomIndex);
            bool selectedByInspector = selection.Count == 0 && room.RoomIndex == snapshot.SelectedRoomIndex;
            if (selectedByGroup || selectedByInspector)
                result.Add(room);
        }
        result.Sort((a, b) => a.RoomIndex.CompareTo(b.RoomIndex));
        return result;
    }

    private static void NormalizeRegion(PlayerMapPresentationSnapshot snapshot)
    {
        string next = snapshot.RegionName ?? string.Empty;
        if (string.Equals(region, next, StringComparison.OrdinalIgnoreCase)) return;
        region = next;
        selection.Clear();
        if (snapshot.SelectedRoomIndex >= 0)
            selection.Add(snapshot.SelectedRoomIndex);
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
