using System;
using System.Runtime.CompilerServices;
using RWCustom;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.World;

/// <summary>
/// Runtime support for endpoint-based world connections.
///
/// Vanilla ShortcutHandler resolves the target entrance with AbstractRoom.ExitIndex(sourceRoom),
/// which uses Array.IndexOf and therefore cannot distinguish repeated A <-> B room links.
/// We capture the exact source endpoint when the creature enters the shortcut, then resolve that
/// endpoint against the latest live topology before the vessel exits. This is deliberately revision
/// aware so Save/Undo/Redo can happen while a creature is already inside a shortcut without losing
/// or applying a stale target Exit.
/// </summary>
internal static class WorldTopologyRuntime
{
    private sealed class PendingRoute
    {
        internal string SourceRoom = string.Empty;
        internal int SourceNode = -1;
        internal string TargetRoom = string.Empty;
        internal int TargetNode = -1;
        internal int Revision;
    }

    private static ConditionalWeakTable<AbstractCreature, PendingRoute> pendingRoutes = new();
    private static int topologyRevision;
    private static bool enabled;

    internal static void Enable()
    {
        if (enabled) return;

        enabled = true;
        topologyRevision = 0;
        pendingRoutes = new ConditionalWeakTable<AbstractCreature, PendingRoute>();

        try
        {
            WorldConnectionSyntax.Enable();
            On.ShortcutHandler.SuckInCreature += ShortcutHandler_SuckInCreature;
            On.ShortcutHandler.VesselAllowedInRoom += ShortcutHandler_VesselAllowedInRoom;
            On.AbstractCreature.Abstractize += AbstractCreature_Abstractize;
            On.Creature.SuckedIntoShortCut += Creature_SuckedIntoShortCut;
        }
        catch (Exception error)
        {
            global::DryCycle.StartupDiagnostics.RollbackAfterFailure(
                "WorldTopologyRuntime.Enable",
                error,
                () => CleanupInstalledState("enable rollback"));
            throw;
        }
    }

    internal static void Disable()
    {
        if (!enabled) return;
        CleanupInstalledState("disable");
    }

    private static void CleanupInstalledState(string phase)
    {
        enabled = false;

        global::DryCycle.StartupDiagnostics.RollbackStep(
            "WorldTopologyRuntime/" + phase + "/Creature.SuckedIntoShortCut",
            () => On.Creature.SuckedIntoShortCut -= Creature_SuckedIntoShortCut);
        global::DryCycle.StartupDiagnostics.RollbackStep(
            "WorldTopologyRuntime/" + phase + "/AbstractCreature.Abstractize",
            () => On.AbstractCreature.Abstractize -= AbstractCreature_Abstractize);
        global::DryCycle.StartupDiagnostics.RollbackStep(
            "WorldTopologyRuntime/" + phase + "/ShortcutHandler.VesselAllowedInRoom",
            () => On.ShortcutHandler.VesselAllowedInRoom -= ShortcutHandler_VesselAllowedInRoom);
        global::DryCycle.StartupDiagnostics.RollbackStep(
            "WorldTopologyRuntime/" + phase + "/ShortcutHandler.SuckInCreature",
            () => On.ShortcutHandler.SuckInCreature -= ShortcutHandler_SuckInCreature);
        global::DryCycle.StartupDiagnostics.RollbackStep(
            "WorldTopologyRuntime/" + phase + "/WorldConnectionSyntax.Disable",
            WorldConnectionSyntax.Disable);

        pendingRoutes = new ConditionalWeakTable<AbstractCreature, PendingRoute>();
        topologyRevision = 0;
    }

    /// <summary>
    /// Called whenever the editable topology changes. Do not clear pending routes here: doing so
    /// strands or misroutes creatures that are already inside a shortcut. Pending routes remember
    /// their source endpoint and are re-resolved lazily against this new revision before exit.
    /// </summary>
    internal static void NotifyTopologyChanged()
    {
        unchecked
        {
            topologyRevision++;
            if (topologyRevision == int.MinValue) topologyRevision = 1;
        }
    }

