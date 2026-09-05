using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using UnityEngine;

namespace DryCycle.Debugging.AI;

// Exports exactly the diagnostic state retained by the Observatory. It never asks an
// AI to recompute a decision while exporting; all data comes from trace/capture buffers.
internal static class AIDebugSessionExporter
{
    private static readonly List<DebugEntityKey> Keys = new(16);
    private static readonly List<AIDebugTraceFrame> Frames = new(600);
    private static readonly List<AIDebugTraceEvent> Events = new(1024);

    internal static string SessionDirectory =>
        Path.Combine(Paths.ConfigPath, "DryCycle.AIObservatory.Sessions");

    internal static string Export()
    {
        Directory.CreateDirectory(SessionDirectory);
        string file = Path.Combine(SessionDirectory,
            $"AIObservatory-{DateTime.Now:yyyyMMdd-HHmmss}.json");

        var text = new StringBuilder(256 * 1024);
        text.AppendLine("{");
        Property(text, "format", "DryCycle.AIObservatory.Session", true, 1);
        Property(text, "formatVersion", 1, true, 1);
        Property(text, "exportedAt", DateTime.Now.ToString("O", CultureInfo.InvariantCulture), true, 1);
        Property(text, "simulationTick", AIDebugTrace.SimulationTick, true, 1);
        Property(text, "simulationTime", AIDebugTrace.SimulationTime, true, 1);
        Property(text, "language", AIDebugLocalization.Language.ToString(), true, 1);
        Property(text, "historySeconds", AIDebugSettings.HistorySeconds, true, 1);
        Property(text, "recordFullHistory", AIDebugSettings.RecordFullHistory, true, 1);
        Property(text, "pendingCaptures", AIDebugCaptureManager.PendingCount, true, 1);

        AIDebugTrace.CopyKeys(Keys);
        text.AppendLine("  \"traces\": [");
        for (int i = 0; i < Keys.Count; i++)
        {
            DebugEntityKey key = Keys[i];
            AIDebugTrace.CopyFrames(key, Frames);
            AIDebugTrace.CopyEvents(key, Events);
            WriteTrace(text, key, Frames, Events, 2);
            text.AppendLine(i + 1 < Keys.Count ? "," : string.Empty);
        }
        text.AppendLine("  ],");

        IReadOnlyList<AIDebugCapture> captures = AIDebugCaptureManager.Captures;
        text.AppendLine("  \"captures\": [");
        for (int i = 0; i < captures.Count; i++)
        {
            WriteCapture(text, captures[i], 2);
            text.AppendLine(i + 1 < captures.Count ? "," : string.Empty);
        }
        text.AppendLine("  ]");
        text.AppendLine("}");

        File.WriteAllText(file, text.ToString(), Encoding.UTF8);
        return file;
    }

    private static void WriteTrace(StringBuilder b, DebugEntityKey key,
        IReadOnlyList<AIDebugTraceFrame> frames, IReadOnlyList<AIDebugTraceEvent> events, int indent)
    {
        Indent(b, indent).AppendLine("{");
        Property(b, "entity", key.ToString(), true, indent + 1);
        Property(b, "template", key.Template, true, indent + 1);
        Property(b, "spawner", key.Spawner, true, indent + 1);
        Property(b, "number", key.Number, true, indent + 1);
        WriteFrames(b, frames, indent + 1, true);
        WriteEvents(b, events, indent + 1, false);
        Indent(b, indent).Append('}');
    }

    private static void WriteCapture(StringBuilder b, AIDebugCapture capture, int indent)
    {
        Indent(b, indent).AppendLine("{");
        Property(b, "entity", capture.Key.ToString(), true, indent + 1);
        Property(b, "reason", capture.Reason, true, indent + 1);
        Property(b, "triggerTime", capture.TriggerTime, true, indent + 1);
        Property(b, "endTime", capture.EndTime, true, indent + 1);
        Property(b, "complete", capture.Complete, true, indent + 1);
        WriteFrames(b, capture.Frames, indent + 1, true);
        WriteEvents(b, capture.Events, indent + 1, false);
        Indent(b, indent).Append('}');
    }

    private static void WriteFrames(StringBuilder b, IReadOnlyList<AIDebugTraceFrame> frames,
        int indent, bool commaAfter)
    {
        Indent(b, indent).AppendLine("\"frames\": [");
        for (int i = 0; i < frames.Count; i++)
        {
            WriteFrame(b, frames[i], indent + 1);
            b.AppendLine(i + 1 < frames.Count ? "," : string.Empty);
        }
        Indent(b, indent).Append(']').AppendLine(commaAfter ? "," : string.Empty);
    }

