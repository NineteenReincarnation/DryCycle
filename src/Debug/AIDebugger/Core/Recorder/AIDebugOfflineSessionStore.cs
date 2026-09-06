using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace DryCycle.Debugging.AI;

internal sealed class AIDebugOfflineSessionSummary
{
    internal readonly string Name;
    internal readonly string Path;
    internal readonly bool Complete;
    internal readonly bool Recovered;
    internal readonly bool Incomplete;
    internal readonly long TraceBytes;
    internal readonly DateTime ModifiedUtc;

    internal AIDebugOfflineSessionSummary(
        string name,
        string path,
        bool complete,
        bool recovered,
        bool incomplete,
        long traceBytes,
        DateTime modifiedUtc)
    {
        Name = name ?? "session";
        Path = path ?? string.Empty;
        Complete = complete;
        Recovered = recovered;
        Incomplete = incomplete;
        TraceBytes = Math.Max(0L, traceBytes);
        ModifiedUtc = modifiedUtc;
    }
}

internal sealed class AIDebugOfflineCatalog
{
    internal static readonly AIDebugOfflineCatalog Empty =
        new(Array.Empty<AIDebugOfflineSessionSummary>(), false, string.Empty);

    internal readonly AIDebugOfflineSessionSummary[] Sessions;
    internal readonly bool Loading;
    internal readonly string Error;

    internal AIDebugOfflineCatalog(AIDebugOfflineSessionSummary[] sessions, bool loading, string error)
    {
        Sessions = sessions ?? Array.Empty<AIDebugOfflineSessionSummary>();
        Loading = loading;
        Error = error ?? string.Empty;
    }
}

internal sealed class AIDebugOfflineEntityTrace
{
    internal readonly int Spawner;
    internal readonly int Number;
    internal readonly string DisplayName;
    internal readonly int StartTick;
    internal readonly int EndTick;
    internal readonly AIDebugMotionSample[] Motion;
    internal readonly AIDebugFastStateSample[] States;
    internal readonly bool Truncated;

    internal AIDebugOfflineEntityTrace(
        int spawner,
        int number,
        int startTick,
        int endTick,
        AIDebugMotionSample[] motion,
        AIDebugFastStateSample[] states,
        bool truncated)
    {
        Spawner = spawner;
        Number = number;
        DisplayName = $"Entity {spawner}:{number}";
        StartTick = startTick;
        EndTick = endTick;
        Motion = motion ?? Array.Empty<AIDebugMotionSample>();
        States = states ?? Array.Empty<AIDebugFastStateSample>();
        Truncated = truncated;
    }
}

internal sealed class AIDebugOfflineTrace
{
    internal static readonly AIDebugOfflineTrace Empty =
        new(string.Empty, string.Empty, false, string.Empty, Array.Empty<AIDebugOfflineEntityTrace>(), 0, 0L);

    internal readonly string SessionName;
    internal readonly string Path;
    internal readonly bool Loading;
    internal readonly string Error;
    internal readonly AIDebugOfflineEntityTrace[] Entities;
    internal readonly int ValidBlocks;
    internal readonly long BytesRead;

    internal AIDebugOfflineTrace(
        string sessionName,
        string path,
        bool loading,
        string error,
        AIDebugOfflineEntityTrace[] entities,
        int validBlocks,
        long bytesRead)
    {
        SessionName = sessionName ?? string.Empty;
        Path = path ?? string.Empty;
        Loading = loading;
        Error = error ?? string.Empty;
        Entities = entities ?? Array.Empty<AIDebugOfflineEntityTrace>();
        ValidBlocks = Math.Max(0, validBlocks);
        BytesRead = Math.Max(0L, bytesRead);
    }
}

