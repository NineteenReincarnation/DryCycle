using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Thin adapter for Rain World's nonvirtual FlyAI Idle/Swarm callbacks.
///
/// Desert Batfly no longer uses native FlyAI.Swarm/SwarmFlight as a gameplay state. Vanilla
/// may still attempt Idle -> Swarm inside FlyAI.Update, so these hook callbacks immediately
/// fold that transition back to Idle. All species lifecycle/state belongs to
/// DB_NeutralBehaviorRuntime; this adapter owns no state and exposes no forwarding facade.
/// </summary>
internal static class DB_SwarmLifecycleRuntime
{
    internal static void AfterNativeIdleUpdate(FlyAI ai, DB_Creature bat)
    {
        SuppressNativeSwarm(ai, bat);
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
}
