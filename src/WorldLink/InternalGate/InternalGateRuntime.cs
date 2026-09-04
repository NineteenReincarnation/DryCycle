using System;
using System.Collections.Generic;

namespace DryCycle.WorldLink.InternalGate;

/// <summary>
/// Makes a vanilla RegionGate work as an intra-region gate when its world.txt room line
/// carries the "InternalGate" tag. The room remains a normal vanilla gate room, but the
/// gate hand-off no longer asks OverWorld to infer/load another region from the room name.
/// </summary>
internal static class InternalGateRuntime
{
    internal const string Tag = "InternalGate";

    private static bool _enabled;
    private static readonly HashSet<string> ReportedInvalidRooms = new(StringComparer.OrdinalIgnoreCase);

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.WorldLoader.MappingRooms += WorldLoader_MappingRooms;
        On.RegionGate.customKarmaGateRequirements += RegionGate_CustomKarmaGateRequirements;
        On.OverWorld.GateRequestsSwitchInitiation += OverWorld_GateRequestsSwitchInitiation;
        On.RegionGate.Update += RegionGate_Update;

        Plugin.Logger?.LogInfo("InternalGate: enabled world.txt tag support.");
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.WorldLoader.MappingRooms -= WorldLoader_MappingRooms;
        On.RegionGate.customKarmaGateRequirements -= RegionGate_CustomKarmaGateRequirements;
        On.OverWorld.GateRequestsSwitchInitiation -= OverWorld_GateRequestsSwitchInitiation;
        On.RegionGate.Update -= RegionGate_Update;