// Disk work for the Offline Viewer is isolated to ThreadPool workers. RWImGUI may only
// request refresh/load operations and read these detached immutable snapshots; Present
// never enumerates directories or parses trace.bin itself.
internal static class AIDebugOfflineSessionStore
{
    private const uint Magic = 0x35494144u;
    private const ushort FormatVersion = 1;
    private const byte TrackMotion = 1;
    private const byte TrackState = 2;
    private const int HeaderBytes = 36;
    private const int MotionPayloadBytes = 20;
    private const int StatePayloadBytes = 48;
    private const int MaxMotionPerEntity = 300000;
    private const int MaxStatesPerEntity = 150000;

    private static AIDebugOfflineCatalog catalog = AIDebugOfflineCatalog.Empty;
    private static AIDebugOfflineTrace trace = AIDebugOfflineTrace.Empty;
    private static int refreshing;
    private static int loadGeneration;

    internal static AIDebugOfflineCatalog Catalog =>
        Volatile.Read(ref catalog) ?? AIDebugOfflineCatalog.Empty;

    internal static AIDebugOfflineTrace Trace =>
        Volatile.Read(ref trace) ?? AIDebugOfflineTrace.Empty;

    internal static void RefreshAsync()
    {
        if (Interlocked.CompareExchange(ref refreshing, 1, 0) != 0) return;
        AIDebugOfflineCatalog previous = Catalog;
        Volatile.Write(ref catalog, new AIDebugOfflineCatalog(previous.Sessions, true, string.Empty));
        ThreadPool.QueueUserWorkItem(_ => RefreshWorker());
    }

    internal static void LoadAsync(string path)
    {
        string validated;
        try
        {
            validated = ValidateSessionPath(path);
        }
        catch (Exception error)
        {
            Volatile.Write(ref trace, new AIDebugOfflineTrace(
                string.Empty, path, false, error.Message, Array.Empty<AIDebugOfflineEntityTrace>(), 0, 0L));
            return;
        }

        int generation = Interlocked.Increment(ref loadGeneration);
        string name = System.IO.Path.GetFileName(validated);
        Volatile.Write(ref trace, new AIDebugOfflineTrace(
            name, validated, true, string.Empty, Array.Empty<AIDebugOfflineEntityTrace>(), 0, 0L));
        ThreadPool.QueueUserWorkItem(_ => LoadWorker(validated, name, generation));
    }

    internal static void Reset()
    {
        Interlocked.Increment(ref loadGeneration);
        Volatile.Write(ref catalog, AIDebugOfflineCatalog.Empty);
        Volatile.Write(ref trace, AIDebugOfflineTrace.Empty);
        Volatile.Write(ref refreshing, 0);
    }

    private static void RefreshWorker()
    {
        try
        {
            string root = AIDebugSessionBlockWriter.SessionDirectory;
            Directory.CreateDirectory(root);
            string[] dirs = Directory.GetDirectories(root, "Recorder-*");
            var summaries = new List<AIDebugOfflineSessionSummary>(dirs.Length);
            for (int i = 0; i < dirs.Length; i++)
            {
                string dir = dirs[i];
                string tracePath = System.IO.Path.Combine(dir, "trace.bin");
                if (!File.Exists(tracePath)) continue;
                FileInfo info = new FileInfo(tracePath);
                bool complete = File.Exists(System.IO.Path.Combine(dir, "complete.json"));
                bool recovered = File.Exists(System.IO.Path.Combine(dir, "recovery.json"));
                summaries.Add(new AIDebugOfflineSessionSummary(
                    System.IO.Path.GetFileName(dir),
                    dir,
                    complete,
                    recovered,
                    !complete,
                    info.Length,
                    info.LastWriteTimeUtc));
            }

            summaries.Sort((a, b) => b.ModifiedUtc.CompareTo(a.ModifiedUtc));
            Volatile.Write(ref catalog,
                new AIDebugOfflineCatalog(summaries.ToArray(), false, string.Empty));
        }
        catch (Exception error)
        {
            Volatile.Write(ref catalog,
                new AIDebugOfflineCatalog(Array.Empty<AIDebugOfflineSessionSummary>(), false, error.Message));
        }
        finally
        {
            Volatile.Write(ref refreshing, 0);
        }
    }

