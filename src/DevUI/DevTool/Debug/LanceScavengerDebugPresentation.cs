using System;
using System.Collections.Generic;
using DryCycle.Creatures.LanceScavenger;
using DryCycle.Items.ScavengerLance;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Debug;

public sealed class LanceScavengerDebugChunkSnapshot
{
    public int Index { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float Radius { get; init; }
    public bool CurrentAimChunk { get; init; }
    public bool BestAimChunk { get; init; }
}

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
    public int BestTargetChunkIndex { get; init; }
    public int ImpactFrame { get; init; }
    public bool ExactAim { get; init; }
    public float AimX { get; init; }
    public float AimY { get; init; }
    public float LancePitchDegrees { get; init; }

    public float OriginX { get; init; }
    public float OriginY { get; init; }
    public float GripX { get; init; }
    public float GripY { get; init; }
    public float LanceDirectionX { get; init; }
    public float LanceDirectionY { get; init; }
    public float LanceForwardLength { get; init; }
    public float ActualBladeHitPadding { get; init; }
    public float PlanningBladeHitPadding { get; init; }
    public float BladeShoulderHalfWidth { get; init; }
    public float BladeTipHalfWidth { get; init; }
    public LanceScavengerDebugChunkSnapshot[] TargetChunks { get; init; } = Array.Empty<LanceScavengerDebugChunkSnapshot>();
    public float[] BodyPathX { get; init; } = Array.Empty<float>();
    public float[] BodyPathY { get; init; } = Array.Empty<float>();
    public float[] TipPathX { get; init; } = Array.Empty<float>();
    public float[] TipPathY { get; init; } = Array.Empty<float>();

    public bool PathClear { get; init; }
    public string LaneReason { get; init; } = string.Empty;
    public bool ChargePriority { get; init; }
    public bool CommitReady { get; init; }
    public bool HardBlocked { get; init; }
    public bool FriendBlocked { get; init; }
    public bool ChargeOpportunity { get; init; }
    public string DecisionReason { get; init; } = string.Empty;

    public float TargetStability { get; init; }
    public float DodgeSeverity { get; init; }
    public float TargetVelocityX { get; init; }
    public float TargetVelocityY { get; init; }

    public bool CounterSweepAttempted { get; init; }
    public bool CounterSweepActive { get; init; }
    public float CounterSweepChance { get; init; }

    // Oldest -> newest samples. They are attached before the snapshot is published,
    // and are only allocated while this debug page owns an active capture lease.
    public float[] AimQualityHistory { get; internal set; } = Array.Empty<float>();
    public bool[] AimReadyHistory { get; internal set; } = Array.Empty<bool>();
    public bool[] HardBlockHistory { get; internal set; } = Array.Empty<bool>();
    public bool[] BraceHistory { get; internal set; } = Array.Empty<bool>();
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
    private const int HistoryFrames = LanceCombatState.BraceFrames;
    private const uint LeaseTimeoutMilliseconds = 750;
    // Keep the last sample through normal pauses / frame stepping. Room changes and closing the
    // page clear immediately; this longer timeout only prevents a paused simulation from making
    // the diagnostic page appear as if the creature vanished.
    private const uint EntryTimeoutMilliseconds = 30000;

    private sealed class EntryRecord
    {
        internal LanceScavengerDebugEntrySnapshot Snapshot;
        internal int LastSeenTick;
        internal readonly float[] Quality = new float[HistoryFrames];
        internal readonly byte[] Flags = new byte[HistoryFrames];
        internal int Next;
        internal int Count;

