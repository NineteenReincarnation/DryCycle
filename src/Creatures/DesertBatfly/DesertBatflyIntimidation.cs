using System.Runtime.CompilerServices;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Player-specific mortality awareness for Desert Batflies.
///
/// A player killing one bat is different from the ordinary local danger alarm: nearby
/// witnesses remember which player demonstrated lethal force. The memory is intentionally
/// tiny (one player + strength + timers per realized bat), decays by itself, and only
/// suppresses aggression against that player. Temperament and Nerve reduce the effect but
/// never make a direct witness completely immune to the immediate death shock.
/// </summary>
internal static class DesertBatflyIntimidation
{
    private const float DirectWitnessRadius = 340f;
    private const float SecondaryAlarmRadius = 180f;
    private const float CorpseReminderRadius = 190f;
    private const float CorpseKillerProximity = 230f;

    private const float DirectGain = 0.50f;
    private const float SecondaryGain = 0.22f;
    private const float MinimumDirectGain = 0.14f;
    private const float MinimumSecondaryGain = 0.06f;

    private const int MemoryMinTicks = 800;
    private const int MemoryMaxTicks = 2400;
    private const int DirectShockMinTicks = 200;
    private const int DirectShockMaxTicks = 500;
    private const int SecondaryShockMinTicks = 110;
    private const int SecondaryShockMaxTicks = 260;
    private const int PanicRefreshTicks = 70;
    private const int AvoidRefreshTicks = 110;

    private const int CorpseLifetimeTicks = 600;
    private const int CorpseSampleTicks = 40;
    private const int CorpseReminderTicks = 600;
    private const int CorpseReminderShockTicks = 60;
    private const int CorpseReminderCooldownTicks = 180;

    private sealed class State
    {
        internal Player Player;
        internal int PlayerNumber = -1;
        internal float Strength;
        internal int MemoryTicks;
        internal int ShockTicks;
        internal int PanicRefresh;
        internal int AvoidRefresh;
        internal int CorpseReminderCooldown;
        internal Vector2 LastLethalPosition;
    }

    private sealed class CorpseWarning : UpdatableAndDeletable
    {
        private readonly DesertBatfly victim;
        private readonly Player killer;
        private readonly Vector2 deathPosition;
        private readonly float threatScale;
        private int age;

        internal CorpseWarning(
            Room room,
            DesertBatfly victim,
            Player killer,
            Vector2 deathPosition,
            float threatScale)
        {
            this.room = room;
            this.victim = victim;
            this.killer = killer;
            this.deathPosition = deathPosition;
            this.threatScale = threatScale;
        }

        public override void Update(bool eu)
        {
            base.Update(eu);
            age++;

            if (age > CorpseLifetimeTicks || room == null || killer == null ||
                killer.room != room || killer.dead ||
                !Custom.DistLess(killer.mainBodyChunk.pos, deathPosition, CorpseKillerProximity))
            {
                Destroy();
                return;
            }

            if (age % CorpseSampleTicks != 0)
                return;

            foreach (Fly other in DesertSwarmRoom.For(room).Hive.flies)
            {
                if (other is not DesertBatfly bat || bat == victim || bat.dead ||
                    bat.room != room || !bat.Consious ||
                    !Custom.DistLess(bat.mainBodyChunk.pos, deathPosition, CorpseReminderRadius) ||
                    !room.VisualContact(bat.mainBodyChunk.pos, deathPosition))
                    continue;

                ReceiveCorpseReminder(bat, killer, deathPosition, threatScale);
            }
        }
    }

    private static ConditionalWeakTable<DesertBatfly, State> states = new();

    internal static void Reset()
    {
        states = new ConditionalWeakTable<DesertBatfly, State>();
    }

