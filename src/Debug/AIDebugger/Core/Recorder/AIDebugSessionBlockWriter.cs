using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Logging;

namespace DryCycle.Debugging.AI;

internal enum AIDebugSessionWriterState : byte
{
    Idle,
    Recording,
    Stopping,
    Faulted
}

// Background durable writer for V5 recorder blocks. The simulation thread only enqueues
// one lease per sealed block; serialization, CRC calculation and disk IO happen here.
// Leases pin their recorder arrays until the worker has durably written and released them.
internal static class AIDebugSessionBlockWriter
{
    private const uint Magic = 0x35494144u; // little-endian bytes: D A I 5
    private const ushort FormatVersion = 1;
    private const byte TrackMotion = 1;
    private const byte TrackState = 2;
    private const int HeaderBytes = 36;
    private const int MotionPayloadBytes = 20;
    private const int StatePayloadBytes = 48;

    private static readonly ConcurrentQueue<WorkItem> Queue = new();
    private static readonly AutoResetEvent Wake = new(false);
    private static readonly object StateGate = new();

    private static ManualLogSource logger;
    private static Thread worker;
    private static volatile bool shutdown;
    private static volatile AIDebugSessionWriterState state;
    private static volatile string activeSessionPath;
    private static string lastError;
    private static long blocksWritten;
    private static long bytesWritten;
    private static int autoSession;

    internal static string SessionDirectory =>
        Path.Combine(Paths.ConfigPath, "DryCycle.AIObservatory.Sessions");

    internal static AIDebugSessionWriterState State => state;
    internal static string ActiveSessionPath => activeSessionPath;
    internal static long BlocksWritten => Interlocked.Read(ref blocksWritten);
    internal static long BytesWritten => Interlocked.Read(ref bytesWritten);
    internal static bool AutomaticSession => Volatile.Read(ref autoSession) != 0;

    internal static void Initialize(ManualLogSource log)
    {
        logger = log;
        EnsureWorker();
        ThreadPool.QueueUserWorkItem(_ => RecoverIncompleteSessions());
    }

    internal static string StartSession(bool automatic = false)
    {
        EnsureWorker();
        lock (StateGate)
        {
            if (state == AIDebugSessionWriterState.Recording && !string.IsNullOrEmpty(activeSessionPath))
            {
                // A user-requested Recording mode promotes an automatic anomaly session
                // instead of racing a second writer against the same recorder blocks.
                if (!automatic && AutomaticSession) Interlocked.Exchange(ref autoSession, 0);
                return activeSessionPath;
            }

            string path = Path.Combine(SessionDirectory,
                $"Recorder-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}");
            activeSessionPath = path;
            lastError = null;
            Interlocked.Exchange(ref blocksWritten, 0L);
            Interlocked.Exchange(ref bytesWritten, 0L);
            Interlocked.Exchange(ref autoSession, automatic ? 1 : 0);
            state = AIDebugSessionWriterState.Recording;
            Queue.Enqueue(new StartWork(path));
            Wake.Set();
            return path;
        }
    }

    internal static void StopSession(bool wait = false)
    {
        EnsureWorker();
        ManualResetEvent completed = wait ? new ManualResetEvent(false) : null;
        lock (StateGate)
        {
            if (state == AIDebugSessionWriterState.Idle)
            {
                completed?.Set();
            }
            else
            {
                state = AIDebugSessionWriterState.Stopping;
                Queue.Enqueue(new StopWork(completed));
                Wake.Set();
            }
        }
        if (wait)
        {
            completed.WaitOne(1500);
            completed.Dispose();
        }
    }

    internal static void Shutdown()
    {
        StopSession(wait: true);
        shutdown = true;
        Wake.Set();
        try { worker?.Join(800); } catch { }
        worker = null;
    }

    internal static void EnqueueMotion(
        DebugEntityKey key,
        AIDebugSealedBlockLease<AIDebugMotionSample> lease,
        bool force = false)
    {
        if (lease == null) return;
        if (state != AIDebugSessionWriterState.Recording || (AutomaticSession && !force))
        {
            lease.Release();
            return;
        }
        Queue.Enqueue(new MotionWork(key, lease));
        Wake.Set();
    }