    private static void WriteFrame(StringBuilder b, AIDebugTraceFrame f, int indent)
    {
        Indent(b, indent).AppendLine("{");
        Property(b, "tick", f.Frame, true, indent + 1);
        Property(b, "time", f.Time, true, indent + 1);
        Property(b, "room", f.Room, true, indent + 1);
        Vector(b, "position", f.Position, true, indent + 1);
        Vector(b, "velocity", f.Velocity, true, indent + 1);
        Vector(b, "localGoal", f.LocalGoal, true, indent + 1);
        Property(b, "mode", f.Mode, true, indent + 1);
        Property(b, "target", f.Target, true, indent + 1);
        Property(b, "role", f.Role, true, indent + 1);
        Property(b, "suppression", f.Suppression, true, indent + 1);
        Property(b, "controlOwner", f.ControlOwner, true, indent + 1);
        Property(b, "utility0", f.Utility0, true, indent + 1);
        Property(b, "utility1", f.Utility1, true, indent + 1);
        Property(b, "utility2", f.Utility2, true, indent + 1);
        Property(b, "panic", f.Panic, true, indent + 1);
        Indent(b, indent + 1).Append("\"history\": ");
        WriteHistory(b, f.History, indent + 1);
        b.AppendLine();
        Indent(b, indent).Append('}');
    }

    private static void WriteHistory(StringBuilder b, AIDebugHistoricalState history, int indent)
    {
        if (history == null)
        {
            b.Append("null");
            return;
        }

        b.AppendLine("{");
        Indent(b, indent + 1).Append("\"snapshot\": ");
        WriteSnapshot(b, history.Snapshot, indent + 1);
        b.AppendLine(",");

        Indent(b, indent + 1).AppendLine("\"utilities\": [");
        for (int i = 0; i < history.Utilities.Length; i++)
        {
            AIDebugUtilityRow u = history.Utilities[i];
            Indent(b, indent + 2).Append('{');
            InlineString(b, "name", u.Name, true);
            InlineNumber(b, "raw", u.Raw, true);
            InlineNumber(b, "smoothed", u.Smoothed, true);
            InlineNumber(b, "weight", u.Weight, true);
            InlineNumber(b, "weighted", u.Weighted, true);
            InlineNumber(b, "continuationBonus", u.ContinuationBonus, true);
            InlineBool(b, "winner", u.Winner, false);
            b.Append('}').AppendLine(i + 1 < history.Utilities.Length ? "," : string.Empty);
        }
        Indent(b, indent + 1).AppendLine("],");

        Indent(b, indent + 1).AppendLine("\"perception\": [");
        for (int i = 0; i < history.Perception.Length; i++)
        {
            AIDebugPerceptionRow p = history.Perception[i];
            Indent(b, indent + 2).Append('{');
            InlineString(b, "entity", p.Key.ToString(), true);
            InlineString(b, "name", p.Name, true);
            InlineBool(b, "visualContact", p.VisualContact, true);
            InlineInt(b, "ticksSinceSeen", p.TicksSinceSeen, true);
            InlineNumber(b, "estimatedChance", p.EstimatedChance, true);
            InlineNumber(b, "priority", p.Priority, true);
            InlineString(b, "lastSeen", p.LastSeen.ToString(), true);
            InlineString(b, "bestGuess", p.BestGuess.ToString(), true);
            InlineString(b, "relationship", p.Relationship, true);
            InlineNumber(b, "relationshipIntensity", p.RelationshipIntensity, false);
            b.Append('}').AppendLine(i + 1 < history.Perception.Length ? "," : string.Empty);
        }
        Indent(b, indent + 1).AppendLine("],");

        Indent(b, indent + 1).Append("\"path\": {");
        InlineString(b, "pathfinder", history.Path.Pathfinder, true);
        InlineString(b, "destination", history.Path.Destination.ToString(), true);
        InlineBool(b, "hasPathfinder", history.Path.HasPathfinder, true);
        InlineBool(b, "destinationReachable", history.Path.DestinationReachable, true);
        InlineBool(b, "canReturn", history.Path.CanReturnFromDestination, true);
        InlineBool(b, "stranded", history.Path.Stranded, false);
        b.AppendLine("}");
        Indent(b, indent).Append('}');
    }

