using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Domains that may become the single ordinary movement owner for one Desert Batfly frame.
/// Signal and Threat-memory are intentionally absent: they are information/modifier layers,
/// never locomotion owners.
/// </summary>
internal enum DB_BehaviorOwner
{
    None,
    CreaturePhysics,
    Restraint,
    Shortcut,
    Emergence,
    NativeSpecial,
    ImmediateDanger,
    InjuryRecovery,
    Travel,
    EnvironmentHardSurvival,
    FearResponse,
    Vengeance,
    EnvironmentLocalSurvival,
    ImmediateProjectileEvade,
    Feeding,
    Combat,
    Roost,
    Social,
    Ordinary,
    VanillaFallback
}

internal enum DB_BehaviorKind
{
    None,
    Disabled,
    Passive,
    Escape,
    Recover,
    CrossRoomTravel,
    Shelter,
    FearRetreat,
    Vengeance,
    ProjectileEvade,
    Feeding,
    Combat,
    Roost,
    Social,
    Idle,
    Native
}

/// <summary>
/// Explicit ownership for movement modes that legitimately bypass ordinary goal steering.
/// R3 only records these owners; later motor work must preserve their special physics.
/// </summary>
internal enum DB_SpecialPhysicsOwner
{
    None,
    CreaturePhysics,
    Grasp,
    Shortcut,
    Emergence,
    FeedingAttach,
    CombatAttach,
    CombatInterfere,
    NativeBurrow,
    NativeChain
}

internal readonly struct DB_BehaviorProposal
{
    internal readonly DB_BehaviorOwner Owner;
    internal readonly int Priority;
    internal readonly DB_BehaviorKind BehaviorKind;
    internal readonly Vector2? Goal;
    internal readonly float NominalSpeed;
    internal readonly FlyAI.Behavior DesiredNativeBehavior;
    internal readonly int DesiredDijkstraMap;
    internal readonly bool RequestBurrow;
    internal readonly bool SuppressCombat;
    internal readonly bool SuppressSocial;
    internal readonly bool PreserveGoal;
    internal readonly float Commitment;
    internal readonly string Reason;

    internal bool Valid => Owner != DB_BehaviorOwner.None;

    internal DB_BehaviorProposal(
        DB_BehaviorOwner owner,
        int priority,
        DB_BehaviorKind behaviorKind,
        Vector2? goal,
        float nominalSpeed,
        FlyAI.Behavior desiredNativeBehavior,
        int desiredDijkstraMap,
        bool requestBurrow,
        bool suppressCombat,
        bool suppressSocial,
        bool preserveGoal,
        float commitment,
        string reason)
    {
        Owner = owner;
        Priority = priority;
        BehaviorKind = behaviorKind;
        Goal = goal;
        NominalSpeed = Mathf.Max(0f, nominalSpeed);
        DesiredNativeBehavior = desiredNativeBehavior;
        DesiredDijkstraMap = desiredDijkstraMap;
        RequestBurrow = requestBurrow;
        SuppressCombat = suppressCombat;
        SuppressSocial = suppressSocial;
        PreserveGoal = preserveGoal;
        Commitment = Mathf.Clamp01(commitment);
        Reason = reason ?? string.Empty;
    }

    internal static DB_BehaviorProposal Create(
        DB_BehaviorOwner owner,
        DB_BehaviorKind behaviorKind,
        string reason,
        Vector2? goal = null,
        float nominalSpeed = 0f,
        FlyAI.Behavior desiredNativeBehavior = null,
        int desiredDijkstraMap = -1,
        bool requestBurrow = false,
        bool suppressCombat = false,
        bool suppressSocial = false,
        bool preserveGoal = false,
        float commitment = 0f)
        => new(
            owner,
            DB_BehaviorArbiter.PriorityOf(owner),
            behaviorKind,
            goal,
            nominalSpeed,
            desiredNativeBehavior,
            desiredDijkstraMap,
            requestBurrow,
            suppressCombat,
            suppressSocial,
            preserveGoal,
            commitment,
            reason);
}

internal readonly struct DB_BehaviorResolution
{
    internal readonly int Clock;
    internal readonly DB_BehaviorOwner PrimaryOwner;
    internal readonly DB_BehaviorProposal WinningProposal;
    internal readonly Vector2? FinalGoal;
    internal readonly float FinalSpeed;
    internal readonly FlyAI.Behavior FinalNativeBehavior;
    internal readonly int FinalDijkstraMap;
    internal readonly DB_SpecialPhysicsOwner SpecialPhysicsOwner;
    internal readonly bool SuppressCombat;
    internal readonly bool SuppressSocial;
    internal readonly string Reason;

    internal bool Resolved => PrimaryOwner != DB_BehaviorOwner.None;

    internal DB_BehaviorResolution(
        int clock,
        in DB_BehaviorProposal winner,
        Vector2? finalGoal,
        FlyAI.Behavior finalNativeBehavior,
        int finalDijkstraMap,
        DB_SpecialPhysicsOwner specialPhysicsOwner,
        string reason)
    {
        Clock = clock;
        PrimaryOwner = winner.Owner;
        WinningProposal = winner;
        FinalGoal = finalGoal;
        FinalSpeed = winner.NominalSpeed;
        FinalNativeBehavior = finalNativeBehavior;
        FinalDijkstraMap = finalDijkstraMap;
        SpecialPhysicsOwner = specialPhysicsOwner;
        SuppressCombat = winner.SuppressCombat;
        SuppressSocial = winner.SuppressSocial;
        Reason = reason ?? winner.Reason ?? string.Empty;
    }
}

internal readonly struct DB_BehaviorRejection
{
    internal readonly DB_BehaviorOwner Owner;
    internal readonly int Priority;
    internal readonly string Reason;

    internal DB_BehaviorRejection(DB_BehaviorOwner owner, int priority, string reason)
    {
        Owner = owner;
        Priority = priority;
        Reason = reason ?? string.Empty;
    }
}

internal readonly struct DB_BehaviorArbiterDebugState
{
    internal readonly DB_BehaviorResolution Resolution;
    internal readonly DB_BehaviorRejection[] Rejected;

    internal DB_BehaviorArbiterDebugState(
        in DB_BehaviorResolution resolution,
        DB_BehaviorRejection[] rejected)
    {
        Resolution = resolution;
        Rejected = rejected ?? System.Array.Empty<DB_BehaviorRejection>();
    }
}