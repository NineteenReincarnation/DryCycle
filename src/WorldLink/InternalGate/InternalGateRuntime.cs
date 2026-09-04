using System;

namespace DryCycle.WorldLink.InternalGate;

/// <summary>
/// Lets an ordinary vanilla karma-gate room connect two rooms inside the same loaded region.
/// No custom world.txt tag is required: a gate is treated as internal only when vanilla has
/// registered it as a gate room and both of its sides resolve to distinct rooms in this World.
/// </summary>
internal static class InternalGateRuntime
{
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.RegionGate.customKarmaGateRequirements += RegionGate_CustomKarmaGateRequirements;
        On.OverWorld.GateRequestsSwitchInitiation += OverWorld_GateRequestsSwitchInitiation;
        On.RegionGate.Update += RegionGate_Update;

        Plugin.Logger?.LogInfo("InternalGate: automatic same-region karma-gate detection enabled.");
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.RegionGate.customKarmaGateRequirements -= RegionGate_CustomKarmaGateRequirements;
        On.OverWorld.GateRequestsSwitchInitiation -= OverWorld_GateRequestsSwitchInitiation;
        On.RegionGate.Update -= RegionGate_Update;
        _enabled = false;
    }

    /// <summary>
    /// Keep vanilla locks.txt behavior. If an automatically detected internal gate has a
    /// missing side requirement, default only that side to one karma so GateKarmaGlyph never
    /// receives a null requirement.
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
    /// Vanilla asks OverWorld to infer another region from a gate room name and create a
    /// WorldLoader. For an internal gate both destination rooms already exist in the active
    /// World, so no world switch is started at all.
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

        MarkGatePassed(reportBackToGate);

        // Do not assign OverWorld.reportBackToGate and do not create a WorldLoader.
        // RegionGate.Update sets waitingForWorldLoader after this call; our Update hook
        // immediately supplies the normal completion callback using the already-loaded room.
        Plugin.Logger?.LogDebug(
            $"InternalGate: '{reportBackToGate.room.abstractRoom.name}' stayed inside region " +
            $"'{reportBackToGate.room.world?.region?.name ?? reportBackToGate.room.world?.name ?? "?"}'.");
    }

    private static void RegionGate_Update(On.RegionGate.orig_Update orig, RegionGate self, bool eu)
    {
        bool internalGate = _enabled && IsInternalGate(self?.room?.abstractRoom);

        orig(self, eu);

        if (internalGate && self.waitingForWorldLoader)
        {
            // Preserve vanilla's post-loader gate state transition (and MMF tutorial side
            // effects) while reporting the same loaded room because no region hand-off occurred.
            self.NewWorldLoaded(self.room);
        }
    }

    /// <summary>
    /// Automatic discriminator:
    /// 1. vanilla itself must have registered this AbstractRoom as a gate (gateIndex >= 0);
    /// 2. two distinct connections must resolve inside the same loaded World.
    ///
    /// A normal cross-region gate fails condition 2 because its far side is not another room
    /// in the current region World, so vanilla cross-region behavior remains untouched.
    /// </summary>
    internal static bool IsInternalGate(AbstractRoom room)
    {
        return room != null && room.gateIndex >= 0 && HasTwoResolvedInternalSides(room);
    }

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
}