    internal static void EnqueueState(
        DebugEntityKey key,
        AIDebugSealedBlockLease<AIDebugFastStateSample> lease,
        bool force = false)
    {
        if (lease == null) return;
        if (state != AIDebugSessionWriterState.Recording || (AutomaticSession && !force))
        {
            lease.Release();
            return;
        }
        Queue.Enqueue(new StateWork(key, lease));
        Wake.Set();
    }

    // Anomaly captures can create an automatic writer session without switching the
    // recorder's Armed/Recording mode. Only explicitly forced pre/post-roll leases are
    // accepted by an automatic session; ordinary sealed blocks are released immediately.
    internal static void EnsureAnomalySession()
    {
        if (state == AIDebugSessionWriterState.Recording) return;
        StartSession(automatic: true);
    }

    internal static void EnqueueCaptureMetadata(
        int captureId,
        DebugEntityKey key,
        int startTick,
        int triggerTick,
        int endTick,
        string reason,
        int motionBlocks,
        int stateBlocks)
    {
        EnsureAnomalySession();
        Queue.Enqueue(new CaptureMetadataWork(
            captureId, key, startTick, triggerTick, endTick, reason, motionBlocks, stateBlocks));
        Wake.Set();
    }

    internal static string Describe()
    {
        string result = state switch
        {
            AIDebugSessionWriterState.Recording =>
                $"{(AutomaticSession ? "capture" : "recording")} {BlocksWritten} blocks / {BytesWritten / 1024.0:0.0} KiB",
            AIDebugSessionWriterState.Stopping => "writer stopping",
            AIDebugSessionWriterState.Faulted => "writer FAILED",
            _ => string.Empty
        };
        if (!string.IsNullOrEmpty(lastError) && state == AIDebugSessionWriterState.Faulted)
            result += ": " + lastError;
        return result;
    }

    private static void EnsureWorker()
    {
        if (worker != null && worker.IsAlive) return;
        lock (StateGate)
        {
            if (worker != null && worker.IsAlive) return;
            shutdown = false;
            worker = new Thread(WorkerMain)
            {
                IsBackground = true,
                Name = "DryCycle AI Observatory block writer"
            };
            worker.Start();
        }
    }

    private static void WorkerMain()
    {
        FileStream stream = null;
        BinaryWriter writer = null;
        string path = null;

        while (!shutdown || !Queue.IsEmpty)
        {
            if (!Queue.TryDequeue(out WorkItem item))
            {
                Wake.WaitOne(250);
                continue;
            }

            try
            {
                switch (item)
                {
                    case StartWork start:
                        writer?.Dispose();
                        stream?.Dispose();
                        path = start.Path;
                        Directory.CreateDirectory(path);
                        WriteManifest(path, incomplete: true);
                        stream = new FileStream(
                            Path.Combine(path, "trace.bin"),
                            FileMode.Append,
                            FileAccess.Write,
                            FileShare.Read,
                            64 * 1024,
                            FileOptions.SequentialScan);
                        writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
                        break;

                    case MotionWork motion:
                        if (writer == null) throw new InvalidOperationException("Session writer has no active trace.bin stream.");
                        try { WriteMotionBlock(writer, motion.Key, motion.Lease); }
                        finally { motion.Lease.Release(); }
                        break;

                    case StateWork fast:
                        if (writer == null) throw new InvalidOperationException("Session writer has no active trace.bin stream.");
                        try { WriteStateBlock(writer, fast.Key, fast.Lease); }
                        finally { fast.Lease.Release(); }
                        break;

                    case CaptureMetadataWork capture:
                        if (path == null) throw new InvalidOperationException("Capture metadata has no active session.");
                        WriteCaptureMetadata(path, capture);
                        break;

                    case StopWork stop:
                        try
                        {
                            writer?.Flush();
                            stream?.Flush(true);
                            writer?.Dispose();
                            stream?.Dispose();
                            writer = null;
                            stream = null;
                            if (!string.IsNullOrEmpty(path))
                            {
                                WriteManifest(path, incomplete: false);
                                File.WriteAllText(
                                    Path.Combine(path, "complete.json"),
                                    "{\"complete\":true,\"utc\":\"" + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + "\"}",
                                    Encoding.UTF8);
                            }
                            path = null;
                            activeSessionPath = null;
                            Interlocked.Exchange(ref autoSession, 0);
                            if (state != AIDebugSessionWriterState.Faulted)
                                state = AIDebugSessionWriterState.Idle;
                        }
                        finally
                        {
                            stop.Completed?.Set();
                        }
                        break;
                }
            }
            catch (Exception error)
            {
                lastError = error.Message;
                state = AIDebugSessionWriterState.Faulted;
                logger?.LogWarning("DryCycle AI Observatory block writer failed: " + error);
                ReleaseItem(item);
            }
        }

        try { writer?.Dispose(); } catch { }
        try { stream?.Dispose(); } catch { }
    }

