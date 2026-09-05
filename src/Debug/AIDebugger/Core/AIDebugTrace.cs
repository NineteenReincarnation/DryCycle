using System;
using System.Collections.Generic;
using UnityEngine;

namespace DryCycle.Debugging.AI;

internal enum AIDebugEventCategory
{
    Decision,
    State,
    Perception,
    Path,
    Combat,
    Social,
    Warning
}

internal readonly struct AIDebugTraceEvent
{
    internal readonly int Frame;
    internal readonly float Time;
    internal readonly AIDebugEventCategory Category;
    internal readonly string Name;
    internal readonly string Detail;
    internal readonly string Reason;

    internal AIDebugTraceEvent(int frame, float time, AIDebugEventCategory category,
        string name, string detail, string reason)
    {
        Frame = frame;
        Time = time;
        Category = category;
        Name = name ?? "?";
        Detail = detail ?? string.Empty;
        Reason = reason ?? string.Empty;
    }
}

internal sealed class AIDebugHistoricalState
{
    internal readonly AIDebugSnapshot Snapshot;
    internal readonly AIDebugUtilityRow[] Utilities;
    internal readonly AIDebugPerceptionRow[] Perception;
    internal readonly AIDebugPathState Path;

    internal AIDebugHistoricalState(AIDebugSnapshot snapshot, AIDebugUtilityRow[] utilities,
        AIDebugPerceptionRow[] perception, AIDebugPathState path)
    {
        Snapshot = snapshot;
        Utilities = utilities ?? Array.Empty<AIDebugUtilityRow>();
        Perception = perception ?? Array.Empty<AIDebugPerceptionRow>();
        Path = path;
    }
}

internal readonly struct AIDebugTraceFrame
{
    internal readonly int Frame;
    internal readonly float Time;
    internal readonly string Room;
    internal readonly Vector2 Position;
    internal readonly Vector2 Velocity;
    internal readonly Vector2 LocalGoal;
    internal readonly string Mode;
    internal readonly string Target;
    internal readonly string Role;
    internal readonly string Suppression;
    internal readonly string ControlOwner;
    internal readonly float Utility0;
    internal readonly float Utility1;
    internal readonly float Utility2;
    internal readonly float Panic;
    internal readonly AIDebugHistoricalState History;

    internal AIDebugTraceFrame(string room, Vector2 position, Vector2 velocity, Vector2 localGoal,
        string mode, string target, string role, string suppression, string controlOwner,
        float utility0 = 0f, float utility1 = 0f, float utility2 = 0f, float panic = 0f,
        AIDebugHistoricalState history = null)
        : this(UnityEngine.Time.frameCount, UnityEngine.Time.unscaledTime, room, position, velocity,
            localGoal, mode, target, role, suppression, controlOwner,
            utility0, utility1, utility2, panic, history)
    {
    }

    private AIDebugTraceFrame(int frame, float time, string room, Vector2 position,
        Vector2 velocity, Vector2 localGoal, string mode, string target, string role,
        string suppression, string controlOwner, float utility0, float utility1,
        float utility2, float panic, AIDebugHistoricalState history)
    {
        Frame = frame;
        Time = time;
        Room = room ?? "—";
        Position = position;
        Velocity = velocity;
        LocalGoal = localGoal;
        Mode = mode ?? "—";
        Target = target ?? "—";
        Role = role ?? "—";
        Suppression = suppression ?? "—";
        ControlOwner = controlOwner ?? "—";
        Utility0 = utility0;
        Utility1 = utility1;
        Utility2 = utility2;
        Panic = panic;
        History = history;
    }

    internal AIDebugTraceFrame WithHistory(AIDebugHistoricalState history) =>
        new(Frame, Time, Room, Position, Velocity, LocalGoal, Mode, Target, Role,
            Suppression, ControlOwner, Utility0, Utility1, Utility2, Panic, history);
}

internal static class AIDebugTrace
{
    private const int EventCapacity = 1024;
    private const int FrameCapacity = 600;
    private const int MaxTraces = 12;
    private const int SampleFrameInterval = 4;