        internal void Append(LanceScavengerDebugEntrySnapshot snapshot, int now)
        {
            Snapshot = snapshot;
            LastSeenTick = now;
            Quality[Next] = snapshot.AimQuality;
            byte flags = 0;
            if (snapshot.AimReady) flags |= 1;
            if (snapshot.HardBlocked) flags |= 2;
            if (string.Equals(snapshot.State, LanceState.Brace.ToString(), StringComparison.Ordinal)) flags |= 4;
            Flags[Next] = flags;
            Next = (Next + 1) % HistoryFrames;
            if (Count < HistoryFrames) Count++;

            float[] quality = new float[Count];
            bool[] ready = new bool[Count];
            bool[] hard = new bool[Count];
            bool[] brace = new bool[Count];
            int start = (Next - Count + HistoryFrames) % HistoryFrames;
            for (int i = 0; i < Count; i++)
            {
                int source = (start + i) % HistoryFrames;
                quality[i] = Quality[source];
                ready[i] = (Flags[source] & 1) != 0;
                hard[i] = (Flags[source] & 2) != 0;
                brace[i] = (Flags[source] & 4) != 0;
            }

            snapshot.AimQualityHistory = quality;
            snapshot.AimReadyHistory = ready;
            snapshot.HardBlockHistory = hard;
            snapshot.BraceHistory = brace;
        }
    }

    private static readonly object Sync = new();
    private static readonly Dictionary<long, EntryRecord> Entries = new();
    private static volatile bool requestedFast;
    private static bool requested;
    private static string requestedRoom = string.Empty;
    private static int lastLeaseTick;

