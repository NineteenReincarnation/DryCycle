using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DryCycle.Debugging.AI;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal enum DesertBatflySocialMode
{
    None,
    CompanionDrift,
    PassBy,
    SocialChase,
    GroupDrift,
    RoostInvitation,
    PositionNegotiation,
    ChainSocialization
}

internal readonly struct DesertBatflySocialDebugState
{
    internal readonly bool Eligible;
    internal readonly float SocialDrive;
    internal readonly int SocialCooldown;
    internal readonly DesertBatflySocialMode Mode;
    internal readonly int InteractionTicks;
    internal readonly int Duration;
    internal readonly string Partner;
    internal readonly string Anchor;
    internal readonly int MicroFlockId;
    internal readonly int MicroFlockSize;
    internal readonly DesertBatflySocialMode LastInteractionType;
    internal readonly string DecisionReason;
    internal readonly int CandidateCount;
    internal readonly Vector2? RoostTarget;
    internal readonly int NegotiationSide;

    internal DesertBatflySocialDebugState(
        bool eligible,
        float socialDrive,
        int socialCooldown,
        DesertBatflySocialMode mode,
        int interactionTicks,
        int duration,
        string partner,
        string anchor,
        int microFlockId,
        int microFlockSize,
        DesertBatflySocialMode lastInteractionType,
        string decisionReason,
        int candidateCount,
        Vector2? roostTarget,
        int negotiationSide)
    {
        Eligible = eligible;
        SocialDrive = socialDrive;
        SocialCooldown = socialCooldown;
        Mode = mode;
        InteractionTicks = interactionTicks;
        Duration = duration;
        Partner = partner ?? "—";
        Anchor = anchor ?? "—";
        MicroFlockId = microFlockId;
        MicroFlockSize = microFlockSize;
        LastInteractionType = lastInteractionType;
        DecisionReason = decisionReason ?? string.Empty;
        CandidateCount = candidateCount;
        RoostTarget = roostTarget;
        NegotiationSide = negotiationSide;
    }
}

/// <summary>
/// Task 10 neutral social-life layer. This state is deliberately realized-only and
/// non-persistent. Existing DesertBatflyAI remains authoritative for survival, combat,
/// injury and roost commitment; Task 10 only shapes neutral local goals after those
/// systems have had a chance to claim the frame.
/// </summary>
internal static class DesertBatflySocialLife
{
    private const int ScanIntervalMin = 16;
    private const int ScanIntervalMax = 30;
    private const int GroupMin = 3;
    private const int GroupMax = 6;
    private const float SocialRange = 240f;
    private const float CloseNegotiationRange = 62f;

    internal const float GroupSeparationXWeight = 1.00f;
    internal const float GroupSeparationYWeight = 0.24f;

    private enum PairRole
    {
        None,
        Anchor,
        Companion,
        PassA,
        PassB,
        Chaser,
        Chased,
        GroupMember,
        Invitee
    }

    private sealed class State
    {
        internal float Drive;
        internal int Cooldown;
        internal DesertBatflySocialMode Mode;
        internal PairRole Role;
        internal int Ticks;
        internal int Duration;
        internal int NextScanTick;
        internal int ScanSerial;
        internal DesertBatfly Partner;
        internal DesertBatfly Anchor;
        internal DesertBatflySocialRoomRuntime.Reservation Token;
        internal DesertBatflySocialMode LastMode;
        internal long LastPartnerKey = long.MinValue;
        internal string DecisionReason = "initial neutral state";
        internal int CandidateCount;
        internal int Side;
        internal Vector2? RoostTarget;
        internal Room LastRoom;
        internal readonly List<DesertBatfly> GroupScratch = new(GroupMax);
    }

    private readonly struct Choice
    {
        internal readonly DesertBatflySocialMode Mode;
        internal readonly DesertBatfly Partner;
        internal readonly float Weight;

        internal Choice(DesertBatflySocialMode mode, DesertBatfly partner, float weight)
        {
            Mode = mode;
            Partner = partner;
            Weight = Mathf.Max(0f, weight);
        }
    }

    private static ConditionalWeakTable<DesertBatfly, State> states = new();

    internal static void Reset()
    {
        states = new ConditionalWeakTable<DesertBatfly, State>();
        DesertBatflySocialRoomRuntime.Reset();
    }

    internal static void Update(DesertBatfly bat)
    {
        if (bat == null) return;
        State state = states.GetValue(bat, CreateState);
        if (state.Cooldown > 0) state.Cooldown--;

        if (state.LastRoom != null && state.LastRoom != bat.room && state.Mode != DesertBatflySocialMode.None)
            CancelState(bat, state, "room transition", true);
        state.LastRoom = bat.room;

        string block = PriorityBlockReason(bat);
        if (block != null)
        {
            if (state.Mode != DesertBatflySocialMode.None)
                CancelState(bat, state, block, true);
            state.Drive = Mathf.Max(0f, state.Drive - 0.0015f);
            state.DecisionReason = "blocked: " + block;
            return;
        }

        if (state.Mode != DesertBatflySocialMode.None)
        {
            UpdateActive(bat, state);
            return;
        }

        state.Drive = Mathf.Clamp01(state.Drive + SocialDrivePerTick(bat.Personality));
        DesertBatflySocialRoomRuntime.RoomState roomState = DesertBatflySocialRoomRuntime.For(bat.room);
        if (roomState == null) return;
        IReadOnlyList<DesertBatfly> candidates = roomState.Candidates;
        state.CandidateCount = Mathf.Max(0, candidates.Count - 1);

        int clock = bat.room?.game?.clock ?? 0;
        if (state.NextScanTick == 0)
            state.NextScanTick = clock + StableInt(bat.Personality.VisualSeed, 0x19D3, ScanIntervalMin, ScanIntervalMax + 1);
        if (clock < state.NextScanTick) return;
        state.NextScanTick = clock + StableInt(
            bat.Personality.VisualSeed,
            ++state.ScanSerial * 31 + 0x51A7,
            ScanIntervalMin,
            ScanIntervalMax + 1);

        if (roomState.IsReserved(bat))
        {
            state.DecisionReason = "reservation conflict";
            return;
        }

        if (TryStartPositionNegotiation(bat, state, roomState, candidates)) return;
        if (state.Cooldown > 0)
        {
            state.DecisionReason = "social cooldown";
            return;
        }

        float threshold = StartThreshold(bat.Personality);
        if (state.Drive < threshold)
        {
            state.DecisionReason = $"social drive {state.Drive:0.00} below {threshold:0.00}";
            return;
        }

        float activeRatio = roomState.CandidateCount <= 0
            ? 0f
            : roomState.ActiveMemberCount / (float)Mathf.Max(1, roomState.CandidateCount);
        if (activeRatio > 0.68f)
        {
            state.DecisionReason = "room social soft cap";
            return;
        }

        TryScheduleInteraction(bat, state, roomState, candidates, activeRatio);
    }

    internal static void CancelForPriority(DesertBatfly bat, string reason)
    {
        if (bat == null || !states.TryGetValue(bat, out State state) || state.Mode == DesertBatflySocialMode.None)
            return;
        CancelState(bat, state, string.IsNullOrEmpty(reason) ? "higher priority" : reason, true);
    }

