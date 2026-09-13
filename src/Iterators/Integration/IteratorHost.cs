using System;
using System.Collections.Generic;
using UnityEngine;

namespace DryCycle.Iterators;

/// <summary>
/// Oracle 实体适配器，不承载剧情/AI。第二阶段为静止、无绘制的物理宿主壳；
/// Body 运动、Arm 和 Graphics 在各自阶段以组合组件接入。
/// </summary>
internal sealed class IteratorHost : Oracle
{
    private IteratorRuntime _runtime;
    private bool _initialized;
    private bool _released;

    private IteratorHost(HostObject abstractObject, Room room) : base(abstractObject, room)
    {
        if (!_initialized)
            throw new InvalidOperationException("IteratorFramework: the Oracle constructor adapter did not initialize its host.");
    }

    internal static IteratorHost Create(IteratorRuntime runtime)
    {
        Room room = runtime.Context.Room;
        Vector2 position = FindSpawnPosition(room);
        var abstractObject = new HostObject(runtime, position);
        try
        {
            return new IteratorHost(abstractObject, room);
        }
        catch
        {
            // A constructor can fail after PhysicalObject already assigned realizedObject.
            if (abstractObject.realizedObject is IteratorHost host) host.Release(room);
            abstractObject.realizedObject = null;
            throw;
        }
    }

    internal static bool InitializeOracle(Oracle oracle, AbstractPhysicalObject abstractObject, Room room)
    {
        if (oracle is not IteratorHost host) return false;
        if (abstractObject is not HostObject marker || marker.Runtime.State != IteratorLifecycle.RuntimeCreated ||
            !ReferenceEquals(marker.Runtime.Context.Room, room) || !ReferenceEquals(abstractObject.realizedObject, host))
            throw new InvalidOperationException("IteratorFramework: invalid or expired Oracle construction request.");
        if (!IteratorRegistry.TryGetOracleID(marker.Runtime.ID, out OracleID gameID))
            throw new InvalidOperationException($"IteratorFramework: Oracle ID '{marker.Runtime.ID}' is no longer registered.");

        host._runtime = marker.Runtime;
        host.room = room;
        host.ID = gameID;
        marker.Runtime.Context.BindOracle(host);
        host.bodyChunks = new[]
        {
            new BodyChunk(host, 0, marker.Position + new Vector2(0f, 4.5f), 6f, 0.5f),
            new BodyChunk(host, 1, marker.Position - new Vector2(0f, 4.5f), 6f, 0.5f)
        };
        host.bodyChunkConnections = new[]
        {
            new BodyChunkConnection(host.bodyChunks[0], host.bodyChunks[1], 9f, BodyChunkConnection.Type.Normal, 1f, 0.5f)
        };
        host.mySwarmers = new List<OracleSwarmer>();
        host.marbles = new List<PebblesPearl>();
        host.airFriction = 0.99f;
        host.gravity = 0f;
        host.bounce = 0.1f;
        host.surfaceFriction = 0.17f;
        host.collisionLayer = 1;
        host.waterFriction = 0.92f;
        host.buoyancy = 0.95f;
        host._initialized = true;
        return true;
    }

    public override void Update(bool eu)
    {
        evenUpdate = eu;
        _runtime?.Tick();
    }

    public override void InitiateGraphicsModule()
    {
        // Phase 4 supplies the graphics component. Never invoke OracleGraphics here:
        // vanilla graphics assumes an OracleArm and one of the vanilla behaviors.
    }

    public override void Destroy()
    {
        if (_runtime != null) _runtime.Destroy(IteratorDestroyReason.OracleRemoved);
        else if (!_released) Release(room);
    }

    internal void RemovedFromRoom() => _runtime?.Destroy(IteratorDestroyReason.OracleRemoved);

    internal void Release(Room owningRoom)
    {
        if (_released) return;
        _released = true;
        IteratorLogger logger = _runtime?.Context.Logger;
        _runtime = null;
        slatedForDeletetion = true;
        Room currentRoom = room;
        try
        {
            base.Destroy();
        }
        catch (Exception exception)
        {
            logger?.Error("Oracle cleanup failed; continuing to detach its bindings.", exception);
        }
        RemoveFrom(currentRoom, logger);
        if (!ReferenceEquals(currentRoom, owningRoom)) RemoveFrom(owningRoom, logger);
        room = null;
        graphicsModule = null;
        arm = null;
        if (abstractPhysicalObject != null)
        {
            if (ReferenceEquals(abstractPhysicalObject.realizedObject, this)) abstractPhysicalObject.realizedObject = null;
            try { abstractPhysicalObject.Destroy(); }
            catch (Exception exception) { logger?.Error("Abstract host cleanup failed.", exception); }
            abstractPhysicalObject.world = null;
        }
    }

    private void RemoveFrom(Room target, IteratorLogger logger)
    {
        if (target == null) return;
        try { target.RemoveObject(this); }
        catch (Exception exception) { logger?.Error("Room removal failed; host is marked for engine cleanup.", exception); }
    }

    private static Vector2 FindSpawnPosition(Room room)
    {
        int bestX = -1, bestY = -1;
        double bestDistance = double.MaxValue;
        double centerX = (room.TileWidth - 1) * 0.5;
        double centerY = (room.TileHeight - 1) * 0.5;
        // One bounded scan per spawn; no RNG consumption or per-frame tile search.
        for (int y = 1; y < room.TileHeight - 1; y++)
        for (int x = 1; x < room.TileWidth - 1; x++)
        {
            if (room.GetTile(x, y).Solid || room.GetTile(x, y - 1).Solid || room.GetTile(x, y + 1).Solid) continue;
            double distance = (x - centerX) * (x - centerX) + (y - centerY) * (y - centerY);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            bestX = x;
            bestY = y;
        }
        if (bestX < 0)
            throw new InvalidOperationException("No clear spawn location exists for the default host in this room.");
        return room.MiddleOfTile(bestX, bestY);
    }

    private sealed class HostObject : AbstractPhysicalObject
    {
        internal readonly IteratorRuntime Runtime;
        internal readonly Vector2 Position;

        internal HostObject(IteratorRuntime runtime, Vector2 position)
            : base(runtime.Context.World, AbstractObjectType.Oracle, null,
                runtime.Context.Room.GetWorldCoordinate(position), runtime.Context.Game.GetNewID())
        {
            Runtime = runtime;
            Position = position;
            destroyOnAbstraction = true;
            // Like vanilla room-spawned Oracles, this realization-only host is not
            // inserted into AbstractRoom.entities or the game's persistent save data.
        }
    }
}
