using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Thin adapter for Rain World's nonvirtual FlyAI Idle/Swarm callbacks.
///
/// Desert Batfly does not use native FlyAI.Swarm/SwarmFlight as a gameplay state. Vanilla
/// IdleUpdate may still attempt Idle -> Swarm, so the transition is folded back to Idle and a
/// long no-swarm guard is installed. Native self-roost remains available only when the shared
/// Desert Batfly roost occupancy policy accepts the selected patch.
/// </summary>
internal static class DB_SwarmLifecycleRuntime
{
    internal static void AfterNativeIdleUpdate(FlyAI ai, DB_Creature bat)
    {
        SuppressNativeSwarm(ai, bat);
        SuppressCrowdedNativeSelfRoost(ai, bat);
    }

    // Kept as the hook boundary for compatibility with DB_RainWorldHooks. The state is always
    // folded back to Idle and SwarmFlight is cleared, so a native swarm cannot persist across
    // frames even if vanilla entered Swarm inside the callback.
    internal static void AfterNativeSwarmUpdate(FlyAI ai, DB_Creature bat)
    {
        SuppressNativeSwarm(ai, bat);
    }

    internal static void SuppressNativeSwarmEntry(FlyAI ai, DB_Creature bat)
    {
        SuppressNativeSwarm(ai, bat);
    }

    private static void SuppressNativeSwarm(FlyAI ai, DB_Creature bat)
    {
        if (ai?.room == null || bat == null || !ReferenceEquals(ai.fly, bat)) return;
        if (DB_NeutralBehaviorRuntime.AllowsNativeSwarm(bat)) return;
        if (ai.behavior != FlyAI.Behavior.Swarm) return;

        ai.ChangeBehavior(FlyAI.Behavior.Idle);
        ai.noSwarmCounter = Mathf.Max(ai.noSwarmCounter, 120);
        bat.movMode = Fly.MovementMode.BatFlight;
    }

    private static void SuppressCrowdedNativeSelfRoost(FlyAI ai, DB_Creature bat)
    {
        if (ai?.room == null || bat == null || !ReferenceEquals(ai.fly, bat) ||
            ai.behavior != FlyAI.Behavior.Chain || !bat.burrowOrHangSpot.HasValue)
            return;

        // ConsiderOtherFly is isolated for Desert Batfly before native IdleUpdate runs, so an
        // Idle -> Chain transition with a tile hang spot here is the vanilla self-roost path,
        // not a social fly-to-fly attachment. Keep the native motion/animation semantics but
        // reject a newly selected tile when another roost already occupies the same small patch.
        if (DB_RoostPolicy.CanStartIndependentRoost(bat, bat.burrowOrHangSpot.Value))
            return;

        bat.LoseAllGrasps();
        bat.burrowOrHangSpot = null;
        ai.ChangeBehavior(FlyAI.Behavior.Idle);
        bat.movMode = Fly.MovementMode.BatFlight;
    }
}
