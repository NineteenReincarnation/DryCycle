using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using BepInEx;

namespace DryCycle.Debugging.AI;

internal enum AIDebugV5ExportState
{
    Idle,
    Writing,
    Complete,
    Failed
}

// V5 export snapshots recorder data on the Unity/main thread, then performs every string
// formatting and disk write on a ThreadPool worker. The worker never reads live recorder
// blocks, Rain World objects, Unity objects, or PresentationHub state.
internal static class AIDebugV5SessionExporter
{
    private const int MaxMotionSamplesPerEntity = 4096; // matches V5 recorder retention.
    private const int MaxStateSamplesPerEntity = 1024;

    private static int busy;
    private static volatile AIDebugV5ExportState state;
    private static string lastPath;
    private static string lastError;

    internal static AIDebugV5ExportState State => state;
    internal static string LastPath => lastPath;
    internal static string LastError => lastError;

    internal static string Describe()
    {
        return state switch
        {
            AIDebugV5ExportState.Writing => "writing",
            AIDebugV5ExportState.Complete => "complete",
            AIDebugV5ExportState.Failed => "failed",
            _ => string.Empty
        };
    }

    internal static bool TryQueue(
        AIDebugPresentationSnapshot presentation,
        out string outputDirectory,
        out string reason)
    {
        outputDirectory = string.Empty;
        reason = string.Empty;

        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
        {
            reason = "A V5 export is already running.";
            return false;
        }

        try
        {
            presentation ??= AIDebugPresentationSnapshot.Empty;
            AIDebugPresentationTrackedEntity[] tracked = presentation.Tracked;
            if (tracked == null || tracked.Length == 0)
            {
                reason = "No V5 selected/pinned recorder entities are available.";
                Interlocked.Exchange(ref busy, 0);
                return false;
            }

            string root = Path.Combine(Paths.ConfigPath, "DryCycle.AIObservatory.Sessions");
            string name = $"AIObservatory-V5-{DateTime.Now:yyyyMMdd-HHmmss-fff}";
            outputDirectory = Path.Combine(root, name);

            ExportSnapshot snapshot = Capture(presentation, outputDirectory);
            lastPath = outputDirectory;
            lastError = null;
            state = AIDebugV5ExportState.Writing;

            ThreadPool.QueueUserWorkItem(_ => WriteWorker(snapshot));
            return true;
        }
        catch (Exception error)
        {
            lastError = error.ToString();
            state = AIDebugV5ExportState.Failed;
            Interlocked.Exchange(ref busy, 0);
            reason = error.Message;
            return false;
        }
    }

    private static ExportSnapshot Capture(AIDebugPresentationSnapshot presentation, string directory)
    {
        AIDebugPresentationTrackedEntity[] tracked = presentation.Tracked;
        var entities = new ExportEntity[tracked.Length];

        for (int i = 0; i < tracked.Length; i++)
        {
            AIDebugPresentationTrackedEntity source = tracked[i];
            AIDebugRecorderReadApi.TryGetEntityStatus(source.Key, out AIDebugRecorderEntityStatus status);
            AIDebugRecorderReadApi.TryGetRetainedTickRange(source.Key, out int oldest, out int newest);

            int motionCapacity = status.Found
                ? (int)Math.Min((long)MaxMotionSamplesPerEntity, Math.Max(0L, status.MotionSamples))
                : 0;
            int stateCapacity = status.Found
                ? (int)Math.Min((long)MaxStateSamplesPerEntity, Math.Max(0L, status.StateChanges))
                : 0;

            AIDebugMotionSample[] motion = motionCapacity > 0
                ? new AIDebugMotionSample[motionCapacity]
                : Array.Empty<AIDebugMotionSample>();
            AIDebugFastStateSample[] states = stateCapacity > 0
                ? new AIDebugFastStateSample[stateCapacity]
                : Array.Empty<AIDebugFastStateSample>();

            int motionCount = motion.Length > 0
                ? AIDebugRecorderReadApi.CopyMotionRange(source.Key, oldest, newest, motion)
                : 0;
            int stateCount = states.Length > 0
                ? AIDebugRecorderReadApi.CopyFastStateRange(source.Key, oldest, newest, states)
                : 0;

            entities[i] = new ExportEntity(
                source.Key,
                source.DisplayName,
                source.Room,
                source.Selected,
                source.Pinned,
                oldest,
                newest,
                motion,
                motionCount,
                states,
                stateCount);
        }

        return new ExportSnapshot(
            directory,
            DateTime.UtcNow,
            presentation.Tick,
            presentation.CursorTick,
            presentation.ViewMode,
            presentation.Status,
            entities,
            presentation.Selected);
    }

