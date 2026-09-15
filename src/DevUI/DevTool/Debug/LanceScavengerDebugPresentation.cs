using System;
using System.Collections.Generic;
using DryCycle.Creatures.LanceScavenger;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Debug;

/// <summary>
/// Detached, render-thread-safe snapshot used by the rebuilt DevTool UI.
/// LanceScavengerAI publishes these records from the simulation thread only while
/// the dedicated debug page is visible.
/// </summary>
public sealed class LanceScavengerDebugEntrySnapshot
{
    public int Spawner { get; init; }
    public int Number { get; init; }
    public string EntityId { get; init; } = string.Empty;
    public string RoomName { get; init; } = string.Empty;

    public string State { get; init; } = string.Empty;
    public int StateAge { get; init; }
    public int Cooldown { get; init; }
    public int AttackSerial { get; init; }
    public bool Conscious { get; init; }
    public bool HasLance { get; init; }
    public bool HasSidearm { get; init; }

    public string Behavior { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
    public string Violence { get; init; } = string.Empty;
    public bool Afraid { get; init; }
    public float Distance { get; init; }

    public float AimQuality { get; init; }
    public float AimThreshold { get; init; }
    public bool AimReady { get; init; }
    public float BestAimQuality { get; init; }
    public int BestAimAge { get; init; }
    public bool BestAimReady { get; init; }
    public int TargetChunkIndex { get; init; }
    public int ImpactFrame { get; init; }
    public bool ExactAim { get; init; }
    public float AimX { get; init; }
    public float AimY { get; init; }
    public float LancePitchDegrees { get; init; }

    public bool PathClear { get; init; }
    public string LaneReason { get; init; } = string.Empty;
    public bool ChargePriority { get; init; }
    public bool CommitReady { get; init; }
    public bool HardBlocked { get; init; }
    public string DecisionReason { get; init; } = string.Empty;

    public float TargetStability { get; init; }
    public float DodgeSeverity { get; init; }
    public float TargetVelocityX { get; init; }
    public float TargetVelocityY { get; init; }

    public bool CounterSweepAttempted { get; init; }
    public bool CounterSweepActive { get; init; }
    public float CounterSweepChance { get; init; }
}

public sealed class LanceScavengerDebugSnapshot
{
    public static readonly LanceScavengerDebugSnapshot Empty = new();

    public bool Requested { get; init; }
    public string RoomName { get; init; } = string.Empty;
    public LanceScavengerDebugEntrySnapshot[] Entries { get; init; } = Array.Empty<LanceScavengerDebugEntrySnapshot>();
}

public static class LanceScavengerDebugPresentationHub
{
    private static readonly object Sync = new();
    private static readonly Dictionary<int, LanceScavengerDebugEntrySnapshot> Entries = new();
    private static bool requested;
    private static string requestedRoom = string.Empty;

    /// <summary>
    /// Called by the ImGui page. A room change starts a fresh capture set so stale
    /// creatures from the previous room can never leak into the new page.
    /// </summary>
    public static void SetRequested(bool value, string roomName)
    {
        string normalized = roomName ?? string.Empty;
        lock (Sync)
        {
            if (!string.Equals(requestedRoom, normalized, StringComparison.Ordinal))
            {
                Entries.Clear();
                requestedRoom = normalized;
            }

            requested = value;
            if (!value)
                Entries.Clear();
        }
    }

    public static LanceScavengerDebugSnapshot Current
    {
        get
        {
            lock (Sync)
            {
                if (!requested)
                    return LanceScavengerDebugSnapshot.Empty;

                LanceScavengerDebugEntrySnapshot[] values = new LanceScavengerDebugEntrySnapshot[Entries.Count];
                Entries.Values.CopyTo(values, 0);
                Array.Sort(values, static (a, b) => a.Number.CompareTo(b.Number));
                return new LanceScavengerDebugSnapshot
                {
                    Requested = true,
                    RoomName = requestedRoom,
                    Entries = values
                };
            }
        }
    }

    internal static void Publish(LanceScavenger owner, LanceScavengerAI brain)
    {
        if (owner?.room == null || brain == null) return;

        string roomName = owner.room.abstractRoom?.name ?? string.Empty;
        lock (Sync)
        {
            if (!requested || !string.Equals(requestedRoom, roomName, StringComparison.Ordinal))
                return;
        }

        LanceScavengerDebugEntrySnapshot snapshot = Capture(owner, brain, roomName);

        lock (Sync)
        {
            if (!requested || !string.Equals(requestedRoom, roomName, StringComparison.Ordinal))
                return;
            Entries[owner.abstractCreature.ID.number] = snapshot;
        }
    }