    private static void WriteSnapshot(StringBuilder b, AIDebugSnapshot snapshot, int indent)
    {
        if (snapshot == null)
        {
            b.Append("null");
            return;
        }

        b.AppendLine("{");
        Property(b, "displayName", snapshot.DisplayName, true, indent + 1);
        Property(b, "entityState", snapshot.EntityState.ToString(), true, indent + 1);
        Property(b, "controlOwner", snapshot.ControlOwner, true, indent + 1);
        Indent(b, indent + 1).AppendLine("\"sections\": [");
        for (int s = 0; s < snapshot.Sections.Count; s++)
        {
            AIDebugSection section = snapshot.Sections[s];
            Indent(b, indent + 2).AppendLine("{");
            Property(b, "titleKey", section.TitleKey, true, indent + 3);
            Indent(b, indent + 3).AppendLine("\"values\": [");
            for (int i = 0; i < section.Values.Count; i++)
            {
                AIDebugValue v = section.Values[i];
                Indent(b, indent + 4).Append('{');
                InlineString(b, "labelKey", v.LabelKey, true);
                InlineString(b, "rawName", v.RawName, true);
                InlineString(b, "value", v.Value, true);
                InlineInt(b, "ageTicks", v.AgeTicks, true);
                InlineString(b, "source", v.Source, false);
                b.Append('}').AppendLine(i + 1 < section.Values.Count ? "," : string.Empty);
            }
            Indent(b, indent + 3).AppendLine("]");
            Indent(b, indent + 2).Append('}').AppendLine(s + 1 < snapshot.Sections.Count ? "," : string.Empty);
        }
        Indent(b, indent + 1).AppendLine("],");

        Indent(b, indent + 1).AppendLine("\"decisions\": [");
        for (int i = 0; i < snapshot.Decisions.Count; i++)
        {
            AIDebugDecisionNode d = snapshot.Decisions[i];
            Indent(b, indent + 2).Append('{');
            InlineString(b, "labelKey", d.LabelKey, true);
            InlineString(b, "state", d.State.ToString(), true);
            InlineString(b, "detail", d.Detail, true);
            InlineString(b, "rawName", d.RawName, true);
            InlineInt(b, "depth", d.Depth, false);
            b.Append('}').AppendLine(i + 1 < snapshot.Decisions.Count ? "," : string.Empty);
        }
        Indent(b, indent + 1).AppendLine("]");
        Indent(b, indent).Append('}');
    }

    private static void WriteEvents(StringBuilder b, IReadOnlyList<AIDebugTraceEvent> events,
        int indent, bool commaAfter)
    {
        Indent(b, indent).AppendLine("\"events\": [");
        for (int i = 0; i < events.Count; i++)
        {
            AIDebugTraceEvent e = events[i];
            Indent(b, indent + 1).Append('{');
            InlineInt(b, "tick", e.Frame, true);
            InlineNumber(b, "time", e.Time, true);
            InlineString(b, "category", e.Category.ToString(), true);
            InlineString(b, "name", e.Name, true);
            InlineString(b, "detail", e.RawDetail, true);
            InlineString(b, "reason", e.RawReason, false);
            b.Append('}').AppendLine(i + 1 < events.Count ? "," : string.Empty);
        }
        Indent(b, indent).Append(']').AppendLine(commaAfter ? "," : string.Empty);
    }

    private static void Property(StringBuilder b, string key, string value, bool comma, int indent)
    {
        Indent(b, indent).Append('"').Append(Escape(key)).Append("\": \"")
            .Append(Escape(value)).Append('"').AppendLine(comma ? "," : string.Empty);
    }

    private static void Property(StringBuilder b, string key, int value, bool comma, int indent)
    {
        Indent(b, indent).Append('"').Append(Escape(key)).Append("\": ").Append(value)
            .AppendLine(comma ? "," : string.Empty);
    }

    private static void Property(StringBuilder b, string key, float value, bool comma, int indent)
    {
        Indent(b, indent).Append('"').Append(Escape(key)).Append("\": ");
        Number(b, value);
        b.AppendLine(comma ? "," : string.Empty);
    }

    private static void Property(StringBuilder b, string key, bool value, bool comma, int indent)
    {
        Indent(b, indent).Append('"').Append(Escape(key)).Append("\": ")
            .Append(value ? "true" : "false").AppendLine(comma ? "," : string.Empty);
    }

    private static void Vector(StringBuilder b, string key, Vector2 value, bool comma, int indent)
    {
        Indent(b, indent).Append('"').Append(Escape(key)).Append("\": {\"x\":");
        Number(b, value.x);
        b.Append(",\"y\":");
        Number(b, value.y);
        b.Append('}').AppendLine(comma ? "," : string.Empty);
    }

    private static void InlineString(StringBuilder b, string key, string value, bool comma)
    {
        b.Append('"').Append(Escape(key)).Append("\":\"").Append(Escape(value)).Append('"');
        if (comma) b.Append(',');
    }

    private static void InlineInt(StringBuilder b, string key, int value, bool comma)
    {
        b.Append('"').Append(Escape(key)).Append("\":").Append(value);
        if (comma) b.Append(',');
    }

    private static void InlineNumber(StringBuilder b, string key, float value, bool comma)
    {
        b.Append('"').Append(Escape(key)).Append("\":");
        Number(b, value);
        if (comma) b.Append(',');
    }

    private static void InlineBool(StringBuilder b, string key, bool value, bool comma)
    {
        b.Append('"').Append(Escape(key)).Append("\":").Append(value ? "true" : "false");
        if (comma) b.Append(',');
    }

    private static void Number(StringBuilder b, float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value)) b.Append("null");
        else b.Append(value.ToString("0.######", CultureInfo.InvariantCulture));
    }

    private static StringBuilder Indent(StringBuilder b, int indent) => b.Append(' ', indent * 2);

    private static string Escape(string value) => (value ?? string.Empty)
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("\r", "\\r")
        .Replace("\n", "\\n")
        .Replace("\t", "\\t");
}
