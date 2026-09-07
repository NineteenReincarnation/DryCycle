using System.Runtime.CompilerServices;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal readonly struct DB_FlightMotorDebugState
{
    internal readonly int Clock;
    internal readonly DB_BehaviorOwner Owner;
    internal readonly Vector2 Goal;
    internal readonly float NominalSpeed;
    internal readonly bool ActiveSteer;
    internal readonly Vector2 RequestedVelocity;
    internal readonly bool PostPhysicsApplied;
    internal readonly Vector2 PostPhysicsVelocity;

    internal DB_FlightMotorDebugState(int clock, DB_BehaviorOwner owner, Vector2 goal, float nominalSpeed, bool activeSteer, Vector2 requestedVelocity, bool postPhysicsApplied, Vector2 postPhysicsVelocity)
    {
        Clock=clock; Owner=owner; Goal=goal; NominalSpeed=nominalSpeed; ActiveSteer=activeSteer;
        RequestedVelocity=requestedVelocity; PostPhysicsApplied=postPhysicsApplied; PostPhysicsVelocity=postPhysicsVelocity;
    }
}

/// <summary>
/// R4 single entry point for ordinary Desert Batfly flight steering.
/// It owns goal intent + requested speed, while Rain World still owns collision and Fly physics.
/// Special physics (grasp/shortcut/emergence/chain/attach/interfere/instant impulses) bypasses it.
/// </summary>
internal static class DB_FlightMotor
{
    private sealed class State
    {
        internal int Clock = int.MinValue;
        internal DB_BehaviorOwner Owner;
        internal Vector2 Goal;
        internal float NominalSpeed;
        internal bool ActiveSteer;
        internal Vector2 RequestedVelocity;
        internal bool PostPhysicsApplied;
        internal Vector2 PostPhysicsVelocity;
    }

    private static ConditionalWeakTable<DesertBatfly, State> states = new();

    internal static void Reset()
    {
        states = new ConditionalWeakTable<DesertBatfly, State>();
        DB_FogGoalModifier.Reset();
    }

    internal static void Forget(DesertBatfly bat)
    {
        if (bat == null) return;
        states.Remove(bat);
        DB_FogGoalModifier.Forget(bat);
    }

    internal static bool TrySteer(
        DesertBatfly bat,
        DB_BehaviorOwner owner,
        Vector2 goal,
        float nominalSpeed,
        bool preserveDijkstra = false,
        float response = 0.22f)
    {
        if (bat?.room == null || bat.AI == null || bat.mainBodyChunk == null ||
            bat.dead || !bat.Consious || bat.inShortcut || bat.Emergence?.Active == true ||
            bat.grabbedBy.Count > 0 || !DB_BehaviorArbiter.IsPrimaryOwner(bat, owner))
            return false;

        if (DB_BehaviorArbiter.TryGetResolution(bat, out DB_BehaviorResolution resolution) &&
            resolution.SpecialPhysicsOwner != DB_SpecialPhysicsOwner.None)
            return false;

        nominalSpeed = Mathf.Max(0.1f, nominalSpeed);
        response = Mathf.Clamp01(response);
        goal = DB_FogGoalModifier.ModifyGoal(bat, owner, goal);

        bat.LoseAllGrasps();
        bat.burrowOrHangSpot = null;
        if (bat.AI.behavior == FlyAI.Behavior.Chain)
            bat.AI.ChangeBehavior(FlyAI.Behavior.Idle);
        else
            bat.AI.behavior = FlyAI.Behavior.Idle;
        if (!preserveDijkstra)
            bat.AI.followingDijkstraMap = -1;
        bat.movMode = Fly.MovementMode.BatFlight;

        Vector2 direction = Custom.DirVec(bat.mainBodyChunk.pos, goal);
        Vector2 probe = bat.mainBodyChunk.pos + direction * 25f;
        if (bat.room.GetTile(probe).Solid ||
            (bat.room.terrain != null && bat.room.terrain.Contains(probe)))
        {
            goal = bat.mainBodyChunk.pos + Vector2.up * 70f;
            nominalSpeed = Mathf.Min(nominalSpeed, 4f);
            direction = Custom.DirVec(bat.mainBodyChunk.pos, goal);
        }

        bat.AI.localGoal = goal;
        Vector2 requested = direction * nominalSpeed;
        bat.mainBodyChunk.vel = Vector2.Lerp(bat.mainBodyChunk.vel, requested, response);

        State state = states.GetOrCreateValue(bat);
        state.Clock = bat.room.game?.clock ?? int.MinValue;
        state.Owner = owner;
        state.Goal = goal;
        state.NominalSpeed = nominalSpeed;
        state.ActiveSteer = true;
        state.RequestedVelocity = requested;
        state.PostPhysicsApplied = false;
        state.PostPhysicsVelocity = default;
        return true;
    }