    private static LanceScavengerDebugEntrySnapshot Capture(
        LanceScavenger owner,
        LanceScavengerAI brain,
        string roomName)
    {
        Creature target = brain.Target;
        LanceAimSolution aim = brain.AimSolution;
        LanceAimSolution best = brain.DebugRecentAimSolution;
        bool bestReady = brain.DebugRecentAimReady;
        ChargeLane lane = brain.Lane;

        float distance = target == null
            ? 0f
            : Vector2.Distance(owner.mainBodyChunk.pos, target.mainBodyChunk.pos);
        bool friendBlocked = lane.Reason == "friend in lane";
        bool hardBlocked = friendBlocked || lane.Reason == "distance" ||
                           lane.Reason == "wall / ceiling" || lane.Reason == "lance blocked";
        bool commitReady = !hardBlocked && (aim.Ready || bestReady);

        int targetChunkIndex = -1;
        if (target?.bodyChunks != null && aim.TargetChunk != null)
        {
            for (int i = 0; i < target.bodyChunks.Length; i++)
            {
                if (!ReferenceEquals(target.bodyChunks[i], aim.TargetChunk)) continue;
                targetChunkIndex = i;
                break;
            }
        }

        BodyChunk trackedChunk = aim.TargetChunk ?? target?.mainBodyChunk;
        Vector2 targetVelocity = trackedChunk == null
            ? Vector2.zero
            : brain.MotionTracker.SmoothedVelocity(trackedChunk);
        float stability = trackedChunk == null ? 0f : brain.MotionTracker.Stability(trackedChunk);
        float pitch = Mathf.Atan2(aim.Valid ? aim.LanceDirection.y : owner.Motor.LanceDirection.y,
            Mathf.Max(0.0001f, Mathf.Abs(aim.Valid ? aim.LanceDirection.x : owner.Motor.LanceDirection.x))) * Mathf.Rad2Deg;

        return new LanceScavengerDebugEntrySnapshot
        {
            Spawner = owner.abstractCreature.ID.spawner,
            Number = owner.abstractCreature.ID.number,
            EntityId = owner.abstractCreature.ID.ToString(),
            RoomName = roomName,

            State = owner.Combat.State.ToString(),
            StateAge = owner.Combat.Age,
            Cooldown = owner.Combat.Cooldown,
            AttackSerial = owner.Combat.AttackSerial,
            Conscious = owner.Consious,
            HasLance = owner.Lance != null,
            HasSidearm = owner.SidearmSpear != null,

            Behavior = brain.behavior?.ToString() ?? string.Empty,
            Target = TargetLabel(target),
            Violence = brain.TargetViolence?.ToString() ?? string.Empty,
            Afraid = brain.TargetAfraid,
            Distance = distance,

            AimQuality = aim.Quality,
            AimThreshold = LanceAimSolver.MinimumAimQuality,
            AimReady = aim.Ready,
            BestAimQuality = best.Quality,
            BestAimAge = brain.DebugRecentAimAge,
            BestAimReady = bestReady,
            TargetChunkIndex = targetChunkIndex,
            ImpactFrame = aim.ImpactFrame,
            ExactAim = aim.Exact,
            AimX = aim.Aim.x,
            AimY = aim.Aim.y,
            LancePitchDegrees = pitch,

            PathClear = lane.PathClear,
            LaneReason = lane.Reason ?? string.Empty,
            ChargePriority = brain.ChargePriority,
            CommitReady = commitReady,
            HardBlocked = hardBlocked,
            DecisionReason = DecisionReason(owner, brain, target, distance, lane, aim, bestReady, hardBlocked),

            TargetStability = stability,
            DodgeSeverity = brain.MotionTracker.DodgeSeverity,
            TargetVelocityX = targetVelocity.x,
            TargetVelocityY = targetVelocity.y,

            CounterSweepAttempted = owner.Motor.CounterSweepAttempted,
            CounterSweepActive = owner.Motor.CounterSweepActive,
            CounterSweepChance = owner.Motor.CounterSweepChance
        };
    }

    private static string TargetLabel(Creature target)
    {
        if (target == null) return string.Empty;
        string type = target.abstractCreature?.creatureTemplate?.type?.value ?? target.GetType().Name;
        return type + " · " + target.abstractCreature.ID;
    }

    private static string DecisionReason(
        LanceScavenger owner,
        LanceScavengerAI brain,
        Creature target,
        float distance,
        ChargeLane lane,
        LanceAimSolution aim,
        bool bestReady,
        bool hardBlocked)
    {
        if (!owner.Consious || owner.grabbedBy.Count > 0) return "inactive";
        if (owner.Lance == null) return "no lance";
        if (target == null) return "no target";
        if (brain.TargetViolence != ScavengerAI.ViolenceType.Lethal) return "not lethal";
        if (!brain.ChargePriority) return "yielding priority";
        if (distance < ChargeLanePlanner.MinimumChargeDistance) return "too close";
        if (distance > ChargeLanePlanner.MaximumChargeDistance(owner)) return "too far";
        if (hardBlocked) return lane.Reason ?? "hard block";
        if (owner.Combat.Cooldown > 0) return "cooldown";
        if (owner.Combat.State == LanceState.Charge)
            return owner.Motor.CounterSweepActive ? "counter sweep" : "charging";
        if (owner.Combat.State == LanceState.Brace)
            return aim.Ready || bestReady ? "brace ready" : "aim quality";
        if (!aim.Valid) return "no aim opportunity";
        if (!aim.Ready) return "aim quality";
        if (!lane.PathClear) return lane.Reason ?? "path blocked";
        return "ready";
    }
}