    private sealed class Trace
    {
        internal readonly AIDebugTraceEvent[] Events = new AIDebugTraceEvent[EventCapacity];
        internal readonly AIDebugTraceFrame[] Frames = new AIDebugTraceFrame[FrameCapacity];
        internal readonly Dictionary<string, string> LastValues = new(16, StringComparer.Ordinal);
        internal int EventHead, EventCount, FrameHead, FrameCount, LastSampleFrame = int.MinValue;
        internal int LastTouchedFrame;
    }

    private static readonly Dictionary<DebugEntityKey, Trace> Traces = new();
    private static readonly HashSet<DebugEntityKey> Watched = new();
    private static readonly List<AIDebugUtilityRow> UtilityScratch = new(32);
    private static readonly List<AIDebugPerceptionRow> PerceptionScratch = new(64);
    private static bool visible;

    internal static bool Visible => visible;

    internal static void SetVisible(bool value)
    {
        visible = value;
        if (!value) Watched.Clear();
    }

    internal static void Watch(DebugEntityKey key, bool value)
    {
        if (!visible) return;
        if (value) Watched.Add(key);
        else Watched.Remove(key);
    }

    internal static void ReplaceWatches(DebugEntityKey selected, bool hasSelection,
        IEnumerable<DebugEntityKey> pinned)
    {
        Watched.Clear();
        if (!visible) return;
        if (hasSelection) Watched.Add(selected);
        if (pinned == null) return;
        foreach (DebugEntityKey key in pinned) Watched.Add(key);
    }

    internal static bool IsWatched(AbstractCreature creature) =>
        visible && creature != null && Watched.Contains(DebugEntityKey.From(creature));

    internal static bool IsWatched(DebugEntityKey key) => visible && Watched.Contains(key);

    internal static void Record(AbstractCreature creature, AIDebugEventCategory category,
        string name, object detail = null, string reason = null)
    {
        if (!IsWatched(creature)) return;
        Record(DebugEntityKey.From(creature), category, name, AIDebugFormat.Value(detail), reason);
    }

    internal static void Record(DebugEntityKey key, AIDebugEventCategory category,
        string name, string detail = null, string reason = null)
    {
        if (!IsWatched(key)) return;
        Trace trace = GetOrCreate(key);
        int index = trace.EventHead;
        var item = new AIDebugTraceEvent(UnityEngine.Time.frameCount,
            UnityEngine.Time.unscaledTime, category, name, detail, reason);
        trace.Events[index] = item;
        trace.EventHead = (index + 1) % EventCapacity;
        if (trace.EventCount < EventCapacity) trace.EventCount++;
        trace.LastTouchedFrame = UnityEngine.Time.frameCount;
        AIDebugBreakpointManager.OnEvent(key, item);
    }

    internal static void RecordChange(AbstractCreature creature, AIDebugEventCategory category,
        string name, object value, string reason = null)
    {
        if (!IsWatched(creature)) return;
        DebugEntityKey key = DebugEntityKey.From(creature);
        Trace trace = GetOrCreate(key);
        string text = AIDebugFormat.Value(value);
        if (trace.LastValues.TryGetValue(name, out string previous) && previous == text) return;
        trace.LastValues[name] = text;
        string detail = previous == null ? text : previous + " → " + text;
        Record(key, category, name, detail, reason);
    }

    internal static void Sample(AbstractCreature creature, AIDebugTraceFrame sample)
    {
        if (!IsWatched(creature)) return;
        DebugEntityKey key = DebugEntityKey.From(creature);
        Trace trace = GetOrCreate(key);
        int now = UnityEngine.Time.frameCount;
        if (now - trace.LastSampleFrame < SampleFrameInterval) return;
        trace.LastSampleFrame = now;

        if (AIDebugSettings.RecordFullHistory)
        {
            using (AIDebugProfiler.Begin(AIDebugProfileCategory.Capture))
            {
                try
                {
                    RainWorldGame game = AIDebugRegistry.CurrentGame;
                    AIDebugSnapshot snapshot = AIDebugRegistry.Capture(creature, game);
                    AIDebugAdvancedCapture.CaptureUtilities(creature, UtilityScratch);
                    AIDebugAdvancedCapture.CapturePerception(creature, PerceptionScratch);
                    AIDebugPathState path = AIDebugAdvancedCapture.CapturePath(creature);
                    var history = new AIDebugHistoricalState(snapshot,
                        UtilityScratch.ToArray(), PerceptionScratch.ToArray(), path);
                    sample = sample.WithHistory(history);
                }
                catch (Exception error)
                {
                    Record(key, AIDebugEventCategory.Warning, "HistoryCaptureFailed",
                        error.GetType().Name, error.Message);
                }
            }
        }

        trace.Frames[trace.FrameHead] = sample;
        trace.FrameHead = (trace.FrameHead + 1) % FrameCapacity;
        if (trace.FrameCount < FrameCapacity) trace.FrameCount++;
        trace.LastTouchedFrame = now;
        AIDebugCaptureManager.OnSample(key, sample);
    }

