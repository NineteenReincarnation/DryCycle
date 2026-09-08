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
/// Persistent trauma is kept in DB_State, not this weak table. This class owns only
/// realized runtime steering and fixed-size fear state; there is no per-frame observer graph.
/// </summary>
internal static partial class DB_FearRuntime
{
    private enum EventKind { PlayerKill, PredatorCapture, PredatorKill }
    private enum VengeanceMode { None, Waiting, Observe, Circle, Feint, RescueCharge, Charge, Withdraw }
    private enum VengeanceParticipation { None, Avenger, Supporter }

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

    // One event has one actual leader. The runtime group cap of three means the normal
    // social form remains one true avenger plus zero, one or two followers even if
    // additional deaths/captures occur before the first group has finished withdrawing.
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
        internal VengeanceParticipation Role;
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

            foreach (Fly other in DB_SwarmRoom.For(room).Hive.flies)
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

    /// <summary>
    /// Read-only R3 query for current Vengeance facts. It never creates State and never
    /// changes Vengeance commitment; consumers no longer reflect into this runtime.
    /// </summary>
    // Read-only: fear checks must never create a morale state, especially for corpses.
    internal static bool HasActiveFearSuppression(DesertBatfly bat)
    {
        if (bat == null || !states.TryGetValue(bat, out State state) || !state.Active) return false;
        return state.PlayerFear.ShockTicks > 0 || state.PredatorFear.ShockTicks > 0 ||
            (state.PlayerFear.Active && (state.PlayerFear.Strength >= 0.22f || state.PlayerFear.CorpseReminderCooldown > 0)) ||
            (state.PredatorFear.Active && (state.PredatorFear.Strength >= 0.10f || state.PredatorFear.CorpseReminderCooldown > 0));
    }

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

    internal static void UpdateState(DesertBatfly bat)
    {
        if (bat == null) return;

        DB_State persistent = bat.DesertState;
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

        if (bat.Injury.BlocksCombat) ClearVengeance(state);
        EnforcePersistentTrauma(bat, state);

        bool vengeanceControlsMovement = state.Vengeance is
            VengeanceMode.Observe or VengeanceMode.Circle or VengeanceMode.Feint or
            VengeanceMode.RescueCharge or VengeanceMode.Charge or VengeanceMode.Withdraw;

        if (!vengeanceControlsMovement)
        {
            EnforceFear(bat, ref state.PlayerFear, true);
            EnforceFear(bat, ref state.PredatorFear, false);
        }

        // R3: Vengeance phase timers/contact/movement are frozen while another owner wins.
        // Fear memory, trauma and collapse checks above continue to tick independently.
        TryDeactivate(bat, state);
    }

    // Compatibility/readability surface: state tick only, never Vengeance locomotion.
    internal static void Update(DesertBatfly bat) => UpdateState(bat);

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

        victim.Injury.BeginCapture(predator);
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
        bool victimWasLeader = victimHadState && victimState.Role == VengeanceParticipation.Avenger &&
                               victimState.Vengeance != VengeanceMode.None;
        bool victimWasFollower = victimHadState && victimState.Role == VengeanceParticipation.Supporter &&
                                 victimState.Vengeance != VengeanceMode.None;
        DesertBatfly victimLeader = victimWasFollower ? victimState.Leader : null;

        bool suppressNewVengeance = revengeFailed || victimWasLeader;
        if (suppressNewVengeance)
            threatScale = Mathf.Min(1.35f, threatScale * 1.28f);

        List<DesertBatfly> bats = new(
            DB_Tuning.HivePopulation + DB_Tuning.CurvePopulation);
        foreach (Fly other in DB_SwarmRoom.For(room).Hive.flies)
        {
            if (other is DesertBatfly bat && bat != victim && !bat.dead &&
                bat.room == room && bat.Consious)
                bats.Add(bat);
        }
        if (bats.Count == 0) return;

        if (victimWasLeader)
        {
            for (int i = 0; i < bats.Count; i++)
            {
                DesertBatfly bat = bats[i];
                if (!TryGetVengeanceState(bat, out State follower) ||
                    follower.Role != VengeanceParticipation.Supporter || follower.Leader != victim ||
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
                    social.Role != VengeanceParticipation.Supporter || social.Leader != victimLeader ||
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
            if (tier[i] < 0) continue;
            // Apply the bonus before ReceiveFear so its existing PTSD collapse checks see it.
            if (tier[i] <= 1 && kind != EventKind.PredatorCapture)
                DB_SocialBond.OnBondPartnerDeath(bats[i], victim, threat);
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

        DesertBatfly previousEscape = null;
        for (int i = 0; i < bats.Count; i++)
        {
            if (tier[i] < 0 || !DB_SocialBond.CanRespond(bats[i])) continue;
            if (tier[i] <= 1 && !IsExtremeVengeanceActive(bats[i]))
            {
                if (previousEscape != null && Vector2.Distance(previousEscape.mainBodyChunk.pos,
                    bats[i].mainBodyChunk.pos) <= 120f)
                    DB_SocialBond.AddBond(bats[i], previousEscape, 0.01f);
                previousEscape = bats[i];
            }
        }

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
        if (tier != 0)
        {
            TraceIndirectFearSuppressed(bat, tier);
            return;
        }

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
        bool followingThisThreat = state.Role == VengeanceParticipation.Supporter &&
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
             PersistentTraumaStrength(bat, threat) >= DB_Tuning.TraumaSevere))
        {
            ClearVengeance(state);
        }

        bat.DesertAI.ThreatenedAt(threat, eventPosition, false, false);
        DB_SignalRuntime.EmitAlarm(
            bat,
            threat,
            eventPosition,
            Custom.DirVec(bat.mainBodyChunk.pos, eventPosition),
            Mathf.Clamp01(0.62f + Mathf.Clamp(threatScale, 0f, 1.5f) * 0.20f),
            "direct Intimidation witness emits sole indirect Alarm generation");
    }

