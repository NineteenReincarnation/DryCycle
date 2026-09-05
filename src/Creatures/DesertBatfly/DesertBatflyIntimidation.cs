using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RWCustom;
using UnityEngine;
using Watcher;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Event-driven mortality awareness for Desert Batflies.
///
/// Death/capture events produce finite social fear waves. Rare true avengers can overcome
/// the first panic and one high-Conformity social group may follow them. Followers remain
/// their own individuals: weak followers only circle/feint, stronger followers make one
/// weaker charge, and any follower may abandon the mob when fear or persistent trauma wins.
///
/// Persistent trauma is kept in DesertBatflyState, not this weak table. This class owns only
/// realized runtime steering and fixed-size fear state; there is no per-frame observer graph.
/// </summary>
internal static class DesertBatflyIntimidation
{
    private enum EventKind { PlayerKill, PredatorCapture, PredatorKill }
    private enum VengeanceMode { None, Waiting, Observe, Circle, Feint, RescueCharge, Charge, Withdraw }
    private enum SocialRole { None, TrueAvenger, Follower }

    private const float DirectWitnessRadius = 340f;
    private const float SecondaryAlarmRadius = 180f;
    private const float ChainFearRadius = 150f;
    private const int ChainFearHops = 2;

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

    private const float CorpseReminderRadius = 190f;
    private const float CorpseKillerProximity = 230f;
    private const int CorpseLifetimeTicks = 600;
    private const int CorpseSampleTicks = 40;
    private const int CorpseReminderTicks = 600;
    private const int CorpseReminderShockTicks = 60;
    private const int CorpseReminderCooldownTicks = 180;

    // One event has one actual leader. The group cap of three therefore means the normal
    // social form is one true avenger plus zero, one or two followers.
    private const int MaxTrueAvengersPerEvent = 1;
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

    private const float PlayerVengeanceDamageMin = 0.30f;
    private const float PlayerVengeanceDamageMax = 1.30f;
    private const float LizardVengeanceDamageMin = 0.12f;
    private const float LizardVengeanceDamageMax = 0.44f;
    private const float VengeanceStunMin = 14f;
    private const float VengeanceStunMax = 58f;
    private const float VengeanceImpactMin = 0.75f;
    private const float VengeanceImpactMax = 2.8f;

    private const float TongueRescueChanceMin = 0.18f;
    private const float TongueRescueChanceMax = 0.48f;
    private const float GraspRescueChanceScale = 0.62f;

    private const int TraumaThreatScanTicks = 20;
    private const int TraumaRetreatRefreshTicks = 120;

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
        internal SocialRole Role;
        internal Creature VengeanceTarget;
        internal DesertBatfly Leader;
        internal DesertBatfly RescueVictim;
        internal LizardTongue RescueTongue;
        internal float Rage;
        internal float Commitment;
        internal float DamageScale = 1f;
        internal int VengeanceTimer;
        internal int PassesRemaining;
        internal bool RescueAttempted;
        internal bool WasRescuePlan;
        internal bool SupportOnly;