    internal static bool TryGetDebugState(DesertBatfly bat, out DesertBatflySocialDebugState debug)
    {
        debug = default;
        if (bat == null || !states.TryGetValue(bat, out State state)) return false;
        int groupSize = state.Token?.Active == true && state.Token.Mode == DesertBatflySocialMode.GroupDrift
            ? state.Token.Members.Count
            : 0;
        debug = new DesertBatflySocialDebugState(
            PriorityBlockReason(bat) == null,
            state.Drive,
            state.Cooldown,
            state.Mode,
            state.Ticks,
            state.Duration,
            Id(state.Partner),
            Id(state.Anchor),
            state.Token?.Active == true && state.Token.Mode == DesertBatflySocialMode.GroupDrift ? state.Token.Id : 0,
            groupSize,
            state.LastMode,
            state.DecisionReason,
            state.CandidateCount,
            state.RoostTarget,
            state.Side);
        return true;
    }

    internal static void SampleTrace(DesertBatfly bat)
    {
        if (bat?.abstractCreature == null || !AIDebugTrace.IsWatched(bat.abstractCreature) ||
            !TryGetDebugState(bat, out DesertBatflySocialDebugState social))
            return;

        float driveBucket = Mathf.Round(social.SocialDrive * 20f) / 20f;
        int cooldownBucket = social.SocialCooldown <= 0 ? 0 : (social.SocialCooldown / 20) * 20;
        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.Social,
            "Task10SocialEligible", social.Eligible, social.DecisionReason);
        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.Social,
            "Task10SocialDrive", driveBucket, "quantized 0.05 SocialDrive bucket");
        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.Social,
            "Task10SocialCooldown", cooldownBucket, "quantized 20-tick cooldown bucket");
        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.Social,
            "Task10SocialMode", social.Mode, social.DecisionReason);
        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.Social,
            "Task10SocialPartner", social.Partner, social.DecisionReason);
        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.Social,
            "Task10MicroFlock", social.MicroFlockId == 0 ? "—" : $"{social.MicroFlockId}:{social.MicroFlockSize}",
            social.DecisionReason);
        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.Social,
            "Task10SocialReason", social.DecisionReason, social.Mode.ToString());
    }

    // Pure helpers are intentionally internal so the managed regression suite can verify
    // Task 10 without constructing a full Unity room/game loop.
    internal static float SocialDrivePerTick(DesertBatflyPersonality personality)
    {
        if (personality == null) return 0f;
        return 0.00175f *
            Mathf.Lerp(0.82f, 1.28f, personality.Conformity) *
            Mathf.Lerp(0.94f, 1.08f, personality.Temperament);
    }

    internal static float StartThreshold(DesertBatflyPersonality personality)
    {
        if (personality == null) return 1f;
        float value = Mathf.Lerp(0.72f, 0.52f, personality.Conformity) -
            Mathf.InverseLerp(0.75f, 1f, personality.Temperament) * 0.04f;
        return Mathf.Clamp(value, 0.46f, 0.78f);
    }

    internal static int StablePairSide(EntityID a, EntityID b)
    {
        long ka = DesertBatflySocialRoomRuntime.Key(a);
        long kb = DesertBatflySocialRoomRuntime.Key(b);
        long low = Math.Min(ka, kb);
        long high = Math.Max(ka, kb);
        unchecked
        {
            ulong x = (ulong)(low * 0x9E3779B1L) ^ (ulong)(high * 0x85EBCA77L);
            x ^= x >> 33;
            x *= 0xff51afd7ed558ccdUL;
            x ^= x >> 33;
            return (x & 1UL) == 0UL ? -1 : 1;
        }
    }

    internal static Vector2 CompanionOffset(int side, float bondStrength)
    {
        float lateral = Mathf.Lerp(34f, 46f, Mathf.Clamp01(bondStrength));
        float vertical = Mathf.Lerp(4f, 8f, 1f - Mathf.Clamp01(bondStrength));
        return new Vector2(Mathf.Sign(side == 0 ? 1 : side) * lateral, vertical);
    }

    internal static bool PriorityAllowsSocialFlags(
        bool danger,
        bool travel,
        bool severeInjury,
        bool stableChain,
        bool passive,
        bool formalAttack,
        bool vanillaPriority)
        => !danger && !travel && !severeInjury && !stableChain && !passive && !formalAttack && !vanillaPriority;

    internal static float PartnerPreference(float conformity, float calmness, float bond, float normalizedDistance)
        => Mathf.Max(0f,
            0.15f + Mathf.Clamp01(conformity) * 0.42f +
            Mathf.Clamp01(calmness) * 0.22f +
            Mathf.Clamp01(bond) * 0.28f -
            Mathf.Clamp01(normalizedDistance) * 0.18f);

    private static State CreateState(DesertBatfly bat)
    {
        return new State
        {
            Drive = Mathf.Lerp(0.08f, 0.30f, Stable01(bat.Personality.VisualSeed, 0x2F13)),
            LastRoom = bat.room
        };
    }

    private static string PriorityBlockReason(DesertBatfly bat)
    {
        if (bat == null || bat.dead || bat.slatedForDeletetion || !bat.Consious || bat.room == null)
            return "unavailable / unconscious";
        if (bat.inShortcut) return "shortcut";
        if (RestrainedByNonFly(bat)) return "restrained by non-Fly";
        if (bat.Emergence?.Active == true) return "emergence";
        if (bat.DesertAI == null || bat.AI == null) return "AI unavailable";
        if (bat.DesertAI.HasImmediateDanger || bat.DesertAI.Mode == DesertBatflyAI.Activity.Escape)
            return "immediate danger";
        if (DesertBatflyTravelNavigation.HasIntent(bat.abstractCreature)) return "Task09 travel priority";
        if (bat.Injury.IsSeverelyInjured || bat.Injury.IsRecovering ||
            bat.DesertAI.Mode == DesertBatflyAI.Activity.InjuryRecovery)
            return "severe injury / recovery";
        if (bat.AI.fleeFromRain || bat.AI.behavior == FlyAI.Behavior.Burrow ||
            bat.AI.luredCounter > 0 || bat.safariControlled)
            return "vanilla priority";
        if (bat.AI.behavior == FlyAI.Behavior.Drop || bat.movMode == Fly.MovementMode.Passive)
            return "Drop / Passive";
        if (bat.AI.behavior == FlyAI.Behavior.Chain || bat.movMode == Fly.MovementMode.Hang ||
            bat.DesertAI.Mode == DesertBatflyAI.Activity.Roost)
            return "roost commitment";
        if (bat.DesertAI.Target != null || bat.DesertAI.FormalAttack ||
            bat.DesertAI.Mode is DesertBatflyAI.Activity.Observe or DesertBatflyAI.Activity.Approach or
                DesertBatflyAI.Activity.Circle or DesertBatflyAI.Activity.FakeDive or DesertBatflyAI.Activity.Dive or
                DesertBatflyAI.Activity.Attach or DesertBatflyAI.Activity.RetaliationCharge or DesertBatflyAI.Activity.Interfere)
            return "formal attack / harassment";
        if (bat.DesertState.Cooldown > 0) return "post-attack cooldown";
        if (DesertBatflyIntimidation.IsExtremeVengeanceActive(bat) ||
            DesertBatflyIntimidation.HasActiveFearSuppression(bat))
            return "fear / vengeance suppression";
        if (ActiveTrauma(bat) >= DesertBatflyTuning.TraumaSevere) return "severe trauma";
        if (bat.AI.behavior != FlyAI.Behavior.Idle && bat.AI.behavior != FlyAI.Behavior.Swarm)
            return "non-neutral vanilla behavior";
        return null;
    }

    private static void TryScheduleInteraction(
        DesertBatfly bat,
        State state,
        DesertBatflySocialRoomRuntime.RoomState roomState,
        IReadOnlyList<DesertBatfly> candidates,
        float activeRatio)
    {
        Choice companion = default;
        Choice passBy = default;
        Choice chase = default;
        Choice roost = default;
        state.GroupScratch.Clear();
        state.GroupScratch.Add(bat);

        for (int i = 0; i < candidates.Count; i++)
        {
            DesertBatfly other = candidates[i];
            if (other == bat || !CanBePartner(bat, other, roomState)) continue;
            Vector2 delta = other.mainBodyChunk.pos - bat.mainBodyChunk.pos;
            float distance = delta.magnitude;
            if (distance > SocialRange) continue;
            if (distance > 70f && !bat.room.VisualContact(bat.mainBodyChunk.pos, other.mainBodyChunk.pos)) continue;

            float bond = Mathf.Max(
                DesertBatflySocialBond.GetBondStrength(bat, other),
                DesertBatflySocialBond.GetBondStrength(other, bat));
            float companionWeight = PartnerPreference(
                bat.Personality.Conformity,
                1f - bat.Personality.Temperament,
                bond,
                Mathf.InverseLerp(55f, SocialRange, distance)) *
                (0.65f + state.Drive * 0.55f) *
                RepeatScale(state, DesertBatflySocialMode.CompanionDrift, other);
            if (companionWeight > companion.Weight)
                companion = new Choice(DesertBatflySocialMode.CompanionDrift, other, companionWeight);

            Vector2 relativeVelocity = other.mainBodyChunk.vel - bat.mainBodyChunk.vel;
            float closing = distance > 0.01f ? -Vector2.Dot(delta / distance, relativeVelocity) : 0f;
            if (distance <= 105f && closing > 0.8f)
            {
                float passWeight = (0.30f + Mathf.Clamp01(closing / 6f) * 0.45f +
                    bat.Personality.Nerve * 0.16f + (1f - bat.Personality.Conformity) * 0.10f) *
                    RepeatScale(state, DesertBatflySocialMode.PassBy, other);
                if (passWeight > passBy.Weight)
                    passBy = new Choice(DesertBatflySocialMode.PassBy, other, passWeight);
            }

            if (bat.Personality.Temperament >= 0.50f && bat.Personality.Nerve >= 0.38f &&
                distance >= 55f && distance <= 190f)
            {
                float chaseWeight = (0.04f + bat.Personality.AggressionDrive * 0.62f +
                    bat.Personality.Nerve * 0.22f) * (0.55f + state.Drive * 0.55f) *
                    RepeatScale(state, DesertBatflySocialMode.SocialChase, other);
                if (chaseWeight > chase.Weight)
                    chase = new Choice(DesertBatflySocialMode.SocialChase, other, chaseWeight);
            }

            if (distance <= 220f && state.GroupScratch.Count < GroupMax && !roomState.IsReserved(other))
                state.GroupScratch.Add(other);
        }

        DesertBatfly roostSource = FindRoostSource(bat, candidates, out int chainSize, out float roostBond);
        if (roostSource != null)
        {
            DesertBatflySocialMode mode = chainSize >= 2
                ? DesertBatflySocialMode.ChainSocialization
                : DesertBatflySocialMode.RoostInvitation;
            float weight = (0.08f + bat.Personality.RoostAffinity * 0.52f +
                bat.Personality.Conformity * 0.24f + (1f - bat.Personality.Temperament) * 0.10f +
                roostBond * 0.20f + Mathf.Clamp01(chainSize / 4f) * 0.12f) *
                (0.55f + state.Drive * 0.60f) * RepeatScale(state, mode, roostSource);
            roost = new Choice(mode, roostSource, weight);
        }

        float groupWeight = state.GroupScratch.Count >= GroupMin
            ? (0.10f + bat.Personality.Conformity * 0.62f +
                (1f - bat.Personality.Temperament) * 0.20f + state.Drive * 0.18f) *
                RepeatScale(state, DesertBatflySocialMode.GroupDrift, null)
            : 0f;
        float capScale = activeRatio <= 0.50f
            ? 1f
            : Mathf.Lerp(1f, 0.22f, Mathf.InverseLerp(0.50f, 0.68f, activeRatio));

        float wc = companion.Weight * capScale;
        float wp = passBy.Weight;
        float wch = chase.Weight * capScale;
        float wg = groupWeight * capScale;
        float wr = roost.Weight;
        float total = wc + wp + wch + wg + wr;
        if (total <= 0.001f)
        {
            state.DecisionReason = "no weighted social candidate";
            return;
        }

        float pick = Stable01(bat.Personality.VisualSeed, state.ScanSerial * 7919 + 0x3A17) * total;
        if ((pick -= wc) < 0f && companion.Partner != null)
        {
            StartCompanion(bat, companion.Partner, roomState);
            return;
        }
        if ((pick -= wp) < 0f && passBy.Partner != null)
        {
            StartPassBy(bat, passBy.Partner, roomState);
            return;
        }
        if ((pick -= wch) < 0f && chase.Partner != null)
        {
            StartChase(bat, chase.Partner, roomState);
            return;
        }
        if ((pick -= wg) < 0f && state.GroupScratch.Count >= GroupMin)
        {
            StartGroup(bat, state, roomState);
            return;
        }
        if (roost.Partner != null)
            StartRoostInvitation(bat, roost.Partner, roost.Mode, state, roomState);
    }

    private static bool TryStartPositionNegotiation(
        DesertBatfly bat,
        State state,
        DesertBatflySocialRoomRuntime.RoomState roomState,
        IReadOnlyList<DesertBatfly> candidates)
    {
        if (state.Cooldown > 0) return false;
        DesertBatfly best = null;
        float bestTime = float.MaxValue;
        for (int i = 0; i < candidates.Count; i++)
        {
            DesertBatfly other = candidates[i];
            if (other == bat || !CanBePartner(bat, other, roomState)) continue;
            Vector2 delta = other.mainBodyChunk.pos - bat.mainBodyChunk.pos;
            float distance = delta.magnitude;
            if (distance > CloseNegotiationRange || distance < 0.01f) continue;
            if (!bat.room.VisualContact(bat.mainBodyChunk.pos, other.mainBodyChunk.pos)) continue;
            Vector2 relative = other.mainBodyChunk.vel - bat.mainBodyChunk.vel;
            float closing = -Vector2.Dot(delta / distance, relative);
            if (closing < 0.7f) continue;
            float time = distance / Mathf.Max(0.7f, closing);
            if (time < bestTime)
            {
                bestTime = time;
                best = other;
            }
        }
        if (best == null ||
            !roomState.TryReservePair(bat, best, DesertBatflySocialMode.PositionNegotiation, out var token))
            return false;

        int side = StablePairSide(bat.abstractCreature.ID, best.abstractCreature.ID);
        AssignPair(
            token,
            bat,
            best,
            DesertBatflySocialMode.PositionNegotiation,
            PairRole.PassA,
            PairRole.PassB,
            StableInt(bat.Personality.VisualSeed ^ best.Personality.VisualSeed, 0x1221, 20, 51),
            side);
        TraceStart(bat, DesertBatflySocialMode.PositionNegotiation, best, "predicted close-spacing conflict");
        return true;
    }

    private static void StartCompanion(
        DesertBatfly initiator,
        DesertBatfly partner,
        DesertBatflySocialRoomRuntime.RoomState roomState)
    {
        if (!roomState.TryReservePair(initiator, partner, DesertBatflySocialMode.CompanionDrift, out var token))
            return;
        long a = DesertBatflySocialRoomRuntime.Key(initiator);
        long b = DesertBatflySocialRoomRuntime.Key(partner);
        DesertBatfly anchor = a <= b ? partner : initiator;
        DesertBatfly companion = anchor == initiator ? partner : initiator;
        float bond = Mathf.Max(
            DesertBatflySocialBond.GetBondStrength(anchor, companion),
            DesertBatflySocialBond.GetBondStrength(companion, anchor));
        int duration = StableInt(
            initiator.Personality.VisualSeed ^ partner.Personality.VisualSeed,
            0x33A9,
            80,
            241) + Mathf.RoundToInt(bond * 30f);
        AssignPair(
            token,
            anchor,
            companion,
            DesertBatflySocialMode.CompanionDrift,
            PairRole.Anchor,
            PairRole.Companion,
            duration,
            StablePairSide(anchor.abstractCreature.ID, companion.abstractCreature.ID));
        TraceStart(initiator, DesertBatflySocialMode.CompanionDrift, partner,
            $"conformity/bond neutral pairing; bond={bond:0.00}");
    }

    private static void StartPassBy(
        DesertBatfly initiator,
        DesertBatfly partner,
        DesertBatflySocialRoomRuntime.RoomState roomState)
    {
        if (!roomState.TryReservePair(initiator, partner, DesertBatflySocialMode.PassBy, out var token))
            return;
        int side = StablePairSide(initiator.abstractCreature.ID, partner.abstractCreature.ID);
        AssignPair(
            token,
            initiator,
            partner,
            DesertBatflySocialMode.PassBy,
            PairRole.PassA,
            PairRole.PassB,
            StableInt(initiator.Personality.VisualSeed ^ partner.Personality.VisualSeed, 0x4553, 20, 49),
            side);
        TraceStart(initiator, DesertBatflySocialMode.PassBy, partner, "closing trajectories / greeting pass");
    }

    private static void StartChase(
        DesertBatfly initiator,
        DesertBatfly partner,
        DesertBatflySocialRoomRuntime.RoomState roomState)
    {
        if (!roomState.TryReservePair(initiator, partner, DesertBatflySocialMode.SocialChase, out var token))
            return;
        DesertBatfly chaser = initiator.Personality.Temperament >= partner.Personality.Temperament
            ? initiator
            : partner;
        DesertBatfly chased = chaser == initiator ? partner : initiator;
        AssignPair(
            token,
            chaser,
            chased,
            DesertBatflySocialMode.SocialChase,
            PairRole.Chaser,
            PairRole.Chased,
            StableInt(initiator.Personality.VisualSeed ^ partner.Personality.VisualSeed, 0x6715, 40, 121),
            StablePairSide(chaser.abstractCreature.ID, chased.abstractCreature.ID));
        TraceStart(chaser, DesertBatflySocialMode.SocialChase, chased, "temperament/nerve play chase");
    }

    private static void StartGroup(
        DesertBatfly initiator,
        State state,
        DesertBatflySocialRoomRuntime.RoomState roomState)
    {
        if (!roomState.TryReserveGroup(state.GroupScratch, out var token)) return;
        int duration = StableInt(initiator.Personality.VisualSeed, state.ScanSerial * 17 + 0x7123, 120, 321);
        for (int i = 0; i < token.Members.Count; i++)
        {
            DesertBatfly member = token.Members[i];
            State memberState = states.GetValue(member, CreateState);
            BeginState(
                memberState,
                DesertBatflySocialMode.GroupDrift,
                PairRole.GroupMember,
                token,
                duration,
                null,
                null,
                StablePairSide(member.abstractCreature.ID, initiator.abstractCreature.ID));
        }
        TraceStart(initiator, DesertBatflySocialMode.GroupDrift, null,
            $"microflock created; size={token.Members.Count}, id={token.Id}");
    }

    private static void StartRoostInvitation(
        DesertBatfly target,
        DesertBatfly source,
        DesertBatflySocialMode mode,
        State state,
        DesertBatflySocialRoomRuntime.RoomState roomState)
    {
        if (!TryFindSocialRoost(target, source, out Vector2 spot))
        {
            state.DecisionReason = "roost invitation rejected: no legal ChainTile/chain target";
            TraceDecision(target, "RoostInvitationRejected", state.DecisionReason);
            return;
        }
        int cap = StableInt(source.Personality.VisualSeed, 0x5411, 2, 5);
        if (!roomState.TryReserveInvitation(target, source, mode, cap, out var token))
        {
            state.DecisionReason = "roost invitation rejected: cap/reservation";
            return;
        }
        BeginState(
            state,
            mode,
            PairRole.Invitee,
            token,
            StableInt(target.Personality.VisualSeed ^ source.Personality.VisualSeed, 0x1957, 80, 221),
            source,
            source,
            StablePairSide(target.abstractCreature.ID, source.abstractCreature.ID));
        state.RoostTarget = spot;
        TraceStart(target, mode, source, $"legal roost target={spot}; invitationCap={cap}");
    }

    private static void AssignPair(
        DesertBatflySocialRoomRuntime.Reservation token,
        DesertBatfly a,
        DesertBatfly b,
        DesertBatflySocialMode mode,
        PairRole roleA,
        PairRole roleB,
        int duration,
        int sideA)
    {
        State sa = states.GetValue(a, CreateState);
        State sb = states.GetValue(b, CreateState);
        BeginState(sa, mode, roleA, token, duration, b, roleA == PairRole.Anchor ? a : null, sideA);
        BeginState(sb, mode, roleB, token, duration, a, roleB == PairRole.Anchor ? b : null, -sideA);
        if (mode == DesertBatflySocialMode.CompanionDrift)
        {
            if (roleA == PairRole.Companion) sa.Anchor = b;
            if (roleB == PairRole.Companion) sb.Anchor = a;
            if (roleA == PairRole.Anchor) sb.Anchor = a;
            if (roleB == PairRole.Anchor) sa.Anchor = b;
        }
    }

    private static void BeginState(
        State state,
        DesertBatflySocialMode mode,
        PairRole role,
        DesertBatflySocialRoomRuntime.Reservation token,
        int duration,
        DesertBatfly partner,
        DesertBatfly anchor,
        int side)
    {
        state.Mode = mode;
        state.Role = role;
        state.Token = token;
        state.Ticks = 0;
        state.Duration = Math.Max(1, duration);
        state.Partner = partner;
        state.Anchor = anchor;
        state.Side = side == 0 ? 1 : Math.Sign(side);
        state.RoostTarget = null;
        state.DecisionReason = "interaction active";
    }

    private static void UpdateActive(DesertBatfly bat, State state)
    {
        if (state.Token == null || !state.Token.Active)
        {
            EndStateOnly(state, "reservation released");
            return;
        }
        string block = PriorityBlockReason(bat);
        if (block != null)
        {
            CancelState(bat, state, block, true);
            return;
        }
        state.Ticks++;
        if (state.Ticks > state.Duration)
        {
            FinishToken(bat, state, "interaction timeout / completed");
            return;
        }

        switch (state.Mode)
        {
            case DesertBatflySocialMode.CompanionDrift:
                UpdateCompanion(bat, state);
                break;
            case DesertBatflySocialMode.PassBy:
                UpdatePassBy(bat, state, false);
                break;
            case DesertBatflySocialMode.PositionNegotiation:
                UpdatePassBy(bat, state, true);
                break;
            case DesertBatflySocialMode.SocialChase:
                UpdateChase(bat, state);
                break;
            case DesertBatflySocialMode.GroupDrift:
                UpdateGroup(bat, state);
                break;
            case DesertBatflySocialMode.RoostInvitation:
            case DesertBatflySocialMode.ChainSocialization:
                UpdateRoostInvitation(bat, state);
                break;
        }
    }

    private static void UpdateCompanion(DesertBatfly bat, State state)
    {
        DesertBatfly anchor = state.Role == PairRole.Anchor ? bat : state.Anchor;
        DesertBatfly companion = state.Role == PairRole.Companion ? bat : state.Partner;
        if (!ValidSocialPeer(anchor, bat) || !ValidSocialPeer(companion, bat) || anchor.room != companion.room)
        {
            CancelState(bat, state, "partner unavailable", true);
            return;
        }
        float distance = Vector2.Distance(anchor.mainBodyChunk.pos, companion.mainBodyChunk.pos);
        if (distance > 285f ||
            (distance > 100f && !bat.room.VisualContact(anchor.mainBodyChunk.pos, companion.mainBodyChunk.pos)))
        {
            CancelState(bat, state, "lost visual contact / companion range", true);
            return;
        }
        if (state.Role == PairRole.Anchor)
        {
            state.DecisionReason = "temporary anchor keeps its neutral vanilla goal";
            return;
        }

        float bond = Mathf.Max(
            DesertBatflySocialBond.GetBondStrength(bat, anchor),
            DesertBatflySocialBond.GetBondStrength(anchor, bat));
        Vector2 velocity = anchor.mainBodyChunk.vel;
        Vector2 backward = velocity.sqrMagnitude > 1f ? -velocity.normalized * 22f : Vector2.zero;
        Vector2 goal = anchor.mainBodyChunk.pos + backward + CompanionOffset(state.Side, bond);
        if (!SocialSteer(bat, goal, Mathf.Lerp(4.2f, 5.1f, bat.Personality.Nerve)))
            CancelState(bat, state, "companion path locally blocked", true);
        else
            state.DecisionReason = "companion side/back offset from temporary anchor";
    }

    private static void UpdatePassBy(DesertBatfly bat, State state, bool negotiation)
    {
        DesertBatfly partner = state.Partner;
        if (!ValidSocialPeer(partner, bat))
        {
            CancelState(bat, state, "partner unavailable", true);
            return;
        }
        float distance = Vector2.Distance(bat.mainBodyChunk.pos, partner.mainBodyChunk.pos);
        if (!negotiation && state.Ticks > 12 && distance > 125f)
        {
            FinishToken(bat, state, "pass-by separation complete");
            return;
        }
        Vector2 ownForward = bat.mainBodyChunk.vel.sqrMagnitude > 1f
            ? bat.mainBodyChunk.vel.normalized
            : Custom.DirVec(partner.mainBodyChunk.pos, bat.mainBodyChunk.pos);
        Vector2 goal = bat.mainBodyChunk.pos +
            ownForward * (negotiation ? 42f : 55f) +
            Vector2.right * state.Side * (negotiation ? 72f : 58f) +
            Vector2.up * (negotiation ? 2f : 5f);
        if (!SocialSteer(bat, goal, negotiation ? 5.2f : 5.6f))
            CancelState(bat, state, "lateral side blocked; vanilla avoidance resumes", true);
        else
            state.DecisionReason = negotiation
                ? "stable opposite-side position negotiation"
                : "stable opposite-side pass-by";
    }

    private static void UpdateChase(DesertBatfly bat, State state)
    {
        DesertBatfly partner = state.Partner;
        if (!ValidSocialPeer(partner, bat))
        {
            CancelState(bat, state, "partner unavailable", true);
            return;
        }
        float distance = Vector2.Distance(bat.mainBodyChunk.pos, partner.mainBodyChunk.pos);
        if (distance > 275f)
        {
            CancelState(bat, state, "social chase range exceeded", true);
            return;
        }

        if (state.Role == PairRole.Chaser)
        {
            Vector2 predicted = partner.mainBodyChunk.pos + partner.mainBodyChunk.vel * 1.15f +
                Vector2.right * state.Side * 22f;
            if (!SocialSteer(bat, predicted, Mathf.Lerp(6.2f, 8.0f, bat.Personality.Nerve)))
                CancelState(bat, state, "chase local path blocked", true);
            else
                state.DecisionReason = "non-contact social chase / predicted offset";
            return;
        }

        Vector2 forward = bat.mainBodyChunk.vel.sqrMagnitude > 1f
            ? bat.mainBodyChunk.vel.normalized
            : Vector2.right * state.Side;
        Vector2 goal = bat.mainBodyChunk.pos +
            forward * 58f +
            Vector2.right * state.Side * 74f +
            Vector2.up * Mathf.Clamp(partner.mainBodyChunk.pos.y - bat.mainBodyChunk.pos.y, -8f, 8f);
        if (!SocialSteer(bat, goal, Mathf.Lerp(5.5f, 7.0f, bat.Personality.Nerve)))
            CancelState(bat, state, "chased local path blocked", true);
        else
            state.DecisionReason = "play chase sidestep; no fear/attack state";
    }

    private static void UpdateGroup(DesertBatfly bat, State state)
    {
        DesertBatflySocialRoomRuntime.Reservation token = state.Token;
        if (token == null || !token.Active || token.Members.Count < GroupMin)
        {
            CancelState(bat, state, "microflock dissolved below three members", true);
            return;
        }

        Vector2 center = Vector2.zero;
        Vector2 averageVelocity = Vector2.zero;
        int count = 0;
        for (int i = 0; i < token.Members.Count; i++)
        {
            DesertBatfly member = token.Members[i];
            if (!ValidSocialPeer(member, bat)) continue;
            center += member.mainBodyChunk.pos;
            averageVelocity += member.mainBodyChunk.vel;
            count++;
        }
        if (count < GroupMin)
        {
            token.Owner.Release(token);
            EndStateOnly(state, "microflock dissolved below three valid members");
            return;
        }
        center /= count;
        averageVelocity /= count;

        Vector2 separation = Vector2.zero;
        for (int i = 0; i < token.Members.Count; i++)
        {
            DesertBatfly member = token.Members[i];
            if (member == bat || !ValidSocialPeer(member, bat)) continue;
            Vector2 delta = bat.mainBodyChunk.pos - member.mainBodyChunk.pos;
            float distance = delta.magnitude;
            float preferred = Mathf.Lerp(42f, 62f, 1f - bat.Personality.Nerve);
            if (distance <= 0.01f || distance >= preferred) continue;
            float strength = 1f - distance / preferred;
            separation.x += Mathf.Sign(delta.x == 0f ? state.Side : delta.x) * strength * GroupSeparationXWeight;
            separation.y += Mathf.Sign(delta.y) * strength * GroupSeparationYWeight;
        }

        Vector2 alignment = averageVelocity.sqrMagnitude > 0.5f
            ? averageVelocity.normalized
            : Vector2.right * state.Side;
        Vector2 cohesion = center - bat.mainBodyChunk.pos;
        Vector2 social =
            alignment * 48f +
            new Vector2(
                Mathf.Clamp(cohesion.x * 0.16f, -28f, 28f),
                Mathf.Clamp(cohesion.y * 0.06f, -10f, 10f)) +
            new Vector2(
                Mathf.Clamp(separation.x * 58f, -70f, 70f),
                Mathf.Clamp(separation.y * 24f, -10f, 10f));
        social.x += state.Side * StableRange(bat.Personality.VisualSeed, token.Id + 0x833, 5f, 15f);
        if (!SocialSteer(bat, bat.mainBodyChunk.pos + social, Mathf.Lerp(4.3f, 5.6f, bat.Personality.Nerve)))
        {
            token.Owner.RemoveGroupMember(token, bat);
            EndStateOnly(state, "member left microflock: local path blocked");
            return;
        }
        state.DecisionReason = $"microflock {token.Id}; alignment + weak cohesion + horizontal separation";
    }

    private static void UpdateRoostInvitation(DesertBatfly bat, State state)
    {
        DesertBatfly source = state.Anchor;
        if (!ValidRoostSource(source, bat))
        {
            CancelState(bat, state, "roost source unavailable", true);
            return;
        }

        if (TryAttachToChainTail(bat, source))
        {
            FinishToken(bat, state, "joined existing legal Fly chain", true);
            return;
        }

        if (!state.RoostTarget.HasValue || !RoostSpotStillLegal(bat, state.RoostTarget.Value))
        {
            if (!TryFindSocialRoost(bat, source, out Vector2 replacement))
            {
                CancelState(bat, state, "roost target invalid / no replacement", true);
                return;
            }
            state.RoostTarget = replacement;
        }

        Vector2 target = state.RoostTarget.Value;
        if (Custom.DistLess(bat.mainBodyChunk.pos, target, 18f))
        {
            CommitTileRoost(bat, target);
            FinishToken(bat, state, "committed to invited legal roost", true);
            return;
        }
        if (!SocialSteer(bat, target, 4.4f))
            CancelState(bat, state, "invited roost locally blocked", true);
        else
            state.DecisionReason = state.Mode == DesertBatflySocialMode.ChainSocialization
                ? "approaching legal roost near existing chain"
                : "responding to nearby roost invitation";
    }

    private static bool SocialSteer(DesertBatfly bat, Vector2 goal, float speed)
    {
        if (bat?.room == null || bat.AI == null || bat.mainBodyChunk == null) return false;
        Vector2 direction = Custom.DirVec(bat.mainBodyChunk.pos, goal);
        if (direction == Vector2.zero) return true;
        Vector2 probe = bat.mainBodyChunk.pos + direction * 25f;
        if (bat.room.GetTile(probe).Solid ||
            (bat.room.terrain != null && bat.room.terrain.Contains(probe)))
        {
            Vector2 leftProbe = bat.mainBodyChunk.pos + Vector2.left * 28f;
            Vector2 rightProbe = bat.mainBodyChunk.pos + Vector2.right * 28f;
            bool leftBlocked = bat.room.GetTile(leftProbe).Solid ||
                (bat.room.terrain != null && bat.room.terrain.Contains(leftProbe));
            bool rightBlocked = bat.room.GetTile(rightProbe).Solid ||
                (bat.room.terrain != null && bat.room.terrain.Contains(rightProbe));
            if (leftBlocked && rightBlocked) return false;
            float desiredSide = Mathf.Sign(direction.x == 0f ? 1f : direction.x);
            if (desiredSide < 0f && leftBlocked) desiredSide = 1f;
            if (desiredSide > 0f && rightBlocked) desiredSide = -1f;
            goal = bat.mainBodyChunk.pos + Vector2.right * desiredSide * 58f + Vector2.up * 6f;
            speed = Mathf.Min(speed, 4.8f);
        }

        bat.Injury.NominalFlightSpeed = speed;
        bat.LoseAllGrasps();
        bat.burrowOrHangSpot = null;
        if (bat.AI.behavior == FlyAI.Behavior.Chain)
            bat.AI.ChangeBehavior(FlyAI.Behavior.Idle);
        else
            bat.AI.behavior = FlyAI.Behavior.Idle;
        bat.AI.followingDijkstraMap = -1;
        bat.movMode = Fly.MovementMode.BatFlight;
        bat.AI.localGoal = goal;
        bat.mainBodyChunk.vel = Vector2.Lerp(
            bat.mainBodyChunk.vel,
            Custom.DirVec(bat.mainBodyChunk.pos, goal) * speed,
            0.18f);
        return true;
    }

    private static bool TryFindSocialRoost(DesertBatfly bat, DesertBatfly source, out Vector2 spot)
    {
        spot = default;
        if (bat?.room == null || bat.AI == null || source?.room != bat.room) return false;
        IntVector2 origin = bat.room.GetTilePosition(source.mainBodyChunk.pos);
        float best = float.MaxValue;
        bool found = false;
        const int radiusX = 7;
        const int radiusY = 5;

        for (int y = -radiusY; y <= radiusY; y++)
        for (int x = -radiusX; x <= radiusX; x++)
        {
            IntVector2 tile = new IntVector2(origin.x + x, origin.y + y);
            if (tile.x <= 0 || tile.x >= bat.room.TileWidth - 1 ||
                tile.y < 4 || tile.y >= bat.room.TileHeight - 1 ||
                !bat.AI.ChainTile(tile))
                continue;
            Vector2 candidate = RoostSpot(bat.room, tile);
            if (!Custom.DistLess(bat.mainBodyChunk.pos, candidate, 230f) ||
                !bat.room.VisualContact(bat.mainBodyChunk.pos, candidate))
                continue;
            float score =
                Vector2.Distance(bat.mainBodyChunk.pos, candidate) +
                Vector2.Distance(source.mainBodyChunk.pos, candidate) * 0.35f -
                Mathf.Min(3, CountRoostingNear(bat.room, candidate, 75f)) * 11f;
            if (score >= best) continue;
            best = score;
            spot = candidate;
            found = true;
        }
        return found;
    }

    private static bool RoostSpotStillLegal(DesertBatfly bat, Vector2 spot)
    {
        if (bat?.room == null || bat.AI == null || !Custom.DistLess(bat.mainBodyChunk.pos, spot, 260f))
            return false;
        IntVector2 tile = bat.room.GetTilePosition(spot);
        return tile.x > 0 && tile.x < bat.room.TileWidth - 1 &&
            tile.y >= 4 && tile.y < bat.room.TileHeight - 1 && bat.AI.ChainTile(tile);
    }

    private static Vector2 RoostSpot(Room room, IntVector2 tile)
    {
        Room.Tile current = room.GetTile(tile);
        Vector2 middle = room.MiddleOfTile(tile);
        return current.horizontalBeam
            ? new Vector2(middle.x, middle.y - 4f)
            : middle + Vector2.up * 10f;
    }

    private static void CommitTileRoost(DesertBatfly bat, Vector2 spot)
    {
        bat.LoseAllGrasps();
        bat.AI.followingDijkstraMap = -1;
        bat.AI.ChangeBehavior(FlyAI.Behavior.Chain);
        bat.burrowOrHangSpot = spot;
        bat.movMode = Fly.MovementMode.Hang;
        bat.mainBodyChunk.vel *= 0.5f;
    }

    private static bool TryAttachToChainTail(DesertBatfly bat, DesertBatfly source)
    {
        if (!ValidRoostSource(source, bat) || bat.AI == null) return false;
        Fly tail = source.LastInChain();
        if (tail == null || tail == bat || ChainLength(source) >= 6 || !bat.AI.CanIHangFromThisFly(tail))
            return false;
        if (!Custom.DistLess(tail.mainBodyChunk.pos, bat.mainBodyChunk.pos, 20f)) return false;
        if (bat.room.GetTile(tail.mainBodyChunk.pos + new Vector2(0f, -20f)).Terrain == Room.Tile.TerrainType.Solid)
            return false;

        bat.AI.ChangeBehavior(FlyAI.Behavior.Chain);
        bat.Grab(
            tail,
            0,
            0,
            Creature.Grasp.Shareability.NonExclusive,
            1f,
            overrideEquallyDominant: false,
            pacifying: false);
        bat.CheckChainForLoops();
        bat.movMode = Fly.MovementMode.Hang;
        return true;
    }

    private static int ChainLength(Fly source)
    {
        Fly member = source?.FirstInChain();
        int count = 0;
        while (member != null && count < 16)
        {
            count++;
            member = member.NextInChain();
        }
        return count;
    }

    private static DesertBatfly FindRoostSource(
        DesertBatfly bat,
        IReadOnlyList<DesertBatfly> candidates,
        out int chainSize,
        out float bond)
    {
        chainSize = 0;
        bond = 0f;
        DesertBatfly best = null;
        float bestScore = float.MinValue;
        for (int i = 0; i < candidates.Count; i++)
        {
            DesertBatfly other = candidates[i];
            if (other == bat || !ValidRoostSource(other, bat)) continue;
            float distance = Vector2.Distance(bat.mainBodyChunk.pos, other.mainBodyChunk.pos);
            if (distance > 190f ||
                (distance > 75f && !bat.room.VisualContact(bat.mainBodyChunk.pos, other.mainBodyChunk.pos)))
                continue;
            int size = ChainLength(other);
            float pairBond = Mathf.Max(
                DesertBatflySocialBond.GetBondStrength(bat, other),
                DesertBatflySocialBond.GetBondStrength(other, bat));
            float score =
                (1f - distance / 190f) * 0.55f +
                Mathf.Clamp01(size / 4f) * 0.25f +
                pairBond * 0.20f;
            if (score <= bestScore) continue;
            bestScore = score;
            best = other;
            chainSize = size;
            bond = pairBond;
        }
        return best;
    }

    private static int CountRoostingNear(Room room, Vector2 point, float radius)
    {
        if (room == null || !DesertSwarmRoom.TryGet(room, out DesertSwarmRoom colony)) return 0;
        int count = 0;
        List<Fly> flies = colony.Hive.flies;
        for (int i = 0; i < flies.Count; i++)
        {
            if (flies[i] is DesertBatfly bat && bat.room == room &&
                bat.AI?.behavior == FlyAI.Behavior.Chain &&
                Custom.DistLess(bat.mainBodyChunk.pos, point, radius))
                count++;
        }
        return count;
    }

    private static bool ValidRoostSource(DesertBatfly source, DesertBatfly observer)
        => source != null && observer != null && source != observer && source.room == observer.room &&
           !source.dead && source.Consious && !source.inShortcut &&
           source.AI?.behavior == FlyAI.Behavior.Chain;

    private static bool CanBePartner(
        DesertBatfly source,
        DesertBatfly candidate,
        DesertBatflySocialRoomRuntime.RoomState roomState)
    {
        if (!IsNeutralCandidate(candidate) || candidate == source || candidate.room != source.room ||
            roomState.IsReserved(candidate))
            return false;
        return SameRipple(source, candidate);
    }

    private static bool IsNeutralCandidate(DesertBatfly bat)
    {
        if (!DesertBatflySocialRoomRuntime.ValidMember(bat) || !bat.Consious || bat.AI == null || bat.DesertAI == null)
            return false;
        if (DesertBatflyTravelNavigation.HasIntent(bat.abstractCreature) ||
            bat.DesertAI.HasImmediateDanger ||
            bat.Injury.IsSeverelyInjured ||
            bat.Injury.IsRecovering ||
            bat.DesertAI.Target != null ||
            bat.DesertAI.FormalAttack ||
            bat.DesertState.Cooldown > 0 ||
            bat.AI.fleeFromRain ||
            bat.AI.luredCounter > 0 ||
            bat.safariControlled ||
            bat.AI.behavior == FlyAI.Behavior.Chain ||
            bat.AI.behavior == FlyAI.Behavior.Burrow ||
            bat.AI.behavior == FlyAI.Behavior.Drop ||
            bat.movMode == Fly.MovementMode.Passive)
            return false;
        return bat.AI.behavior == FlyAI.Behavior.Idle || bat.AI.behavior == FlyAI.Behavior.Swarm;
    }

    private static bool ValidSocialPeer(DesertBatfly peer, DesertBatfly observer)
        => peer != null && observer != null && peer.room == observer.room &&
           SameRipple(peer, observer) && IsNeutralCandidate(peer);

    private static bool SameRipple(DesertBatfly a, DesertBatfly b)
        => a?.abstractCreature != null && b?.abstractCreature != null &&
           (a.abstractCreature.rippleLayer == b.abstractCreature.rippleLayer ||
            a.abstractCreature.rippleBothSides ||
            b.abstractCreature.rippleBothSides);

    private static void FinishToken(DesertBatfly bat, State state, string reason, bool preserveRoost = false)
    {
        DesertBatflySocialRoomRuntime.Reservation token = state.Token;
        if (token == null)
        {
            EndStateOnly(state, reason);
            return;
        }
        var members = new List<DesertBatfly>(token.Members);
        token.Owner.Release(token);
        for (int i = 0; i < members.Count; i++)
        {
            DesertBatfly member = members[i];
            if (member == null || !states.TryGetValue(member, out State memberState)) continue;
            DesertBatflySocialMode completed = memberState.Mode;
            memberState.LastMode = completed;
            memberState.LastPartnerKey = memberState.Partner != null
                ? DesertBatflySocialRoomRuntime.Key(memberState.Partner)
                : long.MinValue;
            memberState.Drive = Mathf.Clamp01(memberState.Drive * 0.22f);
            memberState.Cooldown = SocialCooldown(member, completed, true);
            EndStateOnly(memberState, reason);
            if (member.abstractCreature != null && AIDebugTrace.IsWatched(member.abstractCreature))
                AIDebugTrace.Record(member.abstractCreature, AIDebugEventCategory.Social,
                    "SocialInteractionCompleted", completed, reason);
        }
        if (preserveRoost && bat?.AI?.behavior == FlyAI.Behavior.Chain)
            state.DecisionReason = reason;
    }

    private static void CancelState(DesertBatfly bat, State state, string reason, bool releaseToken)
    {
        DesertBatflySocialRoomRuntime.Reservation token = state.Token;
        if (releaseToken && token?.Active == true)
        {
            var members = new List<DesertBatfly>(token.Members);
            token.Owner.Release(token);
            for (int i = 0; i < members.Count; i++)
            {
                DesertBatfly member = members[i];
                if (member == null || !states.TryGetValue(member, out State memberState)) continue;
                DesertBatflySocialMode cancelled = memberState.Mode;
                memberState.LastMode = cancelled;
                memberState.LastPartnerKey = memberState.Partner != null
                    ? DesertBatflySocialRoomRuntime.Key(memberState.Partner)
                    : long.MinValue;
                memberState.Drive = Mathf.Max(0.12f, memberState.Drive * 0.55f);
                memberState.Cooldown = SocialCooldown(member, cancelled, false);
                EndStateOnly(memberState, reason);
                if (member.abstractCreature != null && AIDebugTrace.IsWatched(member.abstractCreature))
                    AIDebugTrace.Record(member.abstractCreature, AIDebugEventCategory.Social,
                        "SocialInteractionCancelled", cancelled, reason);
            }
            return;
        }

        DesertBatflySocialMode mode = state.Mode;
        state.LastMode = mode;
        state.Drive = Mathf.Max(0.12f, state.Drive * 0.55f);
        state.Cooldown = SocialCooldown(bat, mode, false);
        EndStateOnly(state, reason);
    }

    private static void EndStateOnly(State state, string reason)
    {
        state.Mode = DesertBatflySocialMode.None;
        state.Role = PairRole.None;
        state.Token = null;
        state.Ticks = 0;
        state.Duration = 0;
        state.Partner = null;
        state.Anchor = null;
        state.Side = 0;
        state.RoostTarget = null;
        state.DecisionReason = reason;
    }

    private static int SocialCooldown(DesertBatfly bat, DesertBatflySocialMode mode, bool completed)
    {
        int salt = (int)mode * 193 + (completed ? 0x71 : 0x35);
        int min = mode == DesertBatflySocialMode.SocialChase ? 220 : 120;
        int max = mode == DesertBatflySocialMode.SocialChase ? 401 : 321;
        int value = StableInt(bat?.Personality?.VisualSeed ?? 0, salt, min, max);
        if (bat != null)
            value = Mathf.RoundToInt(value * Mathf.Lerp(1.10f, 0.90f, bat.Personality.Conformity));
        return Mathf.Clamp(value, 90, 420);
    }

    private static float RepeatScale(State state, DesertBatflySocialMode mode, DesertBatfly partner)
    {
        float scale = state.LastMode == mode ? 0.38f : 1f;
        if (partner != null && state.LastPartnerKey == DesertBatflySocialRoomRuntime.Key(partner))
            scale *= mode == DesertBatflySocialMode.SocialChase ? 0.25f : 0.55f;
        return scale;
    }

    private static bool RestrainedByNonFly(DesertBatfly bat)
    {
        if (bat?.grabbedBy == null) return false;
        for (int i = 0; i < bat.grabbedBy.Count; i++)
        {
            if (bat.grabbedBy[i]?.grabber != null && bat.grabbedBy[i].grabber is not Fly)
                return true;
        }
        return false;
    }

    private static float ActiveTrauma(DesertBatfly bat)
        => Mathf.Max(
            bat.DesertState.PlayerTraumaTicks > 0 ? bat.DesertState.PlayerTraumaStrength : 0f,
            bat.DesertState.PredatorTraumaTicks > 0 ? bat.DesertState.PredatorTraumaStrength : 0f);

    private static void TraceStart(
        DesertBatfly bat,
        DesertBatflySocialMode mode,
        DesertBatfly partner,
        string reason)
    {
        if (bat?.abstractCreature == null || !AIDebugTrace.IsWatched(bat.abstractCreature)) return;
        string details = partner == null
            ? reason
            : $"partner={AIDebugFormat.Creature(partner)}; {reason}";
        AIDebugTrace.Record(bat.abstractCreature, AIDebugEventCategory.Social,
            "SocialInteractionStarted", mode, details);
        string specific = mode switch
        {
            DesertBatflySocialMode.CompanionDrift => "CompanionDriftStarted",
            DesertBatflySocialMode.PassBy => "PassByStarted",
            DesertBatflySocialMode.SocialChase => "SocialChaseStarted",
            DesertBatflySocialMode.GroupDrift => "MicroFlockCreated",
            DesertBatflySocialMode.RoostInvitation => "RoostInvitationAccepted",
            DesertBatflySocialMode.PositionNegotiation => "PositionNegotiationStarted",
            DesertBatflySocialMode.ChainSocialization => "RoostInvitationAccepted",
            _ => "SocialInteractionStarted"
        };
        if (specific != "SocialInteractionStarted")
            AIDebugTrace.Record(bat.abstractCreature, AIDebugEventCategory.Social, specific, mode, details);
    }

    private static void TraceDecision(DesertBatfly bat, string key, string reason)
    {
        if (bat?.abstractCreature == null || !AIDebugTrace.IsWatched(bat.abstractCreature)) return;
        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.Social, key, reason, reason);
    }

    private static string Id(DesertBatfly bat)
        => bat?.abstractCreature == null
            ? "—"
            : $"{bat.abstractCreature.ID.spawner}:{bat.abstractCreature.ID.number}";

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

    private static int StableInt(int seed, int salt, int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive) return minInclusive;
        return minInclusive + Mathf.FloorToInt(Stable01(seed, salt) * (maxExclusive - minInclusive));
    }

    private static float StableRange(int seed, int salt, float min, float max)
        => Mathf.Lerp(min, max, Stable01(seed, salt));
}