    private static void TraceIndirectFearSuppressed(DesertBatfly bat, int tier)
    {
        if (bat?.abstractCreature == null ||
            !DryCycle.Debugging.AI.AIDebugTrace.IsWatched(bat.abstractCreature))
            return;
        DryCycle.Debugging.AI.AIDebugTrace.Record(
            bat.abstractCreature,
            DryCycle.Debugging.AI.AIDebugEventCategory.Social,
            "LegacyIndirectFearSuppressed",
            $"tier={tier}",
            "Secondary/Chain fear no longer applies Trauma/Fear directly; Alarm perception owns indirect propagation");
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
        DB_State persistent = bat.DesertState;
        if (!persistent.HasTrauma) return;

        Creature currentTarget = bat.DesertAI.Target;
        if (currentTarget != null &&
            PersistentTraumaStrength(bat, currentTarget) >=
            DB_Tuning.TraumaAggressionBlock)
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
        if (strength < DB_Tuning.TraumaAggressionBlock) return;

        if (state.VengeanceTarget == threat)
            ClearVengeance(state);
        bat.DesertAI.SuppressHostility(threat);

        float fearDistance = Mathf.Lerp(
            DB_Tuning.TraumaFearMinDistance,
            DB_Tuning.TraumaFearMaxDistance,
            strength) *
            Mathf.Lerp(0.95f, 1.18f, bat.Personality.Conformity);
        if (!Custom.DistLess(
                bat.mainBodyChunk.pos,
                threat.mainBodyChunk.pos,
                fearDistance) || state.TraumaRetreatRefresh > 0)
            return;

        state.TraumaRetreatRefresh = TraumaRetreatRefreshTicks;
        bat.DesertAI.Threatened(threat, false);
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

    internal static void AddTrauma(
        DesertBatfly bat,
        Creature threat,
        float gain)
    {
        if (bat == null || threat == null || gain <= 0f) return;
        DB_State state = bat.DesertState;
        gain = Mathf.Clamp(gain, 0f, 0.65f);

        if (threat is Player player)
        {
            int id = player.playerState?.playerNumber ?? 0;
            if (state.PlayerTraumaPlayer != id)
            {
                if (state.PlayerTraumaTicks > 0 && state.PlayerTraumaStrength > gain)
                    return;
                state.PlayerTraumaPlayer = id;
                state.PlayerTraumaStrength = 0f;
                state.PlayerTraumaTicks = 0;
            }
            state.PlayerTraumaStrength = Mathf.Clamp01(
                state.PlayerTraumaStrength + gain);
            state.PlayerTraumaTicks = Mathf.Max(
                state.PlayerTraumaTicks,
                Mathf.RoundToInt(Mathf.Lerp(
                    DB_Tuning.TraumaMinTicks,
                    DB_Tuning.TraumaMaxTicks,
                    state.PlayerTraumaStrength)));
        }
        else if (IsPeach(threat))
        {
            int id = ThreatIdentity(threat);
            if (state.PredatorTraumaId != id)
            {
                if (state.PredatorTraumaTicks > 0 && state.PredatorTraumaStrength > gain)
                    return;
                state.PredatorTraumaId = id;
                state.PredatorTraumaStrength = 0f;
                state.PredatorTraumaTicks = 0;
            }
            state.PredatorTraumaStrength = Mathf.Clamp01(
                state.PredatorTraumaStrength + gain);
            state.PredatorTraumaTicks = Mathf.Max(
                state.PredatorTraumaTicks,
                Mathf.RoundToInt(Mathf.Lerp(
                    DB_Tuning.TraumaMinTicks,
                    DB_Tuning.TraumaMaxTicks,
                    state.PredatorTraumaStrength)));
        }
    }

    private static float PersistentTraumaStrength(
        DesertBatfly bat,
        Creature threat)
    {
        if (bat == null || threat == null) return 0f;
        DB_State state = bat.DesertState;

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

    private static void TryDeactivate(DesertBatfly bat, State state)
    {
        if (state == null || !state.Active || state.PlayerFear.Active ||
            state.PredatorFear.Active || state.Vengeance != VengeanceMode.None ||
            bat.DesertState.HasTrauma)
            return;

        state.Active = false;
        activeStates = Mathf.Max(0, activeStates - 1);
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

}
