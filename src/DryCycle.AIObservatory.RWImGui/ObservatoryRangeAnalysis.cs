using System;
using DryCycle.Debugging.AI;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.AIObservatory.RWImGui;

// Deferred range analysis over the detached Timeline viewport. No work is done until the
// user Shift-clicks two Timeline points; analysis never touches recorder blocks or Rain
// World objects from Present.
internal static class ObservatoryRangeAnalysis
{
    private static bool hasStart;
    private static bool hasEnd;
    private static int startTick;
    private static int endTick;

    internal static bool HasRange => hasStart && hasEnd;

    internal static void HandleShiftClick(int tick)
    {
        if (!hasStart || hasEnd)
        {
            startTick = tick;
            endTick = tick;
            hasStart = true;
            hasEnd = false;
            return;
        }

        endTick = tick;
        hasEnd = true;
    }

    internal static void Draw(AIDebugPresentationSnapshot snapshot)
    {
        AIDebugPresentationTimeline timeline = snapshot.Timeline ?? AIDebugPresentationTimeline.Empty;

        if (!hasStart)
        {
            ImGui.TextDisabled(L(snapshot,
                "Range: Shift-click two Timeline points to analyze a time span.",
                "范围：按住 Shift 在时间轴点击两个位置以分析时间段。"));
            return;
        }

        int a = hasEnd ? Math.Min(startTick, endTick) : startTick;
        int b = hasEnd ? Math.Max(startTick, endTick) : startTick;

        if (!hasEnd)
        {
            ImGui.TextDisabled($"{L(snapshot, "Range start", "范围起点")}: tick {a} · " +
                               L(snapshot, "Shift-click an end point", "按住 Shift 点击终点"));
            ImGui.SameLine();
            if (ImGui.SmallButton(L(snapshot, "Clear##Range", "清除##Range"))) Clear();
            return;
        }

        Analyze(timeline, a, b,
            out int motionSamples,
            out double distance,
            out double averageSpeed,
            out double maxSpeed,
            out int stateRecords,
            out int modeChanges,
            out int targetChanges,
            out int roomChanges);

        float seconds = Math.Max(0, b - a) / 40f;
        ImGui.TextDisabled($"{L(snapshot, "Range", "范围")}: {a}..{b} ({seconds:0.00}s)");
        ImGui.SameLine();
        ImGui.TextUnformatted($"{L(snapshot, "distance", "路程")} {distance:0.0}  " +
                              $"avg v {averageSpeed:0.00}  max v {maxSpeed:0.00}  " +
                              $"{L(snapshot, "states", "状态记录")} {stateRecords}  " +
                              $"{L(snapshot, "mode changes", "模式变化")} {modeChanges}  " +
                              $"{L(snapshot, "target changes", "目标变化")} {targetChanges}  " +
                              $"{L(snapshot, "room changes", "房间变化")} {roomChanges}");
        ImGui.SameLine();
        if (ImGui.SmallButton(L(snapshot, "Clear##Range", "清除##Range"))) Clear();

        if (motionSamples == 0)
            ImGui.TextDisabled(L(snapshot, "No Motion samples fall inside this retained viewport.", "当前保留视口内没有 Motion 样本。"));
    }

    internal static void DrawBoundaries(
        AIDebugPresentationSnapshot snapshot,
        ImDrawListPtr draw,
        float x0,
        float x1,
        float y0,
        float y1)
    {
        if (!hasStart) return;
        int viewStart = ObservatoryTimelineTools.ViewStart;
        int viewEnd = ObservatoryTimelineTools.ViewEnd;
        if (viewEnd <= viewStart) return;

        uint color = ImGui.GetColorU32(ImGuiCol.HeaderHovered);
        int a = hasEnd ? Math.Min(startTick, endTick) : startTick;
        int b = hasEnd ? Math.Max(startTick, endTick) : startTick;

        if (a >= viewStart && a <= viewEnd)
        {
            float x = TickToX(a, viewStart, viewEnd, x0, x1);
            draw.AddLine(new Num.Vector2(x, y0), new Num.Vector2(x, y1), color, 2f);
        }

        if (hasEnd && b >= viewStart && b <= viewEnd)
        {
            float x = TickToX(b, viewStart, viewEnd, x0, x1);
            draw.AddLine(new Num.Vector2(x, y0), new Num.Vector2(x, y1), color, 2f);
        }
    }

    private static void Analyze(
        AIDebugPresentationTimeline timeline,
        int start,
        int end,
        out int motionSamples,
        out double distance,
        out double averageSpeed,
        out double maxSpeed,
        out int stateRecords,
        out int modeChanges,
        out int targetChanges,
        out int roomChanges)
    {
        motionSamples = 0;
        distance = 0.0;
        averageSpeed = 0.0;
        maxSpeed = 0.0;
        stateRecords = 0;
        modeChanges = 0;
        targetChanges = 0;
        roomChanges = 0;

        AIDebugMotionSample[] motion = timeline.Motion ?? Array.Empty<AIDebugMotionSample>();
        bool havePrevious = false;
        AIDebugMotionSample previous = default;
        double speedSum = 0.0;
        for (int i = 0; i < motion.Length; i++)
        {
            AIDebugMotionSample sample = motion[i];
            if (sample.Tick < start || sample.Tick > end) continue;

            double speed = Math.Sqrt(sample.VX * sample.VX + sample.VY * sample.VY);
            speedSum += speed;
            if (speed > maxSpeed) maxSpeed = speed;

            if (havePrevious)
            {
                double dx = sample.X - previous.X;
                double dy = sample.Y - previous.Y;
                distance += Math.Sqrt(dx * dx + dy * dy);
            }

            previous = sample;
            havePrevious = true;
            motionSamples++;
        }

        if (motionSamples > 0) averageSpeed = speedSum / motionSamples;

        AIDebugFastStateSample[] states = timeline.States ?? Array.Empty<AIDebugFastStateSample>();
        bool haveState = false;
        AIDebugFastState last = default;
        for (int i = 0; i < states.Length; i++)
        {
            AIDebugFastStateSample sample = states[i];
            if (sample.Tick < start || sample.Tick > end) continue;
            stateRecords++;

            if (haveState)
            {
                if (sample.State.ModeToken != last.ModeToken) modeChanges++;
                if (sample.State.TargetSpawner != last.TargetSpawner ||
                    sample.State.TargetNumber != last.TargetNumber) targetChanges++;
                if (sample.State.Room != last.Room) roomChanges++;
            }

            last = sample.State;
            haveState = true;
        }
    }

    private static void Clear()
    {
        hasStart = false;
        hasEnd = false;
        startTick = 0;
        endTick = 0;
    }

    private static float TickToX(int tick, int start, int end, float x0, float x1)
    {
        if (end <= start) return x0;
        float t = (tick - start) / (float)(end - start);
        if (t < 0f) t = 0f;
        if (t > 1f) t = 1f;
        return x0 + (x1 - x0) * t;
    }

    private static string L(AIDebugPresentationSnapshot snapshot, string english, string chinese) =>
        snapshot.Language == AIDebugLanguage.Chinese ? chinese : english;
}
