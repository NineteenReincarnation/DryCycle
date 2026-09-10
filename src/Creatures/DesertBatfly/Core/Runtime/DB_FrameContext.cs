using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal readonly struct DB_TravelFrameSummary
{
    internal readonly bool HasIntent;
    internal readonly bool CanOwnRealizedFrame;
    internal readonly DB_TravelPurpose Purpose;
    internal readonly string DestinationRoom;
    internal readonly bool Suspended;
    internal readonly string Reason;

    internal DB_TravelFrameSummary(
        bool hasIntent,
        bool canOwnRealizedFrame,
        DB_TravelPurpose purpose,
        string destinationRoom,
        bool suspended,
        string reason)
    {
        HasIntent = hasIntent;
        CanOwnRealizedFrame = canOwnRealizedFrame;
        Purpose = purpose;
        DestinationRoom = destinationRoom ?? string.Empty;
        Suspended = suspended;
        Reason = reason ?? string.Empty;
    }
}

internal readonly struct DB_ThreatFrameSummary
{
    internal readonly bool Available;
    internal readonly int PlayerSlot;
    internal readonly float Confidence;
    internal readonly bool ProjectileThreat;
    internal readonly bool AcuteThreat;
    internal readonly Vector2? HazardCenter;

    internal DB_ThreatFrameSummary(
        bool available,
        int playerSlot,
        float confidence,
        bool projectileThreat,
        bool acuteThreat,
        Vector2? hazardCenter)
    {
        Available = available;
        PlayerSlot = playerSlot;
        Confidence = Mathf.Clamp01(confidence);
        ProjectileThreat = projectileThreat;
        AcuteThreat = acuteThreat;
        HazardCenter = hazardCenter;
    }
}

internal readonly struct DB_RoostFrameSummary
{
    internal readonly bool Active;
    internal readonly bool NativeChain;
    internal readonly bool SpeciesRoost;

    internal DB_RoostFrameSummary(bool active, bool nativeChain, bool speciesRoost)
    {
        Active = active;
        NativeChain = nativeChain;
        SpeciesRoost = speciesRoost;
    }
}

