using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DryCycle.WorldLink;

internal static class WorldLinkRoomRegistry
{
    private sealed class RoomState
    {
        internal readonly List<MultiGateControllerRuntime> Controllers = new();
        internal readonly List<MultiGatePortRuntime> Ports = new();
        internal readonly HashSet<string> LoggedWarnings = new(StringComparer.OrdinalIgnoreCase);
    }

    private static ConditionalWeakTable<Room, RoomState> States = new();
    internal static bool Enabled { get; private set; }

    internal static void SetEnabled(bool enabled) => Enabled = enabled;

    internal static void Clear()
    {
        Enabled = false;
        States = new ConditionalWeakTable<Room, RoomState>();
    }

    internal static void BuildForRoom(Room room)
    {
        if (!Enabled || room?.roomSettings?.placedObjects == null) return;
        RoomState state = States.GetOrCreateValue(room);

        state.Ports.RemoveAll(port => port == null || port.slatedForDeletetion);
        state.Controllers.RemoveAll(controller => controller == null || controller.slatedForDeletetion);

        // Ports are physical objects. Data.Enabled only controls the directed outgoing
        // route, so disabled routes must still instantiate a closed visible/collidable
        // gate. PlacedObject.active, on the other hand, controls physical authoring.
        for (int i = 0; i < room.roomSettings.placedObjects.Count; i++)
        {
            PlacedObject po = room.roomSettings.placedObjects[i];
            if (po == null || !WorldLinkPlacedObjects.IsPortType(po.type)) continue;
            WorldLinkPlacedObjects.EnsureWorldLinkData(po);
            if (!po.active || po.data is not MultiGatePortData portData) continue;

            if (FindRuntime(state.Ports, po) != null) continue;
            try
            {
                var runtime = new MultiGatePortRuntime(room, po, portData);
                state.Ports.Add(runtime);
                room.AddObject(runtime);
            }
            catch (Exception ex)
            {
                LogOnce(state, $"port-create:{i}", $"WorldLink: failed to create MultiGatePort runtime in '{room.abstractRoom?.name}' at index {i}: {ex}", error: true);
            }
        }

        for (int i = 0; i < room.roomSettings.placedObjects.Count; i++)
        {
            PlacedObject po = room.roomSettings.placedObjects[i];
            if (po == null || !WorldLinkPlacedObjects.IsControllerType(po.type)) continue;
            WorldLinkPlacedObjects.EnsureWorldLinkData(po);
            if (!po.active || po.data is not MultiGateControllerData controllerData) continue;

            if (FindRuntime(state.Controllers, po) != null) continue;
            try
            {
                var runtime = new MultiGateControllerRuntime(room, po, controllerData, state.Ports);
                state.Controllers.Add(runtime);
                room.AddObject(runtime);
            }
            catch (Exception ex)
            {
                LogOnce(state, $"controller-create:{i}", $"WorldLink: failed to create MultiGateController runtime in '{room.abstractRoom?.name}' at index {i}: {ex}", error: true);
            }
        }

        ValidateTopology(room, state);
    }

    internal static IReadOnlyList<MultiGatePortRuntime> Ports(Room room)
    {
        return Enabled && room != null && States.TryGetValue(room, out RoomState state)
            ? state.Ports
            : Array.Empty<MultiGatePortRuntime>();
    }

    internal static MultiGatePortRuntime FindPort(Room room, WorldLinkPortAddress address)
    {
        if (room == null || !States.TryGetValue(room, out RoomState state)) return null;
        MultiGatePortRuntime found = null;
        for (int i = 0; i < state.Ports.Count; i++)
        {
            MultiGatePortRuntime port = state.Ports[i];
            if (!IsLiveAuthoredPort(room, port) || !port.Address.Equals(address)) continue;
            if (found != null)
            {
                LogOnce(state, "duplicate-lookup:" + address, $"WorldLink: ambiguous duplicate port address {address}; lookup failed closed.", error: true);
                return null;
            }
            found = port;
        }
        return found;
    }

