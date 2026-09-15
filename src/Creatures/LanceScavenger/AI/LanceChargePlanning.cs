using System.Collections.Generic;
using UnityEngine;

namespace DryCycle.Creatures.LanceScavenger;

/// <summary>
/// Small per-lancer cache for the expensive part of charge planning. The cached lane intentionally
/// contains only static geometry; live friendly occupancy is reapplied every AI tick by
/// ChargeLanePlanner.ApplyDynamicSafety(). This lets ordinary movement/fear/close-defense remain
/// fully reactive without rebuilding a ballistic plan forty times per second.
/// </summary>
internal sealed class LanceChargePlanCache
{
    private const int BraceRefreshTicks = 2;
    private const int AcquireRefreshTicks = 5;
    private const int TravelRefreshTicks = 7;
    private const int MaximumStaleTicks = 12;
    private const float OwnerMoveThreshold = 14f;
    private const float TargetMoveThreshold = 16f;
    private const float VelocityChangeThreshold = 3.0f;
    private const float StaleMoveThreshold = 38f;

    private Creature _target;
    private LanceAimSolution _aim;
    private ChargeLane _staticLane;
    private Vector2 _ownerPosition;
    private Vector2 _targetPosition;
    private Vector2 _targetVelocity;
    private int _builtAtTick;
    private bool _hasPlan;
    private bool _dirty;

    internal bool HasPlan => _hasPlan;
    internal Creature Target => _target;
    internal LanceAimSolution Aim => _aim;
    internal ChargeLane StaticLane => _staticLane;

    internal void Reset()
    {
        _target = null;
        _aim = default;
        _staticLane = default;
        _ownerPosition = Vector2.zero;
        _targetPosition = Vector2.zero;
        _targetVelocity = Vector2.zero;
        _builtAtTick = 0;
        _hasPlan = false;
        _dirty = false;
    }

    internal void MarkDirty()
    {
        if (_hasPlan) _dirty = true;
    }

    internal void Store(LanceScavenger owner, Creature target, TargetMotionTracker motion,
        LanceAimSolution aim, ChargeLane staticLane, int tick)
    {
        _target = target;
        _aim = aim;
        _staticLane = staticLane;
        _ownerPosition = owner?.mainBodyChunk?.pos ?? Vector2.zero;
        _targetPosition = target?.mainBodyChunk?.pos ?? Vector2.zero;
        _targetVelocity = target?.mainBodyChunk == null
            ? Vector2.zero
            : motion?.SmoothedVelocity(target.mainBodyChunk) ?? target.mainBodyChunk.vel;
        _builtAtTick = tick;
        _hasPlan = target != null;
        _dirty = false;
    }

    internal bool CanReuse(LanceScavenger owner, Creature target, TargetMotionTracker motion,
        LanceState state, int tick)
    {
        if (!_hasPlan || _dirty || target == null || target != _target || owner?.mainBodyChunk == null ||
            target.mainBodyChunk == null)
            return false;

        int age = Mathf.Max(0, tick - _builtAtTick);
        int maximumAge = state == LanceState.Brace ? BraceRefreshTicks :
            state == LanceState.AcquireChargeLane || state == LanceState.CreateDistance
                ? AcquireRefreshTicks
                : TravelRefreshTicks;
        if (age > maximumAge) return false;
        if ((owner.mainBodyChunk.pos - _ownerPosition).sqrMagnitude > OwnerMoveThreshold * OwnerMoveThreshold)
            return false;
        if ((target.mainBodyChunk.pos - _targetPosition).sqrMagnitude > TargetMoveThreshold * TargetMoveThreshold)
            return false;

        Vector2 velocity = motion?.SmoothedVelocity(target.mainBodyChunk) ?? target.mainBodyChunk.vel;
        if ((velocity - _targetVelocity).sqrMagnitude > VelocityChangeThreshold * VelocityChangeThreshold)
            return false;
        if (motion != null && motion.DodgeSeverity > 0.72f && age > 0)
            return false;
        return true;
    }

    /// <summary>
    /// If the room-wide planner budget is exhausted, a very young plan can bridge a few ticks.
    /// The bounds are deliberately much looser than CanReuse but still reject target switches and
    /// large displacements, so budget smoothing never turns into blindly charging an old position.
    /// </summary>
    internal bool CanUseStale(LanceScavenger owner, Creature target, int tick)
    {
        if (!_hasPlan || target == null || target != _target || owner?.mainBodyChunk == null ||
            target.mainBodyChunk == null)
            return false;
        int age = Mathf.Max(0, tick - _builtAtTick);
        if (age > MaximumStaleTicks) return false;
        if ((owner.mainBodyChunk.pos - _ownerPosition).sqrMagnitude > StaleMoveThreshold * StaleMoveThreshold)
            return false;
        return (target.mainBodyChunk.pos - _targetPosition).sqrMagnitude <=
            StaleMoveThreshold * StaleMoveThreshold;
    }
}

/// <summary>
/// Hard cap on full ballistic replans per room per simulation tick. Cheap close-defense and vanilla
/// scavenger AI are not scheduled here; only the expensive charge-plan rebuild uses this budget.
/// Release-frame safety validation bypasses the scheduler because it is a one-off correctness check.
/// </summary>
internal static class LancePlanningScheduler
{
    private const int FullPlansPerRoomPerTick = 2;

    private sealed class RoomBudget
    {
        internal int Tick = int.MinValue;
        internal int Used;
    }

    private static readonly Dictionary<int, RoomBudget> Budgets = new();

    internal static bool TryAcquire(LanceScavenger owner, bool urgent = false)
    {
        if (urgent) return true;
        Room room = owner?.room;
        if (room?.abstractRoom == null || room.game == null) return true;

        int roomIndex = room.abstractRoom.index;
        int tick = room.game.clock;
        if (!Budgets.TryGetValue(roomIndex, out RoomBudget budget))
        {
            budget = new RoomBudget();
            Budgets[roomIndex] = budget;
        }

        if (budget.Tick != tick)
        {
            budget.Tick = tick;
            budget.Used = 0;
        }

        if (budget.Used >= FullPlansPerRoomPerTick) return false;
        budget.Used++;
        return true;
    }
}