/// <summary>
/// One read-only snapshot of the facts used to arbitrate a Desert Batfly frame.
/// This type does not persist and does not execute movement. Domain-specific state remains
/// owned by its original subsystem; FrameContext only copies approved read-only facts.
/// </summary>
internal readonly struct DB_FrameContext
{
    internal readonly DB_Creature Bat;
    internal readonly EntityID EntityId;
    internal readonly int Clock;
    internal readonly Room Room;

    internal readonly Vector2 Position;
    internal readonly Vector2 Velocity;
    internal readonly Vector2? CurrentGoal;
    internal readonly bool Conscious;
    internal readonly bool Dead;
    internal readonly bool Restrained;
    internal readonly bool InShortcut;
    internal readonly bool InHive;
    internal readonly Fly.MovementMode NativeMovementMode;
    internal readonly FlyAI.Behavior NativeBehavior;

    internal readonly DB_Personality Personality;
    internal readonly float PhysicalCapability;
    internal readonly bool SevereInjury;
    internal readonly bool InjuryRecovering;
    internal readonly Vector2? InjuryRecoveryTarget;
    internal readonly float PostStunShock;
    internal readonly float Thirst;
    internal readonly float Trauma;
    internal readonly float Grief;
    internal readonly float BondStrength;

    internal readonly DB_PerceptionSnapshot Perception;
    internal readonly bool IncomingProjectile;
    internal readonly DB_WeaponObservation IncomingProjectileObservation;
    internal readonly float VisibilityFactor;

    internal readonly DB_TravelFrameSummary Travel;
    // Transitional compatibility view for consumers not yet moved to Perception.Signals.
    internal readonly bool HasSignalInfluence;
    internal readonly DB_SignalInfluence SignalInfluence;
    internal readonly bool HasEnvironmentInfluence;
    internal readonly DB_EnvironmentInfluence EnvironmentInfluence;
    internal readonly DB_ThreatFrameSummary Threat;
    internal readonly bool HasSocialState;
    internal readonly DB_SocialDebugState Social;
    internal readonly DB_RoostFrameSummary Roost;
    internal readonly bool VengeanceActive;
    internal readonly Creature VengeanceTarget;
    internal readonly bool FearSuppressed;

    internal readonly bool ImmediateDanger;
    internal readonly bool HardSurvival;
    internal readonly bool CombatAllowed;
    internal readonly bool SocialAllowed;
    internal readonly bool CrossRoomOwned;
    internal readonly DB_SpecialPhysicsOwner SpecialPhysicsOwner;

    internal DB_FrameContext(
        DB_Creature bat,
        EntityID entityId,
        int clock,
        Room room,
        Vector2 position,
        Vector2 velocity,
        Vector2? currentGoal,
        bool conscious,
        bool dead,
        bool restrained,
        bool inShortcut,
        bool inHive,
        Fly.MovementMode nativeMovementMode,
        FlyAI.Behavior nativeBehavior,
        DB_Personality personality,
        float physicalCapability,
        bool severeInjury,
        bool injuryRecovering,
        Vector2? injuryRecoveryTarget,
        float postStunShock,
        float thirst,
        float trauma,
        float grief,
        float bondStrength,
        in DB_PerceptionSnapshot perception,
        bool incomingProjectile,
        in DB_WeaponObservation incomingProjectileObservation,
        float visibilityFactor,
        in DB_TravelFrameSummary travel,
        bool hasSignalInfluence,
        in DB_SignalInfluence signalInfluence,
        bool hasEnvironmentInfluence,
        in DB_EnvironmentInfluence environmentInfluence,
        in DB_ThreatFrameSummary threat,
        bool hasSocialState,
        in DB_SocialDebugState social,
        in DB_RoostFrameSummary roost,
        bool vengeanceActive,
        Creature vengeanceTarget,
        bool fearSuppressed,
        bool immediateDanger,
        bool hardSurvival,
        bool combatAllowed,
        bool socialAllowed,
        bool crossRoomOwned,
        DB_SpecialPhysicsOwner specialPhysicsOwner)
    {
        Bat = bat;
        EntityId = entityId;
        Clock = clock;
        Room = room;
        Position = position;
        Velocity = velocity;
        CurrentGoal = currentGoal;
        Conscious = conscious;
        Dead = dead;
        Restrained = restrained;
        InShortcut = inShortcut;
        InHive = inHive;
        NativeMovementMode = nativeMovementMode;
        NativeBehavior = nativeBehavior;
        Personality = personality;
        PhysicalCapability = Mathf.Clamp01(physicalCapability);
        SevereInjury = severeInjury;
        InjuryRecovering = injuryRecovering;
        InjuryRecoveryTarget = injuryRecoveryTarget;
        PostStunShock = Mathf.Clamp01(postStunShock);
        Thirst = Mathf.Clamp01(thirst);
        Trauma = Mathf.Clamp01(trauma);
        Grief = Mathf.Clamp01(grief);
        BondStrength = Mathf.Clamp01(bondStrength);
        Perception = perception;
        IncomingProjectile = incomingProjectile;
        IncomingProjectileObservation = incomingProjectileObservation;
        VisibilityFactor = Mathf.Clamp01(visibilityFactor);
        Travel = travel;
        HasSignalInfluence = hasSignalInfluence;
        SignalInfluence = signalInfluence;
        HasEnvironmentInfluence = hasEnvironmentInfluence;
        EnvironmentInfluence = environmentInfluence;
        Threat = threat;
        HasSocialState = hasSocialState;
        Social = social;
        Roost = roost;
        VengeanceActive = vengeanceActive;
        VengeanceTarget = vengeanceTarget;
        FearSuppressed = fearSuppressed;
        ImmediateDanger = immediateDanger;
        HardSurvival = hardSurvival;
        CombatAllowed = combatAllowed;
        SocialAllowed = socialAllowed;
        CrossRoomOwned = crossRoomOwned;
        SpecialPhysicsOwner = specialPhysicsOwner;
    }
}

internal static class DB_FrameContextRuntime
{
    private sealed class Cache
    {
        internal int Clock = int.MinValue;
        internal DB_FrameContext Context;
    }

    private static ConditionalWeakTable<DB_Creature, Cache> caches = new();

    internal static void Reset()
    {
        caches = new ConditionalWeakTable<DB_Creature, Cache>();
    }

