using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RWCustom;
using UnityEngine;

namespace DryCycle.WorldLink;

internal static class WorldLinkRoomRegistry
{
    private sealed class RoomState
    {
        internal readonly List<MultiGateControllerRuntime> Controllers = new();
        internal readonly List<MultiGatePortRuntime> Ports = new();
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
        for (int i = 0; i < room.roomSettings.placedObjects.Count; i++)
        {
            PlacedObject po = room.roomSettings.placedObjects[i];
            if (po == null || !po.active) continue;
            if (po.type == WorldLinkPlacedObjects.PortType && po.data is MultiGatePortData portData)
            {
                bool exists = false;
                for (int k = 0; k < state.Ports.Count; k++) if (state.Ports[k].Placed == po) { exists = true; break; }
                if (!exists)
                {
                    var runtime = new MultiGatePortRuntime(room, po, portData);
                    state.Ports.Add(runtime);
                    room.AddObject(runtime);
                }
            }
        }

        for (int i = 0; i < room.roomSettings.placedObjects.Count; i++)
        {
            PlacedObject po = room.roomSettings.placedObjects[i];
            if (po == null || !po.active) continue;
            if (po.type == WorldLinkPlacedObjects.ControllerType && po.data is MultiGateControllerData controllerData)
            {
                bool exists = false;
                for (int k = 0; k < state.Controllers.Count; k++) if (state.Controllers[k].Placed == po) { exists = true; break; }
                if (!exists)
                {
                    var runtime = new MultiGateControllerRuntime(room, po, controllerData, state.Ports);
                    state.Controllers.Add(runtime);
                    room.AddObject(runtime);
                }
            }
        }

        for (int i = 0; i < state.Ports.Count; i++)
        {
            MultiGatePortRuntime a = state.Ports[i];
            for (int j = i + 1; j < state.Ports.Count; j++)
            {
                MultiGatePortRuntime b = state.Ports[j];
                if (a.Address.Equals(b.Address))
                {
                    Plugin.Logger?.LogError($"WorldLink: duplicate port address {a.Address} in room '{room.abstractRoom?.name}'. Directed routes require unique GateID/PortID pairs.");
                }
            }
        }

        // Ports without a matching controller deliberately stay visible/collidable closed.
        for (int i = 0; i < state.Ports.Count; i++)
        {
            if (!HasControllerFor(state, state.Ports[i].Data.GateId))
            {
                Plugin.Logger?.LogWarning($"WorldLink: {room.abstractRoom?.name}/{state.Ports[i].Data.GateId}/{state.Ports[i].Data.PortId} has no matching MultiGateController.");
            }
        }
    }

    internal static IReadOnlyList<MultiGatePortRuntime> Ports(Room room)
    {
        return Enabled && room != null && States.TryGetValue(room, out RoomState state) ? state.Ports : Array.Empty<MultiGatePortRuntime>();
    }

    internal static MultiGatePortRuntime FindPort(Room room, WorldLinkPortAddress address)
    {
        if (room == null || !States.TryGetValue(room, out RoomState state)) return null;
        MultiGatePortRuntime found = null;
        for (int i = 0; i < state.Ports.Count; i++)
        {
            MultiGatePortRuntime port = state.Ports[i];
            if (!port.Address.Equals(address)) continue;
            if (found != null)
            {
                Plugin.Logger?.LogError($"WorldLink: ambiguous duplicate port address {address}; lookup failed closed.");
                return null;
            }
            found = port;
        }
        return found;
    }

    internal static bool RequestInbound(Room room, WorldLinkPortAddress address)
    {
        if (room == null || !States.TryGetValue(room, out RoomState state)) return false;
        MultiGatePortRuntime target = FindPort(room, address);
        if (target == null) return false;
        for (int i = 0; i < state.Controllers.Count; i++)
        {
            if (string.Equals(state.Controllers[i].Data.GateId, target.Data.GateId, StringComparison.OrdinalIgnoreCase))
            {
                return state.Controllers[i].BeginInbound(target);
            }
        }
        return false;
    }


    internal static bool IsPrimaryController(Room room, MultiGateControllerRuntime controller)
    {
        if (room == null || controller == null || !States.TryGetValue(room, out RoomState state)) return false;
        for (int i = 0; i < state.Controllers.Count; i++)
        {
            MultiGateControllerRuntime candidate = state.Controllers[i];
            if (candidate == null || candidate.slatedForDeletetion) continue;
            if (string.Equals(candidate.Data.GateId, controller.Data.GateId, StringComparison.OrdinalIgnoreCase))
            {
                return ReferenceEquals(candidate, controller);
            }
        }
        return false;
    }

    private static bool HasControllerFor(RoomState state, string gateId)
    {
        for (int i = 0; i < state.Controllers.Count; i++)
        {
            if (string.Equals(state.Controllers[i].Data.GateId, gateId, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
