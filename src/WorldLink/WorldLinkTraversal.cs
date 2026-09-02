using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MoreSlugcats;
using UnityEngine;
using Watcher;

namespace DryCycle.WorldLink;

internal static class WorldLinkTraversal
{
    private sealed class SessionState
    {
        internal readonly HashSet<WorldLinkPortAddress> Traversed = new();
    }

    private static ConditionalWeakTable<RainWorldGame, SessionState> Sessions = new();

    internal static void ClearSession() => Sessions = new ConditionalWeakTable<RainWorldGame, SessionState>();

    internal static bool HasTraversed(RainWorldGame game, WorldLinkPortAddress address) =>
        game != null && Sessions.TryGetValue(game, out SessionState state) && state.Traversed.Contains(address);

    private static void MarkTraversed(RainWorldGame game, WorldLinkPortAddress address)
    {
        if (game != null && address.IsValid) Sessions.GetOrCreateValue(game).Traversed.Add(address);
    }

    internal static bool BeginCrossRegion(MultiGatePortRuntime source)
    {
        if (source?.room?.game?.overWorld == null || source.Data.TransitMode != WorldLinkTransitMode.CrossRegion)
        {
            return false;
        }

        MultiGatePortData data = source.Data;
        string region = string.IsNullOrWhiteSpace(data.DestinationRegion)
            ? InferRegion(data.DestinationRoom)
            : data.DestinationRegion.Trim();
        if (region.Length == 0 || string.IsNullOrWhiteSpace(data.DestinationRoom) ||
            string.IsNullOrWhiteSpace(data.DestinationGateId) || string.IsNullOrWhiteSpace(data.DestinationPortId))
        {
            Plugin.Logger?.LogError($"WorldLink: incomplete CrossRegion destination for {source.Address}.");
            return false;
        }

        string currentRegion = source.room.world?.region?.name ?? string.Empty;
        if (currentRegion.Length > 0 && Region.EquivalentRegion(currentRegion, region))
        {
            Plugin.Logger?.LogError($"WorldLink: {source.Address} is configured as CrossRegion but targets the current/equivalent region '{region}'. Use VanillaNode for first-version same-region traversal.");
            return false;
        }

        string roomRegion = InferRegion(data.DestinationRoom);
        if (roomRegion.Length > 0 && !Region.EquivalentRegion(roomRegion, region))
        {
            Plugin.Logger?.LogError($"WorldLink: destination room '{data.DestinationRoom}' does not belong to configured region '{region}'. Traversal failed closed.");
            return false;
        }

        WorldLinkPortAddress targetAddress = new(data.DestinationRoom, data.DestinationGateId, data.DestinationPortId);
        if (!TryLoadTargetPort(source.room.game, targetAddress, out PlacedObject targetObject, out MultiGatePortData targetData))
        {
            Plugin.Logger?.LogError($"WorldLink: destination port {targetAddress} could not be found in RoomSettings.");
            return false;
        }
        if (!targetObject.active || !targetData.Enabled)
        {
            Plugin.Logger?.LogError($"WorldLink: destination port {targetAddress} is disabled/inactive; traversal was refused.");
            return false;
        }

        Vector2 outside = targetObject.pos + targetData.Normal * (targetData.PanelThickness * 0.5f + 32f);
        WarpPoint.WarpPointData warp = new(null)
        {
            destRegion = region,
            destRoom = targetAddress.Room,
            destPos = outside,
            accessibility = WarpPoint.WarpPointData.WarpPointSpawnCondition.AnySlugcat,
            oneWay = true,
            oneWayEntrance = false,
            noRing = true,
            darkWarp = false
        };

        try
        {
            source.room.game.overWorld.InitiateSpecialWarp_WarpPoint(
                new Callback(source.room, targetAddress), warp, useNormalWarpLoader: true);
            MarkTraversed(source.room.game, source.Address);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"WorldLink: cross-region traversal {source.Address} -> {targetAddress} failed: {ex}");
            return false;
        }
    }

    private static bool TryLoadTargetPort(RainWorldGame game, WorldLinkPortAddress address, out PlacedObject target, out MultiGatePortData data)
    {
        target = null;
        data = null;
        try
        {
            RoomSettings settings = new(address.Room, null, template: false, firstTemplate: false, game?.TimelinePoint, game);
            for (int i = 0; i < settings.placedObjects.Count; i++)
            {
                PlacedObject po = settings.placedObjects[i];
                if (po?.type != WorldLinkPlacedObjects.PortType || po.data is not MultiGatePortData pd) continue;
                if (!string.Equals(pd.GateId, address.Gate, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(pd.PortId, address.Port, StringComparison.OrdinalIgnoreCase)) continue;

                if (target != null)
                {
                    Plugin.Logger?.LogError($"WorldLink: destination {address} is duplicated in RoomSettings; traversal failed closed.");
                    target = null;
                    data = null;
                    return false;
                }
                target = po;
                data = pd;
            }
            return target != null;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"WorldLink: target RoomSettings read failed for {address}: {ex.Message}");
        }
        return false;
    }

    private static string InferRegion(string room)
    {
        if (string.IsNullOrWhiteSpace(room)) return string.Empty;
        int underscore = room.IndexOf('_');
        return underscore > 0 ? room.Substring(0, underscore) : string.Empty;
    }

    private static void PlaceArrivingPlayers(Room room, MultiGatePortRuntime target)
    {
        if (room?.game?.Players == null || target == null) return;

        List<Player> players = new();
        for (int i = 0; i < room.game.Players.Count; i++)
        {
            if (room.game.Players[i]?.realizedCreature is Player player && player.room == room) players.Add(player);
        }
        if (players.Count == 0) return;

        Vector2 basePosition = target.Placed.pos + target.Data.Normal * (target.Data.PanelThickness * 0.5f + 32f);
        float usableHalfWidth = Mathf.Max(0f, target.Data.PassageWidth * 0.42f - 12f);
        float spacing = players.Count <= 1 ? 0f : Mathf.Min(24f, usableHalfWidth * 2f / (players.Count - 1));
        float first = -spacing * (players.Count - 1) * 0.5f;

        for (int i = 0; i < players.Count; i++)
        {
            Player player = players[i];
            Vector2 spawn = basePosition + target.Data.Tangent * (first + spacing * i);
            player.SuperHardSetPosition(spawn);
            if (player.bodyChunks == null) continue;
            for (int c = 0; c < player.bodyChunks.Length; c++) player.bodyChunks[c].vel = Vector2.zero;
        }
    }

    private sealed class Callback : ISpecialWarp
    {
        private readonly Room _source;
        private readonly WorldLinkPortAddress _target;

        internal Callback(Room source, WorldLinkPortAddress target)
        {
            _source = source;
            _target = target;
        }

        public Room getSourceRoom() => _source;

        [Obsolete("Use room parameter function instead.")]
        public void NewWorldLoaded() { }

        public void NewWorldLoaded(Room newRoom)
        {
            if (newRoom == null)
            {
                Plugin.Logger?.LogError($"WorldLink: warp callback for {_target} received a null room.");
                return;
            }

            MarkTraversed(newRoom.game, _target);
            WorldLinkRoomRegistry.BuildForRoom(newRoom);
            MultiGatePortRuntime targetPort = WorldLinkRoomRegistry.FindPort(newRoom, _target);
            if (targetPort != null)
            {
                PlaceArrivingPlayers(newRoom, targetPort);
            }
            if (!WorldLinkRoomRegistry.RequestInbound(newRoom, _target))
            {
                Plugin.Logger?.LogWarning($"WorldLink: arrived at {_target}, but its controller could not arm inbound traversal.");
            }
        }
    }
}
