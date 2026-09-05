using System;
using System.Diagnostics;

namespace DryCycle.Debugging.AI;

internal enum AIDebugProfileCategory
{
    Capture,
    UI,
    Overlay,
    Timeline,
    Utility,
    Perception,
    AImap
}

internal static class AIDebugProfiler
{
    private static readonly double[] Smoothed = new double[Enum.GetValues(typeof(AIDebugProfileCategory)).Length];

    internal readonly struct Scope : IDisposable
    {
        private readonly AIDebugProfileCategory category;
        private readonly long start;

        internal Scope(AIDebugProfileCategory category)
        {
            this.category = category;
            start = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            long elapsed = Stopwatch.GetTimestamp() - start;
            double ms = elapsed * 1000.0 / Stopwatch.Frequency;
            int index = (int)category;
            double previous = Smoothed[index];
            Smoothed[index] = previous <= 0.0 ? ms : previous * 0.88 + ms * 0.12;
        }
    }

    internal static Scope Begin(AIDebugProfileCategory category) => new(category);

    internal static double Get(AIDebugProfileCategory category) => Smoothed[(int)category];

    internal static void Reset()
    {
        Array.Clear(Smoothed, 0, Smoothed.Length);
    }
}