    private static void LoadWorker(string sessionPath, string name, int generation)
    {
        try
        {
            string file = System.IO.Path.Combine(sessionPath, "trace.bin");
            var builders = new Dictionary<long, EntityBuilder>();
            int validBlocks = 0;
            long bytesRead = 0L;

            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
            {
                while (stream.Position < stream.Length)
                {
                    long blockStart = stream.Position;
                    if (stream.Length - blockStart < HeaderBytes) break;

                    uint magic = reader.ReadUInt32();
                    ushort version = reader.ReadUInt16();
                    byte trackKind = reader.ReadByte();
                    reader.ReadByte();
                    int spawner = reader.ReadInt32();
                    int number = reader.ReadInt32();
                    int startTick = reader.ReadInt32();
                    int endTick = reader.ReadInt32();
                    int count = reader.ReadInt32();
                    int payloadBytes = reader.ReadInt32();
                    uint expectedCrc = reader.ReadUInt32();

                    int itemBytes = trackKind == TrackMotion ? MotionPayloadBytes :
                        trackKind == TrackState ? StatePayloadBytes : 0;
                    if (magic != Magic || version != FormatVersion || itemBytes == 0 || count < 0 ||
                        payloadBytes < 0 || payloadBytes != count * itemBytes ||
                        stream.Length - stream.Position < payloadBytes)
                        break;

                    long payloadStart = stream.Position;
                    uint crc = ComputeCrc(stream, payloadBytes);
                    if (crc != expectedCrc) break;
                    stream.Position = payloadStart;

                    long entityId = ((long)spawner << 32) ^ (uint)number;
                    if (!builders.TryGetValue(entityId, out EntityBuilder builder))
                    {
                        builder = new EntityBuilder(spawner, number);
                        builders.Add(entityId, builder);
                    }

                    if (trackKind == TrackMotion)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            int tick = reader.ReadInt32();
                            float x = reader.ReadSingle();
                            float y = reader.ReadSingle();
                            float vx = reader.ReadSingle();
                            float vy = reader.ReadSingle();
                            if (builder.Motion.Count < MaxMotionPerEntity)
                                builder.Motion.Add(new AIDebugMotionSample(tick, x, y, vx, vy));
                            else
                                builder.Truncated = true;
                        }
                    }
                    else
                    {
                        for (int i = 0; i < count; i++)
                        {
                            int tick = reader.ReadInt32();
                            uint sequence = reader.ReadUInt32();
                            AIDebugFastState state = ReadFastState(reader);
                            if (builder.States.Count < MaxStatesPerEntity)
                                builder.States.Add(new AIDebugFastStateSample(tick, sequence, state));
                            else
                                builder.Truncated = true;
                        }
                    }

                    builder.Include(startTick, endTick);
                    validBlocks++;
                    bytesRead = stream.Position;
                }
            }

            var entities = new AIDebugOfflineEntityTrace[builders.Count];
            int write = 0;
            foreach (EntityBuilder builder in builders.Values)
                entities[write++] = builder.Build();
            Array.Sort(entities, (a, b) =>
            {
                int bySpawner = a.Spawner.CompareTo(b.Spawner);
                return bySpawner != 0 ? bySpawner : a.Number.CompareTo(b.Number);
            });