    internal static int CopyEvents(DebugEntityKey key, List<AIDebugTraceEvent> output)
    {
        output.Clear();
        if (!Traces.TryGetValue(key, out Trace trace) || trace.EventCount == 0) return 0;
        int newestIndex = (trace.EventHead - 1 + EventCapacity) % EventCapacity;
        float newestTime = trace.Events[newestIndex].Time;
        float cutoff = newestTime - Mathf.Max(10f, AIDebugSettings.HistorySeconds + 10f);
        int first = (trace.EventHead - trace.EventCount + EventCapacity) % EventCapacity;
        for (int i = 0; i < trace.EventCount; i++)
        {
            AIDebugTraceEvent item = trace.Events[(first + i) % EventCapacity];
            if (item.Time >= cutoff) output.Add(item);
        }
        return output.Count;
    }

    internal static int CopyFrames(DebugEntityKey key, List<AIDebugTraceFrame> output)
    {
        output.Clear();
        if (!Traces.TryGetValue(key, out Trace trace) || trace.FrameCount == 0) return 0;
        int newestIndex = (trace.FrameHead - 1 + FrameCapacity) % FrameCapacity;
        float newestTime = trace.Frames[newestIndex].Time;
        float cutoff = newestTime - Mathf.Clamp(AIDebugSettings.HistorySeconds, 5, 60);
        int first = (trace.FrameHead - trace.FrameCount + FrameCapacity) % FrameCapacity;
        for (int i = 0; i < trace.FrameCount; i++)
        {
            AIDebugTraceFrame item = trace.Frames[(first + i) % FrameCapacity];
            if (item.Time >= cutoff) output.Add(item);
        }
        return output.Count;
    }

    internal static bool TryLatest(DebugEntityKey key, out AIDebugTraceFrame frame)
    {
        frame = default;
        if (!Traces.TryGetValue(key, out Trace trace) || trace.FrameCount == 0) return false;
        int index = (trace.FrameHead - 1 + FrameCapacity) % FrameCapacity;
        frame = trace.Frames[index];
        return true;
    }

    internal static void Clear(DebugEntityKey key)
    {
        Traces.Remove(key);
        AIDebugCandidateRegistry.Clear(key);
    }

    internal static void Reset()
    {
        Traces.Clear();
        Watched.Clear();
        visible = false;
        AIDebugCandidateRegistry.Reset();
        AIDebugCaptureManager.Clear();
        AIDebugAnomalyDetector.Reset();
        AIDebugBreakpointManager.Reset();
        AIDebugProfiler.Reset();
    }

    private static Trace GetOrCreate(DebugEntityKey key)
    {
        if (Traces.TryGetValue(key, out Trace trace)) return trace;
        if (Traces.Count >= MaxTraces) EvictOldest();
        trace = new Trace();
        Traces.Add(key, trace);
        return trace;
    }

    private static void EvictOldest()
    {
        DebugEntityKey oldestKey = default;
        int oldestFrame = int.MaxValue;
        bool found = false;
        foreach (KeyValuePair<DebugEntityKey, Trace> pair in Traces)
        {
            if (Watched.Contains(pair.Key)) continue;
            if (pair.Value.LastTouchedFrame >= oldestFrame) continue;
            oldestFrame = pair.Value.LastTouchedFrame;
            oldestKey = pair.Key;
            found = true;
        }
        if (!found)
        {
            foreach (KeyValuePair<DebugEntityKey, Trace> pair in Traces)
            {
                if (pair.Value.LastTouchedFrame >= oldestFrame) continue;
                oldestFrame = pair.Value.LastTouchedFrame;
                oldestKey = pair.Key;
                found = true;
            }
        }
        if (found)
        {
            Traces.Remove(oldestKey);
            AIDebugCandidateRegistry.Clear(oldestKey);
        }
    }
}