    private static void ShortcutHandler_SuckInCreature(
        On.ShortcutHandler.orig_SuckInCreature orig,
        ShortcutHandler self,
        Creature creature,
        global::Room room,
        ShortcutData shortcut)
    {
        WorldTopologyLiveTraversalFix.BeforeShortcutTraversal(room, shortcut);
        CapturePendingRoute(creature, room, shortcut);
        orig(self, creature, room, shortcut);
    }

    private static bool ShortcutHandler_VesselAllowedInRoom(
        On.ShortcutHandler.orig_VesselAllowedInRoom orig,
        ShortcutHandler self,
        ShortcutHandler.Vessel vessel)
    {
        ApplyPendingRouteToVessel(vessel);
        return orig(self, vessel);
    }

    private static void AbstractCreature_Abstractize(
        On.AbstractCreature.orig_Abstractize orig,
        AbstractCreature self,
        WorldCoordinate coord)
    {
        if (TryConsumePendingRoute(self, ref coord))
        {
            // coord now carries both the current target room and its exact entrance node. This also
            // repairs an in-flight route if live authoring changed the destination room mid-shortcut.
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
            CancelBlockedShortcutEntry(self, entrancePos);
            return;
        }

        orig(self, entrancePos, carriedByOther);
    }

    private static void CapturePendingRoute(Creature creature, global::Room room, ShortcutData shortcut)
    {
        AbstractCreature abstractCreature = creature?.abstractCreature;
        AbstractRoom source = room?.abstractRoom;
        global::World world = room?.world;
        if (abstractCreature == null || source == null || world == null ||
            shortcut.shortCutType != ShortcutData.Type.RoomExit ||
            shortcut.destNode < 0)
        {
            if (abstractCreature != null) pendingRoutes.Remove(abstractCreature);
            return;
        }

        if (!TryGetExplicitRoute(
                world.name,
                source.name,
                shortcut.destNode,
                out WorldConnectionEndpoint destination,
                out bool allowsTravel) ||
            !allowsTravel)
        {
            pendingRoutes.Remove(abstractCreature);
            return;
        }

        AbstractRoom target = world.GetAbstractRoom(destination.Room);
        if (!IsValidTargetExit(target, destination.NodeIndex))
        {
            pendingRoutes.Remove(abstractCreature);
            global::DryCycle.Plugin.Logger?.LogWarning(
                "WorldTopology ignored invalid target endpoint " + destination + ".");
            return;
        }

        pendingRoutes.Remove(abstractCreature);
        pendingRoutes.Add(abstractCreature, new PendingRoute
        {
            SourceRoom = source.name,
            SourceNode = shortcut.destNode,
            TargetRoom = destination.Room,
            TargetNode = destination.NodeIndex,
            Revision = topologyRevision
        });
    }

    private static void ApplyPendingRouteToVessel(ShortcutHandler.Vessel vessel)
    {
        AbstractCreature creature = vessel?.creature?.abstractCreature;
        if (creature == null || !pendingRoutes.TryGetValue(creature, out PendingRoute pending)) return;

        if (!TryRefreshPendingRoute(creature, pending, out AbstractRoom target))
        {
            pendingRoutes.Remove(creature);
            return;
        }

        // Vanilla may already have selected another duplicate room exit with ExitIndex(), or may
        // even hold -1 for the reverse side of a one-way link. The exact route is authoritative.
        vessel.room = target;
        vessel.entranceNode = pending.TargetNode;
        pendingRoutes.Remove(creature);
    }

    private static bool TryConsumePendingRoute(
        AbstractCreature creature,
        ref WorldCoordinate coord)
    {
        if (creature?.world == null || !pendingRoutes.TryGetValue(creature, out PendingRoute pending))
            return false;

        if (!TryRefreshPendingRoute(creature, pending, out AbstractRoom target))
        {
            pendingRoutes.Remove(creature);
            return false;
        }

        coord.room = target.index;
        coord.abstractNode = pending.TargetNode;
        pendingRoutes.Remove(creature);
        return true;
    }