        ReportedInvalidRooms.Clear();
        _enabled = false;
    }

    /// <summary>
    /// WorldLoader normally reserves gateIndex only for the vanilla GATE tag. InternalGate
    /// is deliberately also inserted into gatesList so Room.IsGateRoom(), RegionState gate
    /// persistence and the vanilla WaterGate/ElectricGate constructor continue to work.
    /// The InternalGate tag itself is still preserved in AbstractRoom.roomTags by vanilla's
    /// generic room-tag path.
    /// </summary>
    private static void WorldLoader_MappingRooms(On.WorldLoader.orig_MappingRooms orig, WorldLoader self)
    {
        orig(self);

        if (!_enabled || self?.world == null || !CurrentWorldLineHasTag(self))
        {
            return;
        }

        try
        {
            int roomIndex = self.rmcntr + self.world.firstRoomIndex;
            if (!self.gatesList.Contains(roomIndex))
            {
                self.gatesList.Add(roomIndex);
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"InternalGate: failed to register gate index while mapping world.txt: {ex}");
        }
    }

    /// <summary>
    /// Vanilla RegionGate expects every real gate room to have an entry in World/Gates/locks.txt.
    /// Keep that file fully supported, but make InternalGate authoring fail-safe: if either side
    /// has no authored requirement, default only that missing side to one karma instead of letting
    /// the vanilla constructor reach GateKarmaGlyph with a null requirement.
    /// </summary>
    private static void RegionGate_CustomKarmaGateRequirements(
        On.RegionGate.orig_customKarmaGateRequirements orig,
        RegionGate self)
    {
        if (_enabled && IsInternalGate(self?.room?.abstractRoom) && self.karmaRequirements != null)
        {
            for (int i = 0; i < self.karmaRequirements.Length && i < 2; i++)
            {
                self.karmaRequirements[i] ??= RegionGate.GateRequirement.OneKarma;
            }
        }

        orig(self);
    }

    /// <summary>
    /// This is the actual behavioral split from a regional gate. Vanilla derives a destination
    /// region from names such as GATE_SU_HI and starts a WorldLoader. InternalGate never does that:
    /// both exits are already resolved in the current World by world.txt, so the active world must
    /// stay untouched.
    /// </summary>
    private static void OverWorld_GateRequestsSwitchInitiation(
        On.OverWorld.orig_GateRequestsSwitchInitiation orig,
        OverWorld self,
        RegionGate reportBackToGate)
    {
        if (!_enabled || !IsInternalGate(reportBackToGate?.room?.abstractRoom))
        {
            orig(self, reportBackToGate);
            return;
        }

        if (!HasTwoResolvedInternalSides(reportBackToGate.room.abstractRoom))
        {
            reportBackToGate.dontOpen = true;
            ReportInvalidConfiguration(reportBackToGate.room.abstractRoom);
            return;
        }

        MarkGatePassed(reportBackToGate);

        // Do not assign OverWorld.reportBackToGate and do not create a WorldLoader.
        // RegionGate.Update sets waitingForWorldLoader immediately after this call;
        // RegionGate_Update below completes the same callback on the next line of the
        // vanilla state machine without changing worlds.
        Plugin.Logger?.LogDebug(
            $"InternalGate: '{reportBackToGate.room.abstractRoom.name}' kept traversal inside region " +
            $"'{reportBackToGate.room.world?.region?.name ?? reportBackToGate.room.world?.name ?? "?"}'.");
    }

    private static void RegionGate_Update(On.RegionGate.orig_Update orig, RegionGate self, bool eu)
    {
        bool internalGate = _enabled && IsInternalGate(self?.room?.abstractRoom);
        bool valid = !internalGate || HasTwoResolvedInternalSides(self.room.abstractRoom);

        // Invalid internal gates fail closed before vanilla can enter ClosingAirLock.
        if (internalGate && !valid)
        {
            self.dontOpen = true;
            ReportInvalidConfiguration(self.room.abstractRoom);
        }

        orig(self, eu);

        if (!internalGate)
        {
            return;
        }

        if (!valid)
        {
            // Vanilla resets dontOpen when the player leaves the activation zone.
            // Reassert fail-closed state until the world.txt connections are fixed.
            self.dontOpen = true;
            self.waitingForWorldLoader = false;
            return;
        }

        if (self.waitingForWorldLoader)
        {
            // Preserve the vanilla completion callback (including MMF's gate tutorial flag),
            // but report the same already-loaded room because no world hand-off occurred.
            self.NewWorldLoaded(self.room);
        }
    }

    internal static bool IsInternalGate(AbstractRoom room)
    {
        if (room?.roomTags == null)
        {
            return false;
        }

        for (int i = 0; i < room.roomTags.Count; i++)
        {
            if (string.Equals(room.roomTags[i], Tag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// InternalGate requires two distinct, resolved connections in the same loaded World.
    /// This intentionally uses world.txt connectivity, not the gate room's acronym/name.
    /// </summary>
    private static bool HasTwoResolvedInternalSides(AbstractRoom gateRoom)
    {
        if (gateRoom?.world == null || gateRoom.connections == null)
        {
            return false;
        }

        int first = -1;
        for (int i = 0; i < gateRoom.connections.Length; i++)
        {
            int connection = gateRoom.connections[i];
            if (connection < 0 || connection == gateRoom.index || connection == first)
            {
                continue;
            }

            AbstractRoom target;
            try
            {
                target = gateRoom.world.GetAbstractRoom(connection);
            }
            catch
            {
                target = null;
            }

            if (target == null || !ReferenceEquals(target.world, gateRoom.world))
            {
                continue;
            }

            if (first < 0)
            {
                first = connection;
            }
            else
            {
                return true;
            }
        }

        return false;
    }

    private static void MarkGatePassed(RegionGate gate)
    {
        try
        {
            AbstractRoom room = gate?.room?.abstractRoom;
            bool[] passed = gate?.room?.world?.regionState?.gatesPassedThrough;
            if (room == null || passed == null || room.gateIndex < 0 || room.gateIndex >= passed.Length)
            {
                return;
            }

            passed[room.gateIndex] = true;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"InternalGate: could not persist gate-used state: {ex.Message}");
        }
    }

    private static bool CurrentWorldLineHasTag(WorldLoader loader)
    {
        if (loader?.lines == null || loader.cntr < 0 || loader.cntr >= loader.lines.Length)
        {
            return false;
        }

        string line = loader.lines[loader.cntr];
        if (string.IsNullOrEmpty(line) || !line.Contains(" : "))
        {
            return false;
        }

        string[] fields = line.Split(new[] { " : " }, StringSplitOptions.None);
        for (int i = 2; i < fields.Length; i++)
        {
            if (string.Equals(fields[i].Trim(), Tag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void ReportInvalidConfiguration(AbstractRoom room)
    {
        string name = room?.name ?? "<unknown>";
        if (!ReportedInvalidRooms.Add(name))
        {
            return;
        }

        Plugin.Logger?.LogError(
            $"InternalGate: '{name}' is tagged {Tag} but does not have two distinct resolved same-world " +
            "room connections. Author both sides directly in this region's world.txt; the gate is fail-closed.");
    }
}
