using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Gives the native Swarm/SwarmFlight pose a bounded ecological lifetime instead of allowing
/// the DESERTSWARMROOM compatibility hook to re-enter it immediately forever. This runtime
/// does not steer movement and does not compete with R3 ownership. It only gates the ordinary
/// vanilla Idle -> Swarm transition and remembers a short post-swarm roaming window.
///
/// Higher-priority behavior (Social, combat, weather, travel, fear, roost, etc.) is allowed to
/// interrupt Swarm at any time. When ordinary vanilla control returns, that interruption ends
/// the old swarm bout and starts the same roaming recovery window rather than resuming the
/// stale bout.
/// </summary>
internal static class DB_SwarmLifecycleRuntime
{
    private sealed class State
    {
        internal bool Initialized;
        internal bool SwarmBoutActive;
        internal int BoutSerial;
        internal int SwarmUntilTick;
        internal int NextSwarmEligibleTick;
        internal Room LastRoom;
    }

    private static ConditionalWeakTable<DB_Creature, State> states = new();

    internal static void Reset()
    {
        states = new ConditionalWeakTable<DB_Creature, State>();
    }

    internal static void Forget(DB_Creature bat)
    {
        if (bat != null) states.Remove(bat);
    }

    /// <summary>
    /// Applies Desert Batfly's post-vanilla IdleUpdate swarm lifecycle rules. Integration
    /// hooks only provide the nonvirtual Rain World boundary; all species decisions stay here.
    /// </summary>
    internal static void AfterNativeIdleUpdate(FlyAI ai, DB_Creature bat)
    {
        if (ai?.room == null || bat == null) return;

        DB_SocialRoomRuntime.RoomState socialRoom = DB_SocialRoomRuntime.For(ai.room);
        if (socialRoom?.IsReserved(bat) == true)
            return;

        if (!DB_SwarmRoom.IsDB_SwarmRoom(ai.room.abstractRoom))
        {
            if (ai.behavior == FlyAI.Behavior.Swarm)
                ai.ChangeBehavior(FlyAI.Behavior.Idle);
            AllowCurrentBehavior(bat, false);
            return;
        }

        bool currentlySwarm = ai.behavior == FlyAI.Behavior.Swarm;
        if (!AllowCurrentBehavior(bat, currentlySwarm))
        {
            if (currentlySwarm)
                ai.ChangeBehavior(FlyAI.Behavior.Idle);
            return;
        }

        // A live bout remains under native SwarmFlight until vanilla ends it, a higher owner
        // interrupts it, or the bounded lifecycle expires. Do not continually re-run entry.
        if (ai.behavior == FlyAI.Behavior.Swarm)
            return;

        if (ai.behavior == FlyAI.Behavior.Idle && !ai.fleeFromRain &&
            CanEnterSwarm(bat) && ai.ValidSwarmPosition(ai.localGoal))
        {
            ai.ChangeBehavior(FlyAI.Behavior.Swarm);
            EnteredSwarm(bat);
        }
    }

    /// <summary>
    /// Applies Desert Batfly's post-vanilla SwarmUpdate lifecycle gate. The hook itself has no
    /// species policy beyond forwarding this nonvirtual callback.
    /// </summary>
    internal static void AfterNativeSwarmUpdate(FlyAI ai, DB_Creature bat)
    {
        if (ai?.room == null || bat == null) return;

        if (!DB_SwarmRoom.IsDB_SwarmRoom(ai.room.abstractRoom))
        {
            if (ai.behavior == FlyAI.Behavior.Swarm)
                ai.ChangeBehavior(FlyAI.Behavior.Idle);
            AllowCurrentBehavior(bat, false);
            return;
        }

        bool currentlySwarm = ai.behavior == FlyAI.Behavior.Swarm;
        if (!AllowCurrentBehavior(bat, currentlySwarm) && currentlySwarm)
            ai.ChangeBehavior(FlyAI.Behavior.Idle);
    }

    /// <summary>
    /// Called after vanilla IdleUpdate/SwarmUpdate. Returns true only while a current Swarm
    /// bout is still allowed to remain active. If vanilla itself ended Swarm, the bout is
    /// treated as complete and a roaming interval begins.
    /// </summary>
    internal static bool AllowCurrentBehavior(DB_Creature bat, bool currentlySwarm)
    {
        if (bat?.room == null) return currentlySwarm;
        State state = For(bat);
        int tick = bat.room.game?.clock ?? 0;

        if (!currentlySwarm)
        {
            if (state.SwarmBoutActive)
                EndBout(bat, state, tick);
            return true;
        }

        // Vanilla can independently choose Swarm inside IdleUpdate. That transition must obey
        // the same post-bout roaming gate as our compatibility entry path; otherwise native
        // re-entry would bypass the lifecycle and recreate a permanent hover loop.
        if (!state.SwarmBoutActive)
        {
            if (tick < state.NextSwarmEligibleTick)
                return false;
            BeginBout(bat, state, tick);
        }

        if (tick < state.SwarmUntilTick)
            return true;

        EndBout(bat, state, tick);
        return false;
    }