    private static bool TryRefreshPendingRoute(
        AbstractCreature creature,
        PendingRoute pending,
        out AbstractRoom target)
    {
        target = null;
        global::World world = creature?.world;
        if (world == null || pending == null ||
            string.IsNullOrWhiteSpace(pending.SourceRoom) || pending.SourceNode < 0)
            return false;

        if (pending.Revision != topologyRevision)
        {
            if (!TryGetExplicitRoute(
                    world.name,
                    pending.SourceRoom,
                    pending.SourceNode,
                    out WorldConnectionEndpoint destination,
                    out bool allowsTravel) ||
                !allowsTravel)
                return false;

            AbstractRoom refreshedTarget = world.GetAbstractRoom(destination.Room);
            if (!IsValidTargetExit(refreshedTarget, destination.NodeIndex))
                return false;

            pending.TargetRoom = destination.Room;
            pending.TargetNode = destination.NodeIndex;
            pending.Revision = topologyRevision;
        }

        target = world.GetAbstractRoom(pending.TargetRoom);
        return IsValidTargetExit(target, pending.TargetNode);
    }

    private static bool ShouldBlockReverseTravel(Creature creature, IntVector2 entrancePos)
    {
        global::Room room = creature?.room;
        AbstractRoom abstractRoom = room?.abstractRoom;
        if (room == null || abstractRoom == null || room.world == null) return false;
        if (!room.IsPositionInsideBoundries(entrancePos)) return false;
        if (room.GetTile(entrancePos).Terrain != global::Room.Tile.TerrainType.ShortcutEntrance) return false;

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

    private static void CancelBlockedShortcutEntry(Creature creature, IntVector2 entrancePos)
    {
        global::Room room = creature?.room;
        if (room == null) return;

        pendingRoutes.Remove(creature.abstractCreature);
        creature.enteringShortCut = null;
        creature.inShortcut = false;
        creature.inShortcutVessel = null;
        creature.shortcutDelay = Math.Max(creature.shortcutDelay, 20);

        Vector2 direction = Custom.IntVector2ToVector2(room.ShorcutEntranceHoleDirection(entrancePos));
        if (direction.sqrMagnitude < 0.001f) direction = Vector2.up;
        direction.Normalize();
        Vector2 anchor = room.MiddleOfTile(entrancePos) + direction * 10f;

        var connected = creature.abstractCreature?.GetAllConnectedObjects();
        if (connected != null)
        {
            for (int i = 0; i < connected.Count; i++)
            {
                PhysicalObject realized = connected[i]?.realizedObject;
                if (realized?.bodyChunks == null || realized.room != room) continue;
                for (int chunkIndex = 0; chunkIndex < realized.bodyChunks.Length; chunkIndex++)
                {
                    BodyChunk chunk = realized.bodyChunks[chunkIndex];
                    float distance = 6f + Math.Max(0f, chunk.rad);
                    chunk.pos = anchor + direction * distance;
                    chunk.lastPos = chunk.pos;
                    chunk.vel = direction * 2.5f;
                }
            }
        }

        global::DryCycle.Plugin.Logger?.LogDebug(
            "WorldTopology blocked reverse travel at " +
            room.abstractRoom.name + ":" + room.shortcutData(entrancePos).destNode + ".");
    }

    /// <summary>
    /// Exact world.txt syntax is authoritative. WorldTopology.json remains only as a compatibility
    /// fallback for older editor-authored maps that have not yet been resaved with &lt;Exit&gt;Room tokens.
    /// </summary>
    private static bool TryGetExplicitRoute(
        string region,
        string sourceRoom,
        int sourceNode,
        out WorldConnectionEndpoint destination,
        out bool allowsTravel)
    {
        if (WorldConnectionSyntax.TryGetLoadedRoute(region, sourceRoom, sourceNode, out destination))
        {
            allowsTravel = true;
            return true;
        }

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
