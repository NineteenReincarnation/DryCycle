using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Thin adapter for Rain World's nonvirtual FlyAI Idle/Swarm callbacks.
///
/// Desert Batfly no longer uses native FlyAI.Swarm/SwarmFlight as a gameplay state. Vanilla
/// may still attempt Idle -> Swarm inside FlyAI.Update, so these hook callbacks immediately
/// fold that transition back to Idle. Native Idle may also start a self-roost directly on a
/// ChainTile; that transition is retained only when the shared Desert Batfly roost occupancy
/// policy says the local patch is not already crowded.
///
/// All species lifecycle/state belongs to DB_NeutralBehaviorRuntime or DB_RoostPolicy; this
/// adapter owns no persistent state and exposes no forwarding facade.
/// </summary>
internal static class DB_SwarmLifecycleRuntime
{
    internal static void AfterNativeIdleUpdate(FlyAI ai, DB_Creature bat)
    {
        SuppressNativeSwarm(ai, bat);
        SuppressCrowdedNativeSelfRoost(ai, bat);
    }

    internal static void AfterNativeSwarmUpdate(FlyAI ai, DB_Creature bat)
    {
        SuppressNativeSwarm(ai, bat);
    }

    private static void SuppressNativeSwarm(FlyAI ai, DB_Creature bat)
    {
        if (ai?.room == null || bat == null || !ReferenceEquals(ai.fly, bat)) return;
        if (DB_NeutralBehaviorRuntime.AllowsNativeSwarm(bat)) return;
        if (ai.behavior != FlyAI.Behavior.Swarm) return;

        ai.ChangeBehavior(FlyAI.Behavior.Idle);
        // Prevent vanilla IdleUpdate from immediately re-entering Swarm on the next frame.
        // Custom ShortSwarm is independent from this compatibility counter.
        ai.noSwarmCounter = Mathf.Max(ai.noSwarmCounter, 80);
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
