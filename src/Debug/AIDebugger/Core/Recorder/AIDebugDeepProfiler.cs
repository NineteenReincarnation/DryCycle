using System;
using System.Diagnostics;

namespace DryCycle.Debugging.AI;

internal readonly struct AIDebugProfilerStatus
{
    internal readonly bool Enabled;
    internal readonly int Samples;
    internal readonly double AverageMs;
    internal readonly double P95Ms;
    internal readonly double MaxMs;

    internal AIDebugProfilerStatus(bool enabled, int samples, double averageMs, double p95Ms, double maxMs)
    {
        Enabled = enabled;
        Samples = samples;
        AverageMs = averageMs;
        P95Ms = p95Ms;
        MaxMs = maxMs;
    }
}

// Optional profiling ring. Disabled path is one branch; enabled capture uses Stopwatch
// timestamps and preallocated arrays only, preserving the recorder hot-path allocation
// contract while still exposing average/P95/max engineering data.
internal static class AIDebugDeepProfiler
{
    private const int Capacity = 256;
    private static readonly double[] Samples = new double[Capacity];
    private static readonly double[] Scratch = new double[Capacity];
    private static bool enabled;
    private static int write;
    private static int count;

    internal static bool Enabled => enabled;

    internal static void SetEnabled(bool value)
    {
        enabled = value;
        if (!value) ResetSamples();
    }

    internal static void Toggle() => SetEnabled(!enabled);

    internal static long BeginTick() => enabled ? Stopwatch.GetTimestamp() : 0L;

    internal static void EndTick(long startTimestamp)
    {
        if (!enabled || startTimestamp == 0L) return;
        long elapsed = Stopwatch.GetTimestamp() - startTimestamp;
        double ms = elapsed * 1000.0 / Stopwatch.Frequency;
        Samples[write] = ms;
        write++;
        if (write >= Capacity) write = 0;
        if (count < Capacity) count++;
    }

    internal static AIDebugProfilerStatus GetStatus()
    {
        if (!enabled || count <= 0) return new AIDebugProfilerStatus(enabled, count, 0.0, 0.0, 0.0);

        double sum = 0.0;
        double max = 0.0;
        for (int i = 0; i < count; i++)
        {
            double value = Samples[i];
            Scratch[i] = value;
            sum += value;
            if (value > max) max = value;
        }
        Array.Sort(Scratch, 0, count);
        int p95Index = Math.Max(0, Math.Min(count - 1, (int)Math.Ceiling(count * 0.95) - 1));
        return new AIDebugProfilerStatus(true, count, sum / count, Scratch[p95Index], max);
    }

    internal static void Reset()
    {
        enabled = false;
        ResetSamples();
    }

    private static void ResetSamples()
    {
        write = 0;
        count = 0;
        Array.Clear(Samples, 0, Samples.Length);
    }
}
