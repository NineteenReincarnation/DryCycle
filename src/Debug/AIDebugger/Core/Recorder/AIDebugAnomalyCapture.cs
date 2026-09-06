using System;

namespace DryCycle.Debugging.AI;

internal readonly struct AIDebugAnomalyStatus
{
    internal readonly int Active;
    internal readonly int Completed;
    internal readonly int Dropped;
    internal readonly int LastTriggerTick;
    internal readonly string LastReason;

    internal AIDebugAnomalyStatus(int active, int completed, int dropped, int lastTriggerTick, string lastReason)
    {
        Active = active;
        Completed = completed;
        Dropped = dropped;
        LastTriggerTick = lastTriggerTick;
        LastReason = lastReason ?? string.Empty;
    }
}

// Zero-copy trigger capture. Triggering only creates a compact range descriptor and pins
// recorder blocks covering pre-roll/post-roll. At completion those same blocks are leased
// directly to the background writer and are unpinned only after writer leases exist.
internal static class AIDebugAnomalyCapture
{
    private const int PreRollTicks = 160;  // 4 seconds
    private const int PostRollTicks = 80;  // 2 seconds
    private const int MaxActive = 8;
    private const int DuplicateSuppressTicks = 40;

    private sealed class Capture
    {
        internal int Id;
        internal DebugEntityKey Key;
        internal int StartTick;
        internal int TriggerTick;
        internal int EndTick;
        internal string Reason;
        internal AIDebugRecorderRangePin Pin;
        internal bool Active;
    }

    private static readonly Capture[] Active = new Capture[MaxActive];
    private static int nextId = 1;
    private static int activeCount;
    private static int completedCount;
    private static int droppedCount;
    private static int lastTriggerTick = int.MinValue;
    private static DebugEntityKey lastTriggerKey;
    private static string lastReason;

    internal static AIDebugAnomalyStatus GetStatus() =>
        new(activeCount, completedCount, droppedCount, lastTriggerTick, lastReason);

    internal static void OnFastStateChanged(
        DebugEntityKey key,
        int tick,
        AIDebugFastState previous,
        AIDebugFastState current)
    {
        bool previousDead = (previous.Flags & AIDebugFastFlags.Dead) != 0;
        bool currentDead = (current.Flags & AIDebugFastFlags.Dead) != 0;
        if (!previousDead && currentDead)
        {
            Trigger(key, tick, "death");
            return;
        }

        bool previousAttack = (previous.Flags & AIDebugFastFlags.FormalAttack) != 0;
        bool currentAttack = (current.Flags & AIDebugFastFlags.FormalAttack) != 0;
        if (!previousAttack && currentAttack)
        {
            Trigger(key, tick, "formal-attack");
            return;
        }

        bool previousDanger = (previous.Flags & AIDebugFastFlags.HasImmediateDanger) != 0;
        bool currentDanger = (current.Flags & AIDebugFastFlags.HasImmediateDanger) != 0;
        if (!previousDanger && currentDanger)
        {
            Trigger(key, tick, "immediate-danger");
            return;
        }

        if (previous.EntityState != AIDebugEntityState.Deleted && current.EntityState == AIDebugEntityState.Deleted)
            Trigger(key, tick, "deleted");
    }

    internal static bool Trigger(DebugEntityKey key, int tick, string reason)
    {
        if (tick - lastTriggerTick < DuplicateSuppressTicks && key == lastTriggerKey &&
            string.Equals(reason, lastReason, StringComparison.Ordinal))
            return false;

        int slot = -1;
        for (int i = 0; i < Active.Length; i++)
        {
            if (Active[i] == null || !Active[i].Active)
            {
                slot = i;
                break;
            }
        }
        if (slot < 0)
        {
            droppedCount++;
            return false;
        }

        int start = Math.Max(0, tick - PreRollTicks);
        int end = tick + PostRollTicks;
        if (!AIDebugRecorder.TryPinRange(key, start, end, out AIDebugRecorderRangePin pin))
        {
            droppedCount++;
            return false;
        }

        Capture capture = Active[slot] ?? new Capture();
        capture.Id = nextId++;
        if (nextId <= 0) nextId = 1;
        capture.Key = key;
        capture.StartTick = start;
        capture.TriggerTick = tick;
        capture.EndTick = end;
        capture.Reason = string.IsNullOrEmpty(reason) ? "anomaly" : reason;
        capture.Pin = pin;
        capture.Active = true;
        Active[slot] = capture;
        activeCount++;
        lastTriggerTick = tick;
        lastTriggerKey = key;
        lastReason = capture.Reason;
        return true;
    }

    internal static void OnSimulationTick(int tick)
    {
        if (activeCount <= 0) return;

        for (int i = 0; i < Active.Length; i++)
        {
            Capture capture = Active[i];
            if (capture == null || !capture.Active || tick < capture.EndTick) continue;

            try
            {
                AIDebugSessionBlockWriter.EnsureAnomalySession();
                int motionBlocks = AIDebugRecorder.LeaseMotionRange(
                    capture.Key,
                    capture.StartTick,
                    capture.EndTick,
                    lease => AIDebugSessionBlockWriter.EnqueueMotion(capture.Key, lease, force: true));
                int stateBlocks = AIDebugRecorder.LeaseFastStateRange(
                    capture.Key,
                    capture.StartTick,
                    capture.EndTick,
                    lease => AIDebugSessionBlockWriter.EnqueueState(capture.Key, lease, force: true));

                AIDebugSessionBlockWriter.EnqueueCaptureMetadata(
                    capture.Id,
                    capture.Key,
                    capture.StartTick,
                    capture.TriggerTick,
                    capture.EndTick,
                    capture.Reason,
                    motionBlocks,
                    stateBlocks);
                completedCount++;
            }
            catch
            {
                droppedCount++;
            }
            finally
            {
                AIDebugRecorder.ReleasePinnedRange(capture.Pin);
                capture.Active = false;
                capture.Pin = default;
                activeCount--;
            }
        }

        if (activeCount == 0 && AIDebugSessionBlockWriter.AutomaticSession)
            AIDebugSessionBlockWriter.StopSession();
    }

    internal static void Reset()
    {
        for (int i = 0; i < Active.Length; i++)
        {
            Capture capture = Active[i];
            if (capture != null && capture.Active)
            {
                AIDebugRecorder.ReleasePinnedRange(capture.Pin);
                capture.Active = false;
                capture.Pin = default;
            }
        }
        activeCount = 0;
        completedCount = 0;
        droppedCount = 0;
        lastTriggerTick = int.MinValue;
        lastTriggerKey = default;
        lastReason = null;
    }
}
