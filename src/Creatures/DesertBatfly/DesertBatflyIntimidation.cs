using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RWCustom;
using UnityEngine;
using Watcher;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Mortality awareness, finite chain fear and rare extreme vengeance for Desert Batflies.
///
/// Player kills and Peach-Lizard predation share the same event-time fear propagation:
/// direct witnesses react strongly, nearby non-witnesses react more weakly, and panic can
/// travel through at most two additional neighbours. This creates visible flock morale
/// without a global per-frame observer graph or unlimited room-wide telepathy.
///
/// Most bats only flee. A small, stable personality subset can overcome the initial shock
/// and perform extreme vengeance against the actual killer/predator. That special mode is
/// intentionally separate from the ordinary non-damaging retaliation system and is the
/// only Desert-Batfly behavior allowed to deal meaningful blunt damage to a Slugcat or
/// Peach Lizard. It also owns the rare tongue/grasp rescue attempt.
/// </summary>
internal static class DesertBatflyIntimidation
{
    private enum EventKind
    {
        PlayerKill,
        PredatorCapture,
        PredatorKill
    }

    private enum VengeanceMode
    {
        None,
        Waiting,
        Observe,
        Circle,
        Feint,
        RescueCharge,
        Charge,
        Withdraw
    }

    // Direct perception and finite social propagation.
    private const float DirectWitnessRadius = 340f;
    private const float SecondaryAlarmRadius = 180f;
    private const float ChainFearRadius = 150f;
    private const int ChainFearHops = 2;

    // A short-lived corpse remains evidence only while the killer is still nearby.
    private const float CorpseReminderRadius = 190f;
    private const float CorpseKillerProximity = 230f;
    private const int CorpseLifetimeTicks = 600;
    private const int CorpseSampleTicks = 40;
    private const int CorpseReminderTicks = 600;
    private const int CorpseReminderShockTicks = 60;
    private const int CorpseReminderCooldownTicks = 180;

    // Fear gains. Chain tiers are deliberately much weaker than eyewitness memory.
    private const float DirectGain = 0.50f;
    private const float SecondaryGain = 0.22f;
    private const float ChainGain1 = 0.13f;
    private const float ChainGain2 = 0.065f;
    private const float MinimumDirectGain = 0.14f;
    private const float MinimumSecondaryGain = 0.06f;

    private const int MemoryMinTicks = 800;
    private const int MemoryMaxTicks = 2400;
    private const int DirectShockMinTicks = 200;
    private const int DirectShockMaxTicks = 500;
    private const int SecondaryShockMinTicks = 110;
    private const int SecondaryShockMaxTicks = 260;
    private const int ChainShock1MinTicks = 70;
    private const int ChainShock1MaxTicks = 170;
    private const int ChainShock2MinTicks = 40;
    private const int ChainShock2MaxTicks = 105;
    private const int PanicRefreshTicks = 100;
    private const int AvoidRefreshTicks = 110;

    // Only a couple of exceptional individuals may counter-attack one mortality event.
    // This is the hard safety cap that keeps vengeance from becoming another attack swarm.
    private const int MaxVengeanceResponders = 2;
    private const float VengeanceCollapseStrength = 0.84f;
    private const int VengeanceCaptureDelayMin = 12;
    private const int VengeanceCaptureDelayMax = 30;
    private const int VengeanceKillDelayMin = 70;
    private const int VengeanceKillDelayMax = 155;
    private const int VengeanceObserveMinTicks = 18;
    private const int VengeanceObserveMaxTicks = 46;
    private const int VengeanceCircleMinTicks = 22;
    private const int VengeanceCircleMaxTicks = 48;
    private const int VengeanceFeintTicks = 18;
    private const int VengeanceChargeTimeout = 72;
    private const int VengeanceWithdrawMinTicks = 100;
    private const int VengeanceWithdrawMaxTicks = 210;
    private const float VengeanceChargeMinSpeed = 12.5f;
    private const float VengeanceChargeMaxSpeed = 18f;
    private const float VengeanceHitExtraRadius = 5f;

    // Extreme vengeance is dangerous on purpose. Only the highest VengeanceDrive can
    // reach the upper Player damage range, where Creature.Violence may cross the
    // Slugcat template's instant-death threshold. Ordinary retaliation never uses this.
    private const float PlayerVengeanceDamageMin = 0.30f;
    private const float PlayerVengeanceDamageMax = 1.10f;
    private const float LizardVengeanceDamageMin = 0.12f;
    private const float LizardVengeanceDamageMax = 0.44f;
    private const float VengeanceStunMin = 14f;
    private const float VengeanceStunMax = 58f;
    private const float VengeanceImpactMin = 0.75f;
    private const float VengeanceImpactMax = 2.8f;

