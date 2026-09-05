using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace DryCycle.Debugging.AI;

internal sealed class AIDebugCapture
{
    internal readonly DebugEntityKey Key;
    internal readonly string Reason;
    internal readonly float TriggerTime;
    internal readonly List<AIDebugTraceFrame> Frames = new(256);
    internal readonly List<AIDebugTraceEvent> Events = new(128);
    internal bool Complete;
    internal float EndTime;

    internal AIDebugCapture(DebugEntityKey key, string reason)
    {
        Key = key;
        Reason = reason ?? "manual";
        TriggerTime = AIDebugTrace.SimulationTime;
        EndTime = TriggerTime + 5f;
    }
}

internal static class AIDebugCaptureManager
{
    private static readonly List<AIDebugCapture> Completed = new(16);
    private static readonly Dictionary<DebugEntityKey, AIDebugCapture> Pending = new();
    private static readonly List<AIDebugTraceFrame> FrameScratch = new(600);
    private static readonly List<AIDebugTraceEvent> EventScratch = new(512);

    internal static IReadOnlyList<AIDebugCapture> Captures => Completed;
    internal static int PendingCount => Pending.Count;

    internal static void Trigger(DebugEntityKey key, string reason)
    {
        if (!AIDebugSettings.TriggerCapture || Pending.ContainsKey(key)) return;
        var capture = new AIDebugCapture(key, reason);
        AIDebugTrace.CopyFrames(key, FrameScratch);
        AIDebugTrace.CopyEvents(key, EventScratch);
        float cutoff = capture.TriggerTime - 10f;
        for (int i = 0; i < FrameScratch.Count; i++)
            if (FrameScratch[i].Time >= cutoff) capture.Frames.Add(FrameScratch[i]);
        for (int i = 0; i < EventScratch.Count; i++)
            if (EventScratch[i].Time >= cutoff) capture.Events.Add(EventScratch[i]);
        Pending[key] = capture;
    }

    internal static void OnSample(DebugEntityKey key, AIDebugTraceFrame frame)
    {
        if (!Pending.TryGetValue(key, out AIDebugCapture capture)) return;
        if (capture.Frames.Count == 0 || capture.Frames[capture.Frames.Count - 1].Frame != frame.Frame)
            capture.Frames.Add(frame);
        if (frame.Time < capture.EndTime) return;

        AIDebugTrace.CopyEvents(key, EventScratch);
        float start = capture.TriggerTime - 10f;
        float end = capture.EndTime;
        capture.Events.Clear();
        for (int i = 0; i < EventScratch.Count; i++)
            if (EventScratch[i].Time >= start && EventScratch[i].Time <= end) capture.Events.Add(EventScratch[i]);
        capture.Complete = true;
        Pending.Remove(key);
        Completed.Add(capture);
        while (Completed.Count > 16) Completed.RemoveAt(0);
    }

    internal static string Export(AIDebugCapture capture)
    {
        if (capture == null) return null;
        Directory.CreateDirectory(AIDebugSettings.CaptureDirectory);
        string safeTemplate = Sanitize(capture.Key.Template);
        string file = Path.Combine(AIDebugSettings.CaptureDirectory,
            $"{DateTime.Now:yyyyMMdd-HHmmss}-{safeTemplate}-{capture.Key.Number}.json");
        var text = new StringBuilder(64 * 1024);
        text.AppendLine("{");
        Json(text, "entity", capture.Key.ToString(), true, 1);
        Json(text, "reason", capture.Reason, true, 1);
        text.Append("  \"triggerTime\": ").Append(capture.TriggerTime.ToString("0.000", CultureInfo.InvariantCulture)).AppendLine(",");
        text.AppendLine("  \"frames\": [");
        for (int i = 0; i < capture.Frames.Count; i++)
        {
            AIDebugTraceFrame f = capture.Frames[i];
            text.Append("    {\"frame\":").Append(f.Frame)
                .Append(",\"time\":").Append(f.Time.ToString("0.000", CultureInfo.InvariantCulture))
                .Append(",\"room\":\"").Append(Escape(f.Room)).Append("\"")
                .Append(",\"mode\":\"").Append(Escape(f.Mode)).Append("\"")
                .Append(",\"target\":\"").Append(Escape(f.Target)).Append("\"")
                .Append(",\"role\":\"").Append(Escape(f.Role)).Append("\"")
                .Append(",\"suppression\":\"").Append(Escape(f.Suppression)).Append("\"")
                .Append(",\"position\":{").Append("\"x\":").Append(F(f.Position.x)).Append(",\"y\":").Append(F(f.Position.y)).Append("}")
                .Append(",\"velocity\":{").Append("\"x\":").Append(F(f.Velocity.x)).Append(",\"y\":").Append(F(f.Velocity.y)).Append("}")
                .Append('}');
            text.AppendLine(i + 1 < capture.Frames.Count ? "," : string.Empty);
        }
        text.AppendLine("  ],");
        text.AppendLine("  \"events\": [");
        for (int i = 0; i < capture.Events.Count; i++)
        {
            AIDebugTraceEvent e = capture.Events[i];
            text.Append("    {\"frame\":").Append(e.Frame)
                .Append(",\"time\":").Append(e.Time.ToString("0.000", CultureInfo.InvariantCulture))
                .Append(",\"category\":\"").Append(Escape(e.Category.ToString())).Append("\"")
                .Append(",\"name\":\"").Append(Escape(e.Name)).Append("\"")
                .Append(",\"detail\":\"").Append(Escape(e.Detail)).Append("\"")
                .Append(",\"reason\":\"").Append(Escape(e.Reason)).Append("\"}");
            text.AppendLine(i + 1 < capture.Events.Count ? "," : string.Empty);
        }
        text.AppendLine("  ]");
        text.AppendLine("}");
        File.WriteAllText(file, text.ToString(), Encoding.UTF8);
        return file;
    }

