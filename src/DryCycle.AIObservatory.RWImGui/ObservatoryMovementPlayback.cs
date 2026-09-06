using System;
using DryCycle.Debugging.AI;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.AIObservatory.RWImGui;

// Historical movement playback reconstructed only from detached recorder samples and the
// immutable room-geometry cache. Present never dereferences Room/AImap/Creature objects.
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

        int cursorRoom = snapshot.CursorFastState.HasValue
            ? snapshot.CursorFastState.State.Room
            : int.MinValue;
        AIDebugRoomGeometrySnapshot geometry = default;
        bool hasGeometry = cursorRoom != int.MinValue &&
                           AIDebugRoomGeometryCache.TryGet(cursorRoom, out geometry);

        float minX;
        float minY;
        float maxX;
        float maxY;
        if (hasGeometry)
        {
            minX = 0f;
            minY = 0f;
            maxX = Math.Max(20f, geometry.Width * 20f);
            maxY = Math.Max(20f, geometry.Height * 20f);
        }
        else
        {
            minX = float.MaxValue;
            minY = float.MaxValue;
            maxX = float.MinValue;
            maxY = float.MinValue;
            int visible = 0;
            for (int i = 0; i < motion.Length; i++)
            {
                AIDebugMotionSample sample = motion[i];
                if (sample.Tick < viewStart || sample.Tick > viewEnd) continue;
                if (RoomAt(states, sample.Tick) != cursorRoom && cursorRoom != int.MinValue) continue;
                if (sample.X < minX) minX = sample.X;
                if (sample.X > maxX) maxX = sample.X;
                if (sample.Y < minY) minY = sample.Y;
                if (sample.Y > maxY) maxY = sample.Y;
                visible++;
            }

            if (visible == 0)
            {
                ImGui.TextDisabled(L(snapshot, "No movement samples inside the zoomed range for this room.", "当前房间在缩放范围内没有移动样本。"));
                return;
            }

            ExpandBounds(ref minX, ref maxX);
            ExpandBounds(ref minY, ref maxY);
        }

        Num.Vector2 avail = ImGui.GetContentRegionAvail();
        float height = Math.Max(104f, Math.Min(176f, avail.Y));
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
        uint solid = ImGui.GetColorU32(ImGuiCol.Button);
        uint ai = ImGui.GetColorU32(ImGuiCol.Header);
        uint shortcut = ImGui.GetColorU32(ImGuiCol.CheckMark);
        uint beam = ImGui.GetColorU32(ImGuiCol.SeparatorHovered);

        draw.AddRectFilled(canvasMin, canvasMax, frame);
        draw.AddRect(canvasMin, canvasMax, border);

        if (hasGeometry)
            DrawGeometry(draw, geometry, minX, maxX, minY, maxY, canvasMin, canvasMax, solid, ai, shortcut, beam);

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
            if (cursorRoom != int.MinValue && room != cursorRoom)
            {
                havePrevious = false;
                previousRoom = room;
                continue;
            }

            Num.Vector2 point = WorldToCanvas(sample.X, sample.Y, minX, maxX, minY, maxY, canvasMin, canvasMax);
            if (havePrevious && room == previousRoom)
            {
                draw.AddLine(previousPoint, point, line, 1.7f);
            }
            else if (havePrevious)
            {
                draw.AddCircleFilled(point, 3f, transition);
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
            string roomLabel = hasGeometry ? geometry.RoomName : ("room " + s.Room);
            string label = $"tick {snapshot.CursorTick} · {roomLabel}";
            if (s.DestinationRoom != AIDebugFastState.UnknownToken)
                label += $" · dest {s.DestinationRoom}:{s.DestinationX},{s.DestinationY}";
            draw.AddText(new Num.Vector2(canvasMin.X + 6f, canvasMin.Y + 5f), text, label);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(hasGeometry
                ? L(snapshot,
                    "Detached room tiles + AIMap are cached on the simulation thread. The trail is historical recorder data.",
                    "房间地形与 AIMap 已在模拟线程缓存；轨迹来自历史记录器数据。")
                : L(snapshot,
                    "Room geometry was not retained for this historical room; playback falls back to recorded coordinates.",
                    "此历史房间没有保留几何缓存；回放退回到记录坐标。"));
        }
    }

    private static void DrawGeometry(
        ImDrawListPtr draw,
        AIDebugRoomGeometrySnapshot geometry,
        float minX,
        float maxX,
        float minY,
        float maxY,
        Num.Vector2 canvasMin,
        Num.Vector2 canvasMax,
        uint solidColor,
        uint aiColor,
        uint shortcutColor,
        uint beamColor)
    {
        if (geometry.Width <= 0 || geometry.Height <= 0) return;
        for (int y = 0; y < geometry.Height; y++)
        {
            for (int x = 0; x < geometry.Width; x++)
            {
                AIDebugRoomTileFlags flags = geometry.Get(x, y);
                if (flags == AIDebugRoomTileFlags.None) continue;

                float wx0 = x * 20f;
                float wy0 = y * 20f;
                float wx1 = wx0 + 20f;
                float wy1 = wy0 + 20f;
                Num.Vector2 a = WorldToCanvas(wx0, wy0, minX, maxX, minY, maxY, canvasMin, canvasMax);
                Num.Vector2 b = WorldToCanvas(wx1, wy1, minX, maxX, minY, maxY, canvasMin, canvasMax);
                float left = Math.Min(a.X, b.X);
                float right = Math.Max(a.X, b.X);
                float top = Math.Min(a.Y, b.Y);
                float bottom = Math.Max(a.Y, b.Y);

                if ((flags & AIDebugRoomTileFlags.Solid) != 0)
                    draw.AddRectFilled(new Num.Vector2(left, top), new Num.Vector2(right, bottom), solidColor);
                else if ((flags & AIDebugRoomTileFlags.AIWalkable) != 0 && right - left > 2f && bottom - top > 2f)
                    draw.AddRect(new Num.Vector2(left + 1f, top + 1f), new Num.Vector2(right - 1f, bottom - 1f), aiColor, 0f, 0, 1f);

                if ((flags & AIDebugRoomTileFlags.Shortcut) != 0)
                    draw.AddCircleFilled(new Num.Vector2((left + right) * 0.5f, (top + bottom) * 0.5f), 2.6f, shortcutColor);
                if ((flags & AIDebugRoomTileFlags.VerticalBeam) != 0)
                    draw.AddLine(new Num.Vector2((left + right) * 0.5f, top), new Num.Vector2((left + right) * 0.5f, bottom), beamColor, 1f);
                if ((flags & AIDebugRoomTileFlags.HorizontalBeam) != 0)
                    draw.AddLine(new Num.Vector2(left, (top + bottom) * 0.5f), new Num.Vector2(right, (top + bottom) * 0.5f), beamColor, 1f);
            }
        }
    }

    private static int RoomAt(AIDebugFastStateSample[] states, int tick)
    {
        int room = int.MinValue;
        for (int i = 0; i < states.Length; i++)
        {
            if (states[i].Tick > tick) break;
            room = states[i].State.Room;
        }
        return room;
    }

    private static void ExpandBounds(ref float min, ref float max)
    {
        if (max - min >= 20f) return;
        float mid = (max + min) * 0.5f;
        min = mid - 10f;
        max = mid + 10f;
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