    /// <summary>Whether ordinary Idle may begin a new native Swarm bout now.</summary>
    internal static bool CanEnterSwarm(DB_Creature bat)
    {
        if (bat?.room == null) return false;
        State state = For(bat);
        int tick = bat.room.game?.clock ?? 0;
        if (state.SwarmBoutActive) return tick < state.SwarmUntilTick;
        return tick >= state.NextSwarmEligibleTick;
    }

    /// <summary>Records a domain-approved Idle -> Swarm transition.</summary>
    internal static void EnteredSwarm(DB_Creature bat)
    {
        if (bat?.room == null) return;
        State state = For(bat);
        if (state.SwarmBoutActive) return;
        BeginBout(bat, state, bat.room.game?.clock ?? 0);
    }

    private static State For(DB_Creature bat)
    {
        State state = states.GetOrCreateValue(bat);
        Room room = bat.room;
        int tick = room?.game?.clock ?? 0;

        if (!ReferenceEquals(state.LastRoom, room))
        {
            state.LastRoom = room;
            state.Initialized = false;
            state.SwarmBoutActive = false;
            state.BoutSerial = 0;
            state.SwarmUntilTick = 0;
            state.NextSwarmEligibleTick = 0;
        }

        if (!state.Initialized)
        {
            state.Initialized = true;
            state.NextSwarmEligibleTick = tick + InitialRoamTicks(bat);
        }

        return state;
    }

    private static void BeginBout(DB_Creature bat, State state, int tick)
    {
        state.SwarmBoutActive = true;
        state.SwarmUntilTick = tick + SwarmBoutTicks(bat, state.BoutSerial);
    }

    private static void EndBout(DB_Creature bat, State state, int tick)
    {
        state.SwarmBoutActive = false;
        state.BoutSerial++;
        state.SwarmUntilTick = 0;
        state.NextSwarmEligibleTick = tick + RoamRecoveryTicks(bat, state.BoutSerial);
    }

    // First emergence/room arrival deliberately gets a short ordinary-flight window so the
    // species does not visually snap straight from spawn into a permanent hover cluster.
    private static int InitialRoamTicks(DB_Creature bat)
    {
        DB_Personality p = bat.Personality;
        int seed = p?.VisualSeed ?? bat.abstractCreature?.ID.RandomSeed ?? 0;
        float conformity = p?.Conformity ?? 0.5f;
        float roll = Stable01(seed, 0x416B);
        float ticks = Mathf.Lerp(100f, 340f, roll) * Mathf.Lerp(1.08f, 0.82f, conformity);
        return Mathf.Clamp(Mathf.RoundToInt(ticks), 80, 380);
    }

    // Roughly 2-6 seconds at Rain World's 40 Hz simulation. Conformist individuals can linger
    // a little longer, but a single Swarm bout is never allowed to dominate the whole cycle.
    private static int SwarmBoutTicks(DB_Creature bat, int serial)
    {
        DB_Personality p = bat.Personality;
        int seed = p?.VisualSeed ?? bat.abstractCreature?.ID.RandomSeed ?? 0;
        float conformity = p?.Conformity ?? 0.5f;
        float calmness = 1f - (p?.Temperament ?? 0.5f);
        float roll = Stable01(seed, serial * 977 + 0x2D91);
        float ticks = Mathf.Lerp(75f, 205f, roll);
        ticks *= Mathf.Lerp(0.86f, 1.18f, conformity);
        ticks *= Mathf.Lerp(0.94f, 1.08f, calmness);
        return Mathf.Clamp(Mathf.RoundToInt(ticks), 65, 250);
    }

    // The ordinary-flight interval is intentionally longer than the Swarm bout. This keeps
    // Swarm as one visible behavior among roaming, Social, roosting and other ecology instead
    // of the default pose for nearly every neutral frame.
    private static int RoamRecoveryTicks(DB_Creature bat, int serial)
    {
        DB_Personality p = bat.Personality;
        int seed = p?.VisualSeed ?? bat.abstractCreature?.ID.RandomSeed ?? 0;
        float conformity = p?.Conformity ?? 0.5f;
        float temperament = p?.Temperament ?? 0.5f;
        float roll = Stable01(seed, serial * 1237 + 0x73A5);
        float ticks = Mathf.Lerp(260f, 620f, roll);
        ticks *= Mathf.Lerp(1.10f, 0.84f, conformity);
        ticks *= Mathf.Lerp(0.94f, 1.08f, temperament);
        return Mathf.Clamp(Mathf.RoundToInt(ticks), 210, 720);
    }

    private static float Stable01(int seed, int salt)
    {
        unchecked
        {
            uint x = (uint)(seed * 1103515245 + salt * 12345 + 0x6D2B79F5);
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CA68Bu;
            x ^= x >> 16;
            return (x & 0x00FFFFFFu) / 16777215f;
        }
    }
}
