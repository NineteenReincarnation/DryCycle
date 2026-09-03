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
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled) return;
        _enabled = true;
        On.Player.SpitOutOfShortCut += PlayerSpitOutOfShortCut;
    }

    internal static void Disable()
    {
        if (_enabled)
        {
            On.Player.SpitOutOfShortCut -= PlayerSpitOutOfShortCut;
            _enabled = false;
        }
        ClearSession();
    }

    internal static void ClearSession() => Sessions = new ConditionalWeakTable<RainWorldGame, SessionState>();

    internal static bool HasTraversed(RainWorldGame game, WorldLinkPortAddress address)
    {
        if (game == null || !address.IsValid) return false;
        if (Sessions.TryGetValue(game, out SessionState state) && state.Traversed.Contains(address)) return true;
        if (!game.IsStorySession) return false;

        List<string> unlocked = game.GetStorySession.saveState.deathPersistentSaveData.unlockedGates;
        if (unlocked == null) return false;
        for (int i = 0; i < unlocked.Count; i++)
        {
            if (string.Equals(unlocked[i], address.TraversalSaveKey, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static void MarkTraversed(RainWorldGame game, WorldLinkPortAddress address)
    {
        if (game == null || !address.IsValid) return;
        Sessions.GetOrCreateValue(game).Traversed.Add(address);

        // Destination discovery is separate from gate unlocking. Persist a dedicated
        // directed traversal marker only after an actual world handoff completes.
        if (!game.IsStorySession) return;
        DeathPersistentSaveData data = game.GetStorySession.saveState.deathPersistentSaveData;
        data.unlockedGates ??= new List<string>();
        for (int i = 0; i < data.unlockedGates.Count; i++)
        {
            if (string.Equals(data.unlockedGates[i], address.TraversalSaveKey, StringComparison.OrdinalIgnoreCase)) return;
        }
        data.unlockedGates.Add(address.TraversalSaveKey);
    }

    private static void PlayerSpitOutOfShortCut(
        On.Player.orig_SpitOutOfShortCut orig,
        Player self,
        IntVector2 pos,
        Room newRoom,
        bool spitOutAllSticks)
    {
        orig(self, pos, newRoom, spitOutAllSticks);
        if (!_enabled || newRoom?.roomSettings?.placedObjects == null) return;

        try
        {
            ShortcutData shortcut = newRoom.shortcutData(pos);
            if (shortcut.shortCutType != ShortcutData.Type.RoomExit || shortcut.destNode < 0) return;

            // Same-region inbound is authorized only by a real RoomExit arrival at the
            // exact vanilla node assigned to the port. Merely walking around to the
            // back side of a gate in the same room never bypasses its directed route.
            WorldLinkRoomRegistry.BuildForRoom(newRoom);
            WorldLinkRoomRegistry.RequestInboundFromVanillaNode(newRoom, shortcut.destNode);
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"WorldLink: VanillaNode inbound detection failed in room '{newRoom?.abstractRoom?.name}': {ex.Message}");
        }
    }

    internal static bool HasBasicCrossRegionConfiguration(MultiGatePortRuntime source)
    {
        if (source?.room == null || source.Data.TransitMode != WorldLinkTransitMode.CrossRegion) return false;
        MultiGatePortData data = source.Data;
        if (string.IsNullOrWhiteSpace(data.DestinationRoom) ||
            string.IsNullOrWhiteSpace(data.DestinationGateId) ||
            string.IsNullOrWhiteSpace(data.DestinationPortId))
        {
            return false;
        }

        string region = string.IsNullOrWhiteSpace(data.DestinationRegion)
            ? InferRegion(data.DestinationRoom)
            : data.DestinationRegion.Trim();
        if (region.Length == 0) return false;

        string currentRegion = source.room.world?.region?.name ?? string.Empty;
        if (currentRegion.Length > 0 && Region.EquivalentRegion(currentRegion, region)) return false;

        string roomRegion = InferRegion(data.DestinationRoom);
        return roomRegion.Length == 0 || Region.EquivalentRegion(roomRegion, region);
    }

    internal static bool CanResolveCrossRegionDestination(MultiGatePortRuntime source)
    {
        if (!HasBasicCrossRegionConfiguration(source) || source?.room?.game == null) return false;
        WorldLinkPortAddress targetAddress = source.Data.DestinationAddress;
        if (!targetAddress.IsValid) return false;

        bool ok = TryLoadTargetEndpoint(source.room.game, targetAddress, out _, out _);
        if (!ok)
        {
            Plugin.Logger?.LogError($"WorldLink: cross-region route {source.Address} cannot resolve a unique active destination endpoint {targetAddress}. The route remains locked.");
        }
        return ok;
    }

    internal static bool BeginCrossRegion(MultiGatePortRuntime source)
    {
        if (source?.room?.game?.overWorld == null || source.Data.TransitMode != WorldLinkTransitMode.CrossRegion ||
            !source.Data.Enabled || source.Placed?.active != true || !WorldLinkRoomRegistry.IsUniquePortAddress(source.room, source))
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
        if (!TryLoadTargetEndpoint(source.room.game, targetAddress, out PlacedObject targetObject, out MultiGatePortData targetData))
        {
            Plugin.Logger?.LogError($"WorldLink: destination endpoint {targetAddress} is incomplete, duplicated, or has no unique active matching controller.");
            return false;
        }

        // Directed routes are asymmetric. The target port's outgoing Data.Enabled flag
        // must not reject inbound travel. Only the target physical object itself must be
        // authored active; its unique matching controller was preflighted above.
        if (!targetObject.active)
        {
            Plugin.Logger?.LogError($"WorldLink: destination port {targetAddress} is physically inactive; traversal was refused.");
            return false;
        }

        Vector2 outside = targetObject.pos + targetData.Normal * (targetData.PanelThickness * 0.5f + 32f);
        Vector2 emergencyInside = targetObject.pos - targetData.Normal * (targetData.PanelThickness * 0.5f + 48f);
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
                new Callback(
                    source.room,
                    source.Address,
                    targetAddress,
                    emergencyInside,
                    targetData.Tangent,
                    targetData.PassageWidth),
                warp,
                useNormalWarpLoader: true);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"WorldLink: cross-region traversal {source.Address} -> {targetAddress} failed: {ex}");
            return false;
        }
    }

    private static bool TryLoadTargetEndpoint(
        RainWorldGame game,
        WorldLinkPortAddress address,
        out PlacedObject target,
        out MultiGatePortData data)
    {
        target = null;
        data = null;
        try
        {
            RoomSettings settings = new(address.Room, null, template: false, firstTemplate: false, game?.TimelinePoint, game);
            for (int i = 0; i < settings.placedObjects.Count; i++)
            {
                PlacedObject po = settings.placedObjects[i];
                if (po == null || !WorldLinkPlacedObjects.IsPortType(po.type)) continue;
                WorldLinkPlacedObjects.EnsureWorldLinkData(po);
                if (po.data is not MultiGatePortData pd) continue;
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

            if (target == null || !target.active || data == null) return false;

            int activeControllers = 0;
            for (int i = 0; i < settings.placedObjects.Count; i++)
            {
                PlacedObject po = settings.placedObjects[i];
                if (po == null || !po.active || !WorldLinkPlacedObjects.IsControllerType(po.type)) continue;
                WorldLinkPlacedObjects.EnsureWorldLinkData(po);
                if (po.data is not MultiGateControllerData controllerData) continue;
                if (!string.Equals(controllerData.GateId, data.GateId, StringComparison.OrdinalIgnoreCase)) continue;
                activeControllers++;
                if (activeControllers > 1)
                {
                    Plugin.Logger?.LogError($"WorldLink: destination {address} has multiple active controllers for GateID '{data.GateId}'; traversal failed closed.");
                    return false;
                }
            }

            if (activeControllers != 1)
            {
                Plugin.Logger?.LogError($"WorldLink: destination {address} has no active matching controller for GateID '{data.GateId}'; traversal failed closed.");
                return false;
            }

            return true;
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
        if (target == null) return;
        Vector2 basePosition = target.Placed.pos + target.Data.Normal * (target.Data.PanelThickness * 0.5f + 32f);
        PlaceArrivingPlayers(room, basePosition, target.Data.Tangent, target.Data.PassageWidth);
    }

    private static void PlaceArrivingPlayers(Room room, Vector2 basePosition, Vector2 tangent, float passageWidth)
    {
        if (room?.game?.Players == null) return;

        List<Player> players = new();
        for (int i = 0; i < room.game.Players.Count; i++)
        {
            if (room.game.Players[i]?.realizedCreature is Player player && player.room == room) players.Add(player);
        }
        if (players.Count == 0) return;

        float usableHalfWidth = Mathf.Max(0f, passageWidth * 0.42f - 12f);
        float spacing = players.Count <= 1 ? 0f : Mathf.Min(24f, usableHalfWidth * 2f / (players.Count - 1));
        float first = -spacing * (players.Count - 1) * 0.5f;

        for (int i = 0; i < players.Count; i++)
        {
            Player player = players[i];
            Vector2 spawn = basePosition + tangent * (first + spacing * i);
            player.SuperHardSetPosition(spawn);
            if (player.bodyChunks == null) continue;
            for (int c = 0; c < player.bodyChunks.Length; c++) player.bodyChunks[c].vel = Vector2.zero;
        }
    }

    private sealed class Callback : ISpecialWarp
    {
        private readonly Room _source;
        private readonly WorldLinkPortAddress _sourceAddress;
        private readonly WorldLinkPortAddress _target;
        private readonly Vector2 _emergencyInside;
        private readonly Vector2 _targetTangent;
        private readonly float _targetPassageWidth;

        internal Callback(
            Room source,
            WorldLinkPortAddress sourceAddress,
            WorldLinkPortAddress target,
            Vector2 emergencyInside,
            Vector2 targetTangent,
            float targetPassageWidth)
        {
            _source = source;
            _sourceAddress = sourceAddress;
            _target = target;
            _emergencyInside = emergencyInside;
            _targetTangent = targetTangent;
            _targetPassageWidth = targetPassageWidth;
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

            // A hidden destination is revealed only after this directed source route
            // actually completed a world handoff. Arriving at the target does not mark
            // the target's independent outgoing route as traversed.
            MarkTraversed(newRoom.game, _sourceAddress);
            WorldLinkRoomRegistry.BuildForRoom(newRoom);
            MultiGatePortRuntime targetPort = WorldLinkRoomRegistry.FindPort(newRoom, _target);
            if (targetPort != null) PlaceArrivingPlayers(newRoom, targetPort);

            if (targetPort == null || !WorldLinkRoomRegistry.RequestInbound(newRoom, _target))
            {
                // The world handoff is already committed, so fail-closed at this point
                // would strand players outside a physical gate with no recovery path.
                // Use the preflighted target transform to complete the directed transfer
                // safely on the interior side, and surface the runtime failure loudly.
                PlaceArrivingPlayers(newRoom, _emergencyInside, _targetTangent, _targetPassageWidth);
                Plugin.Logger?.LogError($"WorldLink: arrived at {_target}, but its runtime/controller could not arm inbound traversal. Players were moved to the preflighted interior fail-safe position to prevent a softlock.");
            }
        }
    }
}