    /// <summary>
    /// Called every ImGui frame while the page is visible. This is a short lease rather than a
    /// permanent toggle: if DevTools/RWImGui disappears unexpectedly, gameplay stops paying the
    /// debug capture cost automatically within a fraction of a second.
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
            requestedFast = value;
            if (value)
                lastLeaseTick = Environment.TickCount;
            else
                Entries.Clear();
        }
    }

    public static LanceScavengerDebugSnapshot Current
    {
        get
        {
            lock (Sync)
            {
                int now = Environment.TickCount;
                ExpireLeaseUnsafe(now);
                if (!requested)
                    return LanceScavengerDebugSnapshot.Empty;

                RemoveStaleEntriesUnsafe(now);
                LanceScavengerDebugEntrySnapshot[] values = new LanceScavengerDebugEntrySnapshot[Entries.Count];
                int index = 0;
                foreach (EntryRecord record in Entries.Values)
                    values[index++] = record.Snapshot;
                Array.Sort(values, static (a, b) =>
                {
                    int spawner = a.Spawner.CompareTo(b.Spawner);
                    return spawner != 0 ? spawner : a.Number.CompareTo(b.Number);
                });
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
        // Normal gameplay pays only one volatile read when the debug page is closed.
        if (!requestedFast || owner?.room == null || brain == null) return;

        string roomName = owner.room.abstractRoom?.name ?? string.Empty;
        int now = Environment.TickCount;
        lock (Sync)
        {
            ExpireLeaseUnsafe(now);
            if (!requested || !string.Equals(requestedRoom, roomName, StringComparison.Ordinal))
                return;
        }

        LanceScavengerDebugEntrySnapshot snapshot = Capture(owner, brain, roomName);

        lock (Sync)
        {
            now = Environment.TickCount;
            ExpireLeaseUnsafe(now);
            if (!requested || !string.Equals(requestedRoom, roomName, StringComparison.Ordinal))
                return;

            long key = EntityKey(owner.abstractCreature.ID.spawner, owner.abstractCreature.ID.number);
            if (!Entries.TryGetValue(key, out EntryRecord record))
            {
                record = new EntryRecord();
                Entries[key] = record;
            }
            record.Append(snapshot, now);
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
        ChargeLane lane = brain.Lane;

        float distance = target == null
            ? 0f
            : Vector2.Distance(owner.mainBodyChunk.pos, target.mainBodyChunk.pos);

        int targetChunkIndex = FindChunkIndex(target, aim.TargetChunk);
        int bestTargetChunkIndex = FindChunkIndex(target, best.TargetChunk);
        BodyChunk trackedChunk = aim.TargetChunk ?? best.TargetChunk ?? target?.mainBodyChunk;
        Vector2 targetVelocity = trackedChunk == null
            ? Vector2.zero
            : brain.MotionTracker.SmoothedVelocity(trackedChunk);
        float stability = trackedChunk == null ? 0f : brain.MotionTracker.Stability(trackedChunk);

        // During flight the motor owns the actual lance orientation (including counter-sweep).
        // Before takeoff, visualize the current solver direction so the developer can see what is
        // being evaluated for a possible commit.
        Vector2 pitchDirection = owner.Combat.State == LanceState.Charge
            ? owner.Motor.LanceDirection
            : aim.Valid ? aim.LanceDirection : owner.Motor.LanceDirection;
        if (pitchDirection.sqrMagnitude < 0.001f) pitchDirection = Vector2.right;
        pitchDirection.Normalize();
        float pitch = Mathf.Atan2(pitchDirection.y, Mathf.Max(0.0001f, Mathf.Abs(pitchDirection.x))) * Mathf.Rad2Deg;
        Vector2 origin = owner.mainBodyChunk.pos;
        Vector2 grip = origin + new Vector2(pitchDirection.x * 7f, -5f);
        float lanceLength = owner.Lance?.Length ?? LanceCombatMath.DefaultLength;
        float forwardLength = LanceCombatMath.ForwardLength(lanceLength);

        int trajectoryFrames = aim.ImpactFrame > 0
            ? Mathf.Min(LanceCombatState.MaxChargeFrames, aim.ImpactFrame + 3)
            : LanceCombatState.MaxChargeFrames;
        Vector2[] bodyPath = LanceAimSolver.BuildDebugBodyTrajectory(owner, origin, pitchDirection, trajectoryFrames);
        BuildPathArrays(bodyPath, pitchDirection, forwardLength,
            out float[] bodyPathX, out float[] bodyPathY, out float[] tipPathX, out float[] tipPathY);

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
            BestAimReady = brain.DebugRecentAimReady,
            TargetChunkIndex = targetChunkIndex,
            BestTargetChunkIndex = bestTargetChunkIndex,
            ImpactFrame = aim.ImpactFrame,
            ExactAim = aim.Exact,
            AimX = aim.Aim.x,
            AimY = aim.Aim.y,
            LancePitchDegrees = pitch,

            OriginX = origin.x,
            OriginY = origin.y,
            GripX = grip.x,
            GripY = grip.y,
            LanceDirectionX = pitchDirection.x,
            LanceDirectionY = pitchDirection.y,
            LanceForwardLength = forwardLength,
            ActualBladeHitPadding = LanceAimSolver.ActualBladeHitPadding,
            PlanningBladeHitPadding = LanceAimSolver.PlanningBladeHitPadding,
            BladeShoulderHalfWidth = LanceCombatMath.BladeShoulderHalfWidth,
            BladeTipHalfWidth = LanceCombatMath.BladeTipHalfWidth,
            TargetChunks = CaptureChunks(target, targetChunkIndex, bestTargetChunkIndex),
            BodyPathX = bodyPathX,
            BodyPathY = bodyPathY,
            TipPathX = tipPathX,
            TipPathY = tipPathY,

            PathClear = lane.PathClear,
            LaneReason = lane.Reason ?? string.Empty,
            ChargePriority = brain.ChargePriority,
            CommitReady = brain.DebugCommitReady,
            HardBlocked = brain.DebugHardBlocked,
            FriendBlocked = brain.DebugFriendBlocked,
            ChargeOpportunity = brain.DebugChargeOpportunity,
            DecisionReason = DecisionReason(owner, brain, target, distance, lane, aim),

            TargetStability = stability,
            DodgeSeverity = brain.MotionTracker.DodgeSeverity,
            TargetVelocityX = targetVelocity.x,
            TargetVelocityY = targetVelocity.y,

            CounterSweepAttempted = owner.Motor.CounterSweepAttempted,
            CounterSweepActive = owner.Motor.CounterSweepActive,
            CounterSweepChance = owner.Motor.CounterSweepChance
        };
    }

    private static void BuildPathArrays(Vector2[] bodyPath, Vector2 direction, float forwardLength,
        out float[] bodyX, out float[] bodyY, out float[] tipX, out float[] tipY)
    {
        int count = bodyPath?.Length ?? 0;
        bodyX = new float[count];
        bodyY = new float[count];
        tipX = new float[count];
        tipY = new float[count];
        for (int i = 0; i < count; i++)
        {
            Vector2 body = bodyPath[i];
            Vector2 pathGrip = body + new Vector2(direction.x * 7f, -5f);
            Vector2 tip = pathGrip + direction * forwardLength;
            bodyX[i] = body.x;
            bodyY[i] = body.y;
            tipX[i] = tip.x;
            tipY[i] = tip.y;
        }
    }

    private static LanceScavengerDebugChunkSnapshot[] CaptureChunks(Creature target, int current, int best)
    {
        if (target?.bodyChunks == null || target.bodyChunks.Length == 0)
            return Array.Empty<LanceScavengerDebugChunkSnapshot>();

        LanceScavengerDebugChunkSnapshot[] chunks = new LanceScavengerDebugChunkSnapshot[target.bodyChunks.Length];
        for (int i = 0; i < chunks.Length; i++)
        {
            BodyChunk chunk = target.bodyChunks[i];
            chunks[i] = new LanceScavengerDebugChunkSnapshot
            {
                Index = i,
                X = chunk.pos.x,
                Y = chunk.pos.y,
                Radius = chunk.rad,
                CurrentAimChunk = i == current,
                BestAimChunk = i == best
            };
        }
        return chunks;
    }

    private static int FindChunkIndex(Creature target, BodyChunk chunk)
    {
        if (target?.bodyChunks == null || chunk == null) return -1;
        for (int i = 0; i < target.bodyChunks.Length; i++)
            if (ReferenceEquals(target.bodyChunks[i], chunk)) return i;
        return -1;
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
        LanceAimSolution aim)
    {
        if (!owner.Consious || owner.grabbedBy.Count > 0) return "inactive";
        if (owner.Lance == null) return "no lance";
        if (target == null) return "no target";
        if (brain.TargetViolence != ScavengerAI.ViolenceType.Lethal) return "not lethal";
        if (!brain.ChargePriority) return "yielding priority";
        if (distance < ChargeLanePlanner.MinimumChargeDistance) return "too close";
        if (distance > ChargeLanePlanner.MaximumChargeDistance(owner)) return "too far";
        if (brain.DebugHardBlocked) return lane.Reason ?? "hard block";
        if (owner.Combat.Cooldown > 0) return "cooldown";
        if (owner.Combat.State == LanceState.Charge)
            return owner.Motor.CounterSweepActive ? "counter sweep" : "charging";
        if (owner.Combat.State == LanceState.Brace)
            return brain.DebugCommitReady ? "brace ready" : "aim quality";
        if (!aim.Valid) return "no aim opportunity";
        if (!aim.Ready) return "aim quality";
        if (!lane.PathClear) return lane.Reason ?? "path blocked";
        return brain.DebugChargeOpportunity ? "ready" : "waiting state";
    }

    private static void ExpireLeaseUnsafe(int now)
    {
        if (!requested) return;
        if (ElapsedMilliseconds(lastLeaseTick, now) <= LeaseTimeoutMilliseconds) return;
        requested = false;
        requestedFast = false;
        Entries.Clear();
    }

    private static void RemoveStaleEntriesUnsafe(int now)
    {
        if (Entries.Count == 0) return;
        List<long> stale = null;
        foreach (KeyValuePair<long, EntryRecord> pair in Entries)
        {
            if (ElapsedMilliseconds(pair.Value.LastSeenTick, now) <= EntryTimeoutMilliseconds) continue;
            stale ??= new List<long>();
            stale.Add(pair.Key);
        }

        if (stale == null) return;
        for (int i = 0; i < stale.Count; i++)
            Entries.Remove(stale[i]);
    }

    private static long EntityKey(int spawner, int number) =>
        ((long)spawner << 32) ^ (uint)number;

    private static uint ElapsedMilliseconds(int then, int now) =>
        unchecked((uint)(now - then));
}
