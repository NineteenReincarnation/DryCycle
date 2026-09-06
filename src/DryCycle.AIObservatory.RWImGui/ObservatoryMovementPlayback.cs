using System;
using DryCycle.Debugging.AI;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.AIObservatory.RWImGui;

// Historical movement playback reconstructed only from detached Motion/State Timeline
// samples. Room transitions break the polyline so coordinates from different rooms are
// never connected as if they shared one physical space.
internal static class ObservatoryMovementPlayback
{
    internal static void Draw(AIDebugPresentationSnapshot snapshot)
    {
        AIDebugPresentationTimeline timeline = snapshot.Timeline ?? AIDebugPresentationTimeline.Empty;
        AIDebugMotionSample[] motion = timeline.Motion ?? Array.Empty<AIDebugMotionSample>();
        AIDebugFastStateSample[] states = timeline.States ?? Array.Empty<AIDebugFastStateSample>();

        if (motion.Length == 0)
        {
            ImGui.TextDisabled(L(snapshot, "No retained movement samples in this viewport.", "当前视口没有保留的移动样本。"));
            return;
        }

        int viewStart = ObservatoryTimelineTools.ViewStart;
        int viewEnd = ObservatoryTimelineTools.ViewEnd;
        if (viewEnd <= viewStart)
        {
            viewStart = timeline.StartTick;
            viewEnd = timeline.EndTick;
        }

        float minX = float.MaxValue;
        float minY = float.MaxValue;
        float maxX = float.MinValue;
        float maxY = float.MinValue;
        int visibleCount = 0;
        for (int i = 0; i < motion.Length; i++)
        {
            AIDebugMotionSample sample = motion[i];
            if (sample.Tick < viewStart || sample.Tick > viewEnd) continue;
            if (sample.X < minX) minX = sample.X;
            if (sample.X > maxX) maxX = sample.X;
            if (sample.Y < minY) minY = sample.Y;
            if (sample.Y > maxY) maxY = sample.Y;
            visibleCount++;
        }

        if (visibleCount == 0)
        {
            ImGui.TextDisabled(L(snapshot, "No movement samples inside the zoomed range.", "缩放后的范围内没有移动样本。"));
            return;
        }

        if (maxX - minX < 20f)
        {
            float mid = (maxX + minX) * 0.5f;
            minX = mid - 10f;
            maxX = mid + 10f;
        }
        if (maxY - minY < 20f)
        {
            float mid = (maxY + minY) * 0.5f;
            minY = mid - 10f;
            maxY = mid + 10f;
        }

        Num.Vector2 avail = ImGui.GetContentRegionAvail();
        float height = Math.Max(90f, Math.Min(150f, avail.Y));
        ImGui.InvisibleButton("##V5MovementPlayback", new Num.Vector2(Math.Max(120f, avail.X), height));
        Num.Vector2 canvasMin = ImGui.GetItemRectMin();
        Num.Vector2 canvasMax = ImGui.GetItemRectMax();
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        uint border = ImGui.GetColorU32(ImGuiCol.Border);
        uint frame = ImGui.GetColorU32(ImGuiCol.FrameBg);
        uint line = ImGui.GetColorU32(ImGuiCol.PlotLines);
        uint current = ImGui.GetColorU32(ImGuiCol.PlotLinesHovered);
        uint transition = ImGui.GetColorU32(ImGuiCol.CheckMark);
        uint text = ImGui.GetColorU32(ImGuiCol.TextDisabled);

        draw.AddRectFilled(canvasMin, canvasMax, frame);
        draw.AddRect(canvasMin, canvasMax, border);

        bool havePrevious = false;
        Num.Vector2 previousPoint = default;
        int previousRoom = int.MinValue;
        int stateIndex = 0;
        AIDebugFastState currentState = default;
        bool haveState = false;

        for (int i = 0; i < motion.Length; i++)
        {
            AIDebugMotionSample sample = motion[i];
            if (sample.Tick < viewStart || sample.Tick > viewEnd) continue;

            while (stateIndex < states.Length && states[stateIndex].Tick <= sample.Tick)
            {
                currentState = states[stateIndex].State;
                haveState = true;
                stateIndex++;
            }

            int room = haveState ? currentState.Room : int.MinValue;
            Num.Vector2 point = WorldToCanvas(sample.X, sample.Y, minX, maxX, minY, maxY, canvasMin, canvasMax);
            if (havePrevious && room == previousRoom)
            {
                draw.AddLine(previousPoint, point, line, 1.5f);
            }
            else if (havePrevious)
            {
                draw.AddCircleFilled(point, 3f, transition);
                draw.AddText(new Num.Vector2(point.X + 4f, point.Y - 7f), text,
                    room == int.MinValue ? "room ?" : "room " + room);
            }

            previousPoint = point;
            previousRoom = room;
            havePrevious = true;
        }

        if (snapshot.CursorMotion.HasValue && snapshot.CursorTick >= viewStart && snapshot.CursorTick <= viewEnd)
        {
            Num.Vector2 p = WorldToCanvas(snapshot.CursorMotion.X, snapshot.CursorMotion.Y,
                minX, maxX, minY, maxY, canvasMin, canvasMax);
            draw.AddCircleFilled(p, 4f, current);
            draw.AddCircle(p, 7f, current, 0, 1.5f);
        }

        if (snapshot.CursorFastState.HasValue)
        {
            AIDebugFastState s = snapshot.CursorFastState.State;
            string label = $"tick {snapshot.CursorTick} · room {s.Room}";
            if (s.DestinationRoom != AIDebugFastState.UnknownToken)
                label += $" · dest {s.DestinationRoom}:{s.DestinationX},{s.DestinationY}";
            draw.AddText(new Num.Vector2(canvasMin.X + 6f, canvasMin.Y + 5f), text, label);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(L(snapshot,
                "Movement playback uses retained recorder coordinates. Room changes break the path; no live game geometry is read here.",
                "移动回放使用记录器保留的坐标。房间变化会断开路径；这里不会读取实时游戏几何。"));
        }
    }

    private static Num.Vector2 WorldToCanvas(
        float x,
        float y,
        float minX,
        float maxX,
        float minY,
        float maxY,
        Num.Vector2 canvasMin,
        Num.Vector2 canvasMax)
    {
        const float pad = 12f;
        float tx = (x - minX) / Math.Max(0.001f, maxX - minX);
        float ty = (y - minY) / Math.Max(0.001f, maxY - minY);
        float px = canvasMin.X + pad + tx * Math.Max(1f, canvasMax.X - canvasMin.X - pad * 2f);
        float py = canvasMax.Y - pad - ty * Math.Max(1f, canvasMax.Y - canvasMin.Y - pad * 2f);
        return new Num.Vector2(px, py);
    }

    private static string L(AIDebugPresentationSnapshot snapshot, string english, string chinese) =>
        snapshot.Language == AIDebugLanguage.Chinese ? chinese : english;
}