    internal static bool IsUniquePortAddress(Room room, MultiGatePortRuntime port)
    {
        if (room == null || port == null || !States.TryGetValue(room, out RoomState state) || !IsLiveAuthoredPort(room, port)) return false;
        int matches = 0;
        WorldLinkPortAddress address = port.Address;
        for (int i = 0; i < state.Ports.Count; i++)
        {
            MultiGatePortRuntime candidate = state.Ports[i];
            if (!IsLiveAuthoredPort(room, candidate) || !candidate.Address.Equals(address)) continue;
            matches++;
            if (matches > 1) return false;
        }
        return matches == 1;
    }

    internal static bool IsUniqueVanillaNodeBinding(Room room, MultiGatePortRuntime port)
    {
        if (room == null || port == null || port.Data.TransitMode != WorldLinkTransitMode.VanillaNode ||
            port.Data.VanillaNodeIndex < 0 || !States.TryGetValue(room, out RoomState state) || !IsLiveAuthoredPort(room, port))
        {
            return false;
        }

        int matches = 0;
        for (int i = 0; i < state.Ports.Count; i++)
        {
            MultiGatePortRuntime candidate = state.Ports[i];
            if (!IsLiveAuthoredPort(room, candidate) || candidate.Data.TransitMode != WorldLinkTransitMode.VanillaNode ||
                candidate.Data.VanillaNodeIndex != port.Data.VanillaNodeIndex)
            {
                continue;
            }
            matches++;
            if (matches > 1) return false;
        }
        return matches == 1;
    }

    internal static bool RequestInbound(Room room, WorldLinkPortAddress address)
    {
        if (room == null || !States.TryGetValue(room, out RoomState state)) return false;
        MultiGatePortRuntime target = FindPort(room, address);
        if (target == null || !IsUniquePortAddress(room, target)) return false;

        MultiGateControllerRuntime controller = PrimaryControllerFor(room, state, target.Data.GateId);
        return controller != null && controller.BeginInbound(target);
    }

    internal static bool RequestInboundFromVanillaNode(Room room, int nodeIndex)
    {
        if (room == null || nodeIndex < 0 || !States.TryGetValue(room, out RoomState state)) return false;

        MultiGatePortRuntime target = null;
        for (int i = 0; i < state.Ports.Count; i++)
        {
            MultiGatePortRuntime candidate = state.Ports[i];
            if (!IsLiveAuthoredPort(room, candidate) ||
                candidate.Data.TransitMode != WorldLinkTransitMode.VanillaNode ||
                candidate.Data.VanillaNodeIndex != nodeIndex)
            {
                continue;
            }

            if (!IsUniquePortAddress(room, candidate))
            {
                LogOnce(state, $"vanilla-inbound-duplicate:{nodeIndex}",
                    $"WorldLink: VanillaNode {nodeIndex} in room '{room.abstractRoom?.name}' maps to a duplicate port address; inbound failed closed.", error: true);
                return false;
            }

            if (target != null && !ReferenceEquals(target, candidate))
            {
                LogOnce(state, $"vanilla-inbound-ambiguous:{nodeIndex}",
                    $"WorldLink: VanillaNode {nodeIndex} in room '{room.abstractRoom?.name}' is assigned to multiple MultiGatePorts. Inbound failed closed.", error: true);
                return false;
            }
            target = candidate;
        }

        if (target == null || !IsUniqueVanillaNodeBinding(room, target)) return false;
        MultiGateControllerRuntime controller = PrimaryControllerFor(room, state, target.Data.GateId);
        return controller != null && controller.BeginInbound(target);
    }

    internal static bool IsPrimaryController(Room room, MultiGateControllerRuntime controller)
    {
        if (room == null || controller == null || !States.TryGetValue(room, out RoomState state)) return false;
        return ReferenceEquals(PrimaryControllerFor(room, state, controller.Data.GateId), controller);
    }

    internal static bool HasActiveControllerFor(Room room, string gateId)
    {
        return room != null && States.TryGetValue(room, out RoomState state) && PrimaryControllerFor(room, state, gateId) != null;
    }

