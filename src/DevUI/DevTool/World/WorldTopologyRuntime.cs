using System;
using RWCustom;

namespace DryCycle.DevUI.DevTool.World;

/// <summary>
/// Runtime support for endpoint-based world connections.
///
/// Vanilla ShortcutHandler resolves the target entrance with AbstractRoom.ExitIndex(sourceRoom),
/// which uses Array.IndexOf and therefore cannot distinguish repeated A <-> B room links.
/// This runtime corrects the target entrance immediately before the vessel enters/abstractizes,
/// and enforces explicit one-way edges at the source shortcut.
/// </summary>
internal static class WorldTopologyRuntime
{
    private static bool enabled;

    internal static void Enable()
    {
        if (enabled) return;
        enabled = true;
        On.ShortcutHandler.VesselAllowedInRoom += ShortcutHandler_VesselAllowedInRoom;
        On.AbstractCreature.Abstractize += AbstractCreature_Abstractize;
        On.Creature.SuckedIntoShortCut += Creature_SuckedIntoShortCut;
    }

    internal static void Disable()
    {
        if (!enabled) return;
        enabled = false;
        On.ShortcutHandler.VesselAllowedInRoom -= ShortcutHandler_VesselAllowedInRoom;
        On.AbstractCreature.Abstractize -= AbstractCreature_Abstractize;
        On.Creature.SuckedIntoShortCut -= Creature_SuckedIntoShortCut;
    }

    private static bool ShortcutHandler_VesselAllowedInRoom(
        On.ShortcutHandler.orig_VesselAllowedInRoom orig,
        ShortcutHandler self,
        ShortcutHandler.Vessel vessel)
    {
        TryCorrectVesselEntrance(self?.game?.world, vessel);
        return orig(self, vessel);
    }

    private static void AbstractCreature_Abstractize(
        On.AbstractCreature.orig_Abstractize orig,
        AbstractCreature self,
        WorldCoordinate coord)
    {
        if (self?.world != null &&
            self.pos.Valid &&
            coord.Valid &&
            self.pos.room >= 0 &&
            coord.room >= 0 &&
            self.pos.room != coord.room &&
            self.pos.abstractNode >= 0)
        {
            AbstractRoom source = self.world.GetAbstractRoom(self.pos.room);
            AbstractRoom target = self.world.GetAbstractRoom(coord.room);
            if (source != null && target != null &&
                TryGetExplicitRoute(
                    self.world.name,
                    source.name,
                    self.pos.abstractNode,
                    out WorldConnectionEndpoint destination,
                    out bool allowsTravel) &&
                allowsTravel &&
                string.Equals(destination.Room, target.name, StringComparison.OrdinalIgnoreCase) &&
                IsValidTargetExit(target, destination.NodeIndex))
            {
                coord.abstractNode = destination.NodeIndex;
            }
        }

        orig(self, coord);
    }

    private static void Creature_SuckedIntoShortCut(
        On.Creature.orig_SuckedIntoShortCut orig,
        Creature self,
        IntVector2 entrancePos,
        bool carriedByOther)
    {
        if (!carriedByOther && ShouldBlockReverseTravel(self, entrancePos))
        {
            global::DryCycle.Plugin.Logger?.LogDebug(
                "WorldTopology blocked reverse travel at " +
                self.room.abstractRoom.name + ":" +
                self.room.shortcutData(entrancePos).destNode);
            return;
        }

        orig(self, entrancePos, carriedByOther);
    }

    private static void TryCorrectVesselEntrance(World world, ShortcutHandler.Vessel vessel)
    {
        if (world == null || vessel?.room == null || vessel.creature?.abstractCreature == null) return;

        AbstractCreature creature = vessel.creature.abstractCreature;
        if (!creature.pos.Valid || creature.pos.room < 0 || creature.pos.abstractNode < 0) return;

        AbstractRoom source = world.GetAbstractRoom(creature.pos.room);
        AbstractRoom target = vessel.room;
        if (source == null || target == null || source.index == target.index) return;

        if (!TryGetExplicitRoute(
                world.name,
                source.name,
                creature.pos.abstractNode,
                out WorldConnectionEndpoint destination,
                out bool allowsTravel) ||
            !allowsTravel ||
            !string.Equals(destination.Room, target.name, StringComparison.OrdinalIgnoreCase) ||
            !IsValidTargetExit(target, destination.NodeIndex))
            return;

        vessel.entranceNode = destination.NodeIndex;
    }

    private static bool ShouldBlockReverseTravel(Creature creature, IntVector2 entrancePos)
    {
        Room room = creature?.room;
        AbstractRoom abstractRoom = room?.abstractRoom;
        if (room == null || abstractRoom == null || room.world == null) return false;
        if (!room.IsPositionInsideBoundries(entrancePos)) return false;
        if (room.GetTile(entrancePos).Terrain != Room.Tile.TerrainType.ShortcutEntrance) return false;

        ShortcutData shortcut = room.shortcutData(entrancePos);
        if (shortcut.shortCutType != ShortcutData.Type.RoomExit || shortcut.destNode < 0) return false;

        return TryGetExplicitRoute(
                   room.world.name,
                   abstractRoom.name,
                   shortcut.destNode,
                   out _,
                   out bool allowsTravel) &&
               !allowsTravel;
    }

    /// <summary>
    /// Returns true when an explicit edge owns the endpoint. allowsTravel tells the caller
    /// whether the edge direction permits leaving from that endpoint.
    /// </summary>
    private static bool TryGetExplicitRoute(
        string region,
        string sourceRoom,
        int sourceNode,
        out WorldConnectionEndpoint destination,
        out bool allowsTravel)
    {
        destination = default;
        allowsTravel = false;
        WorldConnectionEndpoint source = new(sourceRoom, sourceNode);
        WorldConnectionEdge[] edges = WorldTopologyRegistry.GetRegionEdges(region);

        for (int i = 0; i < edges.Length; i++)
        {
            WorldConnectionEdge edge = edges[i];
            if (edge.A.Equals(source))
            {
                destination = edge.B;
                allowsTravel = edge.AllowsFromA;
                return true;
            }
            if (edge.B.Equals(source))
            {
                destination = edge.A;
                allowsTravel = edge.AllowsFromB;
                return true;
            }
        }

        return false;
    }

    private static bool IsValidTargetExit(AbstractRoom room, int nodeIndex)
    {
        return room?.nodes != null &&
               nodeIndex >= 0 &&
               nodeIndex < room.nodes.Length &&
               room.nodes[nodeIndex].type == AbstractRoomNode.Type.Exit;
    }
}