    // Rescue is never guaranteed; a successful head/face charge can make Peach lose
    // the tongue catch, and a later grasp rescue is possible at a lower probability.
    private const float TongueRescueChanceMin = 0.18f;
    private const float TongueRescueChanceMax = 0.48f;
    private const float GraspRescueChanceScale = 0.62f;

    private struct FearMemory
    {
        internal Creature Threat;
        internal int Identity;
        internal float Strength;
        internal int MemoryTicks;
        internal int ShockTicks;
        internal int PanicRefresh;
        internal int AvoidRefresh;
        internal int CorpseReminderCooldown;
        internal Vector2 LastLethalPosition;

        internal bool Active => MemoryTicks > 0 && Strength > 0f;
    }

    private sealed class State
    {
        internal bool Active;
        internal FearMemory PlayerFear;
        internal FearMemory PredatorFear;

        internal VengeanceMode Vengeance;
        internal Creature VengeanceTarget;
        internal DesertBatfly RescueVictim;
        internal LizardTongue RescueTongue;
        internal float Rage;
        internal int VengeanceTimer;
        internal int PassesRemaining;
        internal bool RescueAttempted;
        internal bool WasRescuePlan;
    }

    private sealed class CaptureStamp
    {
        internal int PredatorIdentity = int.MinValue;
        internal int Clock = int.MinValue;
    }

    private sealed class CorpseWarning : UpdatableAndDeletable
    {
        private readonly DesertBatfly victim;
        private readonly Creature killer;
        private readonly Vector2 deathPosition;
        private readonly float threatScale;
        private int age;