    internal static void Forget(DB_Creature bat)
    {
        if (bat != null) caches.Remove(bat);
    }

    internal static DB_FrameContext For(DB_Creature bat, bool refresh = false)
    {
        if (bat == null) return default;
        int clock = bat.room?.game?.clock ?? int.MinValue;
        Cache cache = caches.GetOrCreateValue(bat);
        if (!refresh && cache.Clock == clock) return cache.Context;
        cache.Context = Capture(bat, clock);
        cache.Clock = clock;
        return cache.Context;
    }

    private static DB_FrameContext Capture(DB_Creature bat, int clock)
    {
        Room room = bat.room;
        DB_Injury injury = bat.Injury;
        DB_AI ai = bat.DesertAI;
        DB_State persistent = bat.DesertState;

        DB_PerceptionSnapshot perception = ai?.Perception?.Snapshot ?? default;
        bool incomingProjectile = perception.HasIncomingProjectile;
        DB_WeaponObservation projectile = incomingProjectile
            ? perception.IncomingProjectile.Observation
            : default;

        bool hasEnvironment = DB_EnvironmentRuntime.TryGetInfluence(
            bat, out DB_EnvironmentInfluence environment);
        if (!hasEnvironment) environment = DB_EnvironmentInfluence.Neutral;

        DB_PerceptionSignalContext signalPerception = perception.Signals;
        bool hasSignal = ai?.Perception?.TryGetSignalContext(out signalPerception) == true;
        DB_SignalInfluence signal = new(
            signalPerception.AlarmPressure,
            signalPerception.AlarmOrigin,
            signalPerception.AlarmThreat,
            signalPerception.DistressInterest,
            signalPerception.DistressSource,
            signalPerception.RallyInterest,
            signalPerception.RallySource,
            signalPerception.RallyTarget,
            signalPerception.RoostInterest,
            signalPerception.RoostSource,
            signalPerception.HarassInterest,
            signalPerception.HarassSource,
            signalPerception.HarassTarget,
            signalPerception.SafeConfidence,
            signalPerception.LastReason);

        bool hasThreat = DB_ThreatRuntime.TryGetDebugState(
            bat, out DB_ThreatDebugState threatDebug);
        bool directAcuteThreat = hasThreat && (
            threatDebug.AcuteExplosionTimer > 0 || threatDebug.AcuteStartleTimer > 0 ||
            threatDebug.AcuteMassCasualtyTimer > 0 || threatDebug.AcuteCaptureTimer > 0 ||
            threatDebug.AcuteShockTimer > 0);
        bool reportedAnonymousHazard = signalPerception.AlarmThreat == null &&
            signalPerception.AlarmPressure >= DB_PerceptionRuntime.ReportedAnonymousHazardThreshold;
        bool acuteThreat = directAcuteThreat || reportedAnonymousHazard;
        Vector2? hazardCenter = hasThreat && threatDebug.HazardCenter.HasValue
            ? threatDebug.HazardCenter
            : reportedAnonymousHazard ? signalPerception.AlarmOrigin : null;
        DB_ThreatFrameSummary threat = new(
            hasThreat || reportedAnonymousHazard,
            hasThreat ? threatDebug.PlayerSlot : -1,
            hasThreat ? threatDebug.Confidence : signalPerception.AlarmPressure,
            incomingProjectile,
            acuteThreat,
            hazardCenter);

        bool hasSocial = DB_SocialRuntime.TryGetDebugState(
            bat, out DB_SocialDebugState social);

        bool hasTravel = DB_TravelRuntime.TryGetDebugState(
            bat.abstractCreature, out DB_TravelDebugState travelDebug);
        bool travelCanOwn = DB_TravelRuntime.CanOwnRealizedFrame(
            bat, out string travelReason);
        DB_TravelFrameSummary travel = new(
            hasTravel,
            travelCanOwn,
            hasTravel ? travelDebug.Purpose : default,
            hasTravel ? travelDebug.DestinationRoom : string.Empty,
            hasTravel && travelDebug.Suspended,
            travelReason);

        bool vengeance = DB_VengeanceRuntime.IsActive(bat);
        DB_VengeanceRuntime.TryGetTarget(bat, out Creature vengeanceTarget);
        bool fear = DB_FearRuntime.HasActiveFearSuppression(bat);

        float trauma = Mathf.Max(
            persistent.PlayerTraumaTicks > 0 ? persistent.PlayerTraumaStrength : 0f,
            persistent.PredatorTraumaTicks > 0 ? persistent.PredatorTraumaStrength : 0f);
        bool nativeChain = bat.AI?.behavior == FlyAI.Behavior.Chain;
        bool speciesRoost = ai?.Mode == DB_AI.Activity.Roost;
        DB_RoostFrameSummary roost = new(nativeChain || speciesRoost, nativeChain, speciesRoost);

        bool restrained = DB_RestraintPolicy.IsRestrainedByNonFly(bat);
        DB_SpecialPhysicsOwner special = ResolveSpecialPhysicsOwner(bat, restrained);
        bool immediateDanger = ai?.HasImmediateDanger == true ||
                               ai?.Mode == DB_AI.Activity.Escape;
        bool hardSurvival = hasEnvironment && environment.HardSurvival;
        bool combatAllowed = !bat.dead && bat.Consious && !restrained && !bat.inShortcut &&
                             special is not (DB_SpecialPhysicsOwner.FeedingAttach or
                                 DB_SpecialPhysicsOwner.CombatAttach or DB_SpecialPhysicsOwner.CombatInterfere) &&
                             !injury.BlocksCombat && !hardSurvival && !fear;
        bool socialAllowed = !bat.dead && bat.Consious && !restrained && !bat.inShortcut &&
                             !immediateDanger && !travelCanOwn && !injury.IsSeverelyInjured &&
                             !injury.IsRecovering && !hardSurvival && ai?.FormalAttack != true && !roost.Active;

        return new DB_FrameContext(
            bat,
            bat.abstractCreature != null ? bat.abstractCreature.ID : default,
            clock,
            room,
            bat.mainBodyChunk?.pos ?? Vector2.zero,
            bat.mainBodyChunk?.vel ?? Vector2.zero,
            bat.AI != null ? bat.AI.localGoal : (Vector2?)null,
            bat.Consious,
            bat.dead,
            restrained,
            bat.inShortcut,
            persistent.InHive,
            bat.movMode,
            bat.AI?.behavior,
            bat.Personality,
            injury.PhysicalCapability,
            injury.IsSeverelyInjured,
            injury.IsRecovering,
            injury.RecoveryTarget,
            injury.PostStunShock,
            persistent.Thirst,
            trauma,
            persistent.GriefStrength,
            persistent.SocialBondStrength,
            perception,
            incomingProjectile,
            projectile,
            hasEnvironment ? environment.VisibilityConfidence : 1f,
            travel,
            hasSignal,
            signal,
            hasEnvironment,
            environment,
            threat,
            hasSocial,
            social,
            roost,
            vengeance,
            vengeanceTarget,
            fear,
            immediateDanger,
            hardSurvival,
            combatAllowed,
            socialAllowed,
            travelCanOwn,
            special);
    }

    private static DB_SpecialPhysicsOwner ResolveSpecialPhysicsOwner(DB_Creature bat, bool restrained)
    {
        if (bat == null || bat.dead || !bat.Consious) return DB_SpecialPhysicsOwner.CreaturePhysics;
        if (restrained) return DB_SpecialPhysicsOwner.Grasp;
        if (bat.inShortcut) return DB_SpecialPhysicsOwner.Shortcut;
        if (bat.Emergence?.Active == true) return DB_SpecialPhysicsOwner.Emergence;
        if (bat.Feeding?.Attached == true) return DB_SpecialPhysicsOwner.FeedingAttach;
        if (bat.DesertAI?.Mode == DB_AI.Activity.Attach) return DB_SpecialPhysicsOwner.CombatAttach;
        if (bat.DesertAI?.Mode == DB_AI.Activity.Interfere) return DB_SpecialPhysicsOwner.CombatInterfere;
        if (bat.AI?.behavior == FlyAI.Behavior.Burrow) return DB_SpecialPhysicsOwner.NativeBurrow;
        if (bat.AI?.behavior == FlyAI.Behavior.Chain) return DB_SpecialPhysicsOwner.NativeChain;
        return DB_SpecialPhysicsOwner.None;
    }
}
