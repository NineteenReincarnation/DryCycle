using System;
using System.Collections.Generic;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Map;
using DryCycle.DevUI.DevTool.Map.PlayerMap;
using ImGuiNET;
using Num = System.Numerics;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// ImGui presentation for WorldSpawnMigrationStream authoring. Geometry is drawn from immutable
/// rebuilt snapshots and edits are queued to PlayerMapMigrationStreamRuntime; the old
/// MapSpawnMigrationStreamControl/BezierSplineControl tree is never constructed.
/// </summary>
internal static class PlayerMapMigrationStreamView
{
    private static bool enabled;
    private static bool streamMode;
    private static string selectedStream = string.Empty;
    private static string region = string.Empty;
    private static int observedStreamCount = -1;
    private static bool selectNewestAfterCreate;

    internal static bool StreamMode => enabled && streamMode;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        logger?.LogInfo("Player Map migration stream ImGui view enabled through direct view calls; no self-detours attached.");
    }

    internal static void Disable()
    {
        streamMode = false;
        selectedStream = string.Empty;
        region = string.Empty;
        observedStreamCount = -1;
        selectNewestAfterCreate = false;
        enabled = false;
    }

    internal static void DrawToolbar(PlayerMapPresentationSnapshot snapshot)
    {
        if (!enabled || snapshot?.Available != true) return;

        PlayerMapMigrationPresentationSnapshot migration = CurrentMigration();
        Normalize(migration);
        ImGui.SameLine(0f, 12f);
        if (DevToolWidgets.ActionButton(
                streamMode ? DevToolUiSettings.T("Streams: 开", "Streams: On") : DevToolUiSettings.T("Streams", "Streams"),
                "PlayerMapMigrationMode",
                streamMode ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
            streamMode = !streamMode;

        if (!streamMode) return;
        ImGui.SameLine(0f, 5f);
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("新建 Stream", "New Stream"),
                "PlayerMapMigrationCreate",
                DevToolButtonTone.Subtle))
        {
            Vector2 center = CreationCenter(snapshot);
            PlayerMapMigrationCommandQueue.Enqueue(new PlayerMapMigrationCommand(
                PlayerMapMigrationCommandKind.Create,
                vector: center));
            selectNewestAfterCreate = true;
        }
    }

    internal static void DrawOverlay(
        ImDrawListPtr draw,
        PlayerMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        PlayerMapRoomSnapshot hovered)
    {
        if (!enabled || !streamMode || snapshot?.Available != true)
            return;

        Num.Vector2 pan = PlayerMapWorkspaceView.Pan;
        float zoom = Math.Max(0.01f, PlayerMapWorkspaceView.Zoom);

        PlayerMapMigrationPresentationSnapshot migration = CurrentMigration();
        Normalize(migration);
        PlayerMapMigrationStreamSnapshot[] streams = migration.Streams ?? Array.Empty<PlayerMapMigrationStreamSnapshot>();
        for (int i = 0; i < streams.Length; i++)
        {
            PlayerMapMigrationStreamSnapshot stream = streams[i];
            if (stream == null) continue;
            bool selected = string.Equals(stream.Name, selectedStream, StringComparison.OrdinalIgnoreCase);
            DrawStream(draw, stream, canvasMin, pan, zoom, selected);
        }
    }

    internal static void DrawInspector(PlayerMapPresentationSnapshot snapshot)
    {
        if (!enabled || !streamMode || snapshot?.Available != true) return;

        PlayerMapMigrationPresentationSnapshot migration = CurrentMigration();
        Normalize(migration);
        ImGui.Separator();
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("迁移 Stream", "MIGRATION STREAMS"));

        PlayerMapMigrationStreamSnapshot[] streams = migration.Streams ?? Array.Empty<PlayerMapMigrationStreamSnapshot>();
        if (streams.Length == 0)
        {
            ImGui.TextDisabled(DevToolUiSettings.T("当前区域没有 Migration Stream。", "No migration streams in this region."));
            return;
        }

        for (int i = 0; i < streams.Length; i++)
        {
            PlayerMapMigrationStreamSnapshot stream = streams[i];
            if (stream == null) continue;
            bool selected = string.Equals(selectedStream, stream.Name, StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable(stream.Name, selected)) selectedStream = stream.Name;
        }

        PlayerMapMigrationStreamSnapshot active = FindSelected(streams);
        if (active == null) return;
        ImGui.Separator();
        ImGui.TextUnformatted(active.Name);
        ImGui.TextDisabled(active.Segments.Length + " segment(s) · " + active.Midpoints.Length + " midpoint(s)");

        DrawVectorEditor("Start", "MigrationStart", active.Start, value => QueueVector(active.Name, PlayerMapMigrationCommandKind.SetStart, value));
        DrawVectorEditor("Start Handle", "MigrationStartHandle", active.StartHandle, value => QueueVector(active.Name, PlayerMapMigrationCommandKind.SetStartHandle, value));
        DrawVectorEditor("End", "MigrationEnd", active.End, value => QueueVector(active.Name, PlayerMapMigrationCommandKind.SetEnd, value));
        DrawVectorEditor("End Handle", "MigrationEndHandle", active.EndHandle, value => QueueVector(active.Name, PlayerMapMigrationCommandKind.SetEndHandle, value));

        float width = active.Width;
        if (ImGui.DragFloat("Width##MigrationWidth", ref width, 0.5f, 1f, 200f, "%.1f"))
            PlayerMapMigrationCommandQueue.Enqueue(new PlayerMapMigrationCommand(
                PlayerMapMigrationCommandKind.SetWidth,
                active.Name,
                number: width));

        int rate = active.Rate;
        if (ImGui.DragInt("Rate##MigrationRate", ref rate, 1f, 5, 200))
            PlayerMapMigrationCommandQueue.Enqueue(new PlayerMapMigrationCommand(
                PlayerMapMigrationCommandKind.SetRate,
                active.Name,
                integer: rate));

        ImGui.TextDisabled("Layers");
        bool[] layers = active.Layers ?? new[] { true, true, true };
        for (int layer = 0; layer < PlayerMapCoordinateSystem.LayerCount; layer++)
        {
            if (layer > 0) ImGui.SameLine();
            bool value = layer < layers.Length && layers[layer];
            if (ImGui.Checkbox(MapRoomLayer.Label(layer) + "##MigrationLayer" + layer, ref value))
                PlayerMapMigrationCommandQueue.Enqueue(new PlayerMapMigrationCommand(
                    PlayerMapMigrationCommandKind.SetLayerEnabled,
                    active.Name,
                    integer: layer,
                    flag: value));
        }

        DrawNextStreamCombo(active, streams);
        DrawDestinationEditor(active);

        if (active.Midpoints.Length > 0)
        {
            ImGui.TextWrapped(DevToolUiSettings.T(
                "现有 midpoint 已完整保留并参与曲线绘制/保存。直接编辑、Split/Join 会在下一步接入同一命令层。",
                "Existing midpoints are fully preserved and used for drawing/saving. Direct midpoint editing and Split/Join will be added on this same command layer."));
        }
    }

    private static void DrawVectorEditor(string label, string id, Vector2 current, Action<Vector2> changed)
    {
        Num.Vector2 value = new(current.x, current.y);
        if (!ImGui.DragFloat2(label + "##" + id, ref value, 0.5f, float.MinValue, float.MaxValue, "%.1f")) return;
        changed?.Invoke(new Vector2(value.X, value.Y));
    }

    private static void DrawNextStreamCombo(
        PlayerMapMigrationStreamSnapshot active,
        PlayerMapMigrationStreamSnapshot[] streams)
    {
        string current = string.IsNullOrWhiteSpace(active.NextStreamName) ? "None" : active.NextStreamName;
        if (!ImGui.BeginCombo("Next Stream##MigrationNext", current)) return;
        if (ImGui.Selectable("None", string.IsNullOrWhiteSpace(active.NextStreamName)))
            QueueText(active.Name, PlayerMapMigrationCommandKind.SetNextStream, string.Empty);
        for (int i = 0; i < streams.Length; i++)
        {
            PlayerMapMigrationStreamSnapshot item = streams[i];
            if (item == null || string.Equals(item.Name, active.Name, StringComparison.OrdinalIgnoreCase)) continue;
            bool selected = string.Equals(item.Name, active.NextStreamName, StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable(item.Name, selected))
                QueueText(active.Name, PlayerMapMigrationCommandKind.SetNextStream, item.Name);
        }
        ImGui.EndCombo();
    }

    private static void DrawDestinationEditor(PlayerMapMigrationStreamSnapshot active)
    {
        string destination = active.DestinationRoom ?? string.Empty;
        if (ImGui.InputText("Warp Dest##MigrationDest", ref destination, 64))
            QueueText(active.Name, PlayerMapMigrationCommandKind.SetDestinationRoom, destination);
        ImGui.TextDisabled(DevToolUiSettings.T("留空 = None", "Empty = None"));
    }

    private static void DrawStream(
        ImDrawListPtr draw,
        PlayerMapMigrationStreamSnapshot stream,
        Num.Vector2 canvasMin,
        Num.Vector2 pan,
        float zoom,
        bool selected)
    {
        uint color = ImGui.GetColorU32(selected ? ImGuiCol.HeaderActive : ImGuiCol.TextDisabled);
        PlayerMapMigrationBezierSnapshot[] segments = stream.Segments ?? Array.Empty<PlayerMapMigrationBezierSnapshot>();
        for (int i = 0; i < segments.Length; i++)
        {
            PlayerMapMigrationBezierSnapshot segment = segments[i];
            Num.Vector2 previous = ToScreen(segment.A, canvasMin, pan, zoom);
            const int samples = 28;
            for (int step = 1; step <= samples; step++)
            {
                float t = (float)step / samples;
                Vector2 point = Cubic(segment.A, segment.HandleA, segment.HandleB, segment.B, t);
                Num.Vector2 next = ToScreen(point, canvasMin, pan, zoom);
                draw.AddLine(previous, next, color, selected ? 2.5f : 1.5f);
                previous = next;
            }
        }

        if (!selected) return;
        uint handleColor = ImGui.GetColorU32(ImGuiCol.PlotHistogram);
        DrawHandlePair(draw, stream.Start, stream.StartHandle, canvasMin, pan, zoom, handleColor);
        DrawHandlePair(draw, stream.End, stream.EndHandle, canvasMin, pan, zoom, handleColor);
        PlayerMapMigrationMidpointSnapshot[] mids = stream.Midpoints ?? Array.Empty<PlayerMapMigrationMidpointSnapshot>();
        for (int i = 0; i < mids.Length; i++)
        {
            Num.Vector2 p = ToScreen(mids[i].Position, canvasMin, pan, zoom);
            draw.AddCircleFilled(p, 4.5f, handleColor);
        }
    }

    private static void DrawHandlePair(
        ImDrawListPtr draw,
        Vector2 point,
        Vector2 handle,
        Num.Vector2 canvasMin,
        Num.Vector2 pan,
        float zoom,
        uint color)
    {
        Num.Vector2 p = ToScreen(point, canvasMin, pan, zoom);
        Num.Vector2 h = ToScreen(handle, canvasMin, pan, zoom);
        draw.AddLine(p, h, color, 1f);
        draw.AddCircleFilled(p, 5f, color);
        draw.AddCircle(h, 4f, color, 0, 1.5f);
    }

    private static Vector2 Cubic(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float u = 1f - t;
        float uu = u * u;
        float tt = t * t;
        return p0 * (uu * u) + p1 * (3f * uu * t) + p2 * (3f * u * tt) + p3 * (tt * t);
    }

    private static Num.Vector2 ToScreen(Vector2 canon, Num.Vector2 canvasMin, Num.Vector2 pan, float zoom) =>
        canvasMin + pan + new Num.Vector2(canon.x, canon.y) * zoom;

    private static Vector2 CreationCenter(PlayerMapPresentationSnapshot player)
    {
        PlayerMapRoomSnapshot[] rooms = player?.Rooms ?? Array.Empty<PlayerMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
            if (rooms[i]?.Selected == true && !rooms[i].Disabled) return rooms[i].EffectivePosition;
        for (int i = 0; i < rooms.Length; i++)
            if (rooms[i] != null && !rooms[i].Disabled) return rooms[i].EffectivePosition;
        return Vector2.zero;
    }

    private static PlayerMapMigrationPresentationSnapshot CurrentMigration() =>
        PlayerMapMigrationStreamRuntime.GetPresentation(DevToolSessionHub.Current);

    private static void Normalize(PlayerMapMigrationPresentationSnapshot snapshot)
    {
        string nextRegion = snapshot?.RegionName ?? string.Empty;
        if (!string.Equals(region, nextRegion, StringComparison.OrdinalIgnoreCase))
        {
            region = nextRegion;
            selectedStream = string.Empty;
            observedStreamCount = -1;
            selectNewestAfterCreate = false;
        }

        PlayerMapMigrationStreamSnapshot[] streams = snapshot?.Streams ?? Array.Empty<PlayerMapMigrationStreamSnapshot>();
        if (selectNewestAfterCreate && streams.Length > observedStreamCount)
        {
            selectedStream = streams.Length > 0 ? streams[streams.Length - 1]?.Name ?? string.Empty : string.Empty;
            selectNewestAfterCreate = false;
        }
        observedStreamCount = streams.Length;

        if (FindSelected(streams) != null) return;
        selectedStream = streams.Length > 0 ? streams[0]?.Name ?? string.Empty : string.Empty;
    }

    private static PlayerMapMigrationStreamSnapshot FindSelected(PlayerMapMigrationStreamSnapshot[] streams)
    {
        streams ??= Array.Empty<PlayerMapMigrationStreamSnapshot>();
        for (int i = 0; i < streams.Length; i++)
            if (streams[i] != null && string.Equals(streams[i].Name, selectedStream, StringComparison.OrdinalIgnoreCase))
                return streams[i];
        return null;
    }

    private static void QueueVector(string name, PlayerMapMigrationCommandKind kind, Vector2 value) =>
        PlayerMapMigrationCommandQueue.Enqueue(new PlayerMapMigrationCommand(kind, name, vector: value));

    private static void QueueText(string name, PlayerMapMigrationCommandKind kind, string value) =>
        PlayerMapMigrationCommandQueue.Enqueue(new PlayerMapMigrationCommand(kind, name, text: value));

}
