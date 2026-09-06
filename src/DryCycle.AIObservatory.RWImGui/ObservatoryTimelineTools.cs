using System;
using System.Collections.Generic;
using DryCycle.Debugging.AI;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.AIObservatory.RWImGui;

// Present-thread-only navigation state for the detached Timeline viewport. This helper
// never reads Rain World objects or recorder blocks; every jump is sent back to the
// Unity/main-thread host through AIDebugPresentationHub.
internal static class ObservatoryTimelineTools
{
    private sealed class Marker
    {
        internal int Tick;
        internal string Label;
    }

    private static readonly List<Marker> Markers = new(64);
    private static string search = string.Empty;
    private static string markerLabel = string.Empty;
    private static float zoom = 1f;
    private static float pan;
    private static int lastTimelineStart = int.MinValue;
    private static int lastTimelineEnd = int.MinValue;

    internal static int ViewStart { get; private set; }
    internal static int ViewEnd { get; private set; }

    internal static void PrepareView(AIDebugPresentationSnapshot snapshot)
    {
        AIDebugPresentationTimeline timeline = snapshot.Timeline ?? AIDebugPresentationTimeline.Empty;
        if (timeline.EndTick <= timeline.StartTick)
        {
            ViewStart = timeline.StartTick;
            ViewEnd = timeline.EndTick;
            return;
        }

        if (timeline.StartTick != lastTimelineStart || timeline.EndTick != lastTimelineEnd)
        {
            // Follow Live windows unless the user has explicitly zoomed/panned. Historical
            // windows still retain their local view state while the cursor is moved.
            if (snapshot.ViewMode == AIDebugViewMode.Live && zoom <= 1.001f && Math.Abs(pan) < 0.001f)
                pan = 0f;
            lastTimelineStart = timeline.StartTick;
            lastTimelineEnd = timeline.EndTick;
        }

        float full = timeline.EndTick - timeline.StartTick;
        float visible = Math.Max(20f, full / zoom);
        if (visible > full) visible = full;
        float maxPan = Math.Max(0f, (full - visible) * 0.5f);
        float panTicks = maxPan * pan;
        float center = snapshot.CursorTick + panTicks;
        float start = center - visible * 0.5f;
        float end = center + visible * 0.5f;

        if (start < timeline.StartTick)
        {
            end += timeline.StartTick - start;
            start = timeline.StartTick;
        }
        if (end > timeline.EndTick)
        {
            start -= end - timeline.EndTick;
            end = timeline.EndTick;
        }
        if (start < timeline.StartTick) start = timeline.StartTick;
        if (end > timeline.EndTick) end = timeline.EndTick;

        ViewStart = (int)Math.Floor(start);
        ViewEnd = (int)Math.Ceiling(end);
        if (ViewEnd <= ViewStart) ViewEnd = ViewStart + 1;
    }

