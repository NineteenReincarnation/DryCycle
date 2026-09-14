using System;
using System.Diagnostics;
using System.Threading;

namespace DryCycle.DevUI.DevTool.Core;

/// <summary>
/// Stable instrumentation points for the rebuilt DevTool. Keep this list intentionally small and
/// architectural: callers should measure meaningful pipeline stages rather than individual helper
/// methods. That makes profiles comparable across refactors instead of tying them to implementation
/// details that change every week.
/// </summary>
internal enum DevToolPerformanceMetric
{
    DevUiUpdateTotal,
    SessionSynchronization,
    DeferredWorkspaceRestore,
    LegacyTransactionBefore,
    InputShortcuts,
    VanillaDevUiUpdate,
    PostLegacySynchronization,
    CommandProcessing,
    LegacyPresentation,
    ObjectGizmoPresentation,
    LegacyQuiescenceBackend,
    CorePresentation,
    RoomPresentation,
    SoundPresentation,
    TriggerPresentation,
    MapPresentation,
    DialogPresentation,
    RelationshipPresentation,
    Count
}

/// <summary>
/// Presentation producers tracked by the cache diagnostics. Keep this aligned with the major
/// immutable snapshot hubs rather than individual widgets so the numbers remain useful after UI
/// layout refactors.
/// </summary>
internal enum DevToolPresentationChannel
{
    Core,
    Room,
    Sound,
    Triggers,
    Map,
    Dialog,
    Relationships,
    Count
}

internal enum DevToolPresentationOutcome
{
    CacheHit,
    PartialRebuild,
    FullRebuild,
    Count
}

/// <summary>
/// Immutable rolling statistics returned to diagnostics/frontends. Values cover the most recent
/// fixed-size sample window; Max is therefore a rolling maximum rather than an unbounded lifetime
/// maximum.
/// </summary>
internal readonly struct DevToolPerformanceStats
{
    internal DevToolPerformanceStats(
        int sampleCount,
        double lastMilliseconds,
        double averageMilliseconds,
        double p95Milliseconds,
        double maxMilliseconds)
    {
        SampleCount = sampleCount;
        LastMilliseconds = lastMilliseconds;
        AverageMilliseconds = averageMilliseconds;
        P95Milliseconds = p95Milliseconds;
        MaxMilliseconds = maxMilliseconds;
    }

    internal int SampleCount { get; }
    internal double LastMilliseconds { get; }
    internal double AverageMilliseconds { get; }
    internal double P95Milliseconds { get; }
    internal double MaxMilliseconds { get; }
    internal bool HasSamples => SampleCount > 0;
}

/// <summary>
/// Lifetime counters for one monitoring run. A cache hit means the producer returned its retained
/// immutable snapshot unchanged. Partial means only a cheap shell/flag/visual layer was rebuilt
/// while expensive payloads were retained. Full means the producer rebuilt its complete snapshot.
/// </summary>
internal readonly struct DevToolPresentationCounters
{
    internal DevToolPresentationCounters(long cacheHits, long partialRebuilds, long fullRebuilds)
    {
        CacheHits = cacheHits;
        PartialRebuilds = partialRebuilds;
        FullRebuilds = fullRebuilds;
    }

    internal long CacheHits { get; }
    internal long PartialRebuilds { get; }
    internal long FullRebuilds { get; }
    internal long Total => CacheHits + PartialRebuilds + FullRebuilds;
    internal double CacheHitRate => Total > 0 ? CacheHits / (double)Total : 0d;
}

/// <summary>
/// Low-overhead rolling performance recorder for DevTool hot paths.
///
/// Design rules:
/// - Disabled is the production default and performs no allocation or locking.
/// - Samples are stored in fixed-size rings so diagnostics never grow memory over time.
/// - The measurement scope is a value type; normal `using` statements do not box or allocate.
/// - Presentation cache outcomes use fixed Interlocked counters and allocate nothing.
/// - A generation token invalidates scopes that were started before Reset/Disable, preventing late
///   Dispose calls from leaking stale timing data into a new measurement run.
/// - Percentiles are computed only when the UI asks for statistics, never on the measured path.
/// </summary>
internal static class DevToolPerformanceMonitor
{
    internal readonly struct Scope : IDisposable
    {
        private readonly DevToolPerformanceMetric metric;
        private readonly long startTimestamp;
        private readonly int generation;

        internal Scope(DevToolPerformanceMetric metric, long startTimestamp, int generation)
        {
            this.metric = metric;
            this.startTimestamp = startTimestamp;
            this.generation = generation;
        }

        public void Dispose()
        {
            if (startTimestamp == 0L)
                return;

            DevToolPerformanceMonitor.Record(metric, startTimestamp, generation);
        }
    }

    private sealed class MetricSeries
    {
        private readonly object sync = new();
        private readonly long[] samples = new long[SampleCapacity];
        private readonly long[] percentileScratch = new long[SampleCapacity];
        private int count;
        private int next;

        internal void Reset()
        {
            lock (sync)
            {
                Array.Clear(samples, 0, samples.Length);
                Array.Clear(percentileScratch, 0, percentileScratch.Length);
                count = 0;
                next = 0;
            }
        }

        internal void Record(long elapsedTicks, int expectedGeneration)
        {
            if (elapsedTicks < 0L)
                elapsedTicks = 0L;

            lock (sync)
            {
                // The first generation check happens before taking this lock to keep the common
                // rejection path cheap. Repeat it here so Reset cannot clear the ring and then have
                // an already-in-flight old scope append a stale sample after the reset boundary.
                if (Volatile.Read(ref enabled) == 0 || expectedGeneration != Volatile.Read(ref generation))
                    return;

                samples[next] = elapsedTicks;
                next++;
                if (next == samples.Length)
                    next = 0;
                if (count < samples.Length)
                    count++;
            }
        }