    /// <summary>
    /// Called after DesertBatfly's custom AI has updated but before attachment physics.
    /// This means an intimidated bat may still use all normal Fly movement/awareness,
    /// while any same-tick attempt to reacquire the lethal player is cancelled before
    /// Attach/Interfere can apply effects.
    /// </summary>
    internal static void Update(DesertBatfly bat)
    {
        if (bat == null || !states.TryGetValue(bat, out State state))
            return;

        if (state.MemoryTicks > 0) state.MemoryTicks--;
        if (state.ShockTicks > 0) state.ShockTicks--;
        if (state.PanicRefresh > 0) state.PanicRefresh--;
        if (state.AvoidRefresh > 0) state.AvoidRefresh--;
        if (state.CorpseReminderCooldown > 0) state.CorpseReminderCooldown--;

        if (state.MemoryTicks <= 0 || state.Strength <= 0f)
        {
            Clear(state);
            return;
        }

        Player player = state.Player;
        bool playerPresent = player != null && !player.dead &&
            player.room != null && player.room == bat.room && !player.inShortcut;

        if (state.ShockTicks > 0)
        {
            // Immediate shock always wins over revenge/aggression. Threatened() already
            // owns chain release, attack cancellation and the native Escape steering.
            // Refresh slower than the normal 90-tick retreat so a 5-12 second shock
            // remains continuous without doing work every frame.
            if (playerPresent && state.PanicRefresh <= 0)
            {
                state.PanicRefresh = PanicRefreshTicks;
                bat.DesertAI.Threatened(player, false);
            }

            if (player != null && bat.DesertAI.Target == player)
                bat.DesertAI.CancelAttack();
            return;
        }

        if (!BlocksAttack(bat, state))
            return;

        // Strong longer-term intimidation blocks only the demonstrated killer. The bat
        // may still hunt/harass something else, so this never becomes a global pacifism
        // flag. Cancelling here also prevents a vacated attack slot being immediately
        // refilled by the same bat on the next attachment-physics pass.
        if (player != null && bat.DesertAI.Target == player)
            bat.DesertAI.CancelAttack();

        if (!playerPresent || state.AvoidRefresh > 0)
            return;

        float fearDistance = Mathf.Lerp(150f, 290f, state.Strength) *
            Mathf.Lerp(1.12f, 0.72f, bat.Personality.Nerve);
        if (!Custom.DistLess(bat.mainBodyChunk.pos, player.mainBodyChunk.pos, fearDistance))
            return;

        state.AvoidRefresh = AvoidRefreshTicks;
        bat.DesertAI.Threatened(player, false);
    }

    internal static void BroadcastPlayerKill(
        DesertBatfly victim,
        Player killer,
        Vector2 deathPosition,
        Fly preDeathChainRoot,
        float threatScale)
    {
        Room room = victim?.room;
        if (room == null || killer == null)
            return;

        threatScale = Mathf.Clamp(threatScale, 0.5f, 1.25f);

        // Death is rare, so an event-time pass over the small Desert colony is cheaper
        // and simpler than maintaining a permanent observer graph. No per-frame room
        // scan is introduced.
        foreach (Fly other in DesertSwarmRoom.For(room).Hive.flies)
        {
            if (other is not DesertBatfly bat || bat == victim || bat.dead ||
                bat.room != room || !bat.Consious)
                continue;

            float distance = Vector2.Distance(bat.mainBodyChunk.pos, deathPosition);
            bool sameChain = preDeathChainRoot != null &&
                bat.AI != null && bat.AI.behavior == FlyAI.Behavior.Chain &&
                bat.FirstInChain() == preDeathChainRoot;
            bool directVisual = distance <= DirectWitnessRadius &&
                (room.VisualContact(bat.mainBodyChunk.pos, deathPosition) ||
                 room.VisualContact(bat.mainBodyChunk.pos, killer.mainBodyChunk.pos));

            if (sameChain || directVisual)
            {
                ReceiveKillWitness(bat, killer, deathPosition, true, threatScale);
            }
            else if (distance <= SecondaryAlarmRadius)
            {
                // One weak non-visual propagation layer: nearby bats know something
                // catastrophic happened, but they do not receive full eyewitness fear.
                ReceiveKillWitness(bat, killer, deathPosition, false, threatScale);
            }
        }

        // For a short time the corpse can reinforce an existing impression if a bat
        // later sees the body while the killer is still standing nearby. It never adds
        // strength every sample; reminders use Max/refresh semantics below.
        room.AddObject(new CorpseWarning(
            room,
            victim,
            killer,
            deathPosition,
            threatScale));
    }

