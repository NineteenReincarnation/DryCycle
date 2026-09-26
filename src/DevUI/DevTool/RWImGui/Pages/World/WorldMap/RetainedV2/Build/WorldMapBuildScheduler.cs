using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Map;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Worker scheduler for pure retained-room geometry builds.
///
/// Jobs receive detached presentation snapshots only. Unity objects, MapPage, textures and Mesh
/// uploads remain on the main thread. Reset advances a generation so late worker completions cannot
/// be committed into a new region/session.
/// </summary>
internal sealed class WorldMapBuildScheduler
{
    internal sealed class RoomBuildResult
    {
        internal int RoomIndex;
        internal int SourceStamp;
        internal int Generation;
        internal RoomGeometryBlob Geometry;
        internal Exception Error;
    }

    private sealed class RoomBuildRequest
    {
        internal int RoomIndex;
        internal int SourceStamp;
        internal int Generation;
        internal EditorMapRoomVisualSnapshot Visual;
    }

    private const int MaxWorkers = 2;

    private readonly object gate = new();
    private readonly Queue<RoomBuildRequest> pending = new();
    private readonly Dictionary<int, int> latestRequestedStamp = new();
    private readonly ConcurrentQueue<RoomBuildResult> completed = new();

    private ManualLogSource log;
    private int activeWorkers;
    private int generation;
    private long totalBuildTicks;
    private long peakBuildTicks;
    private int completedBuilds;

    internal int CompletedBuildCount => Volatile.Read(ref completedBuilds);
    internal double AverageBuildMilliseconds
    {
        get
        {
            int count = Math.Max(1, Volatile.Read(ref completedBuilds));
            return Interlocked.Read(ref totalBuildTicks) * 1000d / Stopwatch.Frequency / count;
        }
    }
    internal double PeakBuildMilliseconds =>
        Interlocked.Read(ref peakBuildTicks) * 1000d / Stopwatch.Frequency;

    internal void Initialize(ManualLogSource logger) => log = logger;

    internal bool ScheduleRoom(
        int roomIndex,
        EditorMapRoomVisualSnapshot visual,
        int sourceStamp)
    {
        if (roomIndex < 0 || visual?.Available != true)
            return false;

        lock (gate)
        {
            if (latestRequestedStamp.TryGetValue(roomIndex, out int existing) &&
                existing == sourceStamp)
                return false;

            latestRequestedStamp[roomIndex] = sourceStamp;
            pending.Enqueue(new RoomBuildRequest
            {
                RoomIndex = roomIndex,
                SourceStamp = sourceStamp,
                Generation = generation,
                Visual = visual
            });
            StartWorkersLocked();
            return true;
        }
    }

    internal int Drain(int maxResults, Action<RoomBuildResult> consumer)
    {
        int drained = 0;
        while (drained < maxResults && completed.TryDequeue(out RoomBuildResult result))
        {
            drained++;

            bool current;
            lock (gate)
            {
                current =
                    result.Generation == generation &&
                    latestRequestedStamp.TryGetValue(result.RoomIndex, out int stamp) &&
                    stamp == result.SourceStamp;

                if (current)
                    latestRequestedStamp.Remove(result.RoomIndex);
            }

            if (!current)
                continue;

            if (result.Error != null)
            {
                log?.LogError(
                    "World Map V2 room geometry build failed for room " +
                    result.RoomIndex + " stamp " + result.SourceStamp + ": " +
                    result.Error);
            }

            consumer?.Invoke(result);
        }
        return drained;
    }

    internal void Reset()
    {
        lock (gate)
        {
            unchecked { generation++; }
            pending.Clear();
            latestRequestedStamp.Clear();
            Interlocked.Exchange(ref totalBuildTicks, 0L);
            Interlocked.Exchange(ref peakBuildTicks, 0L);
            Volatile.Write(ref completedBuilds, 0);
        }

        while (completed.TryDequeue(out _))
        {
        }
    }

    private void StartWorkersLocked()
    {
        while (activeWorkers < MaxWorkers && pending.Count > 0)
        {
            RoomBuildRequest request = pending.Dequeue();
            // A newer room snapshot can arrive while both workers are busy. Do not build an
            // obsolete raster/terrain mesh just to discard it when it reaches the main thread.
            if (request.Generation != generation ||
                !latestRequestedStamp.TryGetValue(request.RoomIndex, out int stamp) ||
                stamp != request.SourceStamp)
                continue;
            activeWorkers++;
            Task.Run(() => Execute(request));
        }
    }

    private void Execute(RoomBuildRequest request)
    {
        RoomBuildResult result = new()
        {
            RoomIndex = request.RoomIndex,
            SourceStamp = request.SourceStamp,
            Generation = request.Generation
        };

        long started = Stopwatch.GetTimestamp();
        try
        {
            result.Geometry = RoomGeometryBuilder.Build(
                request.RoomIndex,
                request.Visual,
                request.SourceStamp);
        }
        catch (Exception error)
        {
            result.Error = error;
        }
        finally
        {
            long elapsed = Math.Max(0L, Stopwatch.GetTimestamp() - started);
            Interlocked.Add(ref totalBuildTicks, elapsed);
            UpdatePeak(ref peakBuildTicks, elapsed);
            Interlocked.Increment(ref completedBuilds);
            completed.Enqueue(result);
            lock (gate)
            {
                activeWorkers = Math.Max(0, activeWorkers - 1);
                StartWorkersLocked();
            }
        }
    }

    private static void UpdatePeak(ref long target, long value)
    {
        while (true)
        {
            long current = Interlocked.Read(ref target);
            if (current >= value) return;
            if (Interlocked.CompareExchange(ref target, value, current) == current)
                return;
        }
    }
}