        internal CorpseWarning(
            Room room,
            DesertBatfly victim,
            Creature killer,
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

            if (age > CorpseLifetimeTicks || room == null || !ValidThreat(killer, room) ||
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
    private static ConditionalWeakTable<DesertBatfly, CaptureStamp> captureStamps = new();
    private static int activeStates;

    internal static void Reset()
    {
        states = new ConditionalWeakTable<DesertBatfly, State>();
        captureStamps = new ConditionalWeakTable<DesertBatfly, CaptureStamp>();
        activeStates = 0;
    }

    internal static bool IsSupportedLethalThreat(Creature creature)
    {
        return creature is Player || IsPeach(creature);
    }

    internal static bool IsExtremeVengeanceActive(DesertBatfly bat)
    {
        return bat != null && states.TryGetValue(bat, out State state) &&
               state.Active && state.Vengeance != VengeanceMode.None;
    }

    /// <summary>
    /// Called after DesertBatflyAI.Update but before its Attach/Interfere physics.
    /// Normal time is extremely cheap: until a mortality event creates state, the
    /// method exits after one integer comparison and performs no weak-table lookup.
    /// </summary>
    internal static void Update(DesertBatfly bat)
    {
        if (bat == null || activeStates <= 0 ||
            !states.TryGetValue(bat, out State state) || !state.Active)
            return;

        TickMemory(ref state.PlayerFear);
        TickMemory(ref state.PredatorFear);

        if (bat.dead || !bat.Consious || bat.room == null || bat.inShortcut ||
            RestrainedByNonFly(bat))
        {
            ClearVengeance(state);
            TryDeactivate(state);
            return;
        }

        // Extreme vengeance is allowed to override the ordinary fear steering only
        // after its explicit waiting phase. Until then, both memories still enforce
        // the visible panic/withdrawal response requested for even the boldest bats.
        bool vengeanceControlsMovement = state.Vengeance is
            VengeanceMode.Observe or VengeanceMode.Circle or VengeanceMode.Feint or
            VengeanceMode.RescueCharge or VengeanceMode.Charge or VengeanceMode.Withdraw;

        if (!vengeanceControlsMovement)
        {
            EnforceFear(bat, state, ref state.PlayerFear, isPlayer: true);
            EnforceFear(bat, state, ref state.PredatorFear, isPlayer: false);
        }

        UpdateVengeance(bat, state);
        TryDeactivate(state);
    }

    internal static void BroadcastPlayerKill(
        DesertBatfly victim,
        Player killer,
        Vector2 deathPosition,
        Fly preDeathChainRoot,
        float threatScale,
        bool revengeFailed = false)
    {
        BroadcastThreatEvent(
            victim,
            killer,
            deathPosition,
            preDeathChainRoot,
            Mathf.Clamp(threatScale, 0.5f, 1.25f),
            EventKind.PlayerKill,
            null,
            revengeFailed);
    }

    internal static void BroadcastPredatorCapture(
        DesertBatfly victim,
        Lizard predator,
        LizardTongue tongue)
    {
        if (victim?.room == null || !IsPeach(predator) || predator.room != victim.room)
            return;

        // Tongue-hit and subsequent Bite/Grasp hooks can observe the same capture.
        // Collapse those into one event so fear and rescue are never double-armed.
        CaptureStamp stamp = captureStamps.GetOrCreateValue(victim);
        int clock = victim.room.game?.clock ?? 0;
        int identity = ThreatIdentity(predator);
        if (stamp.PredatorIdentity == identity && clock - stamp.Clock >= 0 &&
            clock - stamp.Clock < 90)
            return;

        stamp.PredatorIdentity = identity;
        stamp.Clock = clock;

        Fly chainRoot = victim.AI != null && victim.AI.behavior == FlyAI.Behavior.Chain
            ? victim.FirstInChain()
            : null;

        BroadcastThreatEvent(
            victim,
            predator,
            victim.mainBodyChunk.pos,
            chainRoot,
            0.92f,
            EventKind.PredatorCapture,
            tongue,
            revengeFailed: false);
    }

    internal static void BroadcastPredatorKill(
        DesertBatfly victim,
        Lizard predator,
        Vector2 deathPosition,
        Fly preDeathChainRoot,
        float threatScale,
        bool revengeFailed = false)
    {
        if (!IsPeach(predator))
            return;

        BroadcastThreatEvent(
            victim,
            predator,
            deathPosition,
            preDeathChainRoot,
            Mathf.Clamp(threatScale, 0.6f, 1.35f),
            EventKind.PredatorKill,
            null,
            revengeFailed);
    }

    private static void BroadcastThreatEvent(
        DesertBatfly victim,
        Creature threat,
        Vector2 eventPosition,
        Fly preEventChainRoot,
        float threatScale,
        EventKind kind,
        LizardTongue rescueTongue,
        bool revengeFailed)
    {
        Room room = victim?.room;
        if (room == null || !ValidThreat(threat, room))
            return;

        // A failed vengeance attempt being killed is especially demoralizing. It
        // strengthens the fear wave and deliberately suppresses replacement avengers
        // for this event, preventing an endless queue of heroic suicides.
        if (revengeFailed)
            threatScale = Mathf.Min(1.35f, threatScale * 1.28f);

        List<DesertBatfly> bats = new(DesertBatflyTuning.HivePopulation + DesertBatflyTuning.CurvePopulation);
        foreach (Fly other in DesertSwarmRoom.For(room).Hive.flies)
        {
            if (other is DesertBatfly bat && bat != victim && !bat.dead &&
                bat.room == room && bat.Consious)
                bats.Add(bat);
        }

        if (bats.Count == 0)
            return;

        // tier: 0 = direct/same-chain, 1 = nearby secondary, 2/3 = social chain hops.
        int[] tier = new int[bats.Count];
        for (int i = 0; i < tier.Length; i++) tier[i] = -1;

        List<int> frontier = new(bats.Count);
        List<int> next = new(bats.Count);
        List<DesertBatfly> vengeanceCandidates = new(4);

        for (int i = 0; i < bats.Count; i++)
        {
            DesertBatfly bat = bats[i];
            float distance = Vector2.Distance(bat.mainBodyChunk.pos, eventPosition);
            bool sameChain = preEventChainRoot != null &&
                bat.AI != null && bat.AI.behavior == FlyAI.Behavior.Chain &&
                bat.FirstInChain() == preEventChainRoot;
            bool directVisual = distance <= DirectWitnessRadius &&
                (room.VisualContact(bat.mainBodyChunk.pos, eventPosition) ||
                 room.VisualContact(bat.mainBodyChunk.pos, threat.mainBodyChunk.pos));

            if (sameChain || directVisual)
            {
                tier[i] = 0;
                frontier.Add(i);
                if (!revengeFailed && bat.Personality.CanExtremeVengeance)
                    vengeanceCandidates.Add(bat);
            }
            else if (distance <= SecondaryAlarmRadius)
            {
                tier[i] = 1;
                frontier.Add(i);
            }
        }

        // Panic may travel through two more nearby bats. We intentionally do not
        // require line-of-sight for these social hops: the signal is another bat's
        // sudden escape, not direct knowledge of the corpse. Range shrinks per hop.
        for (int hop = 0; hop < ChainFearHops && frontier.Count > 0; hop++)
        {
            next.Clear();
            float radius = ChainFearRadius * (hop == 0 ? 1f : 0.82f);

            for (int f = 0; f < frontier.Count; f++)
            {
                DesertBatfly source = bats[frontier[f]];
                for (int i = 0; i < bats.Count; i++)
                {
                    if (tier[i] >= 0 || bats[i] == source)
                        continue;

                    if (!Custom.DistLess(source.mainBodyChunk.pos, bats[i].mainBodyChunk.pos, radius))
                        continue;

                    tier[i] = 2 + hop;
                    next.Add(i);
                }
            }

            List<int> swap = frontier;
            frontier = next;
            next = swap;
        }

        for (int i = 0; i < bats.Count; i++)
        {
            if (tier[i] < 0)
                continue;
            ReceiveFear(bats[i], threat, eventPosition, tier[i], threatScale);
        }

        ArmVengeanceResponders(
            vengeanceCandidates,
            victim,
            threat,
            kind,
            rescueTongue,
            threatScale,
            revengeFailed);

        if (kind != EventKind.PredatorCapture)
        {
            room.AddObject(new CorpseWarning(
                room,
                victim,
                threat,
                eventPosition,
                threatScale));
        }
    }

    private static void ReceiveFear(
        DesertBatfly bat,
        Creature threat,
        Vector2 eventPosition,
        int tier,
        float threatScale)
    {
        State state = StateFor(bat);
        FearMemory memory = threat is Player ? state.PlayerFear : state.PredatorFear;
        int identity = ThreatIdentity(threat);

        if (!memory.Active || memory.Identity != identity)
        {
            memory = default;
            memory.Identity = identity;
        }

        memory.Threat = threat;
        memory.LastLethalPosition = eventPosition;

        float caution = CautionFactor(bat);
        float baseGain = tier switch
        {
            0 => DirectGain,
            1 => SecondaryGain,
            2 => ChainGain1,
            _ => ChainGain2
        };
        float minimum = tier == 0
            ? MinimumDirectGain
            : tier == 1
                ? MinimumSecondaryGain
                : 0.025f;

        memory.Strength = Mathf.Clamp01(
            memory.Strength + Mathf.Max(minimum, baseGain * caution * threatScale));
        int duration = Mathf.RoundToInt(Mathf.Lerp(
            MemoryMinTicks,
            MemoryMaxTicks,
            memory.Strength));
        if (tier >= 2)
            duration = Mathf.RoundToInt(duration * (tier == 2 ? 0.70f : 0.48f));
        memory.MemoryTicks = Mathf.Max(memory.MemoryTicks, duration);

        float courage = Mathf.Clamp01((bat.Personality.Temperament + bat.Personality.Nerve) * 0.5f);
        int shock = tier switch
        {
            0 => Mathf.RoundToInt(Mathf.Lerp(DirectShockMaxTicks, DirectShockMinTicks, courage)),
            1 => Mathf.RoundToInt(Mathf.Lerp(SecondaryShockMaxTicks, SecondaryShockMinTicks, courage)),
            2 => Mathf.RoundToInt(Mathf.Lerp(ChainShock1MaxTicks, ChainShock1MinTicks, courage)),
            _ => Mathf.RoundToInt(Mathf.Lerp(ChainShock2MaxTicks, ChainShock2MinTicks, courage))
        };
        shock = Mathf.RoundToInt(shock * Mathf.Lerp(0.88f, 1.08f, threatScale));
        memory.ShockTicks = Mathf.Max(memory.ShockTicks, shock);
        memory.PanicRefresh = 0;
        memory.AvoidRefresh = 0;

        if (threat is Player)
            state.PlayerFear = memory;
        else
            state.PredatorFear = memory;

        // Every reached tier visibly reacts now. Threatened() reuses the already-tested
        // chain release, attack cancellation, local alarm and Escape transition.
        bat.DesertAI.Threatened(threat, false);
    }

    private static void ReceiveCorpseReminder(
        DesertBatfly bat,
        Creature threat,
        Vector2 deathPosition,
        float threatScale)
    {
        State state = StateFor(bat);
        FearMemory memory = threat is Player ? state.PlayerFear : state.PredatorFear;
        int identity = ThreatIdentity(threat);

        if (!memory.Active || memory.Identity != identity)
        {
            memory = default;
            memory.Identity = identity;
        }

        memory.Threat = threat;
        memory.Strength = Mathf.Max(
            memory.Strength,
            Mathf.Max(0.06f, 0.10f * CautionFactor(bat) * threatScale));
        memory.MemoryTicks = Mathf.Max(memory.MemoryTicks, CorpseReminderTicks);
        memory.LastLethalPosition = deathPosition;

        if (memory.CorpseReminderCooldown <= 0)
        {
            memory.CorpseReminderCooldown = CorpseReminderCooldownTicks;
            memory.ShockTicks = Mathf.Max(memory.ShockTicks, CorpseReminderShockTicks);
            memory.PanicRefresh = PanicRefreshTicks;
            bat.DesertAI.Threatened(threat, false);
        }

        if (threat is Player)
            state.PlayerFear = memory;
        else
            state.PredatorFear = memory;
    }

    private static void ArmVengeanceResponders(
        List<DesertBatfly> candidates,
        DesertBatfly victim,
        Creature threat,
        EventKind kind,
        LizardTongue rescueTongue,
        float threatScale,
        bool revengeFailed)
    {
        if (revengeFailed || candidates == null || candidates.Count == 0 ||
            !ValidThreat(threat, victim?.room))
            return;

        candidates.Sort((a, b) => b.Personality.VengeanceAffinity.CompareTo(a.Personality.VengeanceAffinity));
        int armed = 0;

        for (int i = 0; i < candidates.Count && armed < MaxVengeanceResponders; i++)
        {
            DesertBatfly bat = candidates[i];
            State state = StateFor(bat);
            FearMemory memory = threat is Player ? state.PlayerFear : state.PredatorFear;

            // Once repeated mortality has already pushed an exceptional bat into
            // overwhelming trauma, even it stops trying to be a hero.
            if (memory.Strength >= VengeanceCollapseStrength)
                continue;

            float drive = bat.Personality.VengeanceDrive;
            float rage = Mathf.Clamp01(
                Mathf.Lerp(0.58f, 1f, drive) * Mathf.Lerp(0.88f, 1.08f, threatScale));

            // If this bat is already avenging the same threat, a later kill/capture
            // reinforces rage but does not rewind it back to the waiting phase.
            if (state.Vengeance != VengeanceMode.None && state.VengeanceTarget == threat)
            {
                state.Rage = Mathf.Max(state.Rage, rage);
                state.PassesRemaining = Mathf.Max(state.PassesRemaining, drive > 0.70f ? 2 : 1);
                armed++;
                continue;
            }

            state.VengeanceTarget = threat;
            state.RescueVictim = kind == EventKind.PredatorCapture ? victim : null;
            state.RescueTongue = kind == EventKind.PredatorCapture ? rescueTongue : null;
            state.RescueAttempted = false;
            state.WasRescuePlan = kind == EventKind.PredatorCapture;
            state.Rage = rage;
            state.PassesRemaining = drive > 0.70f ? 2 : 1;
            state.Vengeance = VengeanceMode.Waiting;

            // A time-sensitive tongue rescue overcomes the first panic much faster,
            // but still produces a visible retreat before the bat turns back around.
            int minDelay = kind == EventKind.PredatorCapture
                ? VengeanceCaptureDelayMin
                : VengeanceKillDelayMin;
            int maxDelay = kind == EventKind.PredatorCapture
                ? VengeanceCaptureDelayMax
                : VengeanceKillDelayMax;
            state.VengeanceTimer = Mathf.RoundToInt(Mathf.Lerp(maxDelay, minDelay, drive));
            armed++;
        }
    }

    private static void UpdateVengeance(DesertBatfly bat, State state)
    {
        if (state.Vengeance == VengeanceMode.None)
            return;

        Creature target = state.VengeanceTarget;
        if (!ValidThreat(target, bat.room))
        {
            ClearVengeance(state);
            return;
        }

        float drive = bat.Personality.VengeanceDrive;
        Vector2 head = target.mainBodyChunk.pos;

        switch (state.Vengeance)
        {
            case VengeanceMode.Waiting:
                if (--state.VengeanceTimer > 0)
                    return;

                if (state.WasRescuePlan && RescueStillPossible(state, target))
                {
                    state.Vengeance = VengeanceMode.RescueCharge;
                    state.VengeanceTimer = 0;
                }
                else
                {
                    state.Vengeance = VengeanceMode.Observe;
                    state.VengeanceTimer = Mathf.RoundToInt(Mathf.Lerp(
                        VengeanceObserveMaxTicks,
                        VengeanceObserveMinTicks,
                        drive));
                }
                break;

            case VengeanceMode.Observe:
                ForceFlight(
                    bat,
                    head + OrbitOffset(bat, 150f, 80f),
                    Mathf.Lerp(6.5f, 8.5f, drive));
                if (--state.VengeanceTimer <= 0)
                {
                    state.Vengeance = VengeanceMode.Circle;
                    state.VengeanceTimer = Mathf.RoundToInt(Mathf.Lerp(
                        VengeanceCircleMaxTicks,
                        VengeanceCircleMinTicks,
                        drive));
                }
                break;

            case VengeanceMode.Circle:
                ForceFlight(
                    bat,
                    head + OrbitOffset(bat, 105f, 60f),
                    Mathf.Lerp(7.5f, 10f, drive));
                if (--state.VengeanceTimer <= 0)
                {
                    state.Vengeance = VengeanceMode.Feint;
                    state.VengeanceTimer = VengeanceFeintTicks;
                }
                break;

            case VengeanceMode.Feint:
            {
                float distance = Vector2.Distance(bat.mainBodyChunk.pos, head);
                Vector2 goal = distance < 58f
                    ? head + Vector2.up * 155f + Custom.DirVec(head, bat.mainBodyChunk.pos) * 70f
                    : head + target.mainBodyChunk.vel * 0.75f;
                ForceFlight(bat, goal, distance < 58f ? 11f : 13f);

                if (--state.VengeanceTimer <= 0)
                {
                    state.Vengeance = VengeanceMode.Charge;
                    state.VengeanceTimer = VengeanceChargeTimeout;
                }
                break;
            }

            case VengeanceMode.RescueCharge:
                ForceFlight(
                    bat,
                    head + target.mainBodyChunk.vel * 0.95f,
                    Mathf.Lerp(VengeanceChargeMinSpeed, VengeanceChargeMaxSpeed, drive));
                state.VengeanceTimer++;
                if (TryVengeanceContact(bat, state, target, rescue: true) ||
                    state.VengeanceTimer > VengeanceChargeTimeout)
                {
                    if (state.Vengeance == VengeanceMode.RescueCharge)
                        ContinueOrWithdraw(bat, state, target);
                }
                break;

            case VengeanceMode.Charge:
                ForceFlight(
                    bat,
                    head + target.mainBodyChunk.vel * 1.05f,
                    Mathf.Lerp(VengeanceChargeMinSpeed, VengeanceChargeMaxSpeed, drive));
                if (--state.VengeanceTimer <= 0 ||
                    TryVengeanceContact(bat, state, target, rescue: false))
                {
                    if (state.Vengeance == VengeanceMode.Charge)
                        ContinueOrWithdraw(bat, state, target);
                }
                break;

            case VengeanceMode.Withdraw:
                ForceFlight(
                    bat,
                    bat.mainBodyChunk.pos +
                        Custom.DirVec(head, bat.mainBodyChunk.pos) * 190f + Vector2.up * 65f,
                    Mathf.Lerp(8f, 10.5f, drive));
                if (--state.VengeanceTimer <= 0)
                    ClearVengeance(state);
                break;
        }
    }

    private static bool TryVengeanceContact(
        DesertBatfly bat,
        State state,
        Creature target,
        bool rescue)
    {
        BodyChunk hitChunk = target.mainBodyChunk;
        if (hitChunk == null ||
            !Custom.DistLess(
                bat.mainBodyChunk.pos,
                hitChunk.pos,
                bat.mainBodyChunk.rad + hitChunk.rad + VengeanceHitExtraRadius))
            return false;

        float drive = bat.Personality.VengeanceDrive;
        Vector2 direction = Custom.DirVec(bat.mainBodyChunk.pos, hitChunk.pos);
        float damageDrive = drive * drive;
        float damage = target is Player
            ? Mathf.Lerp(PlayerVengeanceDamageMin, PlayerVengeanceDamageMax, damageDrive)
            : Mathf.Lerp(LizardVengeanceDamageMin, LizardVengeanceDamageMax, damageDrive);
        damage *= Mathf.Lerp(0.86f, 1.08f, state.Rage);

        float stun = Mathf.Lerp(VengeanceStunMin, VengeanceStunMax, drive) *
            Mathf.Lerp(0.82f, 1.05f, state.Rage);
        Vector2 momentum = direction * Mathf.Lerp(VengeanceImpactMin, VengeanceImpactMax, drive);

        // This is the deliberate lethal exception. A top-end extreme-vengence bat can
        // cross Slugcat instant-death damage, while Peach receives meaningful but much
        // smaller health damage because it is a large predator.
        target.Violence(
            bat.mainBodyChunk,
            momentum,
            hitChunk,
            null,
            Creature.DamageType.Blunt,
            damage,
            stun);

        bat.mainBodyChunk.vel += -direction * Mathf.Lerp(4.5f, 7.5f, drive) + Vector2.up * 2f;

        bool rescued = rescue && TryRescueVictim(bat, state, target);
        state.PassesRemaining = Mathf.Max(0, state.PassesRemaining - 1);

        if (rescued)
        {
            state.Vengeance = VengeanceMode.Withdraw;
            state.VengeanceTimer = Mathf.RoundToInt(Mathf.Lerp(
                VengeanceWithdrawMaxTicks,
                VengeanceWithdrawMinTicks,
                drive));
        }

        return true;
    }

    private static bool TryRescueVictim(
        DesertBatfly bat,
        State state,
        Creature target)
    {
        if (state.RescueAttempted || target is not Lizard lizard || !IsPeach(lizard) ||
            state.RescueVictim == null || state.RescueVictim.dead ||
            state.RescueVictim.room != lizard.room)
            return false;

        state.RescueAttempted = true;
        float drive = bat.Personality.VengeanceDrive;
        float chance = Mathf.Lerp(TongueRescueChanceMin, TongueRescueChanceMax, drive) *
            Mathf.Lerp(0.92f, 1.10f, bat.Personality.Nerve) *
            Mathf.Lerp(0.90f, 1.08f, state.Rage);

        DesertBatfly victim = state.RescueVictim;
        bool tongueCatch = state.RescueTongue != null &&
            lizard.tongue == state.RescueTongue &&
            state.RescueTongue.attached?.owner == victim &&
            state.RescueTongue.state == LizardTongue.State.AttachedInSmallObject;

        bool graspCatch = lizard.grasps != null && lizard.grasps.Length > 0 &&
            lizard.grasps[0] != null && lizard.grasps[0].grabbed == victim;

        if (!tongueCatch && !graspCatch)
            return false;

        if (graspCatch)
            chance *= GraspRescueChanceScale;

        if (UnityEngine.Random.value >= Mathf.Clamp01(chance))
            return false;

        if (tongueCatch)
            state.RescueTongue.Retract();
        if (graspCatch && lizard.grasps[0] != null && lizard.grasps[0].grabbed == victim)
            lizard.ReleaseGrasp(0);

        Vector2 away = Custom.DirVec(lizard.mainBodyChunk.pos, victim.mainBodyChunk.pos);
        victim.mainBodyChunk.vel += away * 6.5f + Vector2.up * 3f;
        victim.DesertAI.Threatened(lizard, true);
        return true;
    }

    private static bool RescueStillPossible(State state, Creature target)
    {
        if (target is not Lizard lizard || !IsPeach(lizard) ||
            state.RescueVictim == null || state.RescueVictim.dead ||
            state.RescueVictim.room != lizard.room)
            return false;

        bool tongueCatch = state.RescueTongue != null &&
            lizard.tongue == state.RescueTongue &&
            state.RescueTongue.attached?.owner == state.RescueVictim &&
            state.RescueTongue.state == LizardTongue.State.AttachedInSmallObject;
        bool graspCatch = lizard.grasps != null && lizard.grasps.Length > 0 &&
            lizard.grasps[0] != null && lizard.grasps[0].grabbed == state.RescueVictim;
        return tongueCatch || graspCatch;
    }

    private static void ContinueOrWithdraw(DesertBatfly bat, State state, Creature target)
    {
        float drive = bat.Personality.VengeanceDrive;
        state.RescueVictim = null;
        state.RescueTongue = null;
        state.WasRescuePlan = false;

        if (state.PassesRemaining > 0 && ValidThreat(target, bat.room))
        {
            state.Vengeance = VengeanceMode.Circle;
            state.VengeanceTimer = Mathf.RoundToInt(Mathf.Lerp(
                VengeanceCircleMaxTicks,
                VengeanceCircleMinTicks,
                drive));
            return;
        }

        state.Vengeance = VengeanceMode.Withdraw;
        state.VengeanceTimer = Mathf.RoundToInt(Mathf.Lerp(
            VengeanceWithdrawMaxTicks,
            VengeanceWithdrawMinTicks,
            drive));
    }

    private static void EnforceFear(
        DesertBatfly bat,
        State state,
        ref FearMemory memory,
        bool isPlayer)
    {
        if (!memory.Active)
            return;

        Creature threat = memory.Threat;
        bool present = ValidThreat(threat, bat.room);

        if (memory.ShockTicks > 0)
        {
            if (present && memory.PanicRefresh <= 0)
            {
                memory.PanicRefresh = PanicRefreshTicks;
                bat.DesertAI.Threatened(threat, false);
            }

            if (threat != null && bat.DesertAI.Target == threat)
                bat.DesertAI.CancelAttack();
            return;
        }

        bool blocks = isPlayer
            ? PlayerFearBlocksAttack(bat, memory.Strength)
            : memory.Strength >= 0.10f;
        if (!blocks)
            return;

        if (threat != null && bat.DesertAI.Target == threat)
            bat.DesertAI.CancelAttack();

        if (!present || memory.AvoidRefresh > 0)
            return;

        float fearDistance = Mathf.Lerp(150f, 300f, memory.Strength) *
            Mathf.Lerp(1.12f, 0.72f, bat.Personality.Nerve);
        if (!Custom.DistLess(bat.mainBodyChunk.pos, threat.mainBodyChunk.pos, fearDistance))
            return;

        memory.AvoidRefresh = AvoidRefreshTicks;
        bat.DesertAI.Threatened(threat, false);
    }

    private static bool PlayerFearBlocksAttack(DesertBatfly bat, float strength)
    {
        // Nasty individuals require a stronger accumulated lesson. Chain fear can
        // therefore make them hesitate without automatically pacifying them forever.
        float threshold = Mathf.Lerp(0.22f, 0.52f, bat.Personality.AggressionDrive);
        return strength >= threshold;
    }

    private static void TickMemory(ref FearMemory memory)
    {
        if (!memory.Active)
            return;

        if (memory.MemoryTicks > 0) memory.MemoryTicks--;
        if (memory.ShockTicks > 0) memory.ShockTicks--;
        if (memory.PanicRefresh > 0) memory.PanicRefresh--;
        if (memory.AvoidRefresh > 0) memory.AvoidRefresh--;
        if (memory.CorpseReminderCooldown > 0) memory.CorpseReminderCooldown--;

        if (memory.MemoryTicks <= 0 || memory.Strength <= 0f)
            memory = default;
    }

    private static State StateFor(DesertBatfly bat)
    {
        State state = states.GetValue(bat, _ => new State());
        if (!state.Active)
        {
            state.Active = true;
            activeStates++;
        }
        return state;
    }

    private static void TryDeactivate(State state)
    {
        if (state == null || !state.Active || state.PlayerFear.Active ||
            state.PredatorFear.Active || state.Vengeance != VengeanceMode.None)
            return;

        state.Active = false;
        activeStates = Mathf.Max(0, activeStates - 1);
    }

    private static void ClearVengeance(State state)
    {
        state.Vengeance = VengeanceMode.None;
        state.VengeanceTarget = null;
        state.RescueVictim = null;
        state.RescueTongue = null;
        state.Rage = 0f;
        state.VengeanceTimer = 0;
        state.PassesRemaining = 0;
        state.RescueAttempted = false;
        state.WasRescuePlan = false;
    }

    private static float CautionFactor(DesertBatfly bat)
    {
        return Mathf.Lerp(1.18f, 0.62f, bat.Personality.Temperament) *
               Mathf.Lerp(1.10f, 0.70f, bat.Personality.Nerve);
    }

    private static int ThreatIdentity(Creature threat)
    {
        if (threat is Player player)
            return player.playerState?.playerNumber ?? 0;
        return threat?.abstractCreature?.ID.number ?? int.MinValue;
    }

    private static bool ValidThreat(Creature threat, Room room)
    {
        return threat != null && room != null && !threat.dead &&
               !threat.slatedForDeletetion && threat.room == room && !threat.inShortcut;
    }

    private static bool IsPeach(Creature creature)
    {
        return ModManager.Watcher && creature is Lizard lizard &&
               lizard.Template != null &&
               lizard.Template.type == WatcherEnums.CreatureTemplateType.PeachLizard;
    }

    private static bool RestrainedByNonFly(DesertBatfly bat)
    {
        if (bat?.grabbedBy == null)
            return false;
        for (int i = 0; i < bat.grabbedBy.Count; i++)
        {
            Creature.Grasp grasp = bat.grabbedBy[i];
            if (grasp?.grabber != null && grasp.grabber is not Fly)
                return true;
        }
        return false;
    }

    private static Vector2 OrbitOffset(DesertBatfly bat, float width, float height)
    {
        float angle = (bat.room.game.clock + (bat.Personality.VisualSeed & 1023)) * 0.033f;
        return new Vector2(
            Mathf.Cos(angle) * width,
            45f + Mathf.Sin(angle) * height * 0.55f);
    }

    private static void ForceFlight(DesertBatfly bat, Vector2 goal, float speed)
    {
        if (bat?.room == null)
            return;

        bat.LoseAllGrasps();
        bat.burrowOrHangSpot = null;
        if (bat.AI.behavior == FlyAI.Behavior.Chain)
            bat.AI.ChangeBehavior(FlyAI.Behavior.Idle);
        else
            bat.AI.behavior = FlyAI.Behavior.Idle;
        bat.AI.followingDijkstraMap = -1;
        bat.movMode = Fly.MovementMode.BatFlight;

        Vector2 direction = Custom.DirVec(bat.mainBodyChunk.pos, goal);
        Vector2 probe = bat.mainBodyChunk.pos + direction * 25f;
        if (bat.room.GetTile(probe).Solid ||
            (bat.room.terrain != null && bat.room.terrain.Contains(probe)))
        {
            goal = bat.mainBodyChunk.pos + Vector2.up * 75f;
            speed = Mathf.Min(speed, 7f);
        }

        bat.AI.localGoal = goal;
        bat.mainBodyChunk.vel = Vector2.Lerp(
            bat.mainBodyChunk.vel,
            Custom.DirVec(bat.mainBodyChunk.pos, goal) * speed,
            0.28f);
    }
}
