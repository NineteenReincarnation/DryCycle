using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Nonvirtual FlyAI hook adapter after the neutral-ecology refactor.
///
/// Desert Batfly no longer uses native FlyAI.Swarm/SwarmFlight as a gameplay state. Vanilla
/// may still attempt Idle -> Swarm inside FlyAI.Update, so these hook callbacks immediately
/// fold that transition back to Idle. The species' visible ShortSwarm is produced explicitly
/// by DB_NeutralBehaviorRuntime and therefore cannot become an unbounded upward hover loop.
/// </summary>
internal static class DB_SwarmLifecycleRuntime
{
    internal static void Reset()
    {
        DB_NeutralBehaviorRuntime.Reset();
    }

    internal static void Forget(DB_Creature bat)
    {
        DB_NeutralBehaviorRuntime.Forget(bat);
    }

    internal static void AfterNativeIdleUpdate(FlyAI ai, DB_Creature bat)
    {
        SuppressNativeSwarm(ai, bat);
    }

    internal static void AfterNativeSwarmUpdate(FlyAI ai, DB_Creature bat)
    {
        SuppressNativeSwarm(ai, bat);
    }

    internal static bool AllowCurrentBehavior(DB_Creature bat, bool currentlySwarm)
        => !currentlySwarm;

    internal static bool CanEnterSwarm(DB_Creature bat) => false;

    internal static void EnteredSwarm(DB_Creature bat)
    {
        if (bat?.AI != null && bat.AI.behavior == FlyAI.Behavior.Swarm)
            SuppressNativeSwarm(bat.AI, bat);
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
