using System;
using DryCycle.Debugging.AI;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.AIObservatory.RWImGui;

internal static class ObservatoryTimeline
{
    internal const float PreferredHeight = 438f;

    internal static void Draw(AIDebugPresentationSnapshot snapshot)
    {
        AIDebugPresentationTimeline timeline = snapshot.Timeline ?? AIDebugPresentationTimeline.Empty;

        ImGui.Text(snapshot.Language == AIDebugLanguage.Chinese ? "时间轴" : "Timeline");
        ImGui.SameLine();
        if (snapshot.Selected != null)
        {
            string pinText;
            if (snapshot.SelectedPinned)
                pinText = snapshot.Language == AIDebugLanguage.Chinese ? "取消固定" : "Unpin";
            else
                pinText = (snapshot.Language == AIDebugLanguage.Chinese ? "固定" : "Pin") +
                          $" {snapshot.PinnedCount}/{AIDebugRecorder.MaxPinnedEntities}";

            bool canToggle = snapshot.SelectedPinned || snapshot.PinnedCount < AIDebugRecorder.MaxPinnedEntities;
            if (canToggle)
            {
                if (ImGui.SmallButton(pinText + "##V5TimelinePin"))
                    AIDebugRecorderControl.RequestTogglePin(snapshot.Selected.Key);
            }
            else
            {
                ImGui.TextDisabled(pinText);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(snapshot.Language == AIDebugLanguage.Chinese
                        ? "最多同时固定 3 个生物。先取消一个固定项。"
                        : "At most 3 creatures can be pinned. Unpin one first.");
            }

            ImGui.SameLine();
            if (snapshot.SelectedPinned)
                ImGui.TextDisabled(snapshot.Language == AIDebugLanguage.Chinese ? "[已固定并持续记录]" : "[PINNED · recording]");
        }

        ImGui.SameLine();
        if (timeline.EndTick > timeline.StartTick)
        {
            float duration = (timeline.EndTick - timeline.StartTick) / 40f;
            ImGui.TextDisabled($"{timeline.StartTick} .. {timeline.EndTick}  ({duration:0.0}s)" +
                               (timeline.Truncated ? "  TRUNCATED" : string.Empty));
        }
        else
        {
            ImGui.TextDisabled(snapshot.Language == AIDebugLanguage.Chinese
                ? "选择并跟踪一个生物后显示记录。"
                : "Select and track a creature to populate the recorder timeline.");
        }

        ObservatoryTrackedCompare.Draw(snapshot);
        ObservatoryAdvancedPanels.Draw(snapshot);
        ObservatoryTimelineTools.DrawControls(snapshot);
        ObservatoryRangeAnalysis.Draw(snapshot);
        ObservatoryTimelineTools.PrepareView(snapshot);

        Num.Vector2 available = ImGui.GetContentRegionAvail();
        float width = Math.Max(120f, available.X);
        float canvasHeight = Math.Max(108f, available.Y);
        ImGui.InvisibleButton("##V5TimelineCanvas", new Num.Vector2(width, canvasHeight));

        Num.Vector2 min = ImGui.GetItemRectMin();
        Num.Vector2 max = ImGui.GetItemRectMax();
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        uint border = ImGui.GetColorU32(ImGuiCol.Border);
        uint text = ImGui.GetColorU32(ImGuiCol.TextDisabled);
        uint plot = ImGui.GetColorU32(ImGuiCol.PlotLines);
        uint plotHover = ImGui.GetColorU32(ImGuiCol.PlotLinesHovered);
        uint frame = ImGui.GetColorU32(ImGuiCol.FrameBg);
        uint header = ImGui.GetColorU32(ImGuiCol.Header);

        draw.AddRectFilled(min, max, frame);
        draw.AddRect(min, max, border);

        if (timeline.EndTick <= timeline.StartTick) return;

        int viewStart = ObservatoryTimelineTools.ViewStart;
        int viewEnd = ObservatoryTimelineTools.ViewEnd;
        if (viewEnd <= viewStart)
        {
            viewStart = timeline.StartTick;
            viewEnd = timeline.EndTick;
        }

        const float labelWidth = 68f;
        float x0 = min.X + labelWidth;
        float x1 = max.X - 6f;
        if (x1 <= x0 + 20f) return;

        float speedTop = min.Y + 18f;
        float speedBottom = min.Y + 66f;
        float modeTop = min.Y + 75f;
        float modeBottom = min.Y + 101f;
        float eventTop = min.Y + 108f;
        float eventBottom = Math.Min(max.Y - 8f, min.Y + 137f);

        draw.AddText(new Num.Vector2(min.X + 6f, speedTop + 12f), text,
            snapshot.Language == AIDebugLanguage.Chinese ? "速度" : "Speed");
        draw.AddText(new Num.Vector2(min.X + 6f, modeTop + 5f), text,
            snapshot.Language == AIDebugLanguage.Chinese ? "模式" : "Mode");
        draw.AddText(new Num.Vector2(min.X + 6f, eventTop + 5f), text,
            snapshot.Language == AIDebugLanguage.Chinese ? "状态" : "State");

        DrawTimeGrid(draw, viewStart, viewEnd, x0, x1, min.Y, max.Y, border, text);
        DrawSpeed(draw, timeline, viewStart, viewEnd, x0, x1, speedTop, speedBottom, plot);
        DrawStates(draw, timeline, viewStart, viewEnd, x0, x1, modeTop, modeBottom, eventTop, eventBottom, header, plotHover, text);
        ObservatoryTimelineTools.DrawMarkers(snapshot, draw, x0, x1, min.Y + 1f, max.Y - 1f);
        ObservatoryRangeAnalysis.DrawBoundaries(snapshot, draw, x0, x1, min.Y + 1f, max.Y - 1f);

        if (snapshot.CursorTick >= viewStart && snapshot.CursorTick <= viewEnd)
        {
            float cursorX = TickToX(snapshot.CursorTick, viewStart, viewEnd, x0, x1);
            draw.AddLine(new Num.Vector2(cursorX, min.Y + 2f), new Num.Vector2(cursorX, max.Y - 2f), plotHover, 2f);
        }

        ObservatoryTimelineTools.HandleMouseWheel(snapshot);
        if (ImGui.IsItemHovered())
        {
            Num.Vector2 mouse = ImGui.GetIO().MousePos;
            int hoverTick = XToTick(mouse.X, viewStart, viewEnd, x0, x1);
            float relativeSeconds = (hoverTick - snapshot.Tick) / 40f;
            ImGui.SetTooltip($"tick {hoverTick}\n{relativeSeconds:+0.00;-0.00;0.00}s\n" +
                             (snapshot.Language == AIDebugLanguage.Chinese
                                 ? "单击移动游标 · Shift+单击选择分析范围 · 滚轮缩放"
                                 : "click: cursor · Shift-click: range · wheel: zoom"));

            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                if (ImGui.GetIO().KeyShift)
                {
                    ObservatoryRangeAnalysis.HandleShiftClick(hoverTick);
                }
                else
                {
                    int delta = hoverTick - snapshot.CursorTick;
                    if (delta != 0)
                        AIDebugPresentationHub.Enqueue(AIDebugUiCommand.SetCursor(hoverTick));
                    else if (hoverTick >= snapshot.Tick)
                        AIDebugPresentationHub.Enqueue(AIDebugUiCommand.Simple(AIDebugUiCommandKind.ReturnLive));
                }
            }
        }
    }

    private static void DrawTimeGrid(
        ImDrawListPtr draw,
        int startTick,
        int endTick,
        float x0,
        float x1,
        float y0,
        float y1,
        uint color,
        uint textColor)
    {
        if (endTick <= startTick) return;
        int span = endTick - startTick;
        int step = span <= 160 ? 20 : span <= 400 ? 40 : span <= 1200 ? 80 : 200;
        int first = ((startTick + step - 1) / step) * step;
        for (int tick = first; tick <= endTick; tick += step)
        {
            float x = TickToX(tick, startTick, endTick, x0, x1);
            draw.AddLine(new Num.Vector2(x, y0 + 1f), new Num.Vector2(x, y1 - 1f), color, 1f);
            draw.AddText(new Num.Vector2(x + 2f, y0 + 2f), textColor, $"{tick / 40f:0.0}");
        }
    }

    private static void DrawSpeed(
        ImDrawListPtr draw,
        AIDebugPresentationTimeline timeline,
        int viewStart,
        int viewEnd,
        float x0,
        float x1,
        float y0,
        float y1,
        uint color)
    {
        AIDebugMotionSample[] motion = timeline.Motion;
        if (motion == null || motion.Length == 0) return;

        double maxSpeed = 0.01;
        for (int i = 0; i < motion.Length; i++)
        {
            if (motion[i].Tick < viewStart || motion[i].Tick > viewEnd) continue;
            double speed = Math.Sqrt(motion[i].VX * motion[i].VX + motion[i].VY * motion[i].VY);
            if (speed > maxSpeed) maxSpeed = speed;
        }

        bool havePrevious = false;
        Num.Vector2 previous = default;
        for (int i = 0; i < motion.Length; i++)
        {
            AIDebugMotionSample sample = motion[i];
            if (sample.Tick < viewStart || sample.Tick > viewEnd) continue;
            float x = TickToX(sample.Tick, viewStart, viewEnd, x0, x1);
            double speed = Math.Sqrt(sample.VX * sample.VX + sample.VY * sample.VY);
            float normalized = (float)(speed / maxSpeed);
            float y = y1 - normalized * (y1 - y0);
            Num.Vector2 current = new Num.Vector2(x, y);
            if (havePrevious) draw.AddLine(previous, current, color, 1.5f);
            previous = current;
            havePrevious = true;
        }
    }

    private static void DrawStates(
        ImDrawListPtr draw,
        AIDebugPresentationTimeline timeline,
        int viewStart,
        int viewEnd,
        float x0,
        float x1,
        float modeTop,
        float modeBottom,
        float eventTop,
        float eventBottom,
        uint stateColor,
        uint eventColor,
        uint textColor)
    {
        AIDebugFastStateSample[] states = timeline.States;
        if (states == null || states.Length == 0) return;

        for (int i = 0; i < states.Length; i++)
        {
            AIDebugFastStateSample sample = states[i];
            int segmentStartTick = Math.Max(viewStart, sample.Tick);
            int segmentEndTick = i + 1 < states.Length
                ? Math.Min(viewEnd, states[i + 1].Tick)
                : viewEnd;
            if (segmentEndTick < viewStart || segmentStartTick > viewEnd) continue;

            float sx = TickToX(segmentStartTick, viewStart, viewEnd, x0, x1);
            float ex = TickToX(segmentEndTick, viewStart, viewEnd, x0, x1);
            if (ex < sx + 1f) ex = sx + 1f;
            draw.AddRectFilled(new Num.Vector2(sx, modeTop), new Num.Vector2(ex, modeBottom), stateColor);

            if (sample.State.ModeToken != AIDebugFastState.UnknownToken && ex - sx > 24f)
                draw.AddText(new Num.Vector2(sx + 3f, modeTop + 4f), textColor, sample.State.ModeToken.ToString());

            if (sample.Tick >= viewStart && sample.Tick <= viewEnd)
            {
                float exTick = TickToX(sample.Tick, viewStart, viewEnd, x0, x1);
                draw.AddLine(new Num.Vector2(exTick, eventTop), new Num.Vector2(exTick, eventBottom), eventColor, 2f);
            }
        }
    }

    private static float TickToX(int tick, int startTick, int endTick, float x0, float x1)
    {
        if (endTick <= startTick) return x0;
        float t = (tick - startTick) / (float)(endTick - startTick);
        if (t < 0f) t = 0f;
        if (t > 1f) t = 1f;
        return x0 + (x1 - x0) * t;
    }

    private static int XToTick(float x, int startTick, int endTick, float x0, float x1)
    {
        if (x1 <= x0 || endTick <= startTick) return startTick;
        float t = (x - x0) / (x1 - x0);
        if (t < 0f) t = 0f;
        if (t > 1f) t = 1f;
        return startTick + (int)Math.Round((endTick - startTick) * t);
    }
}
