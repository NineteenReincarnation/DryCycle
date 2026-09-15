using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using DryCycle.DevUI.DevTool.Debug;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Writes the dedicated LanceScavenger live-diagnostics stream to its own file while the
/// New-UI Lance Scavenger debug page owns an active capture lease. Normal gameplay only pays
/// one cheap snapshot availability check and never opens a file.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(BridgePlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class LanceScavengerRealtimeLogPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.LanceScavengerRealtimeLog";
    public const string PluginName = "DryCycle Lance Scavenger Realtime AI Log";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private const long MaximumLogBytes = 16L * 1024L * 1024L;
    private const string LogFileName = "DryCycle-LanceScavenger-AI.log";

    private sealed class PreviousState
    {
        internal string State = string.Empty;
        internal string Decision = string.Empty;
        internal string Lane = string.Empty;
        internal string Target = string.Empty;
    }

    private StreamWriter writer;
    private bool captureActive;
    private bool capped;
    private string activeRoom = string.Empty;
    private int sessionStartTick;
    private long sequence;
    private readonly Dictionary<long, string> lastPayload = new();
    private readonly Dictionary<long, PreviousState> previousStates = new();

    internal static string DiagnosticLogPath => Path.Combine(Paths.BepInExRootPath, LogFileName);

    private void OnEnable()
    {
        captureActive = false;
        capped = false;
        activeRoom = string.Empty;
        sequence = 0;
    }

    private void Update()
    {
        LanceScavengerDebugSnapshot snapshot;
        try
        {
            snapshot = LanceScavengerDebugPresentationHub.Current;
        }
        catch (Exception error)
        {
            Logger.LogWarning("LanceScavenger realtime log could not read the debug snapshot: " + error.Message);
            return;
        }

        if (!snapshot.Requested)
        {
            if (captureActive)
                EndSession("capture closed");
            return;
        }

        if (!captureActive)
            BeginSession(snapshot.RoomName);
        else if (!string.Equals(activeRoom, snapshot.RoomName ?? string.Empty, StringComparison.Ordinal))
            ChangeRoom(snapshot.RoomName);

        if (writer == null || capped)
            return;

        LanceScavengerDebugEntrySnapshot[] entries = snapshot.Entries ?? Array.Empty<LanceScavengerDebugEntrySnapshot>();
        for (int i = 0; i < entries.Length; i++)
            WriteEntry(entries[i]);

        EnforceSizeLimit();
    }

    private void OnDisable()
    {
        EndSession("plugin disabled");
    }

    private void BeginSession(string roomName)
    {
        EndWriterOnly();
        lastPayload.Clear();
        previousStates.Clear();
        sequence = 0;
        capped = false;
        captureActive = true;
        activeRoom = roomName ?? string.Empty;
        sessionStartTick = Environment.TickCount;

        try
        {
            writer = new StreamWriter(
                DiagnosticLogPath,
                false,
                new UTF8Encoding(false),
                64 * 1024)
            {
                AutoFlush = true
            };

            writer.WriteLine("# DryCycle LanceScavenger realtime AI diagnostic log");
            writer.WriteLine("# format=tsv-v1");
            writer.WriteLine("# opened_utc=" + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteLine("# room=" + Safe(activeRoom));
            writer.WriteLine("# path=" + DiagnosticLogPath);
            writer.WriteLine("# max_bytes=" + MaximumLogBytes.ToString(CultureInfo.InvariantCulture));
            writer.WriteLine("# Only written while the New-UI Lance Scavenger realtime monitor is open.");
            writer.WriteLine(BuildHeader());
            Logger.LogInfo("LanceScavenger realtime AI log started: " + DiagnosticLogPath);
        }
        catch (Exception error)
        {
            writer = null;
            capped = true;
            Logger.LogWarning("Could not create LanceScavenger realtime AI log: " + error.Message);
        }
    }

    private void ChangeRoom(string roomName)
    {
        activeRoom = roomName ?? string.Empty;
        lastPayload.Clear();
        previousStates.Clear();
        if (writer != null)
        {
            writer.WriteLine("# ROOM\t" + ElapsedMilliseconds().ToString(CultureInfo.InvariantCulture) + "\t" + Safe(activeRoom));
            writer.Flush();
        }
    }

    private void EndSession(string reason)
    {
        if (!captureActive && writer == null)
            return;

        if (writer != null)
        {
            try
            {
                writer.WriteLine("# END\t" + ElapsedMilliseconds().ToString(CultureInfo.InvariantCulture) + "\t" + Safe(reason));
                writer.Flush();
            }
            catch
            {
                // The diagnostics writer must never interfere with gameplay shutdown.
            }
        }

        EndWriterOnly();
        captureActive = false;
        capped = false;
        activeRoom = string.Empty;
        lastPayload.Clear();
        previousStates.Clear();
    }

    private void EndWriterOnly()
    {
        if (writer == null) return;
        try { writer.Dispose(); }
        catch { }
        writer = null;
    }

    private void WriteEntry(LanceScavengerDebugEntrySnapshot entry)
    {
        if (entry == null || writer == null) return;

        long key = EntityKey(entry.Spawner, entry.Number);
        string payload = BuildPayload(entry);

        // The frontend may render more often than Rain World's 40 Hz simulation. Do not duplicate
        // an unchanged simulation sample in the file; StateAge/cooldown/aim data make real updates
        // naturally produce a new payload.
        if (lastPayload.TryGetValue(key, out string previousPayload) &&
            string.Equals(previousPayload, payload, StringComparison.Ordinal))
            return;

        lastPayload[key] = payload;
        sequence++;
        long elapsed = ElapsedMilliseconds();

        WriteEvents(key, entry, elapsed);
        writer.Write("FRAME\t");
        writer.Write(elapsed.ToString(CultureInfo.InvariantCulture));
        writer.Write('\t');
        writer.Write(sequence.ToString(CultureInfo.InvariantCulture));
        writer.Write('\t');
        writer.WriteLine(payload);
    }

    private void WriteEvents(long key, LanceScavengerDebugEntrySnapshot entry, long elapsed)
    {
        if (!previousStates.TryGetValue(key, out PreviousState previous))
        {
            previous = new PreviousState();
            previousStates[key] = previous;
            writer.WriteLine("# EVENT\t" + elapsed.ToString(CultureInfo.InvariantCulture) + "\t" +
                             Safe(entry.EntityId) + "\tspawn\t-\t" + Safe(entry.State));
        }
        else
        {
            WriteChangedEvent(elapsed, entry.EntityId, "state", previous.State, entry.State);
            WriteChangedEvent(elapsed, entry.EntityId, "decision", previous.Decision, entry.DecisionReason);
            WriteChangedEvent(elapsed, entry.EntityId, "lane", previous.Lane, entry.LaneReason);
            WriteChangedEvent(elapsed, entry.EntityId, "target", previous.Target, entry.Target);
        }

        previous.State = entry.State ?? string.Empty;
        previous.Decision = entry.DecisionReason ?? string.Empty;
        previous.Lane = entry.LaneReason ?? string.Empty;
        previous.Target = entry.Target ?? string.Empty;
    }

    private void WriteChangedEvent(long elapsed, string entity, string field, string before, string after)
    {
        before ??= string.Empty;
        after ??= string.Empty;
        if (string.Equals(before, after, StringComparison.Ordinal)) return;

        writer.WriteLine("# EVENT\t" + elapsed.ToString(CultureInfo.InvariantCulture) + "\t" +
                         Safe(entity) + "\t" + field + "\t" + Safe(before) + "\t" + Safe(after));
    }

    private static string BuildHeader() =>
        "record\telapsed_ms\tseq\troom\tentity\tspawner\tnumber\tstate\tstate_age\tcooldown\tattack_serial\tconscious\thas_lance\thas_sidearm\tbehavior\ttarget\tviolence\tafraid\tdistance_px\taim_quality\taim_threshold\taim_ready\tbest_aim_quality\tbest_aim_age\tbest_aim_ready\ttarget_chunk\tbest_target_chunk\timpact_frame\texact_aim\taim_x\taim_y\tlance_pitch_deg\torigin_x\torigin_y\tgrip_x\tgrip_y\tlance_dir_x\tlance_dir_y\tpath_clear\tlane_reason\tcharge_priority\tcommit_ready\thard_blocked\tfriend_blocked\tcharge_opportunity\tdecision_reason\ttarget_stability\tdodge_severity\ttarget_vel_x\ttarget_vel_y\tcounter_sweep_attempted\tcounter_sweep_active\tcounter_sweep_chance\tcurrent_chunk_x\tcurrent_chunk_y\tcurrent_chunk_radius\tbest_chunk_x\tbest_chunk_y\tbest_chunk_radius\tbody_path_end_x\tbody_path_end_y\ttip_path_end_x\ttip_path_end_y";

    private static string BuildPayload(LanceScavengerDebugEntrySnapshot entry)
    {
        LanceScavengerDebugChunkSnapshot currentChunk = FindChunk(entry.TargetChunks, entry.TargetChunkIndex);
        LanceScavengerDebugChunkSnapshot bestChunk = FindChunk(entry.TargetChunks, entry.BestTargetChunkIndex);

        GetLastPoint(entry.BodyPathX, entry.BodyPathY, out float bodyEndX, out float bodyEndY);
        GetLastPoint(entry.TipPathX, entry.TipPathY, out float tipEndX, out float tipEndY);

        StringBuilder line = new(768);
        Append(line, Safe(entry.RoomName));
        Append(line, Safe(entry.EntityId));
        Append(line, entry.Spawner);
        Append(line, entry.Number);
        Append(line, Safe(entry.State));
        Append(line, entry.StateAge);
        Append(line, entry.Cooldown);
        Append(line, entry.AttackSerial);
        Append(line, entry.Conscious);
        Append(line, entry.HasLance);
        Append(line, entry.HasSidearm);
        Append(line, Safe(entry.Behavior));
        Append(line, Safe(entry.Target));
        Append(line, Safe(entry.Violence));
        Append(line, entry.Afraid);
        Append(line, entry.Distance);
        Append(line, entry.AimQuality);
        Append(line, entry.AimThreshold);
        Append(line, entry.AimReady);
        Append(line, entry.BestAimQuality);
        Append(line, entry.BestAimAge == int.MaxValue ? -1 : entry.BestAimAge);
        Append(line, entry.BestAimReady);
        Append(line, entry.TargetChunkIndex);
        Append(line, entry.BestTargetChunkIndex);
        Append(line, entry.ImpactFrame);
        Append(line, entry.ExactAim);
        Append(line, entry.AimX);
        Append(line, entry.AimY);
        Append(line, entry.LancePitchDegrees);
        Append(line, entry.OriginX);
        Append(line, entry.OriginY);
        Append(line, entry.GripX);
        Append(line, entry.GripY);
        Append(line, entry.LanceDirectionX);
        Append(line, entry.LanceDirectionY);
        Append(line, entry.PathClear);
        Append(line, Safe(entry.LaneReason));
        Append(line, entry.ChargePriority);
        Append(line, entry.CommitReady);
        Append(line, entry.HardBlocked);
        Append(line, entry.FriendBlocked);
        Append(line, entry.ChargeOpportunity);
        Append(line, Safe(entry.DecisionReason));
        Append(line, entry.TargetStability);
        Append(line, entry.DodgeSeverity);
        Append(line, entry.TargetVelocityX);
        Append(line, entry.TargetVelocityY);
        Append(line, entry.CounterSweepAttempted);
        Append(line, entry.CounterSweepActive);
        Append(line, entry.CounterSweepChance);
        AppendChunk(line, currentChunk);
        AppendChunk(line, bestChunk);
        Append(line, bodyEndX);
        Append(line, bodyEndY);
        Append(line, tipEndX);
        AppendLast(line, tipEndY);
        return line.ToString();
    }

    private static LanceScavengerDebugChunkSnapshot FindChunk(
        LanceScavengerDebugChunkSnapshot[] chunks,
        int index)
    {
        if (chunks == null || index < 0) return null;
        for (int i = 0; i < chunks.Length; i++)
            if (chunks[i] != null && chunks[i].Index == index)
                return chunks[i];
        return null;
    }

    private static void AppendChunk(StringBuilder line, LanceScavengerDebugChunkSnapshot chunk)
    {
        if (chunk == null)
        {
            Append(line, float.NaN);
            Append(line, float.NaN);
            Append(line, float.NaN);
            return;
        }

        Append(line, chunk.X);
        Append(line, chunk.Y);
        Append(line, chunk.Radius);
    }

    private static void GetLastPoint(float[] x, float[] y, out float lastX, out float lastY)
    {
        int count = Math.Min(x?.Length ?? 0, y?.Length ?? 0);
        if (count <= 0)
        {
            lastX = float.NaN;
            lastY = float.NaN;
            return;
        }

        lastX = x[count - 1];
        lastY = y[count - 1];
    }

    private void EnforceSizeLimit()
    {
        if (writer == null || capped) return;
        try
        {
            writer.Flush();
            if (writer.BaseStream.Position < MaximumLogBytes) return;

            writer.WriteLine("# STOP\t" + ElapsedMilliseconds().ToString(CultureInfo.InvariantCulture) +
                             "\tlog reached 16 MiB; close and reopen the diagnostics page to start a fresh file");
            writer.Flush();
            capped = true;
            Logger.LogWarning("LanceScavenger realtime AI log reached 16 MiB and stopped recording until the page is reopened.");
        }
        catch (Exception error)
        {
            capped = true;
            Logger.LogWarning("LanceScavenger realtime AI log failed while checking size: " + error.Message);
        }
    }

    private long ElapsedMilliseconds() => unchecked((uint)(Environment.TickCount - sessionStartTick));

    private static long EntityKey(int spawner, int number) =>
        ((long)spawner << 32) ^ (uint)number;

    private static string Safe(string value)
    {
        if (string.IsNullOrEmpty(value)) return "-";
        return value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    }

    private static void Append(StringBuilder line, string value)
    {
        if (line.Length > 0) line.Append('\t');
        line.Append(value);
    }

    private static void Append(StringBuilder line, int value) => Append(line, value.ToString(CultureInfo.InvariantCulture));
    private static void Append(StringBuilder line, bool value) => Append(line, value ? "1" : "0");
    private static void Append(StringBuilder line, float value) =>
        Append(line, float.IsNaN(value) ? "nan" : value.ToString("0.####", CultureInfo.InvariantCulture));

    private static void AppendLast(StringBuilder line, float value)
    {
        if (line.Length > 0) line.Append('\t');
        line.Append(float.IsNaN(value) ? "nan" : value.ToString("0.####", CultureInfo.InvariantCulture));
    }
}