    internal static void Clear()
    {
        Pending.Clear();
        Completed.Clear();
    }

    private static string F(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value)) return "Creature";
        foreach (char c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
        return value;
    }

    private static void Json(StringBuilder b, string key, string value, bool comma, int indent)
    {
        b.Append(' ', indent * 2).Append('"').Append(Escape(key)).Append("\": \"")
            .Append(Escape(value)).Append('"').AppendLine(comma ? "," : string.Empty);
    }

    private static string Escape(string value) => (value ?? string.Empty)
        .Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
}

internal static class AIDebugAnomalyDetector
{
    private static readonly Dictionary<DebugEntityKey, float> LastTrigger = new();

    internal static string Evaluate(AbstractCreature creature, List<AIDebugTraceFrame> frames)
    {
        if (!AIDebugSettings.DetectAnomalies || creature == null || frames == null || frames.Count < 2) return null;
        DebugEntityKey key = DebugEntityKey.From(creature);
        float now = AIDebugTrace.SimulationTime;
        if (LastTrigger.TryGetValue(key, out float last) && now - last < 5f) return null;
        AIDebugTraceFrame current = frames[frames.Count - 1];
        bool paused = AIDebugSimulationControl.Paused;

        string reason = null;
        if (!Finite(current.Position) || !Finite(current.Velocity) || !Finite(current.LocalGoal))
            reason = "InvalidNumber";
        else if (!paused && current.Velocity.magnitude > 55f && creature.realizedCreature?.inShortcut != true)
            reason = "VelocitySpike";
        else
        {
            int start = Mathf.Max(1, frames.Count - 14);
            int modeChanges = 0, targetChanges = 0;
            for (int i = start; i < frames.Count; i++)
            {
                if (!string.Equals(frames[i].Mode, frames[i - 1].Mode, StringComparison.Ordinal)) modeChanges++;
                if (!string.Equals(frames[i].Target, frames[i - 1].Target, StringComparison.Ordinal)) targetChanges++;
            }
            if (modeChanges >= 7) reason = "StateOscillation";
            else if (targetChanges >= 6) reason = "TargetThrashing";
            else if (!paused && frames.Count >= 20)
            {
                AIDebugTraceFrame old = frames[frames.Count - 20];
                float moved = Vector2.Distance(old.Position, current.Position);
                float wanted = Vector2.Distance(current.Position, current.LocalGoal);
                if (moved < 6f && wanted > 55f) reason = "PossibleStuck";
            }
        }

        if (reason == null && creature.realizedCreature is Creatures.DesertBatfly.DesertBatfly bat &&
            bat.room?.abstractRoom?.creatures != null && bat.DesertAI.Target != null)
        {
            int attackers = 0;
            foreach (AbstractCreature abs in bat.room.abstractRoom.creatures)
                if (abs?.realizedCreature is Creatures.DesertBatfly.DesertBatfly other &&
                    other != bat && other.DesertAI.Target == bat.DesertAI.Target && other.DesertAI.FormalAttack)
                    attackers++;
            if (bat.DesertAI.FormalAttack) attackers++;
            if (attackers > Creatures.DesertBatfly.DesertBatflyTuning.AttackSlots)
                reason = "AttackSlotsViolation";
        }

        if (reason == null) return null;
        LastTrigger[key] = now;
        AIDebugTrace.Record(key, AIDebugEventCategory.Warning, reason, current.Mode, "automatic anomaly detector");
        AIDebugCaptureManager.Trigger(key, reason);
        return reason;
    }

    internal static void Reset() => LastTrigger.Clear();

    private static bool Finite(Vector2 value) =>
        !float.IsNaN(value.x) && !float.IsInfinity(value.x) && !float.IsNaN(value.y) && !float.IsInfinity(value.y);
}

internal sealed class AIDebugBreakpoint
{
    internal bool Enabled = true;
    internal AIDebugEventCategory? Category;
    internal string NameContains = string.Empty;
    internal DebugEntityKey? Entity;
}

internal static class AIDebugBreakpointManager
{
    private static readonly List<AIDebugBreakpoint> Rules = new(16);
    internal static IReadOnlyList<AIDebugBreakpoint> Breakpoints => Rules;
    internal static string LastHit { get; private set; }

    internal static AIDebugBreakpoint Add(string nameContains, AIDebugEventCategory? category = null,
        DebugEntityKey? entity = null)
    {
        var rule = new AIDebugBreakpoint { NameContains = nameContains ?? string.Empty, Category = category, Entity = entity };
        Rules.Add(rule);
        return rule;
    }

    internal static void RemoveAt(int index)
    {
        if (index >= 0 && index < Rules.Count) Rules.RemoveAt(index);
    }

    internal static void OnEvent(DebugEntityKey key, AIDebugTraceEvent e)
    {
        for (int i = 0; i < Rules.Count; i++)
        {
            AIDebugBreakpoint rule = Rules[i];
            if (!rule.Enabled) continue;
            if (rule.Entity.HasValue && rule.Entity.Value != key) continue;
            if (rule.Category.HasValue && rule.Category.Value != e.Category) continue;
            if (!string.IsNullOrEmpty(rule.NameContains) &&
                e.Name.IndexOf(rule.NameContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
            LastHit = key + " · " + e.Category + " · " + e.Name;
            if (AIDebugSettings.BreakpointPausesWorld) AIDebugSimulationControl.PauseForBreakpoint();
            break;
        }
    }

    internal static void Reset()
    {
        Rules.Clear();
        LastHit = null;
    }
}
