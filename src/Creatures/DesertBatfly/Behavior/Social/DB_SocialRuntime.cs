using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DryCycle.Debugging.AI;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal enum DB_SocialMode
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

/// <summary>
/// Broad need family used only to diversify formal social choices. SocialDrive answers
/// "do I want social contact now?"; the fatigue axes answer "what kind of contact have I
/// recently had enough of?". PositionNegotiation is coordination, not need satisfaction.
/// </summary>
internal enum DB_SocialNeedAxis
{
    None,
    Affiliation,
    Play,
    Greeting
}

internal readonly struct DB_SocialDebugState
{
    internal readonly bool Eligible;
    internal readonly float SocialDrive;
    internal readonly int SocialCooldown;
    internal readonly int NegotiationCooldown;
    internal readonly float AffiliationFatigue;
    internal readonly float PlayFatigue;
    internal readonly float GreetingFatigue;
    internal readonly DB_SocialMode Mode;
    internal readonly int InteractionTicks;
    internal readonly int Duration;
    internal readonly string Partner;
    internal readonly string Anchor;
    internal readonly int MicroFlockId;
    internal readonly int MicroFlockSize;
    internal readonly DB_SocialMode LastInteractionType;
    internal readonly DB_SocialMode RecentInteraction0;
    internal readonly DB_SocialMode RecentInteraction1;
    internal readonly DB_SocialMode RecentInteraction2;
    internal readonly string DecisionReason;
    internal readonly int CandidateCount;
    internal readonly Vector2? RoostTarget;
    internal readonly int NegotiationSide;

    internal DB_SocialDebugState(
        bool eligible,
        float socialDrive,
        int socialCooldown,
        int negotiationCooldown,
        float affiliationFatigue,
        float playFatigue,
        float greetingFatigue,
        DB_SocialMode mode,
        int interactionTicks,
        int duration,
        string partner,
        string anchor,
        int microFlockId,
        int microFlockSize,
        DB_SocialMode lastInteractionType,
        DB_SocialMode recentInteraction0,
        DB_SocialMode recentInteraction1,
        DB_SocialMode recentInteraction2,
        string decisionReason,
        int candidateCount,
        Vector2? roostTarget,
        int negotiationSide)
    {
        Eligible = eligible;
        SocialDrive = socialDrive;
        SocialCooldown = socialCooldown;
        NegotiationCooldown = negotiationCooldown;
        AffiliationFatigue = Mathf.Clamp01(affiliationFatigue);
        PlayFatigue = Mathf.Clamp01(playFatigue);
        GreetingFatigue = Mathf.Clamp01(greetingFatigue);
        Mode = mode;
        InteractionTicks = interactionTicks;
        Duration = duration;
        Partner = partner ?? "—";
        Anchor = anchor ?? "—";
        MicroFlockId = microFlockId;
        MicroFlockSize = microFlockSize;
        LastInteractionType = lastInteractionType;
        RecentInteraction0 = recentInteraction0;
        RecentInteraction1 = recentInteraction1;
        RecentInteraction2 = recentInteraction2;
        DecisionReason = decisionReason ?? string.Empty;
        CandidateCount = candidateCount;
        RoostTarget = roostTarget;
        NegotiationSide = negotiationSide;
    }
}

/// <summary>
/// Formal realized-only social-event layer. Background peer drift, loose flocking and short
/// swarm motion belong to DB_NeutralBehaviorRuntime; this domain owns only temporary reserved
/// interactions such as companion drift, greeting passes, play chase, microflocks and roost
/// invitations. Vanilla Fly.BatFlight remains the locomotion implementation.
///
/// One SocialDrive controls overall event cadence. Three temporary need-fatigue axes and a
/// three-event history diversify the selected event without erasing personality preferences.
/// A completed interaction satisfies SocialDrive by an amount appropriate to its duration and
/// meaning; PositionNegotiation consumes no SocialDrive because it is collision coordination.
/// </summary>
internal static class DB_SocialRuntime
{
    private const int ScanIntervalMin = 16;
    private const int ScanIntervalMax = 30;
    private const int GroupMin = 3;
    private const int GroupMax = 5;
    private const float SocialRange = 240f;
    private const float CloseNegotiationRange = 62f;
    private const float GroupCloseSeparationRadius = 52f;

    private const float AffiliationFatigueRecovery = 0.00155f;
    private const float PlayFatigueRecovery = 0.00135f;
    private const float GreetingFatigueRecovery = 0.00220f;
    private const float FatigueWeightFloor = 0.32f;

    // Retained as the ordinary microflock bias. A second isotropic close-separation term below
    // prevents actual overlap/vertical stacking without turning the whole formation vertical.
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
        internal int NegotiationCooldown;
        internal float AffiliationFatigue;
        internal float PlayFatigue;
        internal float GreetingFatigue;

        internal DB_SocialMode Mode;
        internal PairRole Role;
        internal int Ticks;
        internal int Duration;
        internal int NextScanTick;
        internal int ScanSerial;
        internal DB_Creature Partner;
        internal DB_Creature Anchor;
        internal DB_SocialRoomRuntime.Reservation Token;
        internal bool InitiatedInteraction;

        internal DB_SocialMode LastMode;
        internal DB_SocialMode RecentMode0;
        internal DB_SocialMode RecentMode1;
        internal DB_SocialMode RecentMode2;
        internal long RecentPartner0 = long.MinValue;
        internal long RecentPartner1 = long.MinValue;
        internal long RecentPartner2 = long.MinValue;