        internal DevToolPerformanceStats Snapshot()
        {
            lock (sync)
            {
                if (count == 0)
                    return default;

                long sum = 0L;
                long max = 0L;
                for (int i = 0; i < count; i++)
                {
                    long value = samples[i];
                    percentileScratch[i] = value;
                    sum += value;
                    if (value > max)
                        max = value;
                }

                Array.Sort(percentileScratch, 0, count);
                int p95Index = Math.Max(0, Math.Min(count - 1, (int)Math.Ceiling(count * 0.95d) - 1));
                int lastIndex = next == 0 ? samples.Length - 1 : next - 1;
                long last = samples[lastIndex];

                return new DevToolPerformanceStats(
                    count,
                    TicksToMilliseconds(last),
                    TicksToMilliseconds(sum / (double)count),
                    TicksToMilliseconds(percentileScratch[p95Index]),
                    TicksToMilliseconds(max));
            }
        }
    }

    private const int SampleCapacity = 240;
    private static readonly MetricSeries[] Series = CreateSeries();
    private static readonly long[] PresentationCounters =
        new long[(int)DevToolPresentationChannel.Count * (int)DevToolPresentationOutcome.Count];
    private static int enabled;
    private static int generation = 1;

    internal static bool Enabled => Volatile.Read(ref enabled) != 0;
    internal static int RollingSampleCapacity => SampleCapacity;

    /// <summary>
    /// Enables/disables instrumentation. Enabling starts a fresh run; disabling invalidates every
    /// outstanding scope so a late Dispose cannot write into a future run.
    /// </summary>
    internal static void SetEnabled(bool value)
    {
        if (value == Enabled)
            return;

        Volatile.Write(ref enabled, 0);
        Interlocked.Increment(ref generation);

        if (!value)
            return;

        ResetStorage();
        Volatile.Write(ref enabled, 1);
    }

    /// <summary>
    /// Clears the rolling timing window and cache counters without changing whether measurement is
    /// enabled. Reset is initiated by diagnostics UI and is not part of the normal update hot path.
    /// </summary>
    internal static void Reset()
    {
        bool resume = Enabled;
        Volatile.Write(ref enabled, 0);
        Interlocked.Increment(ref generation);
        ResetStorage();
        if (resume)
            Volatile.Write(ref enabled, 1);
    }

    internal static Scope Measure(DevToolPerformanceMetric metric)
    {
        int index = (int)metric;
        if (Volatile.Read(ref enabled) == 0 || index < 0 || index >= Series.Length)
            return default;

        return new Scope(metric, Stopwatch.GetTimestamp(), Volatile.Read(ref generation));
    }

    internal static DevToolPerformanceStats GetStats(DevToolPerformanceMetric metric)
    {
        int index = (int)metric;
        if (index < 0 || index >= Series.Length)
            return default;

        return Series[index].Snapshot();
    }

    internal static void RecordPresentation(
        DevToolPresentationChannel channel,
        DevToolPresentationOutcome outcome)
    {
        if (Volatile.Read(ref enabled) == 0)
            return;

        int channelIndex = (int)channel;
        int outcomeIndex = (int)outcome;
        int outcomes = (int)DevToolPresentationOutcome.Count;
        if (channelIndex < 0 || channelIndex >= (int)DevToolPresentationChannel.Count ||
            outcomeIndex < 0 || outcomeIndex >= outcomes)
            return;

        Interlocked.Increment(ref PresentationCounters[channelIndex * outcomes + outcomeIndex]);
    }

    internal static DevToolPresentationCounters GetPresentationCounters(DevToolPresentationChannel channel)
    {
        int channelIndex = (int)channel;
        if (channelIndex < 0 || channelIndex >= (int)DevToolPresentationChannel.Count)
            return default;

        int outcomes = (int)DevToolPresentationOutcome.Count;
        int offset = channelIndex * outcomes;
        return new DevToolPresentationCounters(
            Volatile.Read(ref PresentationCounters[offset + (int)DevToolPresentationOutcome.CacheHit]),
            Volatile.Read(ref PresentationCounters[offset + (int)DevToolPresentationOutcome.PartialRebuild]),
            Volatile.Read(ref PresentationCounters[offset + (int)DevToolPresentationOutcome.FullRebuild]));
    }

    private static void Record(DevToolPerformanceMetric metric, long startTimestamp, int scopeGeneration)
    {
        if (Volatile.Read(ref enabled) == 0 || scopeGeneration != Volatile.Read(ref generation))
            return;

        int index = (int)metric;
        if (index < 0 || index >= Series.Length)
            return;

        long elapsed = Stopwatch.GetTimestamp() - startTimestamp;
        Series[index].Record(elapsed, scopeGeneration);
    }

    private static MetricSeries[] CreateSeries()
    {
        int count = (int)DevToolPerformanceMetric.Count;
        MetricSeries[] result = new MetricSeries[count];
        for (int i = 0; i < result.Length; i++)
            result[i] = new MetricSeries();
        return result;
    }

    private static void ResetStorage()
    {
        for (int i = 0; i < Series.Length; i++)
            Series[i].Reset();
        for (int i = 0; i < PresentationCounters.Length; i++)
            Interlocked.Exchange(ref PresentationCounters[i], 0L);
    }

    private static double TicksToMilliseconds(long ticks) =>
        ticks * 1000d / Stopwatch.Frequency;

    private static double TicksToMilliseconds(double ticks) =>
        ticks * 1000d / Stopwatch.Frequency;
}
