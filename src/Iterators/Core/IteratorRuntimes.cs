using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DryCycle.Iterators;

/// <summary>运行实例的查询与生成入口；与保存静态定义的 IteratorRegistry 分离。</summary>
public static class IteratorRuntimes
{
    private static ConditionalWeakTable<Room, RoomSlot> _rooms = new();
    private static ConditionalWeakTable<Oracle, IteratorRuntime> _oracles = new();
    private static ConditionalWeakTable<RainWorldGame, ClosedSession> _closedGames = new();
    private static readonly List<IteratorRuntime> Live = new();
    private static bool _enabled;
    private static int _generation;

    public static bool IsEnabled => _enabled;

    /// <summary>当前 Active 实例的只读快照。仅在显式查询时分配，不在 Update 中枚举全局对象。</summary>
    public static IReadOnlyList<IteratorRuntime> Active
    {
        get
        {
            var result = new List<IteratorRuntime>();
            foreach (IteratorRuntime runtime in Live)
                if (runtime.IsActive) result.Add(runtime);
            return result.AsReadOnly();
        }
    }

    /// <summary>按具体 Room 对象查询，允许初始化中的实例；不同 Session 同名房间不会相互覆盖。</summary>
    public static bool TryGet(Room room, out IteratorRuntime runtime)
    {
        runtime = null;
        if (room == null || !_rooms.TryGetValue(room, out RoomSlot slot) || !IsLive(slot.Runtime))
            return false;
        runtime = slot.Runtime;
        return true;
    }

    /// <summary>只有由框架创建和绑定的 Oracle 才能查询到实例，不按游戏 ID 猜测所有权。</summary>
    public static bool TryGet(Oracle oracle, out IteratorRuntime runtime)
    {
        runtime = null;
        if (oracle == null || !_oracles.TryGetValue(oracle, out IteratorRuntime found) || !IsLive(found))
            return false;
        runtime = found;
        return true;
    }

    /// <summary>显式请求在已完全加载且已绑定定义的房间生成。已有 Active 实例时返回同一实例；失败返回 false/null 并记录原因。</summary>
    public static bool TrySpawn(Room room, out IteratorRuntime runtime) => TrySpawn(room, false, out runtime);

    internal static void Enable()
    {
        if (_enabled) return;
        _generation++;
        _enabled = true;
    }

    internal static void Disable()
    {
        _enabled = false;
        _generation++;
        foreach (IteratorRuntime runtime in Live.ToArray())
            runtime.Destroy(IteratorDestroyReason.FrameworkDisabled);
        _rooms = new ConditionalWeakTable<Room, RoomSlot>();
        _oracles = new ConditionalWeakTable<Oracle, IteratorRuntime>();
        _closedGames = new ConditionalWeakTable<RainWorldGame, ClosedSession>();
    }

    internal static void RoomReady(Room room) => TrySpawn(room, true, out _);

    internal static void RoomUnloaded(Room room)
    {
        if (room == null) return;
        RoomSlot slot = _rooms.GetValue(room, _ => new RoomSlot());
        slot.Closed = true;
        slot.Runtime?.Destroy(IteratorDestroyReason.RoomUnloaded);
    }

    internal static void SessionEnded(RainWorldGame game)
    {
        if (game == null) return;
        _closedGames.GetValue(game, _ => new ClosedSession());
        foreach (IteratorRuntime runtime in Live.ToArray())
            if (ReferenceEquals(runtime.Context.Game, game)) runtime.Destroy(IteratorDestroyReason.SessionEnded);
    }

    internal static void DestroyForDescriptor(IteratorDescriptor descriptor)
    {
        foreach (IteratorRuntime runtime in Live.ToArray())
            if (ReferenceEquals(runtime.Descriptor, descriptor)) runtime.Destroy(IteratorDestroyReason.DefinitionUnregistered);
    }