    internal static void DrawControls(AIDebugPresentationSnapshot snapshot)
    {
        AIDebugPresentationTimeline timeline = snapshot.Timeline ?? AIDebugPresentationTimeline.Empty;
        PrepareView(snapshot);

        ImGui.PushID("V5TimelineTools");

        if (ImGui.SmallButton(L(snapshot, "Prev change", "上一变化"))) JumpChange(snapshot, -1);
        ImGui.SameLine();
        if (ImGui.SmallButton(L(snapshot, "Next change", "下一变化"))) JumpChange(snapshot, +1);
        ImGui.SameLine();
        if (ImGui.SmallButton("Zoom +"))
        {
            zoom = Math.Min(12f, zoom * 1.5f);
            PrepareView(snapshot);
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Zoom -"))
        {
            zoom = Math.Max(1f, zoom / 1.5f);
            if (zoom <= 1.001f) pan = 0f;
            PrepareView(snapshot);
        }
        ImGui.SameLine();
        if (ImGui.SmallButton(L(snapshot, "Pan <", "左移")))
        {
            pan = Math.Max(-1f, pan - 0.25f);
            PrepareView(snapshot);
        }
        ImGui.SameLine();
        if (ImGui.SmallButton(L(snapshot, "Pan >", "右移")))
        {
            pan = Math.Min(1f, pan + 0.25f);
            PrepareView(snapshot);
        }
        ImGui.SameLine();
        if (ImGui.SmallButton(L(snapshot, "Fit", "适配")))
        {
            zoom = 1f;
            pan = 0f;
            PrepareView(snapshot);
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"view {ViewStart}..{ViewEnd}  x{zoom:0.0}");

        ImGui.SetNextItemWidth(180f);
        ImGui.InputTextWithHint("##TimelineSearch", L(snapshot, "Search mode/target/state/marker", "搜索模式/目标/状态/标记"), ref search, 96);
        ImGui.SameLine();
        if (ImGui.SmallButton(L(snapshot, "Find prev", "向前找"))) JumpSearch(snapshot, -1);
        ImGui.SameLine();
        if (ImGui.SmallButton(L(snapshot, "Find next", "向后找"))) JumpSearch(snapshot, +1);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(130f);
        ImGui.InputTextWithHint("##MarkerLabel", L(snapshot, "marker label", "标记名称"), ref markerLabel, 48);
        ImGui.SameLine();
        if (ImGui.SmallButton(L(snapshot, "Add marker", "添加标记")))
        {
            string label = string.IsNullOrWhiteSpace(markerLabel)
                ? $"M{Markers.Count + 1}"
                : markerLabel.Trim();
            Markers.Add(new Marker { Tick = snapshot.CursorTick, Label = label });
            markerLabel = string.Empty;
        }
        ImGui.SameLine();
        if (Markers.Count > 0 && ImGui.SmallButton(L(snapshot, "Clear markers", "清空标记"))) Markers.Clear();

        if (timeline.EndTick > timeline.StartTick && Markers.Count > 0)
        {
            int visible = 0;
            for (int i = 0; i < Markers.Count; i++)
                if (Markers[i].Tick >= timeline.OldestRetainedTick && Markers[i].Tick <= timeline.NewestRetainedTick)
                    visible++;
            ImGui.SameLine();
            ImGui.TextDisabled($"markers {visible}/{Markers.Count}");
        }

        ImGui.PopID();
    }

    internal static void DrawMarkers(
        AIDebugPresentationSnapshot snapshot,
        ImDrawListPtr draw,
        float x0,
        float x1,
        float y0,
        float y1)
    {
        if (Markers.Count == 0 || ViewEnd <= ViewStart) return;
        uint color = ImGui.GetColorU32(ImGuiCol.CheckMark);
        uint text = ImGui.GetColorU32(ImGuiCol.Text);

        for (int i = 0; i < Markers.Count; i++)
        {
            Marker marker = Markers[i];
            if (marker.Tick < ViewStart || marker.Tick > ViewEnd) continue;
            float x = TickToX(marker.Tick, ViewStart, ViewEnd, x0, x1);
            draw.AddLine(new Num.Vector2(x, y0), new Num.Vector2(x, y1), color, 1.5f);
            draw.AddText(new Num.Vector2(x + 3f, y0 + 3f), text, marker.Label ?? "M");
        }
    }

    internal static void HandleMouseWheel(AIDebugPresentationSnapshot snapshot)
    {
        if (!ImGui.IsItemHovered()) return;
        float wheel = ImGui.GetIO().MouseWheel;
        if (Math.Abs(wheel) < 0.01f) return;
        if (wheel > 0f) zoom = Math.Min(12f, zoom * 1.25f);
        else
        {
            zoom = Math.Max(1f, zoom / 1.25f);
            if (zoom <= 1.001f) pan = 0f;
        }
        PrepareView(snapshot);
    }

    private static void JumpChange(AIDebugPresentationSnapshot snapshot, int direction)
    {
        AIDebugFastStateSample[] states = snapshot.Timeline?.States ?? Array.Empty<AIDebugFastStateSample>();
        if (states.Length == 0) return;

        if (direction < 0)
        {
            for (int i = states.Length - 1; i >= 0; i--)
            {
                if (states[i].Tick >= snapshot.CursorTick) continue;
                AIDebugPresentationHub.Enqueue(AIDebugUiCommand.SetCursor(states[i].Tick));
                return;
            }
        }
        else
        {
            for (int i = 0; i < states.Length; i++)
            {
                if (states[i].Tick <= snapshot.CursorTick) continue;
                AIDebugPresentationHub.Enqueue(AIDebugUiCommand.SetCursor(states[i].Tick));
                return;
            }
        }
    }

    private static void JumpSearch(AIDebugPresentationSnapshot snapshot, int direction)
    {
        string query = search?.Trim();
        if (string.IsNullOrEmpty(query))
        {
            JumpChange(snapshot, direction);
            return;
        }

        AIDebugFastStateSample[] states = snapshot.Timeline?.States ?? Array.Empty<AIDebugFastStateSample>();
        if (direction < 0)
        {
            for (int i = states.Length - 1; i >= 0; i--)
            {
                if (states[i].Tick >= snapshot.CursorTick || !Matches(states[i], query)) continue;
                AIDebugPresentationHub.Enqueue(AIDebugUiCommand.SetCursor(states[i].Tick));
                return;
            }
            for (int i = Markers.Count - 1; i >= 0; i--)
            {
                Marker marker = Markers[i];
                if (marker.Tick < snapshot.CursorTick && Contains(marker.Label, query))
                {
                    AIDebugPresentationHub.Enqueue(AIDebugUiCommand.SetCursor(marker.Tick));
                    return;
                }
            }
        }
        else
        {
            for (int i = 0; i < states.Length; i++)
            {
                if (states[i].Tick <= snapshot.CursorTick || !Matches(states[i], query)) continue;
                AIDebugPresentationHub.Enqueue(AIDebugUiCommand.SetCursor(states[i].Tick));
                return;
            }
            for (int i = 0; i < Markers.Count; i++)
            {
                Marker marker = Markers[i];
                if (marker.Tick > snapshot.CursorTick && Contains(marker.Label, query))
                {
                    AIDebugPresentationHub.Enqueue(AIDebugUiCommand.SetCursor(marker.Tick));
                    return;
                }
            }
        }
    }

    private static bool Matches(AIDebugFastStateSample sample, string query)
    {
        AIDebugFastState s = sample.State;
        if (Contains(s.EntityState.ToString(), query) || Contains(s.Flags.ToString(), query)) return true;
        if (s.ModeToken != AIDebugFastState.UnknownToken && Contains("mode:" + s.ModeToken, query)) return true;
        if (s.TargetNumber != AIDebugFastState.UnknownToken &&
            (Contains("target:" + s.TargetSpawner + ":" + s.TargetNumber, query) || Contains(s.TargetNumber.ToString(), query))) return true;
        if (Contains("room:" + s.Room, query)) return true;
        return false;
    }

    private static bool Contains(string value, string query) =>
        !string.IsNullOrEmpty(value) && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

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