    private static void ReceiveKillWitness(
        DesertBatfly bat,
        Player killer,
        Vector2 deathPosition,
        bool directWitness,
        float threatScale)
    {
        State state = StateForPlayer(bat, killer);
        float caution = CautionFactor(bat);
        float gain = (directWitness ? DirectGain : SecondaryGain) * caution * threatScale;
        gain = Mathf.Max(
            directWitness ? MinimumDirectGain : MinimumSecondaryGain,
            gain);

        state.Strength = Mathf.Clamp01(state.Strength + gain);
        state.LastLethalPosition = deathPosition;

        int duration = Mathf.RoundToInt(Mathf.Lerp(
            MemoryMinTicks,
            MemoryMaxTicks,
            state.Strength));
        state.MemoryTicks = Mathf.Max(state.MemoryTicks, duration);

        float courage = Mathf.Clamp01((bat.Personality.Temperament + bat.Personality.Nerve) * 0.5f);
        int shock = Mathf.RoundToInt(directWitness
            ? Mathf.Lerp(DirectShockMaxTicks, DirectShockMinTicks, courage)
            : Mathf.Lerp(SecondaryShockMaxTicks, SecondaryShockMinTicks, courage));
        shock = Mathf.RoundToInt(shock * Mathf.Lerp(0.88f, 1.08f, threatScale));
        state.ShockTicks = Mathf.Max(state.ShockTicks, shock);
        state.PanicRefresh = 0;
        state.AvoidRefresh = 0;

        // Immediate event response. Even the nastiest/highest-Nerve bat gets this
        // short retreat; personality only changes how quickly it becomes bold again.
        bat.DesertAI.Threatened(killer, false);
    }

    private static void ReceiveCorpseReminder(
        DesertBatfly bat,
        Player killer,
        Vector2 deathPosition,
        float threatScale)
    {
        State state = StateForPlayer(bat, killer);
        float reminderStrength = Mathf.Max(
            0.06f,
            0.10f * CautionFactor(bat) * threatScale);

        state.Strength = Mathf.Max(state.Strength, reminderStrength);
        state.MemoryTicks = Mathf.Max(state.MemoryTicks, CorpseReminderTicks);
        state.LastLethalPosition = deathPosition;

        if (state.CorpseReminderCooldown > 0)
            return;

        state.CorpseReminderCooldown = CorpseReminderCooldownTicks;
        state.ShockTicks = Mathf.Max(state.ShockTicks, CorpseReminderShockTicks);
        state.PanicRefresh = PanicRefreshTicks;
        bat.DesertAI.Threatened(killer, false);
    }

    private static State StateForPlayer(DesertBatfly bat, Player player)
    {
        State state = states.GetValue(bat, _ => new State());
        int playerNumber = player?.playerState?.playerNumber ?? 0;
        if (state.PlayerNumber != playerNumber)
        {
            Clear(state);
            state.PlayerNumber = playerNumber;
        }
        state.Player = player;
        return state;
    }

    private static bool BlocksAttack(DesertBatfly bat, State state)
    {
        if (state.ShockTicks > 0)
            return true;

        // Nasty individuals require a stronger accumulated lesson before they stop
        // attacking after the shock. At the extreme, roughly three witnessed lethal
        // kills are enough to cross the threshold instead of producing a suicide squad.
        float threshold = Mathf.Lerp(0.22f, 0.52f, bat.Personality.AggressionDrive);
        return state.Strength >= threshold;
    }

    private static float CautionFactor(DesertBatfly bat)
    {
        return Mathf.Lerp(1.18f, 0.62f, bat.Personality.Temperament) *
               Mathf.Lerp(1.10f, 0.70f, bat.Personality.Nerve);
    }

    private static void Clear(State state)
    {
        state.Player = null;
        state.PlayerNumber = -1;
        state.Strength = 0f;
        state.MemoryTicks = 0;
        state.ShockTicks = 0;
        state.PanicRefresh = 0;
        state.AvoidRefresh = 0;
        state.CorpseReminderCooldown = 0;
        state.LastLethalPosition = Vector2.zero;
    }
}