    /// <summary>
    /// Owner-validated goal submission that deliberately leaves velocity production to Rain World
    /// native Fly physics. This preserves legacy Social/Travel/projectile behavior while making
    /// DB_FlightMotor the single Desert Batfly localGoal write boundary.
    /// </summary>
    internal static bool TryGuideNative(
        DesertBatfly bat,
        DB_BehaviorOwner owner,
        Vector2 goal,
        float nominalSpeed = 0f)
    {
        if (bat?.room == null || bat.AI == null || bat.mainBodyChunk == null ||
            bat.dead || !bat.Consious || bat.inShortcut || bat.Emergence?.Active == true ||
            bat.grabbedBy.Count > 0 || !DB_BehaviorArbiter.IsPrimaryOwner(bat, owner))
            return false;

        if (DB_BehaviorArbiter.TryGetResolution(bat, out DB_BehaviorResolution resolution) &&
            resolution.SpecialPhysicsOwner != DB_SpecialPhysicsOwner.None)
            return false;

        goal = DB_FogGoalModifier.ModifyGoal(bat, owner, goal);
        bat.AI.localGoal = goal;

        State state = states.GetOrCreateValue(bat);
        state.Clock = bat.room.game?.clock ?? int.MinValue;
        state.Owner = owner;
        state.Goal = goal;
        state.NominalSpeed = Mathf.Max(0f, nominalSpeed);
        state.ActiveSteer = false;
        state.RequestedVelocity = default;
        state.PostPhysicsApplied = false;
        state.PostPhysicsVelocity = default;
        return true;
    }

    /// <summary>
    /// Same-owner tactical goal adjustment after an owner has already submitted its base intent.
    /// It never changes ownership or injects a second velocity controller.
    /// </summary>
    internal static bool TryRetarget(DesertBatfly bat, DB_BehaviorOwner owner, Vector2 goal)
    {
        if (bat?.room == null || bat.AI == null ||
            !DB_BehaviorArbiter.IsPrimaryOwner(bat, owner))
            return false;
        int clock = bat.room.game?.clock ?? int.MinValue;
        if (!states.TryGetValue(bat, out State state) || state.Clock != clock || state.Owner != owner)
            return false;
        if (DB_BehaviorArbiter.TryGetResolution(bat, out DB_BehaviorResolution resolution) &&
            resolution.SpecialPhysicsOwner != DB_SpecialPhysicsOwner.None)
            return false;

        goal = DB_FogGoalModifier.ModifyGoal(bat, owner, goal);
        bat.AI.localGoal = goal;
        state.Goal = goal;
        return true;
    }

    /// <summary>
    /// Final R4 injury-flight pass. This does not choose a goal; it only modifies the velocity
    /// produced by the selected owner/native Fly physics using the pure Injury modifier math.
    /// </summary>
    internal static void ApplyPostPhysics(DesertBatfly bat, Vector2 previousVelocity)
    {
        if (bat?.room == null || bat.mainBodyChunk == null || bat.dead || !bat.Consious ||
            bat.inShortcut || bat.grabbedBy.Count > 0 || bat.Emergence?.Active == true ||
            bat.AI?.behavior == FlyAI.Behavior.Chain || bat.movMode != Fly.MovementMode.BatFlight)
            return;

        float nominalSpeed = 12f;
        int clock = bat.room.game?.clock ?? int.MinValue;
        if (states.TryGetValue(bat, out State state) && state.Clock == clock && state.NominalSpeed > 0f)
            nominalSpeed = state.NominalSpeed;

        bat.mainBodyChunk.vel = bat.Injury.ModifyFlight(
            previousVelocity,
            bat.mainBodyChunk.vel,
            nominalSpeed);
        if (states.TryGetValue(bat, out State postState) && postState.Clock == clock)
        {
            postState.PostPhysicsApplied = true;
            postState.PostPhysicsVelocity = bat.mainBodyChunk.vel;
        }
    }

    internal static bool TryGetDebugState(DesertBatfly bat, out DB_FlightMotorDebugState debug)
    {
        debug = default;
        if (bat?.room == null || !states.TryGetValue(bat, out State state)) return false;
        int clock = bat.room.game?.clock ?? int.MinValue;
        if (state.Clock != clock) return false;
        debug = new DB_FlightMotorDebugState(state.Clock, state.Owner, state.Goal, state.NominalSpeed, state.ActiveSteer, state.RequestedVelocity, state.PostPhysicsApplied, state.PostPhysicsVelocity);
        return true;
    }

    internal static bool TryGetIntent(
        DesertBatfly bat,
        out DB_BehaviorOwner owner,
        out Vector2 goal,
        out float nominalSpeed)
    {
        owner = DB_BehaviorOwner.None;
        goal = default;
        nominalSpeed = 0f;
        if (bat?.room == null || !states.TryGetValue(bat, out State state)) return false;
        int clock = bat.room.game?.clock ?? int.MinValue;
        if (state.Clock != clock) return false;
        owner = state.Owner;
        goal = state.Goal;
        nominalSpeed = state.NominalSpeed;
        return true;
    }
}
