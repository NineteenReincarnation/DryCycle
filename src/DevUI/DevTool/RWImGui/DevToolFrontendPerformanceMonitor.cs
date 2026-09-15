using System;
using System.Diagnostics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Stable RWImGui frontend timing channels. These deliberately measure presentation work
/// after the backend immutable snapshots have already been published, so frontend cost can
/// be separated from DevUI/session/presentation production cost.
/// </summary>
internal enum DevToolFrontendPerformanceMetric
{
    FrontendFrameTotal,
    UiModeSwitch,
    FontSettings,
    Overlay,
    SceneWorkspace,
    ScenePlacement,
    ActionToast,
    Count
}

internal readonly struct DevToolFrontendPerformanceStats
{
    internal DevToolFrontendPerformanceStats(
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
/// Allocation-free-on-the-hot-path timing recorder for the RWImGui layer.
///
/// This intentionally lives in the frontend assembly rather than extending the backend
/// performance enum. Phase 6's dependency boundary remains one-way: backend knows nothing
/// about RWImGui, while the frontend can correlate its own draw cost with backend metrics.
/// </summary>
internal static class DevToolFrontendPerformanceMonitor
{
    internal readonly struct Scope : IDisposable
    {
        private readonly DevToolFrontendPerformanceMetric metric;
        private readonly long startTimestamp;
        private readonly int generation;

        internal Scope(DevToolFrontendPerformanceMetric metric, long startTimestamp, int generation)
        {
  this.metric = metric;
  this.startTimestamp = startTimestamp;
  this.generation = generation;
        }

        public void Dispose()
        {
  if (startTimestamp == 0L) return;
  DevToolFrontendPerformanceMonitor.Record(metric, startTimestamp, generation);
        }
    }

    private sealed class MetricSeries
    {
        private readonly long[] samples = new long[SampleCapacity];
        private readonly long[] percentileScratch = new long[SampleCapacity];
        private int count;
        private int next;

        internal void Reset()
        {
  Array.Clear(samples, 0, samples.Length);
  Array.Clear(percentileScratch, 0, percentileScratch.Length);
  count = 0;
  next = 0;
        }

        internal void Record(long elapsedTicks)
        {
  if (elapsedTicks < 0L) elapsedTicks = 0L;
  samples[next] = elapsedTicks;
  next++;
  if (next == samples.Length) next = 0;
  if (count < samples.Length) count++;
        }

        internal DevToolFrontendPerformanceStats Snapshot()
        {
  if (count == 0) return default;

  long sum = 0L;
  long max = 0L;
  for (int i = 0; i < count; i++)
  {
      long value = samples[i];
      percentileScratch[i] = value;
      sum += value;
      if (value > max) max = value;
  }

  Array.Sort(percentileScratch, 0, count);
  int p95Index = Math.Max(0, Math.Min(count - 1, (int)Math.Ceiling(count * 0.95d) - 1));
  int lastIndex = next == 0 ? samples.Length - 1 : next - 1;
  long last = samples[lastIndex];

  return new DevToolFrontendPerformanceStats(
      count,
      TicksToMilliseconds(last),
      TicksToMilliseconds(sum / (double)count),
      TicksToMilliseconds(percentileScratch[p95Index]),
      TicksToMilliseconds(max));
        }
    }

    private const int SampleCapacity = 240;
    private static readonly MetricSeries[] Series = CreateSeries();
    private static bool enabled;
    private static int generation = 1;

    internal static bool Enabled => enabled;
    internal static int RollingSampleCapacity => SampleCapacity;

    internal static void SetEnabled(bool value)
    {
        if (value == enabled) return;

        enabled = false;
        generation++;
        if (!value) return;

        ResetStorage();
        enabled = true;
    }

    internal static void Reset()
    {
        bool resume = enabled;
        enabled = false;
        generation++;
        ResetStorage();
        enabled = resume;
    }

    internal static Scope Measure(DevToolFrontendPerformanceMetric metric)
    {
        int index = (int)metric;
        if (!enabled || index < 0 || index >= Series.Length)
  return default;

        return new Scope(metric, Stopwatch.GetTimestamp(), generation);
    }

    internal static DevToolFrontendPerformanceStats GetStats(DevToolFrontendPerformanceMetric metric)
    {
        int index = (int)metric;
        if (index < 0 || index >= Series.Length)
  return default;

        return Series[index].Snapshot();
    }

    private static void Record(
        DevToolFrontendPerformanceMetric metric,
        long startTimestamp,
        int scopeGeneration)
    {
        if (!enabled || scopeGeneration != generation) return;

        int index = (int)metric;
        if (index < 0 || index >= Series.Length) return;

        Series[index].Record(Stopwatch.GetTimestamp() - startTimestamp);
    }

    private static MetricSeries[] CreateSeries()
    {
        MetricSeries[] result = new MetricSeries[(int)DevToolFrontendPerformanceMetric.Count];
        for (int i = 0; i < result.Length; i++)
  result[i] = new MetricSeries();
        return result;
    }

    private static void ResetStorage()
    {
        for (int i = 0; i < Series.Length; i++)
  Series[i].Reset();
    }

    private static double TicksToMilliseconds(long ticks) =>
        ticks * 1000d / Stopwatch.Frequency;

    private static double TicksToMilliseconds(double ticks) =>
        ticks * 1000d / Stopwatch.Frequency;
}