        internal int TraumaThreatScan;
        internal int TraumaRetreatRefresh;
    }

    private sealed class CaptureStamp
    {
        internal int PredatorIdentity = int.MinValue;
        internal int Clock = int.MinValue;
    }

    private readonly struct FollowerCandidate
    {
        internal readonly DesertBatfly Bat;
        internal readonly DesertBatfly Leader;
        internal readonly float Score;

        internal FollowerCandidate(DesertBatfly bat, DesertBatfly leader, float score)
        {
            Bat = bat;
            Leader = leader;
            Score = score;
        }
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

            if (age % CorpseSampleTicks != 0) return;

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

    internal static void Forget(DesertBatfly bat)
    {
        if (bat == null) return;
        if (states.TryGetValue(bat, out State state) && state.Active)
            activeStates = Mathf.Max(0, activeStates - 1);
        states.Remove(bat);
        captureStamps.Remove(bat);
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
    /// Capture a hanging chain before Creature.Die()/Threatened can tear down its grasps.
    /// This lets death be broadcast only after death is confirmed while still preserving
    /// exact same-chain eyewitnesses.
    /// </summary>
    internal static DesertBatfly[] SnapshotChainWitnesses(DesertBatfly victim)
    {
        if (victim?.AI == null || victim.AI.behavior != FlyAI.Behavior.Chain)
            return Array.Empty<DesertBatfly>();

        List<DesertBatfly> result = new(6);
        Fly member = victim.FirstInChain();
        int guard = 0;
        while (member != null && guard++ < 32)
        {
            Fly next = member.NextInChain();
            if (member is DesertBatfly bat && bat != victim)
                result.Add(bat);
            member = next;
        }
        return result.Count == 0 ? Array.Empty<DesertBatfly>() : result.ToArray();
    }

    internal static void Update(DesertBatfly bat)
    {
        if (bat == null) return;

        DesertBatflyState persistent = bat.DesertState;
        State state;
        if (persistent.HasTrauma)
        {
            state = StateFor(bat);
        }
        else if (activeStates <= 0 || !states.TryGetValue(bat, out state) || !state.Active)
        {
            return;
        }

        TickMemory(ref state.PlayerFear);
        TickMemory(ref state.PredatorFear);

        if (bat.dead || !bat.Consious || bat.room == null || bat.inShortcut || RestrainedByNonFly(bat))
        {
            ClearVengeance(state);
            TryDeactivate(bat, state);
            return;
        }

        EnforcePersistentTrauma(bat, state);

        bool vengeanceControlsMovement = state.Vengeance is
            VengeanceMode.Observe or VengeanceMode.Circle or VengeanceMode.Feint or
            VengeanceMode.RescueCharge or VengeanceMode.Charge or VengeanceMode.Withdraw;

        if (!vengeanceControlsMovement)
        {
            EnforceFear(bat, ref state.PlayerFear, true);
            EnforceFear(bat, ref state.PredatorFear, false);
        }

        UpdateVengeance(bat, state);
        TryDeactivate(bat, state);
    }

    internal static void BroadcastPlayerKill(
        DesertBatfly victim,
        Player killer,
        Vector2 deathPosition,
        DesertBatfly[] chainWitnesses,
        float threatScale,
        bool revengeFailed = false)
    {
        BroadcastThreatEvent(
            victim,
            killer,
            deathPosition,
            chainWitnesses,
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

        CaptureStamp stamp = captureStamps.GetOrCreateValue(victim);
        int clock = victim.room.game?.clock ?? -1;
        int identity = ThreatIdentity(predator);
        if (clock >= 0 && stamp.Clock >= 0 && stamp.PredatorIdentity == identity &&
            clock - stamp.Clock >= 0 && clock - stamp.Clock < 90)
            return;

        stamp.PredatorIdentity = identity;
        stamp.Clock = clock;

        DesertBatfly[] chainWitnesses = SnapshotChainWitnesses(victim);
        BroadcastThreatEvent(
            victim,
            predator,
            victim.mainBodyChunk.pos,
            chainWitnesses,
            0.92f,
            EventKind.PredatorCapture,
            tongue,
            false);
    }

    internal static void BroadcastPredatorKill(
        DesertBatfly victim,
        Lizard predator,
        Vector2 deathPosition,
        DesertBatfly[] chainWitnesses,
        float threatScale,
        bool revengeFailed = false)
    {
        if (!IsPeach(predator)) return;

        int clock = victim?.room?.game?.clock ?? -1;
        if (clock >= 0 && victim != null &&
            captureStamps.TryGetValue(victim, out CaptureStamp stamp) && stamp.Clock >= 0 &&
            stamp.PredatorIdentity == ThreatIdentity(predator) &&
            clock - stamp.Clock >= 0 && clock - stamp.Clock < 120)
        {
            // A tongue catch followed immediately by the inevitable bite/death is one
            // predation episode, not two independent full-strength catastrophes.
            threatScale *= 0.78f;
        }

        BroadcastThreatEvent(
            victim,
            predator,
            deathPosition,
            chainWitnesses,
            Mathf.Clamp(threatScale, 0.6f, 1.35f),
            EventKind.PredatorKill,
            null,
            revengeFailed);
    }

    private static void BroadcastThreatEvent(
        DesertBatfly victim,
        Creature threat,
        Vector2 eventPosition,
        DesertBatfly[] chainWitnesses,
        float threatScale,
        EventKind kind,
        LizardTongue rescueTongue,
        bool revengeFailed)
    {
        Room room = victim?.room;
        if (room == null || !ValidThreat(threat, room)) return;

        bool victimHadState = TryGetVengeanceState(victim, out State victimState);
        bool victimWasLeader = victimHadState && victimState.Role == SocialRole.TrueAvenger &&
                               victimState.Vengeance != VengeanceMode.None;
        bool victimWasFollower = victimHadState && victimState.Role == SocialRole.Follower &&
                                 victimState.Vengeance != VengeanceMode.None;
        DesertBatfly victimLeader = victimWasFollower ? victimState.Leader : null;

        bool suppressNewVengeance = revengeFailed || victimWasLeader;
        if (suppressNewVengeance)
            threatScale = Mathf.Min(1.35f, threatScale * 1.28f);

        List<DesertBatfly> bats = new(
            DesertBatflyTuning.HivePopulation + DesertBatflyTuning.CurvePopulation);
        foreach (Fly other in DesertSwarmRoom.For(room).Hive.flies)
        {
            if (other is DesertBatfly bat && bat != victim && !bat.dead &&
                bat.room == room && bat.Consious)
                bats.Add(bat);
        }
        if (bats.Count == 0) return;

        if (victimWasLeader)
        {
            // Followers copied the leader's decision, so watching that leader fail is
            // especially damaging. High-Conformity followers take the largest PTSD hit.
            for (int i = 0; i < bats.Count; i++)
            {
                DesertBatfly bat = bats[i];
                if (!TryGetVengeanceState(bat, out State follower) ||
                    follower.Role != SocialRole.Follower || follower.Leader != victim ||
                    follower.VengeanceTarget != threat)
                    continue;

                AddTrauma(
                    bat,
                    threat,
                    Mathf.Lerp(0.30f, 0.55f, bat.Personality.Conformity) * threatScale);
                ClearVengeance(follower);
                bat.DesertAI.Threatened(threat, false);
            }
        }
        else if (victimWasFollower)
        {
            for (int i = 0; i < bats.Count; i++)
            {
                DesertBatfly bat = bats[i];
                if (!TryGetVengeanceState(bat, out State social) ||
                    social.Role != SocialRole.Follower || social.Leader != victimLeader ||
                    social.VengeanceTarget != threat)
                    continue;

                AddTrauma(
                    bat,
                    threat,
                    Mathf.Lerp(0.08f, 0.20f, bat.Personality.Conformity) * threatScale);
            }
        }

        int[] tier = new int[bats.Count];
        for (int i = 0; i < tier.Length; i++) tier[i] = -1;
        List<int> frontier = new(bats.Count);
        List<int> next = new(bats.Count);
        List<DesertBatfly> trueCandidates = new(4);

        for (int i = 0; i < bats.Count; i++)
        {
            DesertBatfly bat = bats[i];
            float distance = Vector2.Distance(bat.mainBodyChunk.pos, eventPosition);
            bool sameChain = WasChainWitness(chainWitnesses, bat);
            bool directVisual = distance <= DirectWitnessRadius &&
                (room.VisualContact(bat.mainBodyChunk.pos, eventPosition) ||
                 room.VisualContact(bat.mainBodyChunk.pos, threat.mainBodyChunk.pos));

            if (sameChain || directVisual)
            {
                tier[i] = 0;
                frontier.Add(i);
                if (!suppressNewVengeance && bat.Personality.CanExtremeVengeance)
                    trueCandidates.Add(bat);
            }
            else if (distance <= SecondaryAlarmRadius)
            {
                tier[i] = 1;
                frontier.Add(i);
            }
        }

        // Only two social hops are allowed. A hop means seeing another bat panic, not
        // magically knowing who died, so line of sight to the original event is not used.
        for (int hop = 0; hop < ChainFearHops && frontier.Count > 0; hop++)
        {
            next.Clear();
            float radius = ChainFearRadius * (hop == 0 ? 1f : 0.82f);
            for (int f = 0; f < frontier.Count; f++)
            {
                DesertBatfly source = bats[frontier[f]];
                for (int i = 0; i < bats.Count; i++)
                {
                    if (tier[i] >= 0 || bats[i] == source) continue;
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
            if (tier[i] >= 0)
                ReceiveFear(bats[i], threat, eventPosition, tier[i], threatScale, kind);
        }

        ArmVengeanceGroup(
            trueCandidates,
            bats,
            tier,
            victim,
            threat,
            kind,
            rescueTongue,
            suppressNewVengeance);

        if (kind != EventKind.PredatorCapture)
            room.AddObject(new CorpseWarning(room, victim, threat, eventPosition, threatScale));
    }

    private static void ReceiveFear(
        DesertBatfly bat,
        Creature threat,
        Vector2 eventPosition,
        int tier,
        float threatScale,
        EventKind kind)
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

        float socialScale = tier == 0
            ? Mathf.Lerp(0.92f, 1.10f, bat.Personality.Conformity)
            : bat.Personality.SocialFearScale;
        bool followingThisThreat = state.Role == SocialRole.Follower &&
                                   state.VengeanceTarget == threat;
        if (followingThisThreat)
            socialScale *= Mathf.Lerp(1.22f, 1.90f, bat.Personality.Conformity);

        memory.Strength = Mathf.Clamp01(
            memory.Strength +
            Mathf.Max(minimum, baseGain * CautionFactor(bat) * socialScale * threatScale));

        int duration = Mathf.RoundToInt(Mathf.Lerp(
            MemoryMinTicks,
            MemoryMaxTicks,
            memory.Strength));
        if (tier >= 2)
            duration = Mathf.RoundToInt(duration * (tier == 2 ? 0.70f : 0.48f));
        memory.MemoryTicks = Mathf.Max(memory.MemoryTicks, duration);

        float courage = Mathf.Clamp01(
            (bat.Personality.Temperament + bat.Personality.Nerve) * 0.5f);
        int shock = tier switch
        {
            0 => Mathf.RoundToInt(Mathf.Lerp(DirectShockMaxTicks, DirectShockMinTicks, courage)),
            1 => Mathf.RoundToInt(Mathf.Lerp(SecondaryShockMaxTicks, SecondaryShockMinTicks, courage)),
            2 => Mathf.RoundToInt(Mathf.Lerp(ChainShock1MaxTicks, ChainShock1MinTicks, courage)),
            _ => Mathf.RoundToInt(Mathf.Lerp(ChainShock2MaxTicks, ChainShock2MinTicks, courage))
        };
        shock = Mathf.RoundToInt(
            shock * Mathf.Lerp(0.88f, 1.08f, threatScale) *
            Mathf.Lerp(0.88f, 1.18f, bat.Personality.Conformity));
        memory.ShockTicks = Mathf.Max(memory.ShockTicks, shock);
        memory.PanicRefresh = 0;
        memory.AvoidRefresh = 0;

        if (threat is Player) state.PlayerFear = memory;
        else state.PredatorFear = memory;

        float traumaBase = kind switch
        {
            EventKind.PredatorCapture => 0.035f,
            EventKind.PlayerKill => 0.085f,
            _ => 0.095f
        };
        float tierScale = tier switch
        {
            0 => 1f,
            1 => 0.62f,
            2 => 0.34f,
            _ => 0.18f
        };
        float traumaGain = traumaBase * tierScale *
                           Mathf.Lerp(0.72f, 1.55f, bat.Personality.Conformity) *
                           threatScale;
        if (followingThisThreat)
            traumaGain *= Mathf.Lerp(1.45f, 2.25f, bat.Personality.Conformity);
        AddTrauma(bat, threat, traumaGain);

        if (state.VengeanceTarget == threat &&
            (memory.Strength >= VengeanceCollapseStrength ||
             PersistentTraumaStrength(bat, threat) >= DesertBatflyTuning.TraumaSevere))
        {
            ClearVengeance(state);
        }

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
            Mathf.Max(
                0.06f,
                0.10f * CautionFactor(bat) * bat.Personality.SocialFearScale * threatScale));
        memory.MemoryTicks = Mathf.Max(memory.MemoryTicks, CorpseReminderTicks);
        memory.LastLethalPosition = deathPosition;

        if (memory.CorpseReminderCooldown <= 0)
        {
            memory.CorpseReminderCooldown = CorpseReminderCooldownTicks;
            memory.ShockTicks = Mathf.Max(memory.ShockTicks, CorpseReminderShockTicks);
            memory.PanicRefresh = PanicRefreshTicks;
            bat.DesertAI.Threatened(threat, false);
        }

        if (threat is Player) state.PlayerFear = memory;
        else state.PredatorFear = memory;
    }

    private static void ArmVengeanceGroup(
        List<DesertBatfly> trueCandidates,
        List<DesertBatfly> bats,
        int[] tier,
        DesertBatfly victim,
        Creature threat,
        EventKind kind,
        LizardTongue rescueTongue,
        bool suppressNewVengeance)
    {
        if (suppressNewVengeance || trueCandidates == null || trueCandidates.Count == 0 ||
            !ValidThreat(threat, victim?.room))
            return;

        trueCandidates.Sort(
            (a, b) => b.Personality.VengeanceAffinity.CompareTo(a.Personality.VengeanceAffinity));

        List<DesertBatfly> leaders = new(MaxTrueAvengersPerEvent);
        int participants = 0;
        for (int i = 0; i < trueCandidates.Count &&
             leaders.Count < MaxTrueAvengersPerEvent &&
             participants < DesertBatflyTuning.SocialVengeanceGroupCap; i++)
        {
            DesertBatfly bat = trueCandidates[i];
            State state = StateFor(bat);
            FearMemory fear = threat is Player ? state.PlayerFear : state.PredatorFear;
            float trauma = PersistentTraumaStrength(bat, threat);
            if (fear.Strength >= VengeanceCollapseStrength ||
                trauma >= DesertBatflyTuning.TraumaAggressionBlock)
                continue;

            ArmVengeance(
                bat,
                state,
                threat,
                kind,
                victim,
                rescueTongue,
                bat.Personality.VengeanceDrive,
                1f,
                false,
                null);
            leaders.Add(bat);
            participants++;
        }

        if (leaders.Count == 0 || participants >= DesertBatflyTuning.SocialVengeanceGroupCap)
            return;

        List<FollowerCandidate> followers = new(bats.Count);
        for (int i = 0; i < bats.Count; i++)
        {
            DesertBatfly bat = bats[i];
            if (tier[i] < 0 || tier[i] > 1 || bat.Personality.CanExtremeVengeance ||
                bat.Personality.Conformity < DesertBatflyTuning.SocialFollowerMinConformity ||
                IsExtremeVengeanceActive(bat))
                continue;

            float trauma = PersistentTraumaStrength(bat, threat);
            if (trauma >= DesertBatflyTuning.TraumaAggressionBlock) continue;

            DesertBatfly bestLeader = null;
            float bestLeaderDrive = 0f;
            for (int l = 0; l < leaders.Count; l++)
            {
                DesertBatfly leader = leaders[l];
                float distance = Vector2.Distance(
                    bat.mainBodyChunk.pos,
                    leader.mainBodyChunk.pos);
                bool sociallyVisible =
                    distance <= DesertBatflyTuning.SocialFollowerRange ||
                    (distance <= DesertBatflyTuning.SocialFollowerRange * 1.45f &&
                     bat.room.VisualContact(bat.mainBodyChunk.pos, leader.mainBodyChunk.pos));
                if (!sociallyVisible) continue;

                if (leader.Personality.VengeanceDrive > bestLeaderDrive)
                {
                    bestLeader = leader;
                    bestLeaderDrive = leader.Personality.VengeanceDrive;
                }
            }
            if (bestLeader == null) continue;

            State batState = StateFor(bat);
            FearMemory fear = threat is Player ? batState.PlayerFear : batState.PredatorFear;
            float score =
                bat.Personality.Conformity * 0.50f +
                bat.Personality.Temperament * 0.20f +
                bat.Personality.Nerve * 0.15f +
                bestLeaderDrive * 0.15f -
                fear.Strength * 0.28f -
                trauma * 0.65f;

            if (score < 0.44f) continue;
            float probability = Mathf.InverseLerp(0.44f, 0.84f, score) *
                                Mathf.Lerp(0.55f, 1f, bat.Personality.Conformity);
            if (StableEvent01(bat, victim, threat, kind) > probability) continue;

            followers.Add(new FollowerCandidate(bat, bestLeader, score));
        }

        followers.Sort((a, b) => b.Score.CompareTo(a.Score));
        for (int i = 0; i < followers.Count &&
             participants < DesertBatflyTuning.SocialVengeanceGroupCap; i++)
        {
            FollowerCandidate follower = followers[i];
            State state = StateFor(follower.Bat);
            float commitment = Mathf.Clamp01(
                Mathf.InverseLerp(0.44f, 0.90f, follower.Score));
            bool supportOnly = follower.Score < 0.66f ||
                               follower.Bat.Personality.Temperament < 0.50f;
            float damageScale = supportOnly
                ? Mathf.Lerp(0.20f, 0.34f, commitment)
                : Mathf.Lerp(0.35f, 0.65f, commitment);

            ArmVengeance(
                follower.Bat,
                state,
                threat,
                kind,
                victim,
                rescueTongue,
                Mathf.Lerp(0.38f, 0.72f, commitment),
                damageScale,
                supportOnly,
                follower.Leader);
            state.Commitment = commitment;
            participants++;
        }
    }

    private static void ArmVengeance(
        DesertBatfly bat,
        State state,
        Creature threat,
        EventKind kind,
        DesertBatfly victim,
        LizardTongue rescueTongue,
        float drive,
        float damageScale,
        bool supportOnly,
        DesertBatfly leader)
    {
        float rage = Mathf.Clamp01(Mathf.Lerp(0.58f, 1f, drive));
        if (state.Vengeance != VengeanceMode.None && state.VengeanceTarget == threat)
        {
            state.Rage = Mathf.Max(state.Rage, rage);
            if (state.Role == SocialRole.TrueAvenger)
                state.PassesRemaining = Mathf.Max(
                    state.PassesRemaining,
                    drive > 0.70f ? 2 : 1);
            return;
        }

        state.VengeanceTarget = threat;
        state.Leader = leader;
        state.RescueVictim = kind == EventKind.PredatorCapture ? victim : null;
        state.RescueTongue = kind == EventKind.PredatorCapture ? rescueTongue : null;
        state.RescueAttempted = false;
        state.WasRescuePlan = kind == EventKind.PredatorCapture;
        state.Rage = rage;
        state.DamageScale = damageScale;
        state.SupportOnly = supportOnly;
        state.PassesRemaining = supportOnly
            ? 0
            : (leader == null && drive > 0.70f ? 2 : 1);
        state.Role = leader == null ? SocialRole.TrueAvenger : SocialRole.Follower;
        state.Vengeance = VengeanceMode.Waiting;

        int minDelay = kind == EventKind.PredatorCapture
            ? VengeanceCaptureDelayMin
            : VengeanceKillDelayMin;
        int maxDelay = kind == EventKind.PredatorCapture
            ? VengeanceCaptureDelayMax
            : VengeanceKillDelayMax;
        int socialDelay = leader == null
            ? 0
            : Mathf.RoundToInt(Mathf.Lerp(26f, 8f, bat.Personality.Conformity));
        state.VengeanceTimer = Mathf.RoundToInt(
            Mathf.Lerp(maxDelay, minDelay, drive)) + socialDelay;
    }

    private static void UpdateVengeance(DesertBatfly bat, State state)
    {
        if (state.Vengeance == VengeanceMode.None) return;

        Creature target = state.VengeanceTarget;
        if (!ValidThreat(target, bat.room))
        {
            ClearVengeance(state);
            return;
        }

        if (state.Role == SocialRole.Follower)
        {
            if (state.Leader == null || state.Leader.dead || state.Leader.room != bat.room ||
                !TryGetVengeanceState(state.Leader, out State leaderState) ||
                leaderState.Role != SocialRole.TrueAvenger ||
                leaderState.VengeanceTarget != target)
            {
                AddTrauma(
                    bat,
                    target,
                    Mathf.Lerp(0.05f, 0.14f, bat.Personality.Conformity));
                ClearVengeance(state);
                bat.DesertAI.Threatened(target, false);
                return;
            }

            // A follower does not keep attacking after the individual it copied has
            // already decided to withdraw.
            if (leaderState.Vengeance == VengeanceMode.Withdraw)
            {
                StartWithdraw(state, CombatDrive(bat, state));
            }
        }

        if (PersistentTraumaStrength(bat, target) >= DesertBatflyTuning.TraumaSevere)
        {
            ClearVengeance(state);
            bat.DesertAI.Threatened(target, false);
            return;
        }

        float drive = CombatDrive(bat, state);
        Vector2 head = target.mainBodyChunk.pos;

        switch (state.Vengeance)
        {
            case VengeanceMode.Waiting:
                if (--state.VengeanceTimer > 0) return;
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
                    ? head + Vector2.up * 155f +
                      Custom.DirVec(head, bat.mainBodyChunk.pos) * 70f
                    : head + target.mainBodyChunk.vel * 0.75f;
                ForceFlight(bat, goal, distance < 58f ? 11f : 13f);

                if (--state.VengeanceTimer <= 0)
                {
                    if (state.SupportOnly)
                    {
                        StartWithdraw(state, drive);
                    }
                    else
                    {
                        state.Vengeance = VengeanceMode.Charge;
                        state.VengeanceTimer = VengeanceChargeTimeout;
                    }
                }
                break;
            }

            case VengeanceMode.RescueCharge:
                ForceFlight(
                    bat,
                    head + target.mainBodyChunk.vel * 0.95f,
                    Mathf.Lerp(VengeanceChargeMinSpeed, VengeanceChargeMaxSpeed, drive));
                state.VengeanceTimer++;
                if (TryVengeanceContact(bat, state, target, true))
                {
                    if (state.Vengeance == VengeanceMode.RescueCharge)
                        ContinueOrWithdraw(state, target, drive);
                }
                else if (state.VengeanceTimer > VengeanceChargeTimeout)
                {
                    state.PassesRemaining = Mathf.Max(0, state.PassesRemaining - 1);
                    ContinueOrWithdraw(state, target, drive);
                }
                break;

            case VengeanceMode.Charge:
                ForceFlight(
                    bat,
                    head + target.mainBodyChunk.vel * 1.05f,
                    Mathf.Lerp(VengeanceChargeMinSpeed, VengeanceChargeMaxSpeed, drive));
                state.VengeanceTimer--;
                if (TryVengeanceContact(bat, state, target, false))
                {
                    if (state.Vengeance == VengeanceMode.Charge)
                        ContinueOrWithdraw(state, target, drive);
                }
                else if (state.VengeanceTimer <= 0)
                {
                    state.PassesRemaining = Mathf.Max(0, state.PassesRemaining - 1);
                    ContinueOrWithdraw(state, target, drive);
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

        float drive = CombatDrive(bat, state);
        Vector2 direction = Custom.DirVec(bat.mainBodyChunk.pos, hitChunk.pos);
        float damageDrive = target is Player ? drive : drive * drive;
        float damage = target is Player
            ? Mathf.Lerp(PlayerVengeanceDamageMin, PlayerVengeanceDamageMax, damageDrive)
            : Mathf.Lerp(LizardVengeanceDamageMin, LizardVengeanceDamageMax, damageDrive);
        damage *= Mathf.Lerp(0.86f, 1.08f, state.Rage) * state.DamageScale;

        float stun = Mathf.Lerp(VengeanceStunMin, VengeanceStunMax, drive) *
                     Mathf.Lerp(0.82f, 1.05f, state.Rage) *
                     Mathf.Lerp(0.72f, 1f, state.DamageScale);
        Vector2 momentum = direction *
                           Mathf.Lerp(VengeanceImpactMin, VengeanceImpactMax, drive) *
                           Mathf.Lerp(0.65f, 1f, state.DamageScale);

        target.Violence(
            bat.mainBodyChunk,
            momentum,
            hitChunk,
            null,
            Creature.DamageType.Blunt,
            damage,
            stun);

        bat.mainBodyChunk.vel +=
            -direction * Mathf.Lerp(4.5f, 7.5f, drive) + Vector2.up * 2f;

        bool rescued = rescue && TryRescueVictim(bat, state, target);
        state.PassesRemaining = Mathf.Max(0, state.PassesRemaining - 1);
        if (rescued) StartWithdraw(state, drive);
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
        float drive = CombatDrive(bat, state);
        float chance = Mathf.Lerp(TongueRescueChanceMin, TongueRescueChanceMax, drive) *
                       Mathf.Lerp(0.92f, 1.10f, bat.Personality.Nerve) *
                       Mathf.Lerp(0.90f, 1.08f, state.Rage);
        if (state.Role == SocialRole.Follower)
            chance *= Mathf.Lerp(0.72f, 0.96f, state.Commitment);

        DesertBatfly victim = state.RescueVictim;
        bool tongueCatch = state.RescueTongue != null &&
            lizard.tongue == state.RescueTongue &&
            state.RescueTongue.attached?.owner == victim &&
            state.RescueTongue.state == LizardTongue.State.AttachedInSmallObject;
        bool graspCatch = lizard.grasps != null && lizard.grasps.Length > 0 &&
            lizard.grasps[0] != null && lizard.grasps[0].grabbed == victim;
        if (!tongueCatch && !graspCatch) return false;

        if (graspCatch) chance *= GraspRescueChanceScale;
        if (UnityEngine.Random.value >= Mathf.Clamp01(chance)) return false;

        if (tongueCatch) state.RescueTongue.Retract();
        if (graspCatch && lizard.grasps[0] != null && lizard.grasps[0].grabbed == victim)
            lizard.ReleaseGrasp(0);

        Vector2 away = Custom.DirVec(
            lizard.mainBodyChunk.pos,
            victim.mainBodyChunk.pos);
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

    private static void ContinueOrWithdraw(
        State state,
        Creature target,
        float drive)
    {
        state.RescueVictim = null;
        state.RescueTongue = null;
        state.WasRescuePlan = false;

        if (state.Role == SocialRole.TrueAvenger && state.PassesRemaining > 0 &&
            target != null && !target.dead)
        {
            state.Vengeance = VengeanceMode.Circle;
            state.VengeanceTimer = Mathf.RoundToInt(Mathf.Lerp(
                VengeanceCircleMaxTicks,
                VengeanceCircleMinTicks,
                drive));
            return;
        }

        StartWithdraw(state, drive);
    }

    private static void StartWithdraw(State state, float drive)
    {
        state.Vengeance = VengeanceMode.Withdraw;
        state.VengeanceTimer = Mathf.RoundToInt(Mathf.Lerp(
            VengeanceWithdrawMaxTicks,
            VengeanceWithdrawMinTicks,
            drive));
    }

    private static void EnforceFear(
        DesertBatfly bat,
        ref FearMemory memory,
        bool isPlayer)
    {
        if (!memory.Active) return;

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
            ? memory.Strength >= Mathf.Lerp(
                0.22f,
                0.52f,
                bat.Personality.AggressionDrive)
            : memory.Strength >= 0.10f;
        if (!blocks) return;

        if (threat != null && bat.DesertAI.Target == threat)
            bat.DesertAI.CancelAttack();
        if (!present || memory.AvoidRefresh > 0) return;

        float fearDistance = Mathf.Lerp(150f, 300f, memory.Strength) *
                             Mathf.Lerp(1.12f, 0.72f, bat.Personality.Nerve) *
                             Mathf.Lerp(0.92f, 1.18f, bat.Personality.Conformity);
        if (!Custom.DistLess(
                bat.mainBodyChunk.pos,
                threat.mainBodyChunk.pos,
                fearDistance))
            return;

        memory.AvoidRefresh = AvoidRefreshTicks;
        bat.DesertAI.Threatened(threat, false);
    }

    private static void EnforcePersistentTrauma(DesertBatfly bat, State state)
    {
        DesertBatflyState persistent = bat.DesertState;
        if (!persistent.HasTrauma) return;

        Creature currentTarget = bat.DesertAI.Target;
        if (currentTarget != null &&
            PersistentTraumaStrength(bat, currentTarget) >=
            DesertBatflyTuning.TraumaAggressionBlock)
        {
            bat.DesertAI.SuppressHostility(currentTarget);
        }

        if (state.TraumaThreatScan > 0) state.TraumaThreatScan--;
        if (state.TraumaRetreatRefresh > 0) state.TraumaRetreatRefresh--;
        if (state.TraumaThreatScan > 0) return;
        state.TraumaThreatScan = TraumaThreatScanTicks;

        Creature threat = ResolveStrongestTraumaThreat(bat);
        if (!ValidThreat(threat, bat.room)) return;
        float strength = PersistentTraumaStrength(bat, threat);
        if (strength < DesertBatflyTuning.TraumaAggressionBlock) return;

        if (state.VengeanceTarget == threat)
            ClearVengeance(state);
        bat.DesertAI.SuppressHostility(threat);

        float fearDistance = Mathf.Lerp(
            DesertBatflyTuning.TraumaFearMinDistance,
            DesertBatflyTuning.TraumaFearMaxDistance,
            strength) *
            Mathf.Lerp(0.95f, 1.18f, bat.Personality.Conformity);
        if (!Custom.DistLess(
                bat.mainBodyChunk.pos,
                threat.mainBodyChunk.pos,
                fearDistance) || state.TraumaRetreatRefresh > 0)
            return;

        state.TraumaRetreatRefresh = TraumaRetreatRefreshTicks;
        bat.DesertAI.Threatened(threat, false);
        // Threatened records an attacker for ordinary short fear. PTSD should retain
        // Escape steering but not re-arm that old hostile memory.
        bat.DesertAI.SuppressHostility(threat);
    }

    private static Creature ResolveStrongestTraumaThreat(DesertBatfly bat)
    {
        Creature best = null;
        float bestStrength = 0f;
        foreach (AbstractCreature abs in bat.room.abstractRoom.creatures)
        {
            Creature creature = abs.realizedCreature;
            if (!ValidThreat(creature, bat.room)) continue;
            float strength = PersistentTraumaStrength(bat, creature);
            if (strength <= bestStrength) continue;
            bestStrength = strength;
            best = creature;
        }
        return best;
    }

    private static void AddTrauma(
        DesertBatfly bat,
        Creature threat,
        float gain)
    {
        if (bat == null || threat == null || gain <= 0f) return;
        DesertBatflyState state = bat.DesertState;
        gain = Mathf.Clamp(gain, 0f, 0.65f);

        if (threat is Player player)
        {
            int id = player.playerState?.playerNumber ?? 0;
            if (state.PlayerTraumaPlayer != id)
            {
                state.PlayerTraumaPlayer = id;
                state.PlayerTraumaStrength = 0f;
                state.PlayerTraumaTicks = 0;
            }
            state.PlayerTraumaStrength = Mathf.Clamp01(
                state.PlayerTraumaStrength + gain);
            state.PlayerTraumaTicks = Mathf.Max(
                state.PlayerTraumaTicks,
                Mathf.RoundToInt(Mathf.Lerp(
                    DesertBatflyTuning.TraumaMinTicks,
                    DesertBatflyTuning.TraumaMaxTicks,
                    state.PlayerTraumaStrength)));
        }
        else if (IsPeach(threat))
        {
            int id = ThreatIdentity(threat);
            if (state.PredatorTraumaId != id)
            {
                state.PredatorTraumaId = id;
                state.PredatorTraumaStrength = 0f;
                state.PredatorTraumaTicks = 0;
            }
            state.PredatorTraumaStrength = Mathf.Clamp01(
                state.PredatorTraumaStrength + gain);
            state.PredatorTraumaTicks = Mathf.Max(
                state.PredatorTraumaTicks,
                Mathf.RoundToInt(Mathf.Lerp(
                    DesertBatflyTuning.TraumaMinTicks,
                    DesertBatflyTuning.TraumaMaxTicks,
                    state.PredatorTraumaStrength)));
        }
    }

    private static float PersistentTraumaStrength(
        DesertBatfly bat,
        Creature threat)
    {
        if (bat == null || threat == null) return 0f;
        DesertBatflyState state = bat.DesertState;

        if (threat is Player player)
        {
            int id = player.playerState?.playerNumber ?? 0;
            return state.PlayerTraumaTicks > 0 &&
                   state.PlayerTraumaPlayer == id
                ? state.PlayerTraumaStrength
                : 0f;
        }

        if (IsPeach(threat))
        {
            int id = ThreatIdentity(threat);
            return state.PredatorTraumaTicks > 0 &&
                   state.PredatorTraumaId == id
                ? state.PredatorTraumaStrength
                : 0f;
        }

        return 0f;
    }

    private static float CombatDrive(DesertBatfly bat, State state)
    {
        return state.Role == SocialRole.Follower
            ? Mathf.Lerp(0.38f, 0.72f, state.Commitment)
            : bat.Personality.VengeanceDrive;
    }

    private static void TickMemory(ref FearMemory memory)
    {
        if (!memory.Active) return;
        if (memory.MemoryTicks > 0) memory.MemoryTicks--;
        if (memory.ShockTicks > 0) memory.ShockTicks--;
        if (memory.PanicRefresh > 0) memory.PanicRefresh--;
        if (memory.AvoidRefresh > 0) memory.AvoidRefresh--;
        if (memory.CorpseReminderCooldown > 0)
            memory.CorpseReminderCooldown--;
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

    private static bool TryGetVengeanceState(
        DesertBatfly bat,
        out State state)
    {
        state = null;
        return bat != null && states.TryGetValue(bat, out state) && state.Active;
    }

    private static void TryDeactivate(DesertBatfly bat, State state)
    {
        if (state == null || !state.Active || state.PlayerFear.Active ||
            state.PredatorFear.Active || state.Vengeance != VengeanceMode.None ||
            bat.DesertState.HasTrauma)
            return;

        state.Active = false;
        activeStates = Mathf.Max(0, activeStates - 1);
    }

    private static void ClearVengeance(State state)
    {
        if (state == null) return;
        state.Vengeance = VengeanceMode.None;
        state.Role = SocialRole.None;
        state.VengeanceTarget = null;
        state.Leader = null;
        state.RescueVictim = null;
        state.RescueTongue = null;
        state.Rage = 0f;
        state.Commitment = 0f;
        state.DamageScale = 1f;
        state.VengeanceTimer = 0;
        state.PassesRemaining = 0;
        state.RescueAttempted = false;
        state.WasRescuePlan = false;
        state.SupportOnly = false;
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

    private static float StableEvent01(
        DesertBatfly bat,
        DesertBatfly victim,
        Creature threat,
        EventKind kind)
    {
        unchecked
        {
            uint x = (uint)bat.Personality.VisualSeed;
            x ^= (uint)(victim?.Personality.VisualSeed ?? 0) * 0x9E3779B9u;
            x ^= (uint)ThreatIdentity(threat) * 0x85EBCA6Bu;
            x ^= (uint)((int)kind + 1) * 0xC2B2AE35u;
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CA68Bu;
            x ^= x >> 16;
            return (x & 0x00FFFFFFu) / 16777215f;
        }
    }

    private static bool WasChainWitness(
        DesertBatfly[] witnesses,
        DesertBatfly bat)
    {
        if (witnesses == null || witnesses.Length == 0 || bat == null)
            return false;
        for (int i = 0; i < witnesses.Length; i++)
            if (witnesses[i] == bat) return true;
        return false;
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
        if (bat?.grabbedBy == null) return false;
        for (int i = 0; i < bat.grabbedBy.Count; i++)
        {
            Creature.Grasp grasp = bat.grabbedBy[i];
            if (grasp?.grabber != null && grasp.grabber is not Fly)
                return true;
        }
        return false;
    }

    private static Vector2 OrbitOffset(
        DesertBatfly bat,
        float width,
        float height)
    {
        float angle =
            (bat.room.game.clock + (bat.Personality.VisualSeed & 1023)) * 0.033f;
        return new Vector2(
            Mathf.Cos(angle) * width,
            45f + Mathf.Sin(angle) * height * 0.55f);
    }

    private static void ForceFlight(
        DesertBatfly bat,
        Vector2 goal,
        float speed)
    {
        if (bat?.room == null) return;

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