    private static void WriteWorker(ExportSnapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(snapshot.Directory);
            WriteManifest(snapshot);
            WriteMotionCsv(snapshot);
            WriteStatesCsv(snapshot);
            WriteRichJson(snapshot);
            lastPath = snapshot.Directory;
            lastError = null;
            state = AIDebugV5ExportState.Complete;
        }
        catch (Exception error)
        {
            lastError = error.ToString();
            state = AIDebugV5ExportState.Failed;
        }
        finally
        {
            Interlocked.Exchange(ref busy, 0);
        }
    }

    private static void WriteManifest(ExportSnapshot snapshot)
    {
        using var writer = NewWriter(Path.Combine(snapshot.Directory, "manifest.json"));
        writer.WriteLine("{");
        JsonProperty(writer, "format", "DryCycle.AIObservatory.V5", true, 1);
        JsonProperty(writer, "formatVersion", 5, true, 1);
        JsonProperty(writer, "exportedAtUtc", snapshot.ExportedAtUtc.ToString("O", CultureInfo.InvariantCulture), true, 1);
        JsonProperty(writer, "simulationTick", snapshot.SimulationTick, true, 1);
        JsonProperty(writer, "cursorTick", snapshot.CursorTick, true, 1);
        JsonProperty(writer, "viewMode", snapshot.ViewMode.ToString(), true, 1);
        JsonProperty(writer, "status", snapshot.Status, true, 1);
        writer.WriteLine("  \"entities\": [");
        for (int i = 0; i < snapshot.Entities.Length; i++)
        {
            ExportEntity entity = snapshot.Entities[i];
            writer.WriteLine("    {");
            JsonProperty(writer, "template", entity.Key.Template, true, 3);
            JsonProperty(writer, "spawner", entity.Key.Spawner, true, 3);
            JsonProperty(writer, "number", entity.Key.Number, true, 3);
            JsonProperty(writer, "displayName", entity.DisplayName, true, 3);
            JsonProperty(writer, "room", entity.Room, true, 3);
            JsonProperty(writer, "selected", entity.Selected, true, 3);
            JsonProperty(writer, "pinned", entity.Pinned, true, 3);
            JsonProperty(writer, "oldestRetainedTick", entity.OldestTick, true, 3);
            JsonProperty(writer, "newestRetainedTick", entity.NewestTick, true, 3);
            JsonProperty(writer, "motionSamples", entity.MotionCount, true, 3);
            JsonProperty(writer, "stateChanges", entity.StateCount, false, 3);
            writer.Write("    }");
            writer.WriteLine(i + 1 < snapshot.Entities.Length ? "," : string.Empty);
        }
        writer.WriteLine("  ]");
        writer.WriteLine("}");
    }

    private static void WriteMotionCsv(ExportSnapshot snapshot)
    {
        using var writer = NewWriter(Path.Combine(snapshot.Directory, "motion.csv"));
        writer.WriteLine("template,spawner,number,tick,x,y,vx,vy,speed");
        for (int e = 0; e < snapshot.Entities.Length; e++)
        {
            ExportEntity entity = snapshot.Entities[e];
            for (int i = 0; i < entity.MotionCount; i++)
            {
                AIDebugMotionSample row = entity.Motion[i];
                double speed = Math.Sqrt(row.VX * row.VX + row.VY * row.VY);
                Csv(writer, entity.Key.Template);
                writer.Write(',');
                writer.Write(entity.Key.Spawner.ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(entity.Key.Number.ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(row.Tick.ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(row.X.ToString("R", CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(row.Y.ToString("R", CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(row.VX.ToString("R", CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(row.VY.ToString("R", CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.WriteLine(speed.ToString("R", CultureInfo.InvariantCulture));
            }
        }
    }

    private static void WriteStatesCsv(ExportSnapshot snapshot)
    {
        using var writer = NewWriter(Path.Combine(snapshot.Directory, "states.csv"));
        writer.WriteLine("template,spawner,number,tick,sequence,room,entityState,destinationRoom,destinationX,destinationY,destinationNode,modeToken,targetSpawner,targetNumber,flags");
        for (int e = 0; e < snapshot.Entities.Length; e++)
        {
            ExportEntity entity = snapshot.Entities[e];
            for (int i = 0; i < entity.StateCount; i++)
            {
                AIDebugFastStateSample row = entity.States[i];
                AIDebugFastState value = row.State;
                Csv(writer, entity.Key.Template);
                writer.Write(',');
                writer.Write(entity.Key.Spawner.ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(entity.Key.Number.ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(row.Tick.ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(row.Sequence.ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(value.Room.ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                Csv(writer, value.EntityState.ToString());
                writer.Write(',');
                writer.Write(value.DestinationRoom.ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(value.DestinationX.ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(value.DestinationY.ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(value.DestinationNode.ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(value.ModeToken.ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(value.TargetSpawner.ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(value.TargetNumber.ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                Csv(writer, value.Flags.ToString());
                writer.WriteLine();
            }
        }
    }

    private static void WriteRichJson(ExportSnapshot snapshot)
    {
        AIDebugPresentationCreature selected = snapshot.Selected;
        if (selected == null) return;

        using var writer = NewWriter(Path.Combine(snapshot.Directory, "selected-rich.json"));
        writer.WriteLine("{");
        JsonProperty(writer, "entity", selected.DisplayName, true, 1);
        JsonProperty(writer, "template", selected.Key.Template, true, 1);
        JsonProperty(writer, "spawner", selected.Key.Spawner, true, 1);
        JsonProperty(writer, "number", selected.Key.Number, true, 1);
        JsonProperty(writer, "entityState", selected.State.ToString(), true, 1);
        JsonProperty(writer, "controlOwner", selected.ControlOwner, true, 1);
        JsonProperty(writer, "snapshotAgeTicks", selected.AdvancedAgeTicks, true, 1);

        writer.WriteLine("  \"sections\": [");
        for (int s = 0; s < selected.Sections.Length; s++)
        {
            AIDebugPresentationSection section = selected.Sections[s];
            writer.WriteLine("    {");
            JsonProperty(writer, "titleKey", section.TitleKey, true, 3);
            writer.WriteLine("      \"values\": [");
            for (int i = 0; i < section.Values.Length; i++)
            {
                AIDebugPresentationValue value = section.Values[i];
                writer.WriteLine("        {");
                JsonProperty(writer, "labelKey", value.LabelKey, true, 5);
                JsonProperty(writer, "rawName", value.RawName, true, 5);
                JsonProperty(writer, "value", value.Value, true, 5);
                JsonProperty(writer, "ageTicks", value.AgeTicks, true, 5);
                JsonProperty(writer, "source", value.Source, false, 5);
                writer.Write("        }");
                writer.WriteLine(i + 1 < section.Values.Length ? "," : string.Empty);
            }
            writer.WriteLine("      ]");
            writer.Write("    }");
            writer.WriteLine(s + 1 < selected.Sections.Length ? "," : string.Empty);
        }
        writer.WriteLine("  ],");

        writer.WriteLine("  \"utility\": [");
        for (int i = 0; i < selected.Utilities.Length; i++)
        {
            AIDebugUtilityRow row = selected.Utilities[i];
            writer.WriteLine("    {");
            JsonProperty(writer, "name", row.Name, true, 3);
            JsonNumber(writer, "raw", row.Raw, true, 3);
            JsonNumber(writer, "smoothed", row.Smoothed, true, 3);
            JsonNumber(writer, "weight", row.Weight, true, 3);
            JsonNumber(writer, "weighted", row.Weighted, true, 3);
            JsonNumber(writer, "continuationBonus", row.ContinuationBonus, true, 3);
            JsonProperty(writer, "winner", row.Winner, false, 3);
            writer.Write("    }");
            writer.WriteLine(i + 1 < selected.Utilities.Length ? "," : string.Empty);
        }
        writer.WriteLine("  ],");

        writer.WriteLine("  \"perception\": [");
        for (int i = 0; i < selected.Perception.Length; i++)
        {
            AIDebugPerceptionRow row = selected.Perception[i];
            writer.WriteLine("    {");
            JsonProperty(writer, "name", row.Name, true, 3);
            JsonProperty(writer, "entity", row.Key.ToString(), true, 3);
            JsonProperty(writer, "visualContact", row.VisualContact, true, 3);
            JsonProperty(writer, "ticksSinceSeen", row.TicksSinceSeen, true, 3);
            JsonNumber(writer, "estimatedChance", row.EstimatedChance, true, 3);
            JsonNumber(writer, "priority", row.Priority, true, 3);
            JsonProperty(writer, "lastSeen", row.LastSeen.ToString(), true, 3);
            JsonProperty(writer, "bestGuess", row.BestGuess.ToString(), true, 3);
            JsonProperty(writer, "relationship", row.Relationship, true, 3);
            JsonNumber(writer, "relationshipIntensity", row.RelationshipIntensity, false, 3);
            writer.Write("    }");
            writer.WriteLine(i + 1 < selected.Perception.Length ? "," : string.Empty);
        }
        writer.WriteLine("  ],");

        writer.WriteLine("  \"path\": {");
        JsonProperty(writer, "pathfinder", selected.Path.Pathfinder, true, 2);
        JsonProperty(writer, "destination", selected.Path.Destination.ToString(), true, 2);
        JsonProperty(writer, "hasPathfinder", selected.Path.HasPathfinder, true, 2);
        JsonProperty(writer, "destinationReachable", selected.Path.DestinationReachable, true, 2);
        JsonProperty(writer, "canReturnFromDestination", selected.Path.CanReturnFromDestination, true, 2);
        JsonProperty(writer, "stranded", selected.Path.Stranded, false, 2);
        writer.WriteLine("  }");
        writer.WriteLine("}");
    }

    private static StreamWriter NewWriter(string path) =>
        new StreamWriter(path, false, new UTF8Encoding(false), 64 * 1024);

    private static void JsonProperty(StreamWriter writer, string name, string value, bool comma, int indent)
    {
        Indent(writer, indent);
        writer.Write('"');
        writer.Write(Escape(name));
        writer.Write("\": \"");
        writer.Write(Escape(value ?? string.Empty));
        writer.Write('"');
        if (comma) writer.Write(',');
        writer.WriteLine();
    }

    private static void JsonProperty(StreamWriter writer, string name, int value, bool comma, int indent)
    {
        Indent(writer, indent);
        writer.Write('"');
        writer.Write(Escape(name));
        writer.Write("\": ");
        writer.Write(value.ToString(CultureInfo.InvariantCulture));
        if (comma) writer.Write(',');
        writer.WriteLine();
    }

    private static void JsonProperty(StreamWriter writer, string name, bool value, bool comma, int indent)
    {
        Indent(writer, indent);
        writer.Write('"');
        writer.Write(Escape(name));
        writer.Write("\": ");
        writer.Write(value ? "true" : "false");
        if (comma) writer.Write(',');
        writer.WriteLine();
    }

    private static void JsonNumber(StreamWriter writer, string name, float value, bool comma, int indent)
    {
        Indent(writer, indent);
        writer.Write('"');
        writer.Write(Escape(name));
        writer.Write("\": ");
        if (float.IsNaN(value) || float.IsInfinity(value)) writer.Write("null");
        else writer.Write(value.ToString("R", CultureInfo.InvariantCulture));
        if (comma) writer.Write(',');
        writer.WriteLine();
    }

    private static void Csv(StreamWriter writer, string value)
    {
        value ??= string.Empty;
        bool quote = value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0;
        if (!quote)
        {
            writer.Write(value);
            return;
        }

        writer.Write('"');
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '"') writer.Write("\"\"");
            else writer.Write(c);
        }
        writer.Write('"');
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var builder = new StringBuilder(value.Length + 8);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            switch (c)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c < 32) builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else builder.Append(c);
                    break;
            }
        }
        return builder.ToString();
    }

    private static void Indent(StreamWriter writer, int depth)
    {
        for (int i = 0; i < depth; i++) writer.Write("  ");
    }

    private sealed class ExportSnapshot
    {
        internal readonly string Directory;
        internal readonly DateTime ExportedAtUtc;
        internal readonly int SimulationTick;
        internal readonly int CursorTick;
        internal readonly AIDebugViewMode ViewMode;
        internal readonly string Status;
        internal readonly ExportEntity[] Entities;
        internal readonly AIDebugPresentationCreature Selected;

        internal ExportSnapshot(
            string directory,
            DateTime exportedAtUtc,
            int simulationTick,
            int cursorTick,
            AIDebugViewMode viewMode,
            string status,
            ExportEntity[] entities,
            AIDebugPresentationCreature selected)
        {
            Directory = directory;
            ExportedAtUtc = exportedAtUtc;
            SimulationTick = simulationTick;
            CursorTick = cursorTick;
            ViewMode = viewMode;
            Status = status ?? string.Empty;
            Entities = entities ?? Array.Empty<ExportEntity>();
            Selected = selected;
        }
    }

    private sealed class ExportEntity
    {
        internal readonly DebugEntityKey Key;
        internal readonly string DisplayName;
        internal readonly string Room;
        internal readonly bool Selected;
        internal readonly bool Pinned;
        internal readonly int OldestTick;
        internal readonly int NewestTick;
        internal readonly AIDebugMotionSample[] Motion;
        internal readonly int MotionCount;
        internal readonly AIDebugFastStateSample[] States;
        internal readonly int StateCount;

        internal ExportEntity(
            DebugEntityKey key,
            string displayName,
            string room,
            bool selected,
            bool pinned,
            int oldestTick,
            int newestTick,
            AIDebugMotionSample[] motion,
            int motionCount,
            AIDebugFastStateSample[] states,
            int stateCount)
        {
            Key = key;
            DisplayName = displayName ?? key.ToString();
            Room = room ?? "—";
            Selected = selected;
            Pinned = pinned;
            OldestTick = oldestTick;
            NewestTick = newestTick;
            Motion = motion ?? Array.Empty<AIDebugMotionSample>();
            MotionCount = motionCount;
            States = states ?? Array.Empty<AIDebugFastStateSample>();
            StateCount = stateCount;
        }
    }
}