    private static void WriteMotionBlock(BinaryWriter writer, DebugEntityKey key, AIDebugSealedBlockLease<AIDebugMotionSample> lease)
    {
        int payloadBytes = checked(lease.Count * MotionPayloadBytes);
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < lease.Count; i++)
        {
            AIDebugMotionSample s = lease.Items[i];
            crc = CrcInt(crc, s.Tick);
            crc = CrcInt(crc, AIDebugFloatBits.Of(s.X));
            crc = CrcInt(crc, AIDebugFloatBits.Of(s.Y));
            crc = CrcInt(crc, AIDebugFloatBits.Of(s.VX));
            crc = CrcInt(crc, AIDebugFloatBits.Of(s.VY));
        }
        crc ^= 0xFFFFFFFFu;

        WriteHeader(writer, TrackMotion, key, lease.StartTick, lease.EndTick, lease.Count, payloadBytes, crc);
        for (int i = 0; i < lease.Count; i++)
        {
            AIDebugMotionSample s = lease.Items[i];
            writer.Write(s.Tick);
            writer.Write(s.X);
            writer.Write(s.Y);
            writer.Write(s.VX);
            writer.Write(s.VY);
        }
        Interlocked.Increment(ref blocksWritten);
        Interlocked.Add(ref bytesWritten, HeaderBytes + payloadBytes);
    }

    private static void WriteStateBlock(BinaryWriter writer, DebugEntityKey key, AIDebugSealedBlockLease<AIDebugFastStateSample> lease)
    {
        int payloadBytes = checked(lease.Count * StatePayloadBytes);
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < lease.Count; i++)
        {
            AIDebugFastStateSample s = lease.Items[i];
            crc = CrcInt(crc, s.Tick);
            crc = CrcInt(crc, unchecked((int)s.Sequence));
            crc = CrcFastState(crc, s.State);
        }
        crc ^= 0xFFFFFFFFu;

        WriteHeader(writer, TrackState, key, lease.StartTick, lease.EndTick, lease.Count, payloadBytes, crc);
        for (int i = 0; i < lease.Count; i++)
        {
            AIDebugFastStateSample s = lease.Items[i];
            writer.Write(s.Tick);
            writer.Write(s.Sequence);
            WriteFastState(writer, s.State);
        }
        Interlocked.Increment(ref blocksWritten);
        Interlocked.Add(ref bytesWritten, HeaderBytes + payloadBytes);
    }

    private static void WriteHeader(
        BinaryWriter writer,
        byte track,
        DebugEntityKey key,
        int startTick,
        int endTick,
        int count,
        int payloadBytes,
        uint crc)
    {
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(track);
        writer.Write((byte)0);
        writer.Write(key.Spawner);
        writer.Write(key.Number);
        writer.Write(startTick);
        writer.Write(endTick);
        writer.Write(count);
        writer.Write(payloadBytes);
        writer.Write(crc);
    }

    private static void WriteFastState(BinaryWriter writer, AIDebugFastState s)
    {
        writer.Write(s.Room);
        writer.Write((int)s.EntityState);
        writer.Write(s.DestinationRoom);
        writer.Write(s.DestinationX);
        writer.Write(s.DestinationY);
        writer.Write(s.DestinationNode);
        writer.Write(s.ModeToken);
        writer.Write(s.TargetSpawner);
        writer.Write(s.TargetNumber);
        writer.Write((int)s.Flags);
    }

    private static uint CrcFastState(uint crc, AIDebugFastState s)
    {
        crc = CrcInt(crc, s.Room);
        crc = CrcInt(crc, (int)s.EntityState);
        crc = CrcInt(crc, s.DestinationRoom);
        crc = CrcInt(crc, s.DestinationX);
        crc = CrcInt(crc, s.DestinationY);
        crc = CrcInt(crc, s.DestinationNode);
        crc = CrcInt(crc, s.ModeToken);
        crc = CrcInt(crc, s.TargetSpawner);
        crc = CrcInt(crc, s.TargetNumber);
        return CrcInt(crc, (int)s.Flags);
    }

    private static uint CrcInt(uint crc, int value)
    {
        unchecked
        {
            crc = CrcByte(crc, (byte)value);
            crc = CrcByte(crc, (byte)(value >> 8));
            crc = CrcByte(crc, (byte)(value >> 16));
            return CrcByte(crc, (byte)(value >> 24));
        }
    }

    private static uint CrcByte(uint crc, byte value)
    {
        crc ^= value;
        for (int i = 0; i < 8; i++)
            crc = (crc & 1u) != 0u ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        return crc;
    }

    private static void WriteManifest(string path, bool incomplete)
    {
        string json = "{" +
                      "\"format\":\"DryCycle.AIObservatory.TraceBin\"," +
                      "\"version\":" + FormatVersion + "," +
                      "\"incomplete\":" + (incomplete ? "true" : "false") + "," +
                      "\"blocks\":" + BlocksWritten + "," +
                      "\"bytes\":" + BytesWritten + "," +
                      "\"utc\":\"" + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + "\"" +
                      "}";
        File.WriteAllText(Path.Combine(path, "manifest.json"), json, Encoding.UTF8);
    }

    private static void WriteCaptureMetadata(string path, CaptureMetadataWork capture)
    {
        string safeReason = Json(capture.Reason ?? "anomaly");
        string json = "{" +
                      "\"captureId\":" + capture.CaptureId + "," +
                      "\"entity\":\"" + Json(capture.Key.ToString()) + "\"," +
                      "\"spawner\":" + capture.Key.Spawner + "," +
                      "\"number\":" + capture.Key.Number + "," +
                      "\"startTick\":" + capture.StartTick + "," +
                      "\"triggerTick\":" + capture.TriggerTick + "," +
                      "\"endTick\":" + capture.EndTick + "," +
                      "\"reason\":\"" + safeReason + "\"," +
                      "\"motionBlocks\":" + capture.MotionBlocks + "," +
                      "\"stateBlocks\":" + capture.StateBlocks +
                      "}";
        File.WriteAllText(Path.Combine(path, $"capture-{capture.CaptureId:D4}.json"), json, Encoding.UTF8);
    }

    private static string Json(string value) =>
        (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");

    private static void ReleaseItem(WorkItem item)
    {
        if (item is MotionWork motion) motion.Lease.Release();
        else if (item is StateWork fast) fast.Lease.Release();
        else if (item is StopWork stop) stop.Completed?.Set();
    }

    private static void RecoverIncompleteSessions()
    {
        try
        {
            Directory.CreateDirectory(SessionDirectory);
            string[] dirs = Directory.GetDirectories(SessionDirectory, "Recorder-*");
            for (int i = 0; i < dirs.Length; i++)
            {
                string dir = dirs[i];
                if (File.Exists(Path.Combine(dir, "complete.json"))) continue;
                string trace = Path.Combine(dir, "trace.bin");
                if (!File.Exists(trace)) continue;
                RecoverTrace(dir, trace);
            }
        }
        catch (Exception error)
        {
            logger?.LogWarning("DryCycle AI Observatory crash recovery scan failed: " + error.Message);
        }
    }

    private static void RecoverTrace(string directory, string tracePath)
    {
        long lastGood = 0L;
        int validBlocks = 0;
        bool incomplete = false;
        using (var stream = new FileStream(tracePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
        {
            while (stream.Position < stream.Length)
            {
                long headerStart = stream.Position;
                if (stream.Length - headerStart < HeaderBytes)
                {
                    incomplete = true;
                    break;
                }

                uint magic = reader.ReadUInt32();
                ushort version = reader.ReadUInt16();
                byte track = reader.ReadByte();
                reader.ReadByte();
                reader.ReadInt32(); // spawner
                reader.ReadInt32(); // number
                reader.ReadInt32(); // start
                reader.ReadInt32(); // end
                int count = reader.ReadInt32();
                int payloadBytes = reader.ReadInt32();
                uint expectedCrc = reader.ReadUInt32();

                int itemBytes = track == TrackMotion ? MotionPayloadBytes : track == TrackState ? StatePayloadBytes : 0;
                if (magic != Magic || version != FormatVersion || itemBytes == 0 || count < 0 ||
                    payloadBytes != count * itemBytes || payloadBytes < 0 || payloadBytes > 32 * 1024 * 1024 ||
                    stream.Length - stream.Position < payloadBytes)
                {
                    incomplete = true;
                    break;
                }

                uint crc = 0xFFFFFFFFu;
                int remaining = payloadBytes;
                byte[] buffer = new byte[Math.Min(8192, Math.Max(1, payloadBytes))];
                while (remaining > 0)
                {
                    int read = stream.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                    if (read <= 0)
                    {
                        incomplete = true;
                        break;
                    }
                    for (int i = 0; i < read; i++) crc = CrcByte(crc, buffer[i]);
                    remaining -= read;
                }
                if (incomplete) break;
                crc ^= 0xFFFFFFFFu;
                if (crc != expectedCrc)
                {
                    incomplete = true;
                    break;
                }

                lastGood = stream.Position;
                validBlocks++;
            }

            if (incomplete && lastGood < stream.Length)
                stream.SetLength(lastGood);
        }

        string recovery = "{" +
                          "\"recovered\":true," +
                          "\"validBlocks\":" + validBlocks + "," +
                          "\"truncatedTrailingData\":" + (incomplete ? "true" : "false") + "," +
                          "\"utc\":\"" + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + "\"" +
                          "}";
        File.WriteAllText(Path.Combine(directory, "recovery.json"), recovery, Encoding.UTF8);
        logger?.LogInfo($"DryCycle AI Observatory recovered incomplete session '{Path.GetFileName(directory)}': blocks={validBlocks}, truncated={incomplete}.");
    }

    private abstract class WorkItem { }

    private sealed class StartWork : WorkItem
    {
        internal readonly string Path;
        internal StartWork(string path) => Path = path;
    }

    private sealed class StopWork : WorkItem
    {
        internal readonly ManualResetEvent Completed;
        internal StopWork(ManualResetEvent completed) => Completed = completed;
    }

    private sealed class MotionWork : WorkItem
    {
        internal readonly DebugEntityKey Key;
        internal readonly AIDebugSealedBlockLease<AIDebugMotionSample> Lease;
        internal MotionWork(DebugEntityKey key, AIDebugSealedBlockLease<AIDebugMotionSample> lease)
        {
            Key = key;
            Lease = lease;
        }
    }

    private sealed class StateWork : WorkItem
    {
        internal readonly DebugEntityKey Key;
        internal readonly AIDebugSealedBlockLease<AIDebugFastStateSample> Lease;
        internal StateWork(DebugEntityKey key, AIDebugSealedBlockLease<AIDebugFastStateSample> lease)
        {
            Key = key;
            Lease = lease;
        }
    }

    private sealed class CaptureMetadataWork : WorkItem
    {
        internal readonly int CaptureId;
        internal readonly DebugEntityKey Key;
        internal readonly int StartTick;
        internal readonly int TriggerTick;
        internal readonly int EndTick;
        internal readonly string Reason;
        internal readonly int MotionBlocks;
        internal readonly int StateBlocks;

        internal CaptureMetadataWork(
            int captureId,
            DebugEntityKey key,
            int startTick,
            int triggerTick,
            int endTick,
            string reason,
            int motionBlocks,
            int stateBlocks)
        {
            CaptureId = captureId;
            Key = key;
            StartTick = startTick;
            TriggerTick = triggerTick;
            EndTick = endTick;
            Reason = reason;
            MotionBlocks = motionBlocks;
            StateBlocks = stateBlocks;
        }
    }
}