    private static bool TrySpawn(Room room, bool automatic, out IteratorRuntime result)
    {
        result = null;
        if (!_enabled || room?.game?.session == null || room.world == null || !room.fullyLoaded ||
            !ReferenceEquals(room.world.game, room.game) || !ReferenceEquals(room.abstractRoom?.world, room.world) ||
            !ReferenceEquals(room.abstractRoom?.realizedRoom, room) ||
            _closedGames.TryGetValue(room.game, out _) ||
            !IteratorRegistry.TryGetByRoom(room.abstractRoom.name, out IteratorDescriptor descriptor) ||
            IteratorRegistry.IsRetiring(descriptor))
            return false;

        RoomSlot slot = _rooms.GetValue(room, _ => new RoomSlot());
        if (slot.Closed || slot.Spawning) return false;
        if (slot.Runtime != null)
        {
            if (slot.Runtime.IsActive)
            {
                result = slot.Runtime;
                return true;
            }
            return false;
        }
        if (automatic && ReferenceEquals(slot.AutomaticAttempt, descriptor)) return false;

        slot.Spawning = true;
        slot.AutomaticAttempt = descriptor;
        int generation = _generation;
        var context = new IteratorContext(descriptor, room);
        var logger = new IteratorLogger(descriptor.ID).ForModule("Spawn").ForPhase("SpawnRequested");
        IteratorRuntime runtime = null;
        try
        {
            logger.Info($"Spawn requested in room '{room.abstractRoom.name}'.");
            context.Logger = logger.ForPhase("RuntimeFactory");
            IteratorRuntime candidate = descriptor.RuntimeFactory(context);
            if (candidate == null || !ReferenceEquals(candidate.Context, context) ||
                candidate.State != IteratorLifecycle.RuntimeCreated || context.Runtime != null)
                throw new InvalidOperationException("RuntimeFactory must return a new Runtime built with the supplied Context; it returned null, a reused instance, or a foreign Context.");

            runtime = candidate;
            context.BindRuntime(runtime);
            runtime.SetRelease(Release);
            slot.Runtime = runtime;
            Live.Add(runtime);
            if (!_enabled || generation != _generation || slot.Closed || _closedGames.TryGetValue(room.game, out _))
            {
                runtime.Destroy(!_enabled || generation != _generation ? IteratorDestroyReason.FrameworkDisabled :
                    slot.Closed ? IteratorDestroyReason.RoomUnloaded : IteratorDestroyReason.SessionEnded);
                return false;
            }
            if (!IteratorRegistry.TryGet(descriptor.ID, out IteratorDescriptor current) || !ReferenceEquals(current, descriptor) || IteratorRegistry.IsRetiring(descriptor))
            {
                runtime.Destroy(IteratorDestroyReason.DefinitionUnregistered);
                return false;
            }

            IteratorHost host = IteratorHost.Create(runtime);
            if (runtime.State != IteratorLifecycle.RuntimeCreated) return false;
            _oracles.Add(host, runtime);
            room.AddObject(host);
            if (!ReferenceEquals(host.room, room) || !room.updateList.Contains(host))
                throw new InvalidOperationException("Room.AddObject did not retain the framework host in its owning room.");
            if (!runtime.Initialize()) return false;
            result = runtime;
            return true;
        }
        catch (Exception exception)
        {
            logger.Error($"Could not spawn in room '{room.abstractRoom.name}'.", exception);
            runtime?.Destroy(IteratorDestroyReason.InitializationFailed);
            return false;
        }
        finally
        {
            slot.Spawning = false;
            if (runtime == null) context.ReleaseGameReferences();
        }
    }

    private static void Release(IteratorRuntime runtime)
    {
        Room room = runtime.Context.Room;
        Oracle oracle = runtime.Context.Oracle;
        try
        {
            if (oracle is IteratorHost host) host.Release(room);
        }
        finally
        {
            if (oracle != null) _oracles.Remove(oracle);
            if (room != null && _rooms.TryGetValue(room, out RoomSlot slot) && ReferenceEquals(slot.Runtime, runtime))
                slot.Runtime = null;
            Live.Remove(runtime);
        }
    }

    private static bool IsLive(IteratorRuntime runtime) => runtime != null &&
        runtime.State != IteratorLifecycle.Destroying && runtime.State != IteratorLifecycle.Destroyed;

    private sealed class RoomSlot
    {
        internal bool Spawning;
        internal bool Closed;
        internal IteratorDescriptor AutomaticAttempt;
        internal IteratorRuntime Runtime;
    }

    private sealed class ClosedSession { }
}