            if (generation != Volatile.Read(ref loadGeneration)) return;
            Volatile.Write(ref trace,
                new AIDebugOfflineTrace(name, sessionPath, false, string.Empty, entities, validBlocks, bytesRead));
        }
        catch (Exception error)
        {
            if (generation != Volatile.Read(ref loadGeneration)) return;
            Volatile.Write(ref trace,
                new AIDebugOfflineTrace(name, sessionPath, false, error.Message,
                    Array.Empty<AIDebugOfflineEntityTrace>(), 0, 0L));
        }
    }

    private static string ValidateSessionPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Session path is empty.");
        string root = System.IO.Path.GetFullPath(AIDebugSessionBlockWriter.SessionDirectory)
            .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) +
            System.IO.Path.DirectorySeparatorChar;
        string candidate = System.IO.Path.GetFullPath(path)
            .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) +
            System.IO.Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Offline session must be inside the AI Observatory session directory.");
        string tracePath = System.IO.Path.Combine(candidate, "trace.bin");
        if (!File.Exists(tracePath)) throw new FileNotFoundException("trace.bin was not found for this session.", tracePath);
        return candidate.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
    }

    private static AIDebugFastState ReadFastState(BinaryReader reader)
    {
        return new AIDebugFastState(
            reader.ReadInt32(),
            (AIDebugEntityState)reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            (AIDebugFastFlags)reader.ReadInt32());
    }

    private static uint ComputeCrc(Stream stream, int bytes)
    {
        uint crc = 0xFFFFFFFFu;
        byte[] buffer = new byte[Math.Min(8192, Math.Max(1, bytes))];
        int remaining = bytes;
        while (remaining > 0)
        {
            int read = stream.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            if (read <= 0) throw new EndOfStreamException();
            for (int i = 0; i < read; i++) crc = CrcByte(crc, buffer[i]);
            remaining -= read;
        }
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint CrcByte(uint crc, byte value)
    {
        crc ^= value;
        for (int i = 0; i < 8; i++)
            crc = (crc & 1u) != 0u ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        return crc;
    }

    private sealed class EntityBuilder
    {
        internal readonly int Spawner;
        internal readonly int Number;
        internal readonly List<AIDebugMotionSample> Motion = new(2048);
        internal readonly List<AIDebugFastStateSample> States = new(256);
        internal bool Truncated;
        private int startTick = int.MaxValue;
        private int endTick = int.MinValue;

        internal EntityBuilder(int spawner, int number)
        {
            Spawner = spawner;
            Number = number;
        }

        internal void Include(int start, int end)
        {
            if (start < startTick) startTick = start;
            if (end > endTick) endTick = end;
        }

        internal AIDebugOfflineEntityTrace Build()
        {
            Motion.Sort((a, b) => a.Tick.CompareTo(b.Tick));
            States.Sort((a, b) =>
            {
                int tick = a.Tick.CompareTo(b.Tick);
                return tick != 0 ? tick : a.Sequence.CompareTo(b.Sequence);
            });

            AIDebugMotionSample[] motion = UniqueMotion(Motion);
            AIDebugFastStateSample[] states = UniqueStates(States);
            int start = startTick == int.MaxValue ? 0 : startTick;
            int end = endTick == int.MinValue ? start : endTick;
            return new AIDebugOfflineEntityTrace(Spawner, Number, start, end, motion, states, Truncated);
        }

        private static AIDebugMotionSample[] UniqueMotion(List<AIDebugMotionSample> source)
        {
            if (source.Count == 0) return Array.Empty<AIDebugMotionSample>();
            var output = new AIDebugMotionSample[source.Count];
            int written = 0;
            for (int i = 0; i < source.Count; i++)
            {
                AIDebugMotionSample sample = source[i];
                if (written > 0 && output[written - 1].Tick == sample.Tick)
                {
                    output[written - 1] = sample;
                    continue;
                }
                output[written++] = sample;
            }
            if (written == output.Length) return output;
            Array.Resize(ref output, written);
            return output;
        }

        private static AIDebugFastStateSample[] UniqueStates(List<AIDebugFastStateSample> source)
        {
            if (source.Count == 0) return Array.Empty<AIDebugFastStateSample>();
            var output = new AIDebugFastStateSample[source.Count];
            int written = 0;
            for (int i = 0; i < source.Count; i++)
            {
                AIDebugFastStateSample sample = source[i];
                if (written > 0 && output[written - 1].Tick == sample.Tick &&
                    output[written - 1].Sequence == sample.Sequence)
                {
                    output[written - 1] = sample;
                    continue;
                }
                output[written++] = sample;
            }
            if (written == output.Length) return output;
            Array.Resize(ref output, written);
            return output;
        }
    }
}