    private static MultiGateControllerRuntime PrimaryControllerFor(Room room, RoomState state, string gateId)
    {
        MultiGateControllerRuntime found = null;
        for (int i = 0; i < state.Controllers.Count; i++)
        {
            MultiGateControllerRuntime candidate = state.Controllers[i];
            if (candidate == null || candidate.slatedForDeletetion || candidate.Placed?.active != true) continue;
            if (room.roomSettings?.placedObjects == null || !room.roomSettings.placedObjects.Contains(candidate.Placed)) continue;
            if (!string.Equals(candidate.Data.GateId, gateId, StringComparison.OrdinalIgnoreCase)) continue;

            if (found != null)
            {
                LogOnce(state, "duplicate-controller:" + gateId,
                    $"WorldLink: room '{room.abstractRoom?.name}' has multiple active controllers with GateID '{gateId}'. The entire GateID is fail-closed until exactly one controller remains.", error: true);
                return null;
            }
            found = candidate;
        }
        return found;
    }

    private static bool IsLiveAuthoredPort(Room room, MultiGatePortRuntime port)
    {
        return port != null && !port.slatedForDeletetion && port.Placed?.active == true &&
               room?.roomSettings?.placedObjects != null && room.roomSettings.placedObjects.Contains(port.Placed);
    }

    private static void ValidateTopology(Room room, RoomState state)
    {
        for (int i = 0; i < state.Ports.Count; i++)
        {
            MultiGatePortRuntime a = state.Ports[i];
            if (!IsLiveAuthoredPort(room, a)) continue;

            for (int j = i + 1; j < state.Ports.Count; j++)
            {
                MultiGatePortRuntime b = state.Ports[j];
                if (!IsLiveAuthoredPort(room, b)) continue;
                if (a.Address.Equals(b.Address))
                {
                    LogOnce(state, "duplicate-port:" + a.Address,
                        $"WorldLink: duplicate port address {a.Address} in room '{room.abstractRoom?.name}'. Both directed routes are fail-closed until GateID/PortID is unique.", error: true);
                }
                if (a.Data.TransitMode == WorldLinkTransitMode.VanillaNode && b.Data.TransitMode == WorldLinkTransitMode.VanillaNode &&
                    a.Data.VanillaNodeIndex >= 0 && a.Data.VanillaNodeIndex == b.Data.VanillaNodeIndex)
                {
                    LogOnce(state, "duplicate-node:" + a.Data.VanillaNodeIndex,
                        $"WorldLink: VanillaNode {a.Data.VanillaNodeIndex} in room '{room.abstractRoom?.name}' is bound to multiple MultiGatePorts. Those routes are fail-closed because inbound cannot be resolved uniquely.", error: true);
                }
            }

            if (PrimaryControllerFor(room, state, a.Data.GateId) == null)
            {
                LogOnce(state, "missing-or-ambiguous-controller:" + a.Address,
                    $"WorldLink: {a.Address} has no unique active matching MultiGateController. The physical gate remains fail-closed.");
            }
        }

        for (int i = 0; i < state.Controllers.Count; i++)
        {
            MultiGateControllerRuntime a = state.Controllers[i];
            if (a == null || a.slatedForDeletetion || a.Placed?.active != true) continue;
            for (int j = i + 1; j < state.Controllers.Count; j++)
            {
                MultiGateControllerRuntime b = state.Controllers[j];
                if (b == null || b.slatedForDeletetion || b.Placed?.active != true) continue;
                if (string.Equals(a.Data.GateId, b.Data.GateId, StringComparison.OrdinalIgnoreCase))
                {
                    LogOnce(state, "duplicate-controller:" + a.Data.GateId,
                        $"WorldLink: room '{room.abstractRoom?.name}' has multiple active controllers with GateID '{a.Data.GateId}'. The entire GateID is fail-closed until exactly one controller remains.", error: true);
                }
            }
        }
    }

    private static MultiGatePortRuntime FindRuntime(List<MultiGatePortRuntime> list, PlacedObject placed)
    {
        for (int i = 0; i < list.Count; i++) if (list[i]?.Placed == placed) return list[i];
        return null;
    }

    private static MultiGateControllerRuntime FindRuntime(List<MultiGateControllerRuntime> list, PlacedObject placed)
    {
        for (int i = 0; i < list.Count; i++) if (list[i]?.Placed == placed) return list[i];
        return null;
    }

    private static void LogOnce(RoomState state, string key, string message, bool error = false)
    {
        if (!state.LoggedWarnings.Add(key)) return;
        if (error) Plugin.Logger?.LogError(message);
        else Plugin.Logger?.LogWarning(message);
    }
}