        internal string DecisionReason = "initial neutral state";
        internal int CandidateCount;
        internal int Side;
        internal DB_RoostAnchor? RoostAnchor;
        internal Vector2? ChainApproachTarget;
        internal Room LastRoom;
        internal readonly List<DB_Creature> GroupScratch = new(GroupMax);
    }

    private readonly struct Choice
    {
        internal readonly DB_SocialMode Mode;
        internal readonly DB_Creature Partner;
        internal readonly float Weight;

        internal Choice(DB_SocialMode mode, DB_Creature partner, float weight)
        {
            Mode = mode;
            Partner = partner;
            Weight = Mathf.Max(0f, weight);
        }
    }

    private static ConditionalWeakTable<DB_Creature, State> states = new();

    internal static void Reset()
    {
        states = new ConditionalWeakTable<DB_Creature, State>();
        DB_SocialRoomRuntime.Reset();
    }

    internal static void RefreshState(DB_Creature bat)
    {
        if (bat == null) return;
        State state = states.GetValue(bat, CreateState);
        if (state.Cooldown > 0) state.Cooldown--;
        if (state.NegotiationCooldown > 0) state.NegotiationCooldown--;

        if (state.LastRoom != null && state.LastRoom != bat.room && state.Mode != DB_SocialMode.None)
            CancelForPriorityState(bat, state, "room transition");
        state.LastRoom = bat.room;

        // Need fatigue is satiation, not a permission timer. It recovers whenever the bat is not
        // actively socializing, including while danger/weather temporarily suppresses social life.
        if (state.Mode == DB_SocialMode.None)
            RecoverNeedFatigue(state);

        string block = PriorityBlockReason(bat);
        if (block != null)
        {
            if (state.Mode != DB_SocialMode.None)
                CancelForPriorityState(bat, state, block);
            state.Drive = Mathf.Max(0f, state.Drive - 0.0015f);
            state.DecisionReason = block;
            return;
        }

        // Active interaction timers and all SocialSteer/roost-join writes are advanced only
        // by ApplyOwnedBehavior after Arbiter selected Social. Preemption therefore cannot
        // progress a social interaction in the background.
        if (state.Mode != DB_SocialMode.None) return;

        float traumaScale = Mathf.Lerp(
            1f,
            0.58f,
            Mathf.InverseLerp(0.18f, DB_Tuning.TraumaSevere, ActiveTrauma(bat)));
        state.Drive = Mathf.Clamp01(state.Drive + SocialDrivePerTick(bat.Personality) * traumaScale *
            DB_EnvironmentalPolicy.SocialDriveScale(bat));

        DB_SocialRoomRuntime.RoomState roomState = DB_SocialRoomRuntime.For(bat.room);
        if (roomState == null) return;
        IReadOnlyList<DB_Creature> candidates = roomState.Candidates;
        state.CandidateCount = Mathf.Max(0, candidates.Count - 1);

        int clock = bat.room?.game?.clock ?? 0;
        if (state.NextScanTick == 0)
            state.NextScanTick = clock + StableInt(
                bat.Personality.VisualSeed, 0x19D3, ScanIntervalMin, ScanIntervalMax + 1);
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

        // Collision coordination is deliberately outside the SocialDrive economy. It can run
        // while the ordinary social cooldown is active, but has its own short anti-ping-pong gate.
        if (TryStartPositionNegotiation(bat, state, roomState, candidates)) return;

        if (state.Cooldown > 0)
        {
            state.DecisionReason = "social cooldown";
            return;
        }

        float threshold = StartThreshold(bat.Personality);
        if (state.Drive < threshold)
        {
            state.DecisionReason = "social drive below interaction threshold";
            return;
        }

        float activeRatio = roomState.CandidateCount <= 0
            ? 0f
            : roomState.ActiveMemberCount / (float)Mathf.Max(1, roomState.CandidateCount);
        if (activeRatio > 0.64f)
        {
            state.DecisionReason = "room social soft cap";
            return;
        }

        TryScheduleInteraction(bat, state, roomState, candidates, activeRatio);
    }

    internal static bool ApplyOwnedBehavior(DB_Creature bat)
    {
        if (bat == null || !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.Social) ||
            !states.TryGetValue(bat, out State state) || state.Mode == DB_SocialMode.None)
            return false;
        UpdateActive(bat, state);
        return true;
    }

    internal static void CancelForPriority(DB_Creature bat, string reason)
    {
        if (bat == null || !states.TryGetValue(bat, out State state) || state.Mode == DB_SocialMode.None)
            return;
        CancelForPriorityState(bat, state, string.IsNullOrEmpty(reason) ? "higher priority" : reason);
    }

    internal static bool TryGetDebugState(DB_Creature bat, out DB_SocialDebugState debug)
    {
        debug = default;
        if (bat == null || !states.TryGetValue(bat, out State state)) return false;
        int groupSize = state.Token?.Active == true && state.Token.Mode == DB_SocialMode.GroupDrift
            ? state.Token.Members.Count
            : 0;
        Vector2? debugRoostTarget = state.RoostAnchor.HasValue
            ? state.RoostAnchor.Value.Spot
            : state.ChainApproachTarget;
        debug = new DB_SocialDebugState(
            PriorityBlockReason(bat) == null,
            state.Drive,
            state.Cooldown,
            state.NegotiationCooldown,
            state.AffiliationFatigue,
            state.PlayFatigue,
            state.GreetingFatigue,
            state.Mode,
            state.Ticks,
            state.Duration,
            Id(state.Partner),
            Id(state.Anchor),
            state.Token?.Active == true && state.Token.Mode == DB_SocialMode.GroupDrift ? state.Token.Id : 0,
            groupSize,
            state.LastMode,
            state.RecentMode0,
            state.RecentMode1,
            state.RecentMode2,
            state.DecisionReason,
            state.CandidateCount,
            debugRoostTarget,
            state.Side);
        return true;
    }

    internal static void SampleTrace(DB_Creature bat)
    {
        if (bat?.abstractCreature == null || !AIDebugTrace.IsWatched(bat.abstractCreature) ||
            !TryGetDebugState(bat, out DB_SocialDebugState social))
            return;

        float driveBucket = Mathf.Round(social.SocialDrive * 20f) / 20f;
        int cooldownBucket = social.SocialCooldown <= 0 ? 0 : (social.SocialCooldown / 20) * 20;
        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.Social,
            "SocialEligible", social.Eligible, social.DecisionReason);
        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.Social,
            "SocialDrive", driveBucket, "quantized 0.05 SocialDrive bucket");
        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.Social,
            "SocialCooldown", cooldownBucket, "quantized 20-tick cooldown bucket");
        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.Social,
            "SocialNeedFatigue",
            $"A={social.AffiliationFatigue:0.00};P={social.PlayFatigue:0.00};G={social.GreetingFatigue:0.00}",
            "temporary need fatigue; lower is fresher");
        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.Social,
            "SocialRecentModes",
            $"{social.RecentInteraction0}/{social.RecentInteraction1}/{social.RecentInteraction2}",
            "three-event diversity history");
        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.Social,
            "SocialMode", social.Mode, social.DecisionReason);
        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.Social,
            "SocialPartner", social.Partner, social.DecisionReason);
        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.Social,
            "SocialMicroFlock", social.MicroFlockId == 0 ? "—" : $"{social.MicroFlockId}:{social.MicroFlockSize}",
            social.DecisionReason);
        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.Social,
            "SocialReason", social.DecisionReason, social.Mode.ToString());
    }

    // Pure helpers are internal so managed/probability tests can exercise the motivation math
    // without constructing a Unity room/game loop.
    internal static float SocialDrivePerTick(DB_Personality personality)
    {
        if (personality == null) return 0f;
        return 0.00230f *
            Mathf.Lerp(0.84f, 1.24f, personality.Conformity) *
            Mathf.Lerp(0.95f, 1.07f, personality.Temperament);
    }

    internal static float StartThreshold(DB_Personality personality)
    {
        if (personality == null) return 1f;
        float value = Mathf.Lerp(0.68f, 0.48f, personality.Conformity) -
            Mathf.InverseLerp(0.75f, 1f, personality.Temperament) * 0.03f;
        return Mathf.Clamp(value, 0.46f, 0.72f);
    }

    internal static DB_SocialNeedAxis NeedAxisOf(DB_SocialMode mode) => mode switch
    {
        DB_SocialMode.CompanionDrift => DB_SocialNeedAxis.Affiliation,
        DB_SocialMode.GroupDrift => DB_SocialNeedAxis.Affiliation,
        DB_SocialMode.RoostInvitation => DB_SocialNeedAxis.Affiliation,
        DB_SocialMode.ChainSocialization => DB_SocialNeedAxis.Affiliation,
        DB_SocialMode.SocialChase => DB_SocialNeedAxis.Play,
        DB_SocialMode.PassBy => DB_SocialNeedAxis.Greeting,
        _ => DB_SocialNeedAxis.None
    };

    internal static float NeedFreshness(float fatigue)
        => Mathf.Lerp(1f, FatigueWeightFloor, Mathf.Clamp01(fatigue));

    internal static float ModeNoveltyScale(
        DB_SocialMode mode,
        float affiliationFatigue,
        float playFatigue,
        float greetingFatigue,
        DB_SocialMode recent0,
        DB_SocialMode recent1,
        DB_SocialMode recent2)
    {
        if (mode == DB_SocialMode.None || mode == DB_SocialMode.PositionNegotiation)
            return 1f;

        float fatigue = NeedAxisOf(mode) switch
        {
            DB_SocialNeedAxis.Affiliation => affiliationFatigue,
            DB_SocialNeedAxis.Play => playFatigue,
            DB_SocialNeedAxis.Greeting => greetingFatigue,
            _ => 0f
        };
        float history = recent0 == mode ? 0.35f :
            recent1 == mode ? 0.62f :
            recent2 == mode ? 0.80f : 1f;
        return NeedFreshness(fatigue) * history;
    }

    internal static float SocialSatisfactionFraction(
        DB_SocialMode mode,
        bool initiated,
        float currentDrive)
    {
        currentDrive = Mathf.Clamp01(currentDrive);
        return mode switch
        {
            DB_SocialMode.PassBy => 0.16f,
            DB_SocialMode.SocialChase => 0.42f,
            DB_SocialMode.CompanionDrift => 0.50f,
            DB_SocialMode.GroupDrift when initiated => 0.62f,
            DB_SocialMode.GroupDrift => Mathf.Lerp(
                0.24f, 0.44f, Mathf.InverseLerp(0.25f, 0.75f, currentDrive)),
            DB_SocialMode.RoostInvitation => 0.46f,
            DB_SocialMode.ChainSocialization => 0.58f,
            DB_SocialMode.PositionNegotiation => 0f,
            _ => 0f
        };
    }

    internal static float DriveAfterInteraction(
        float currentDrive,
        DB_SocialMode mode,
        bool completed,
        bool initiated)
    {
        currentDrive = Mathf.Clamp01(currentDrive);
        float satisfaction = SocialSatisfactionFraction(mode, initiated, currentDrive);
        if (!completed) satisfaction *= 0.22f;
        return Mathf.Clamp01(currentDrive * (1f - satisfaction));
    }

    internal static float NeedFatigueGain(
        DB_SocialMode mode,
        bool completed,
        bool initiated,
        float currentDrive)
    {
        float gain = mode switch
        {
            DB_SocialMode.PassBy => 0.42f,
            DB_SocialMode.SocialChase => 0.62f,
            DB_SocialMode.CompanionDrift => 0.55f,
            DB_SocialMode.GroupDrift when initiated => 0.72f,
            DB_SocialMode.GroupDrift => Mathf.Lerp(
                0.38f, 0.54f, Mathf.InverseLerp(0.25f, 0.75f, Mathf.Clamp01(currentDrive))),
            DB_SocialMode.RoostInvitation => 0.48f,
            DB_SocialMode.ChainSocialization => 0.62f,
            _ => 0f
        };
        return completed ? gain : gain * 0.25f;
    }

    /// <summary>
    /// A recently social individual remains a valid partner but is less attractive. This keeps
    /// one completed group from removing most of the room's candidate pool while still avoiding
    /// immediate ping-pong interactions with the same recently active bats.
    /// </summary>
    internal static float PartnerCooldownWeight(int cooldown)
    {
        if (cooldown <= 0) return 1f;
        return Mathf.Lerp(0.72f, 0.30f, Mathf.InverseLerp(0f, 260f, cooldown));
    }

    internal static int StablePairSide(EntityID a, EntityID b)
    {
        long ka = DB_SocialRoomRuntime.Key(a);
        long kb = DB_SocialRoomRuntime.Key(b);
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

    internal static float GroupJoinPreference(float conformity, float socialDrive)
        => Mathf.Clamp01(0.15f + Mathf.Clamp01(conformity) * 0.58f + Mathf.Clamp01(socialDrive) * 0.27f);

    private static State CreateState(DB_Creature bat)
    {
        return new State
        {
            Drive = Mathf.Lerp(0.10f, 0.34f, Stable01(bat.Personality.VisualSeed, 0x2F13)),
            LastRoom = bat.room
        };
    }

    private static void RecoverNeedFatigue(State state)
    {
        state.AffiliationFatigue = Mathf.Max(0f, state.AffiliationFatigue - AffiliationFatigueRecovery);
        state.PlayFatigue = Mathf.Max(0f, state.PlayFatigue - PlayFatigueRecovery);
        state.GreetingFatigue = Mathf.Max(0f, state.GreetingFatigue - GreetingFatigueRecovery);
    }

    private static float ModeNoveltyScale(State state, DB_SocialMode mode)
        => ModeNoveltyScale(
            mode,
            state.AffiliationFatigue,
            state.PlayFatigue,
            state.GreetingFatigue,
            state.RecentMode0,
            state.RecentMode1,
            state.RecentMode2);

    private static float PartnerHistoryScale(State state, DB_SocialMode mode, DB_Creature partner)
    {
        if (partner?.abstractCreature == null) return 1f;
        long key = DB_SocialRoomRuntime.Key(partner);
        if (key == state.RecentPartner0)
            return mode == DB_SocialMode.SocialChase ? 0.32f : 0.55f;
        if (key == state.RecentPartner1)
            return mode == DB_SocialMode.SocialChase ? 0.55f : 0.72f;
        if (key == state.RecentPartner2)
            return 0.86f;
        return 1f;
    }

    private static float ChoiceDiversityScale(State state, DB_SocialMode mode, DB_Creature partner)
        => ModeNoveltyScale(state, mode) * PartnerHistoryScale(state, mode, partner);

    private static string PriorityBlockReason(DB_Creature bat)
    {
        if (bat == null || bat.dead || bat.slatedForDeletetion || !bat.Consious || bat.room == null)
            return "unavailable / unconscious";
        if (bat.inShortcut) return "shortcut";
        if (DB_RestraintPolicy.IsRestrainedByNonFly(bat)) return "restrained by non-Fly";
        if (bat.Emergence?.Active == true) return "emergence";
        if (bat.DesertAI == null || bat.AI == null) return "AI unavailable";
        if (DB_EnvironmentalPolicy.BlocksNeutralSocial(bat)) return "environmental survival priority";
        if (bat.DesertAI.HasImmediateDanger || bat.DesertAI.Mode == DB_AI.Activity.Escape)
            return "immediate danger";
        if (DB_TravelRuntime.CanOwnRealizedFrame(bat, out _)) return "cross-room travel priority";
        if (bat.Injury.IsSeverelyInjured || bat.Injury.IsRecovering ||
            bat.DesertAI.Mode == DB_AI.Activity.InjuryRecovery)
            return "severe injury / recovery";
        if (bat.Feeding?.Active == true) return "dehydrated-player feeding commitment";
        if (bat.Rescue?.Active == true) return "companion rescue commitment";
        if (bat.AI.fleeFromRain || bat.AI.behavior == FlyAI.Behavior.Burrow ||
            bat.AI.luredCounter > 0 || bat.safariControlled)
            return "vanilla priority";
        if (bat.AI.behavior == FlyAI.Behavior.Drop || bat.movMode == Fly.MovementMode.Passive)
            return "Drop / Passive";
        if (bat.AI.behavior == FlyAI.Behavior.Chain || bat.movMode == Fly.MovementMode.Hang ||
            bat.DesertAI.Mode == DB_AI.Activity.Roost)
            return "roost commitment";
        if (bat.DesertAI.Target != null || bat.DesertAI.FormalAttack ||
            bat.DesertAI.Mode is DB_AI.Activity.Observe or DB_AI.Activity.Approach or
                DB_AI.Activity.Circle or DB_AI.Activity.FakeDive or DB_AI.Activity.Dive or
                DB_AI.Activity.Attach or DB_AI.Activity.RetaliationCharge or DB_AI.Activity.Interfere)
            return "formal attack / harassment";
        if (DB_VengeanceRuntime.IsActive(bat) ||
            DB_FearRuntime.HasActiveFearSuppression(bat))
            return "fear / vengeance suppression";
        if (ActiveTrauma(bat) >= DB_Tuning.TraumaSevere) return "severe trauma";
        if (bat.AI.behavior != FlyAI.Behavior.Idle && bat.AI.behavior != FlyAI.Behavior.Swarm)
            return "non-neutral vanilla behavior";
        return null;
    }

    private static void TryScheduleInteraction(
        DB_Creature bat,
        State state,
        DB_SocialRoomRuntime.RoomState roomState,
        IReadOnlyList<DB_Creature> candidates,
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
            DB_Creature other = candidates[i];
            if (other == bat || !CanBePartner(bat, other, roomState)) continue;
            Vector2 delta = other.mainBodyChunk.pos - bat.mainBodyChunk.pos;
            float distance = delta.magnitude;
            if (distance > SocialRange) continue;
            if (distance > 70f && !bat.room.VisualContact(bat.mainBodyChunk.pos, other.mainBodyChunk.pos)) continue;

            float availability = PartnerAvailability(other);
            float bond = Mathf.Max(
                DB_SocialBond.GetBondStrength(bat, other),
                DB_SocialBond.GetBondStrength(other, bat));
            float companionWeight = PartnerPreference(
                    bat.Personality.Conformity,
                    1f - bat.Personality.Temperament,
                    bond,
                    Mathf.InverseLerp(55f, SocialRange, distance)) *
                (0.65f + state.Drive * 0.55f) *
                ChoiceDiversityScale(state, DB_SocialMode.CompanionDrift, other) * availability;
            if (companionWeight > companion.Weight)
                companion = new Choice(DB_SocialMode.CompanionDrift, other, companionWeight);

            Vector2 relativeVelocity = other.mainBodyChunk.vel - bat.mainBodyChunk.vel;
            float closing = distance > 0.01f ? -Vector2.Dot(delta / distance, relativeVelocity) : 0f;
            if (distance <= 105f && closing > 0.8f)
            {
                float passWeight = (0.30f + Mathf.Clamp01(closing / 6f) * 0.45f +
                        bat.Personality.Nerve * 0.16f + (1f - bat.Personality.Conformity) * 0.10f) *
                    ChoiceDiversityScale(state, DB_SocialMode.PassBy, other) * availability;
                if (passWeight > passBy.Weight)
                    passBy = new Choice(DB_SocialMode.PassBy, other, passWeight);
            }

            if (CanPlayChase(bat) && CanPlayChase(other) &&
                bat.Personality.Temperament >= 0.50f && bat.Personality.Nerve >= 0.38f &&
                distance >= 55f && distance <= 190f)
            {
                float chaseWeight = (0.04f + bat.Personality.AggressionDrive * 0.62f +
                        bat.Personality.Nerve * 0.22f) * (0.55f + state.Drive * 0.55f) *
                    ChoiceDiversityScale(state, DB_SocialMode.SocialChase, other) * availability;
                if (chaseWeight > chase.Weight)
                    chase = new Choice(DB_SocialMode.SocialChase, other, chaseWeight);
            }

            if (distance <= 220f && state.GroupScratch.Count < GroupMax && !roomState.IsReserved(other))
            {
                State otherState = states.GetValue(other, CreateState);
                float groupFreshness = ModeNoveltyScale(otherState, DB_SocialMode.GroupDrift);
                float joinPreference = Mathf.Clamp01(
                    GroupJoinPreference(other.Personality.Conformity, otherState.Drive) *
                    DB_EnvironmentalPolicy.GroupCohesionScale(bat) *
                    Mathf.Lerp(0.65f, 1f, availability) *
                    Mathf.Lerp(0.62f, 1f, groupFreshness));
                float joinGate = 0.20f + Stable01(
                    other.Personality.VisualSeed,
                    state.ScanSerial * 97 + bat.Personality.VisualSeed) * 0.55f;
                if (joinPreference >= joinGate)
                    state.GroupScratch.Add(other);
            }
        }

        IReadOnlyList<DB_Creature> roosting = roomState.Roosting;
        DB_Creature roostSource = FindRoostSource(bat, roosting, out int chainSize, out float roostBond);
        if (roostSource != null)
        {
            DB_SocialMode mode = chainSize >= 2
                ? DB_SocialMode.ChainSocialization
                : DB_SocialMode.RoostInvitation;
            float weight = (0.08f + bat.Personality.RoostAffinity * 0.52f +
                    bat.Personality.Conformity * 0.24f + (1f - bat.Personality.Temperament) * 0.10f +
                    roostBond * 0.20f + Mathf.Clamp01(chainSize / 4f) * 0.12f) *
                (0.55f + state.Drive * 0.60f) *
                ChoiceDiversityScale(state, mode, roostSource);
            roost = new Choice(mode, roostSource, weight);
        }

        float groupWeight = state.GroupScratch.Count >= GroupMin
            ? (0.10f + bat.Personality.Conformity * 0.62f +
                (1f - bat.Personality.Temperament) * 0.20f + state.Drive * 0.18f) *
                ModeNoveltyScale(state, DB_SocialMode.GroupDrift)
            : 0f;
        float capScale = activeRatio <= 0.46f
            ? 1f
            : Mathf.Lerp(1f, 0.20f, Mathf.InverseLerp(0.46f, 0.64f, activeRatio));

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
        DB_Creature bat,
        State state,
        DB_SocialRoomRuntime.RoomState roomState,
        IReadOnlyList<DB_Creature> candidates)
    {
        if (state.NegotiationCooldown > 0) return false;
        DB_Creature best = null;
        float bestTime = float.MaxValue;
        for (int i = 0; i < candidates.Count; i++)
        {
            DB_Creature other = candidates[i];
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
            !roomState.TryReservePair(bat, best, DB_SocialMode.PositionNegotiation, out var token))
            return false;

        int side = StablePairSide(bat.abstractCreature.ID, best.abstractCreature.ID);
        AssignPair(
            token,
            bat,
            best,
            DB_SocialMode.PositionNegotiation,
            PairRole.PassA,
            PairRole.PassB,
            StableInt(bat.Personality.VisualSeed ^ best.Personality.VisualSeed, 0x1221, 20, 51),
            side,
            bat);
        TraceStart(bat, DB_SocialMode.PositionNegotiation, best, "predicted close-spacing conflict; no SocialDrive cost");
        return true;
    }

    private static void StartCompanion(
        DB_Creature initiator,
        DB_Creature partner,
        DB_SocialRoomRuntime.RoomState roomState)
    {
        if (!roomState.TryReservePair(initiator, partner, DB_SocialMode.CompanionDrift, out var token))
            return;
        long a = DB_SocialRoomRuntime.Key(initiator);
        long b = DB_SocialRoomRuntime.Key(partner);
        DB_Creature anchor = a <= b ? partner : initiator;
        DB_Creature companion = anchor == initiator ? partner : initiator;
        float bond = Mathf.Max(
            DB_SocialBond.GetBondStrength(anchor, companion),
            DB_SocialBond.GetBondStrength(companion, anchor));
        int duration = StableInt(
            initiator.Personality.VisualSeed ^ partner.Personality.VisualSeed,
            0x33A9,
            70,
            201) + Mathf.RoundToInt(bond * 24f);
        AssignPair(
            token,
            anchor,
            companion,
            DB_SocialMode.CompanionDrift,
            PairRole.Anchor,
            PairRole.Companion,
            duration,
            StablePairSide(anchor.abstractCreature.ID, companion.abstractCreature.ID),
            initiator);
        TraceStart(initiator, DB_SocialMode.CompanionDrift, partner,
            $"conformity/bond pairing; bond={bond:0.00}");
    }

    private static void StartPassBy(
        DB_Creature initiator,
        DB_Creature partner,
        DB_SocialRoomRuntime.RoomState roomState)
    {
        if (!roomState.TryReservePair(initiator, partner, DB_SocialMode.PassBy, out var token))
            return;
        int side = StablePairSide(initiator.abstractCreature.ID, partner.abstractCreature.ID);
        AssignPair(
            token,
            initiator,
            partner,
            DB_SocialMode.PassBy,
            PairRole.PassA,
            PairRole.PassB,
            StableInt(initiator.Personality.VisualSeed ^ partner.Personality.VisualSeed, 0x4553, 20, 49),
            side,
            initiator);
        TraceStart(initiator, DB_SocialMode.PassBy, partner, "closing trajectories / greeting pass");
    }

    private static void StartChase(
        DB_Creature initiator,
        DB_Creature partner,
        DB_SocialRoomRuntime.RoomState roomState)
    {
        if (!CanPlayChase(initiator) || !CanPlayChase(partner) ||
            !roomState.TryReservePair(initiator, partner, DB_SocialMode.SocialChase, out var token))
            return;
        DB_Creature chaser = initiator.Personality.Temperament >= partner.Personality.Temperament
            ? initiator
            : partner;
        DB_Creature chased = chaser == initiator ? partner : initiator;
        AssignPair(
            token,
            chaser,
            chased,
            DB_SocialMode.SocialChase,
            PairRole.Chaser,
            PairRole.Chased,
            StableInt(initiator.Personality.VisualSeed ^ partner.Personality.VisualSeed, 0x6715, 40, 111),
            StablePairSide(chaser.abstractCreature.ID, chased.abstractCreature.ID),
            initiator);
        TraceStart(chaser, DB_SocialMode.SocialChase, chased, "temperament/nerve play chase");
    }

    private static void StartGroup(
        DB_Creature initiator,
        State state,
        DB_SocialRoomRuntime.RoomState roomState)
    {
        if (!roomState.TryReserveGroup(state.GroupScratch, out var token)) return;
        int duration = StableInt(initiator.Personality.VisualSeed, state.ScanSerial * 17 + 0x7123, 90, 241);
        for (int i = 0; i < token.Members.Count; i++)
        {
            DB_Creature member = token.Members[i];
            State memberState = states.GetValue(member, CreateState);
            BeginState(
                memberState,
                DB_SocialMode.GroupDrift,
                PairRole.GroupMember,
                token,
                duration,
                null,
                null,
                StablePairSide(member.abstractCreature.ID, initiator.abstractCreature.ID),
                member == initiator);
            TraceGroupEvent(member, "MicroFlockJoined", token,
                member == initiator ? "microflock initiator" : "microflock participant");
        }
        TraceStart(initiator, DB_SocialMode.GroupDrift, null,
            $"microflock created; size={token.Members.Count}, id={token.Id}");
    }

    private static void StartRoostInvitation(
        DB_Creature target,
        DB_Creature source,
        DB_SocialMode mode,
        State state,
        DB_SocialRoomRuntime.RoomState roomState)
    {
        Vector2 approachTarget = default;
        DB_RoostAnchor tileAnchor = default;
        bool approachingChain = mode == DB_SocialMode.ChainSocialization &&
            TryGetChainApproachTarget(target, source, out approachTarget);
        bool hasTarget = approachingChain ||
            TryFindSocialRoost(target, source, roomState, out tileAnchor);
        if (!hasTarget)
        {
            state.DecisionReason = "roost invitation rejected: no legal chain/tile target";
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
            StableInt(target.Personality.VisualSeed ^ source.Personality.VisualSeed, 0x1957, 70, 191),
            source,
            source,
            StablePairSide(target.abstractCreature.ID, source.abstractCreature.ID),
            true);

        Vector2 targetSpot;
        if (approachingChain)
        {
            state.ChainApproachTarget = approachTarget;
            targetSpot = approachTarget;
        }
        else
        {
            state.RoostAnchor = tileAnchor;
            targetSpot = tileAnchor.Spot;
        }
        TraceStart(target, mode, source, $"legal roost target={targetSpot}; invitationCap={cap}");
    }

    private static void AssignPair(
        DB_SocialRoomRuntime.Reservation token,
        DB_Creature a,
        DB_Creature b,
        DB_SocialMode mode,
        PairRole roleA,
        PairRole roleB,
        int duration,
        int sideA,
        DB_Creature initiator)
    {
        State sa = states.GetValue(a, CreateState);
        State sb = states.GetValue(b, CreateState);
        BeginState(sa, mode, roleA, token, duration, b,
            roleA == PairRole.Anchor ? a : null, sideA, a == initiator);
        BeginState(sb, mode, roleB, token, duration, a,
            roleB == PairRole.Anchor ? b : null, -sideA, b == initiator);
        if (mode == DB_SocialMode.CompanionDrift)
        {
            if (roleA == PairRole.Companion) sa.Anchor = b;
            if (roleB == PairRole.Companion) sb.Anchor = a;
            if (roleA == PairRole.Anchor) sb.Anchor = a;
            if (roleB == PairRole.Anchor) sa.Anchor = b;
        }
    }

    private static void BeginState(
        State state,
        DB_SocialMode mode,
        PairRole role,
        DB_SocialRoomRuntime.Reservation token,
        int duration,
        DB_Creature partner,
        DB_Creature anchor,
        int side,
        bool initiated)
    {
        state.Mode = mode;
        state.Role = role;
        state.Token = token;
        state.Ticks = 0;
        state.Duration = Math.Max(1, duration);
        state.Partner = partner;
        state.Anchor = anchor;
        state.Side = side == 0 ? 1 : Math.Sign(side);
        state.InitiatedInteraction = initiated;
        state.RoostAnchor = null;
        state.ChainApproachTarget = null;
        state.DecisionReason = "interaction active";
    }

    private static void UpdateActive(DB_Creature bat, State state)
    {
        if (state.Token == null || !state.Token.Active)
        {
            CancelState(bat, state, "reservation invalidated", false);
            return;
        }
        string block = PriorityBlockReason(bat);
        if (block != null)
        {
            CancelForPriorityState(bat, state, block);
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
            case DB_SocialMode.CompanionDrift:
                UpdateCompanion(bat, state);
                break;
            case DB_SocialMode.PassBy:
                UpdatePassBy(bat, state, false);
                break;
            case DB_SocialMode.PositionNegotiation:
                UpdatePassBy(bat, state, true);
                break;
            case DB_SocialMode.SocialChase:
                UpdateChase(bat, state);
                break;
            case DB_SocialMode.GroupDrift:
                UpdateGroup(bat, state);
                break;
            case DB_SocialMode.RoostInvitation:
            case DB_SocialMode.ChainSocialization:
                UpdateRoostInvitation(bat, state);
                break;
        }
    }

    private static void UpdateCompanion(DB_Creature bat, State state)
    {
        DB_Creature anchor = state.Role == PairRole.Anchor ? bat : state.Anchor;
        DB_Creature companion = state.Role == PairRole.Companion ? bat : state.Partner;
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
            DB_SocialBond.GetBondStrength(bat, anchor),
            DB_SocialBond.GetBondStrength(anchor, bat));
        Vector2 velocity = anchor.mainBodyChunk.vel;
        Vector2 backward = velocity.sqrMagnitude > 1f ? -velocity.normalized * 22f : Vector2.zero;
        Vector2 goal = anchor.mainBodyChunk.pos + backward + CompanionOffset(state.Side, bond);
        if (!SocialSteer(bat, goal, Mathf.Lerp(4.2f, 5.1f, bat.Personality.Nerve), state.Side))
            CancelState(bat, state, "companion path locally blocked", true);
        else
            state.DecisionReason = "companion side/back offset from temporary anchor";
    }

    private static void UpdatePassBy(DB_Creature bat, State state, bool negotiation)
    {
        DB_Creature partner = state.Partner;
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
        if (!SocialSteer(bat, goal, negotiation ? 5.2f : 5.6f, state.Side))
            CancelState(bat, state, "lateral side blocked; vanilla avoidance resumes", true);
        else
            state.DecisionReason = negotiation
                ? "stable opposite-side position negotiation"
                : "stable opposite-side pass-by";
    }

    private static void UpdateChase(DB_Creature bat, State state)
    {
        DB_Creature partner = state.Partner;
        if (!ValidSocialPeer(partner, bat) || !CanPlayChase(bat) || !CanPlayChase(partner))
        {
            CancelState(bat, state, "chase participant unavailable / flight capability reduced", true);
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
            if (!SocialSteer(bat, predicted, Mathf.Lerp(6.2f, 8.0f, bat.Personality.Nerve), state.Side))
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
        if (!SocialSteer(bat, goal, Mathf.Lerp(5.5f, 7.0f, bat.Personality.Nerve), state.Side))
            CancelState(bat, state, "chased local path blocked", true);
        else
            state.DecisionReason = "play chase sidestep; no fear/attack state";
    }

    private static void UpdateGroup(DB_Creature bat, State state)
    {
        DB_SocialRoomRuntime.Reservation token = state.Token;
        if (token == null || !token.Active || token.Members.Count < GroupMin)
        {
            CancelState(bat, state, "microflock dissolved below three members", token?.Active == true);
            return;
        }

        Vector2 center = Vector2.zero;
        Vector2 averageVelocity = Vector2.zero;
        int count = 0;
        for (int i = 0; i < token.Members.Count; i++)
        {
            DB_Creature member = token.Members[i];
            if (!ValidSocialPeer(member, bat)) continue;
            center += member.mainBodyChunk.pos;
            averageVelocity += member.mainBodyChunk.vel;
            count++;
        }
        if (count < GroupMin)
        {
            CancelState(bat, state, "microflock dissolved below three valid members", true);
            return;
        }
        center /= count;
        averageVelocity /= count;

        Vector2 separation = Vector2.zero;
        Vector2 closeSeparation = Vector2.zero;
        for (int i = 0; i < token.Members.Count; i++)
        {
            DB_Creature member = token.Members[i];
            if (member == bat || !ValidSocialPeer(member, bat)) continue;
            Vector2 delta = bat.mainBodyChunk.pos - member.mainBodyChunk.pos;
            float distance = delta.magnitude;
            if (distance <= 0.01f)
            {
                int tieSide = StablePairSide(bat.abstractCreature.ID, member.abstractCreature.ID);
                int tieVertical = DB_SocialRoomRuntime.Key(bat) < DB_SocialRoomRuntime.Key(member) ? -1 : 1;
                closeSeparation += new Vector2(tieSide * 0.55f, tieVertical * 0.85f);
                continue;
            }

            float preferred = Mathf.Lerp(42f, 62f, 1f - bat.Personality.Nerve);
            if (distance < preferred)
            {
                float strength = 1f - distance / preferred;
                separation.x += Mathf.Sign(delta.x == 0f ? state.Side : delta.x) * strength * GroupSeparationXWeight;
                separation.y += Mathf.Sign(delta.y) * strength * GroupSeparationYWeight;
            }
            if (distance < GroupCloseSeparationRadius)
            {
                float closeStrength = Mathf.Pow(1f - distance / GroupCloseSeparationRadius, 2f);
                closeSeparation += delta / distance * closeStrength;
            }
        }

        Vector2 alignment = averageVelocity.sqrMagnitude > 0.5f
            ? averageVelocity.normalized
            : Vector2.right * state.Side;
        alignment.y *= 0.62f;
        if (Mathf.Abs(alignment.x) < 0.32f)
            alignment.x = state.Side * 0.32f;

        Vector2 cohesion = center - bat.mainBodyChunk.pos;
        Vector2 social =
            alignment * 46f +
            new Vector2(
                Mathf.Clamp(cohesion.x * 0.14f, -25f, 25f),
                Mathf.Clamp(cohesion.y * 0.045f, -8f, 8f)) +
            new Vector2(
                Mathf.Clamp(separation.x * 58f, -70f, 70f),
                Mathf.Clamp(separation.y * 24f, -10f, 10f)) +
            new Vector2(
                Mathf.Clamp(closeSeparation.x * 86f, -92f, 92f),
                Mathf.Clamp(closeSeparation.y * 78f, -86f, 86f));
        social.x += state.Side * StableRange(bat.Personality.VisualSeed, token.Id + 0x833, 7f, 17f);
        if (!SocialSteer(
                bat,
                bat.mainBodyChunk.pos + social,
                Mathf.Lerp(4.3f, 5.6f, bat.Personality.Nerve),
                state.Side))
        {
            LeaveGroupParticipant(bat, state, "member left microflock: local path blocked");
            return;
        }
        state.DecisionReason = "microflock active: alignment + weak cohesion + close 2D separation";
    }

    private static void UpdateRoostInvitation(DB_Creature bat, State state)
    {
        DB_Creature source = state.Anchor;
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

        DB_SocialRoomRuntime.RoomState roomState = state.Token?.Owner;
        Vector2 target = default;
        bool approachingChain = state.Mode == DB_SocialMode.ChainSocialization &&
            TryGetChainApproachTarget(bat, source, out target);
        if (approachingChain)
        {
            state.RoostAnchor = null;
            state.ChainApproachTarget = target;
        }
        else
        {
            state.ChainApproachTarget = null;
            if (!state.RoostAnchor.HasValue ||
                !Custom.DistLess(bat.mainBodyChunk.pos, state.RoostAnchor.Value.Spot, 260f) ||
                !DB_RoostPolicy.IsStillValid(bat, state.RoostAnchor.Value))
            {
                if (roomState == null ||
                    !TryFindSocialRoost(bat, source, roomState, out DB_RoostAnchor replacement))
                {
                    CancelState(bat, state, "roost target invalid / no replacement", true);
                    return;
                }
                state.RoostAnchor = replacement;
            }
            target = state.RoostAnchor.Value.Spot;
        }

        if (!approachingChain && Custom.DistLess(bat.mainBodyChunk.pos, target, 18f))
        {
            if (!DB_RoostPolicy.IsStillValid(bat, state.RoostAnchor.Value))
            {
                state.RoostAnchor = null;
                return;
            }
            CommitTileRoost(bat, state.RoostAnchor.Value);
            FinishToken(bat, state, "committed to invited legal roost", true);
            return;
        }
        if (!SocialSteer(bat, target, 4.4f, state.Side))
            CancelState(bat, state, "invited roost locally blocked", true);
        else
            state.DecisionReason = approachingChain
                ? "approaching existing legal Fly chain tail"
                : "responding to nearby legal tile roost invitation";
    }

    /// <summary>
    /// Formal social behavior owns only its temporary goal, not flight physics. No velocity is
    /// written here: Fly.Act calls vanilla BatFlight after FlyAI.Update and follows localGoal.
    /// </summary>
    private static bool SocialSteer(DB_Creature bat, Vector2 goal, float speed, int preferredSide)
    {
        if (bat?.room == null || bat.AI == null || bat.mainBodyChunk == null ||
            !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.Social))
            return false;
        Vector2 direction = Custom.DirVec(bat.mainBodyChunk.pos, goal);
        if (direction == Vector2.zero) return true;

        Vector2 probe = bat.mainBodyChunk.pos + direction * 25f;
        if (Obstructed(bat.room, probe))
        {
            Vector2 leftProbe = bat.mainBodyChunk.pos + Vector2.left * 28f;
            Vector2 rightProbe = bat.mainBodyChunk.pos + Vector2.right * 28f;
            bool leftBlocked = Obstructed(bat.room, leftProbe);
            bool rightBlocked = Obstructed(bat.room, rightProbe);
            if (leftBlocked && rightBlocked) return false;

            float desiredSide = preferredSide == 0
                ? Mathf.Sign(direction.x == 0f ? 1f : direction.x)
                : Mathf.Sign(preferredSide);
            if (desiredSide < 0f && leftBlocked) desiredSide = 1f;
            if (desiredSide > 0f && rightBlocked) desiredSide = -1f;
            goal = bat.mainBodyChunk.pos + Vector2.right * desiredSide * 58f + Vector2.up * 6f;
            speed = Mathf.Min(speed, 4.8f);
        }

        bat.burrowOrHangSpot = null;
        if (bat.AI.behavior != FlyAI.Behavior.Idle)
            bat.AI.ChangeBehavior(FlyAI.Behavior.Idle);
        bat.AI.followingDijkstraMap = -1;
        bat.movMode = Fly.MovementMode.BatFlight;
        return DB_FlightMotor.TryGuideNative(bat, DB_BehaviorOwner.Social, goal, speed);
    }

    private static bool Obstructed(Room room, Vector2 point)
    {
        if (room == null) return true;
        IntVector2 tile = room.GetTilePosition(point);
        if (room.GetTile(tile).Solid) return true;
        return room.terrain != null && room.terrain.ObstructsTile(tile);
    }

    private static bool TryFindSocialRoost(
        DB_Creature bat,
        DB_Creature source,
        DB_SocialRoomRuntime.RoomState roomState,
        out DB_RoostAnchor anchor)
    {
        anchor = default;
        if (bat?.room == null || bat.AI == null || source?.room != bat.room || roomState == null)
            return false;
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
                !DB_RoostPolicy.TryGetAnchor(bat, tile, out DB_RoostAnchor candidate))
                continue;
            if (!Custom.DistLess(bat.mainBodyChunk.pos, candidate.Spot, 230f) ||
                !bat.room.VisualContact(bat.mainBodyChunk.pos, candidate.Spot))
                continue;
            float score =
                Vector2.Distance(bat.mainBodyChunk.pos, candidate.Spot) +
                Vector2.Distance(source.mainBodyChunk.pos, candidate.Spot) * 0.35f -
                Mathf.Min(3, roomState.CountRoostingNear(candidate.Spot, 75f)) * 11f;
            if (score >= best) continue;
            best = score;
            anchor = candidate;
            found = true;
        }
        return found;
    }

    private static bool TryGetChainApproachTarget(
        DB_Creature bat,
        DB_Creature source,
        out Vector2 target)
    {
        target = default;
        if (!ValidRoostSource(source, bat) || bat?.AI == null) return false;
        Fly tail = source.LastInChain();
        if (tail == null || tail == bat || ChainLength(source) >= 6 || !bat.AI.CanIHangFromThisFly(tail))
            return false;
        target = tail.mainBodyChunk.pos + Vector2.down * 14f;
        if (!Custom.DistLess(bat.mainBodyChunk.pos, target, 230f)) return false;
        return Custom.DistLess(bat.mainBodyChunk.pos, target, 75f) ||
            bat.room.VisualContact(bat.mainBodyChunk.pos, target);
    }

    private static void CommitTileRoost(DB_Creature bat, in DB_RoostAnchor anchor)
    {
        bat.LoseAllGrasps();
        bat.AI.followingDijkstraMap = -1;
        bat.AI.ChangeBehavior(FlyAI.Behavior.Chain);
        bat.burrowOrHangSpot = anchor.Spot;
        bat.movMode = Fly.MovementMode.Hang;
        bat.mainBodyChunk.vel *= 0.5f;
    }

    private static bool TryAttachToChainTail(DB_Creature bat, DB_Creature source)
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

    private static DB_Creature FindRoostSource(
        DB_Creature bat,
        IReadOnlyList<DB_Creature> roosting,
        out int chainSize,
        out float bond)
    {
        chainSize = 0;
        bond = 0f;
        DB_PerceptionRuntime perception = bat?.DesertAI?.Perception;
        if (bat == null || bat.room == null || perception == null ||
            !perception.TryGetSignalContext(out DB_PerceptionSignalContext signal) ||
            signal.RoostInterest < 0.16f)
            return null;

        DB_Creature source = signal.RoostSource;
        if (!ValidRoostSource(source, bat) ||
            Vector2.Distance(bat.mainBodyChunk.pos, source.mainBodyChunk.pos) > 230f)
            return null;

        chainSize = ChainLength(source);
        bond = Mathf.Max(
            DB_SocialBond.GetBondStrength(bat, source),
            DB_SocialBond.GetBondStrength(source, bat));
        return source;
    }

    private static bool ValidRoostSource(DB_Creature source, DB_Creature observer)
        => source != null && observer != null && source != observer && source.room == observer.room &&
           !source.dead && source.Consious && !source.inShortcut &&
           source.AI?.behavior == FlyAI.Behavior.Chain;

    private static bool CanBePartner(
        DB_Creature source,
        DB_Creature candidate,
        DB_SocialRoomRuntime.RoomState roomState)
    {
        if (!IsNeutralCandidate(candidate) || candidate == source || candidate.room != source.room ||
            roomState.IsReserved(candidate))
            return false;
        if (states.TryGetValue(candidate, out State candidateState) &&
            candidateState.Mode != DB_SocialMode.None)
            return false;
        if (!DB_EnvironmentalPolicy.WithinActivityRange(source, candidate, SocialRange))
            return false;
        return SameRipple(source, candidate);
    }

    private static float PartnerAvailability(DB_Creature candidate)
    {
        if (candidate == null || !states.TryGetValue(candidate, out State candidateState))
            return 1f;
        return PartnerCooldownWeight(candidateState.Cooldown);
    }

    private static bool IsNeutralCandidate(DB_Creature bat)
    {
        if (!DB_SocialRoomRuntime.ValidMember(bat) || !bat.Consious || bat.AI == null || bat.DesertAI == null)
            return false;
        if (DB_TravelRuntime.CanOwnRealizedFrame(bat, out _) ||
            bat.DesertAI.HasImmediateDanger ||
            bat.Injury.IsSeverelyInjured ||
            bat.Injury.IsRecovering ||
            bat.Feeding?.Active == true ||
            bat.Rescue?.Active == true ||
            bat.DesertAI.Target != null ||
            bat.DesertAI.FormalAttack ||
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

    private static bool CanPlayChase(DB_Creature bat)
        => bat != null && bat.DesertState.Cooldown <= 0 &&
           !bat.Injury.IsSeverelyInjured && !bat.Injury.IsRecovering &&
           bat.Injury.PostStunShock < 0.28f && bat.Injury.PhysicalCapability >= 0.72f &&
           DB_EnvironmentalPolicy.AllowsPlayChase(bat);

    private static bool ValidSocialPeer(DB_Creature peer, DB_Creature observer)
        => peer != null && observer != null && peer.room == observer.room &&
           SameRipple(peer, observer) && IsNeutralCandidate(peer);

    private static bool SameRipple(DB_Creature a, DB_Creature b)
        => a?.abstractCreature != null && b?.abstractCreature != null &&
           (a.abstractCreature.rippleLayer == b.abstractCreature.rippleLayer ||
            a.abstractCreature.rippleBothSides ||
            b.abstractCreature.rippleBothSides);

    private static void FinishToken(DB_Creature bat, State state, string reason, bool preserveRoost = false)
    {
        DB_SocialRoomRuntime.Reservation token = state.Token;
        if (token == null)
        {
            FinalizeParticipant(bat, state, reason, true);
            return;
        }

        token.Owner.Release(token);
        for (int i = 0; i < token.Members.Count; i++)
        {
            DB_Creature member = token.Members[i];
            if (member == null || !states.TryGetValue(member, out State memberState)) continue;
            DB_SocialMode completedMode = memberState.Mode;
            FinalizeParticipant(member, memberState, reason, true);
            if (completedMode == DB_SocialMode.GroupDrift)
                TraceGroupEvent(member, "MicroFlockLeft", token, reason);
        }
        if (preserveRoost && bat?.AI?.behavior == FlyAI.Behavior.Chain &&
            states.TryGetValue(bat, out State finishedState))
            finishedState.DecisionReason = reason;
    }

    private static void CancelForPriorityState(DB_Creature bat, State state, string reason)
    {
        if (state.Mode == DB_SocialMode.GroupDrift && state.Token?.Active == true)
        {
            LeaveGroupParticipant(bat, state, reason);
            return;
        }
        CancelState(bat, state, reason, true);
    }

    private static void LeaveGroupParticipant(DB_Creature bat, State state, string reason)
    {
        DB_SocialRoomRuntime.Reservation token = state.Token;
        if (token == null || !token.Active || token.Mode != DB_SocialMode.GroupDrift)
        {
            FinalizeParticipant(bat, state, reason, false);
            return;
        }

        bool remainsActive = token.Owner.RemoveGroupMember(token, bat);
        FinalizeParticipant(bat, state, reason, false);
        TraceGroupEvent(bat, "MicroFlockLeft", token, reason);
        if (!remainsActive)
            FinalizeReleasedGroup(token, "microflock dissolved below three members");
    }

    private static void CancelState(DB_Creature bat, State state, string reason, bool releaseToken)
    {
        DB_SocialRoomRuntime.Reservation token = state.Token;
        if (releaseToken && token?.Active == true)
        {
            token.Owner.Release(token);
            for (int i = 0; i < token.Members.Count; i++)
            {
                DB_Creature member = token.Members[i];
                if (member == null || !states.TryGetValue(member, out State memberState)) continue;
                DB_SocialMode cancelledMode = memberState.Mode;
                FinalizeParticipant(member, memberState, reason, false);
                if (cancelledMode == DB_SocialMode.GroupDrift)
                    TraceGroupEvent(member, "MicroFlockLeft", token, reason);
            }
            return;
        }

        FinalizeParticipant(bat, state, reason, false);
    }

    private static void FinalizeReleasedGroup(
        DB_SocialRoomRuntime.Reservation token,
        string reason)
    {
        if (token == null) return;
        for (int i = 0; i < token.Members.Count; i++)
        {
            DB_Creature member = token.Members[i];
            if (member == null || !states.TryGetValue(member, out State memberState) ||
                memberState.Mode != DB_SocialMode.GroupDrift)
                continue;
            FinalizeParticipant(member, memberState, reason, false);
            TraceGroupEvent(member, "MicroFlockLeft", token, reason);
        }
    }

    private static void FinalizeParticipant(
        DB_Creature bat,
        State state,
        string reason,
        bool completed)
    {
        DB_SocialMode mode = state.Mode;
        bool initiated = state.InitiatedInteraction;
        float beforeDrive = state.Drive;
        state.LastMode = mode;

        if (mode == DB_SocialMode.PositionNegotiation)
        {
            // Coordination has its own anti-ping-pong timer. It neither satisfies nor fatigues
            // social needs, and it does not overwrite an existing formal-social cooldown.
            state.NegotiationCooldown = NegotiationCooldown(bat, completed);
            EndStateOnly(state, reason);
            TraceFinalize(bat, mode, completed, reason, beforeDrive, beforeDrive);
            return;
        }

        if (mode != DB_SocialMode.None)
        {
            // Only completed events count toward the three-event novelty history. A cancelled
            // attempt already receives a cooldown and a small fatigue gain; treating it as a
            // full recent experience would suppress an interaction the bat never actually had.
            if (completed)
            {
                long partnerKey = state.Partner != null
                    ? DB_SocialRoomRuntime.Key(state.Partner)
                    : long.MinValue;
                PushHistory(state, mode, partnerKey);
            }

            state.Drive = DriveAfterInteraction(beforeDrive, mode, completed, initiated);
            ApplyNeedFatigue(state, mode,
                NeedFatigueGain(mode, completed, initiated, beforeDrive));
            state.Cooldown = SocialCooldown(bat, mode, completed, initiated);
        }
        EndStateOnly(state, reason);
        TraceFinalize(bat, mode, completed, reason, beforeDrive, state.Drive);
    }

    private static void PushHistory(State state, DB_SocialMode mode, long partnerKey)
    {
        if (mode == DB_SocialMode.None || mode == DB_SocialMode.PositionNegotiation) return;
        state.RecentMode2 = state.RecentMode1;
        state.RecentMode1 = state.RecentMode0;
        state.RecentMode0 = mode;
        state.RecentPartner2 = state.RecentPartner1;
        state.RecentPartner1 = state.RecentPartner0;
        state.RecentPartner0 = partnerKey;
    }

    private static void ApplyNeedFatigue(State state, DB_SocialMode mode, float gain)
    {
        if (gain <= 0f) return;
        switch (NeedAxisOf(mode))
        {
            case DB_SocialNeedAxis.Affiliation:
                state.AffiliationFatigue = Mathf.Clamp01(state.AffiliationFatigue + gain);
                break;
            case DB_SocialNeedAxis.Play:
                state.PlayFatigue = Mathf.Clamp01(state.PlayFatigue + gain);
                break;
            case DB_SocialNeedAxis.Greeting:
                state.GreetingFatigue = Mathf.Clamp01(state.GreetingFatigue + gain);
                break;
        }
    }

    private static void EndStateOnly(State state, string reason)
    {
        state.Mode = DB_SocialMode.None;
        state.Role = PairRole.None;
        state.Token = null;
        state.Ticks = 0;
        state.Duration = 0;
        state.Partner = null;
        state.Anchor = null;
        state.Side = 0;
        state.InitiatedInteraction = false;
        state.RoostAnchor = null;
        state.ChainApproachTarget = null;
        state.DecisionReason = reason;
    }

    private static int SocialCooldown(
        DB_Creature bat,
        DB_SocialMode mode,
        bool completed,
        bool initiated)
    {
        int salt = (int)mode * 193 + (completed ? 0x71 : 0x35);
        int min;
        int max;
        switch (mode)
        {
            case DB_SocialMode.PassBy:
                min = 48; max = 131;
                break;
            case DB_SocialMode.SocialChase:
                min = 135; max = 301;
                break;
            case DB_SocialMode.RoostInvitation:
            case DB_SocialMode.ChainSocialization:
                min = 80; max = 221;
                break;
            default:
                min = 70; max = 211;
                break;
        }

        int value = StableInt(bat?.Personality?.VisualSeed ?? 0, salt, min, max);
        if (bat != null)
            value = Mathf.RoundToInt(value * Mathf.Lerp(1.08f, 0.90f, bat.Personality.Conformity));
        if (mode == DB_SocialMode.GroupDrift && !initiated)
            value = Mathf.RoundToInt(value * 0.72f);
        if (!completed)
            value = Mathf.RoundToInt(value * 0.70f);
        return Mathf.Clamp(value, 38, 320);
    }

    private static int NegotiationCooldown(DB_Creature bat, bool completed)
    {
        int seed = bat?.Personality?.VisualSeed ?? 0;
        return completed
            ? StableInt(seed, 0x4A31, 28, 56)
            : StableInt(seed, 0x4A37, 16, 33);
    }

    private static float ActiveTrauma(DB_Creature bat)
        => Mathf.Max(
            bat.DesertState.PlayerTraumaTicks > 0 ? bat.DesertState.PlayerTraumaStrength : 0f,
            bat.DesertState.PredatorTraumaTicks > 0 ? bat.DesertState.PredatorTraumaStrength : 0f);

    private static void TraceFinalize(
        DB_Creature bat,
        DB_SocialMode mode,
        bool completed,
        string reason,
        float beforeDrive,
        float afterDrive)
    {
        if (bat?.abstractCreature == null || !AIDebugTrace.IsWatched(bat.abstractCreature)) return;
        AIDebugTrace.Record(
            bat.abstractCreature,
            AIDebugEventCategory.Social,
            completed ? "SocialInteractionCompleted" : "SocialInteractionCancelled",
            mode,
            $"{reason}; drive={beforeDrive:0.00}->{afterDrive:0.00}");
    }

    private static void TraceStart(
        DB_Creature bat,
        DB_SocialMode mode,
        DB_Creature partner,
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
            DB_SocialMode.CompanionDrift => "CompanionDriftStarted",
            DB_SocialMode.PassBy => "PassByStarted",
            DB_SocialMode.SocialChase => "SocialChaseStarted",
            DB_SocialMode.GroupDrift => "MicroFlockCreated",
            DB_SocialMode.RoostInvitation => "RoostInvitationAccepted",
            DB_SocialMode.PositionNegotiation => "PositionNegotiationStarted",
            DB_SocialMode.ChainSocialization => "RoostInvitationAccepted",
            _ => "SocialInteractionStarted"
        };
        if (specific != "SocialInteractionStarted")
            AIDebugTrace.Record(bat.abstractCreature, AIDebugEventCategory.Social, specific, mode, details);
    }

    private static void TraceGroupEvent(
        DB_Creature bat,
        string key,
        DB_SocialRoomRuntime.Reservation token,
        string reason)
    {
        if (bat?.abstractCreature == null || !AIDebugTrace.IsWatched(bat.abstractCreature)) return;
        AIDebugTrace.Record(
            bat.abstractCreature,
            AIDebugEventCategory.Social,
            key,
            token?.Id ?? 0,
            $"group={token?.Id ?? 0}; size={token?.Members.Count ?? 0}; {reason}");
    }

    private static void TraceDecision(DB_Creature bat, string key, string reason)
    {
        if (bat?.abstractCreature == null || !AIDebugTrace.IsWatched(bat.abstractCreature)) return;
        AIDebugTrace.RecordChange(bat.abstractCreature, AIDebugEventCategory.Social, key, reason, reason);
    }

    private static string Id(DB_Creature bat)
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
