using System;

namespace DryCycle.Debugging.AI;

// Main-thread viewport builder. It reads only V5 recorder value tracks and emits a small,
// detached presentation window. Full retained history never crosses into RWImGUI.
internal static class AIDebugTimelinePresentationBuilder
{
    private const int WindowTicks = 600; // 15 seconds at 40 Hz.
    private const int HalfHistoricalWindowTicks = WindowTicks / 2;
    private const int MotionScratchCapacity = WindowTicks + 8;
    private const int StateScratchCapacity = 256;
    private const int MotionPresentationBudget = 256;

    private static readonly AIDebugMotionSample[] MotionScratch = new AIDebugMotionSample[MotionScratchCapacity];
    private static readonly AIDebugFastStateSample[] StateScratch = new AIDebugFastStateSample[StateScratchCapacity];

    internal static AIDebugPresentationTimeline Build(
        DebugEntityKey key,
        int gameTick,
        int cursorTick,
        AIDebugViewMode viewMode)
    {
        if (!AIDebugRecorderReadApi.TryGetRetainedTickRange(key, out int oldest, out int newest))
            return AIDebugPresentationTimeline.Empty;

        int end;
        int start;
        if (viewMode == AIDebugViewMode.Historical)
        {
            start = cursorTick - HalfHistoricalWindowTicks;
            end = cursorTick + HalfHistoricalWindowTicks;
            if (start < oldest)
            {
                end += oldest - start;
                start = oldest;
            }
            if (end > gameTick)
            {
                start -= end - gameTick;
                end = gameTick;
            }
        }
        else
        {
            end = gameTick;
            start = end - WindowTicks;
        }

        if (start < oldest) start = oldest;
        if (end > newest) end = newest;
        if (end < start) end = start;

        int motionCount = AIDebugRecorderReadApi.CopyMotionRange(
            key, start, end, MotionScratch, 0);

        int stateOffset = 0;
        if (AIDebugRecorderReadApi.TryResolveFastState(key, start, out AIDebugResolvedFastState prefix) &&
            prefix.HasValue && prefix.Tick < start && StateScratchCapacity > 0)
        {
            StateScratch[0] = new AIDebugFastStateSample(prefix.Tick, prefix.Sequence, prefix.State);
            stateOffset = 1;
        }

        int stateCount = stateOffset + AIDebugRecorderReadApi.CopyFastStateRange(
            key,
            start,
            end,
            StateScratch,
            stateOffset);

        AIDebugMotionSample[] motion = BuildMotionLod(MotionScratch, motionCount);
        var states = new AIDebugFastStateSample[stateCount];
        if (stateCount > 0) Array.Copy(StateScratch, states, stateCount);

        bool truncated = motionCount >= MotionScratchCapacity || stateCount >= StateScratchCapacity;
        return new AIDebugPresentationTimeline(start, end, oldest, newest, motion, states, truncated);
    }

    private static AIDebugMotionSample[] BuildMotionLod(AIDebugMotionSample[] source, int count)
    {
        if (count <= 0) return Array.Empty<AIDebugMotionSample>();
        if (count <= MotionPresentationBudget)
        {
            var direct = new AIDebugMotionSample[count];
            Array.Copy(source, direct, count);
            return direct;
        }

        // Two extrema per bucket preserve short speed spikes better than taking every Nth
        // point. Extrema are emitted in tick order so DrawList can connect them directly.
        int bucketCount = MotionPresentationBudget / 2;
        var output = new AIDebugMotionSample[MotionPresentationBudget];
        int written = 0;

        for (int bucket = 0; bucket < bucketCount && written < output.Length; bucket++)
        {
            int begin = bucket * count / bucketCount;
            int end = (bucket + 1) * count / bucketCount;
            if (end <= begin) end = begin + 1;
            if (end > count) end = count;

            int minIndex = begin;
            int maxIndex = begin;
            float minSpeed = SpeedSquared(source[begin]);
            float maxSpeed = minSpeed;
            for (int i = begin + 1; i < end; i++)
            {
                float speed = SpeedSquared(source[i]);
                if (speed < minSpeed)
                {
                    minSpeed = speed;
                    minIndex = i;
                }
                if (speed > maxSpeed)
                {
                    maxSpeed = speed;
                    maxIndex = i;
                }
            }

            if (minIndex == maxIndex)
            {
                output[written++] = source[minIndex];
                continue;
            }

            if (minIndex < maxIndex)
            {
                output[written++] = source[minIndex];
                if (written < output.Length) output[written++] = source[maxIndex];
            }
            else
            {
                output[written++] = source[maxIndex];
                if (written < output.Length) output[written++] = source[minIndex];
            }
        }

        if (written == output.Length) return output;
        var trimmed = new AIDebugMotionSample[written];
        Array.Copy(output, trimmed, written);
        return trimmed;
    }

    private static float SpeedSquared(AIDebugMotionSample sample) =>
        sample.VX * sample.VX + sample.VY * sample.VY;
}
