using System;
using System.Collections.Generic;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Map.PlayerMap;
using ImGuiNET;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Exact multi-room alignment/distribution tools. Unlike drag/nudge commands these operations submit
/// independent target positions through EnqueueExact, deliberately bypassing shared-delta snapping.
/// All selected rooms must have a ready bake so edge/gap math uses their real Player Map rectangles.
/// </summary>
internal static class PlayerMapGroupLayoutControls
{
    private enum LayoutOperation
    {
        AlignLeft,
        AlignCenterX,
        AlignRight,
        AlignBottom,
        AlignCenterY,
        AlignTop,
        SpaceX,
        SpaceY
    }

    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = PlayerMapSelectionAccess.Available;
        if (enabled)
            logger?.LogInfo("Player Map grouped align/distribute controls enabled through direct inspector calls; no self-detour attached.");
        else
            logger?.LogWarning("Player Map grouped layout controls disabled because selection adapter is unavailable.");
    }

    internal static void Disable() => enabled = false;

    internal static void Draw(PlayerMapPresentationSnapshot snapshot)
    {
        if (!enabled || snapshot?.Available != true) return;

        List<PlayerMapRoomSnapshot> selected = PlayerMapSelectionAccess.Collect(snapshot);
        if (selected.Count < 2) return;

        ImGui.Separator();
        DevToolWidgets.SectionHeader(DevToolUiSettings.T("多选布局", "MULTI LAYOUT"));

        bool ready = true;
        for (int i = 0; i < selected.Count; i++)
        {
            RoomMapBakeSnapshot bake = selected[i].Bake;
            if (bake?.Status == RoomMapBakeStatus.Ready && bake.Width > 0 && bake.Height > 0) continue;
            ready = false;
            break;
        }
        if (!ready)
        {
            ImGui.TextWrapped(DevToolUiSettings.T(
                "等待所选房间的 Map Bake 完成后才能进行精确对齐/分布。",
                "Exact align/distribute is available after all selected room bakes are ready."));
            return;
        }

        LayoutButton("Left", "PlayerMapAlignLeft", LayoutOperation.AlignLeft, selected);
        ImGui.SameLine();
        LayoutButton("Center X", "PlayerMapAlignCenterX", LayoutOperation.AlignCenterX, selected);
        ImGui.SameLine();
        LayoutButton("Right", "PlayerMapAlignRight", LayoutOperation.AlignRight, selected);

        LayoutButton("Bottom", "PlayerMapAlignBottom", LayoutOperation.AlignBottom, selected);
        ImGui.SameLine();
        LayoutButton("Center Y", "PlayerMapAlignCenterY", LayoutOperation.AlignCenterY, selected);
        ImGui.SameLine();
        LayoutButton("Top", "PlayerMapAlignTop", LayoutOperation.AlignTop, selected);

        if (selected.Count >= 3)
        {
            LayoutButton("Space X", "PlayerMapSpaceX", LayoutOperation.SpaceX, selected);
            ImGui.SameLine();
            LayoutButton("Space Y", "PlayerMapSpaceY", LayoutOperation.SpaceY, selected);
        }
    }

    private static void LayoutButton(
        string label,
        string id,
        LayoutOperation operation,
        List<PlayerMapRoomSnapshot> selected)
    {
        if (DevToolWidgets.ActionButton(label, id, DevToolButtonTone.Subtle))
            Apply(operation, selected);
    }

    private static void Apply(LayoutOperation operation, List<PlayerMapRoomSnapshot> selected)
    {
        if (selected == null || selected.Count < 2) return;

        Dictionary<int, Vector2> targets = new(selected.Count);
        for (int i = 0; i < selected.Count; i++)
            targets[selected[i].RoomIndex] = selected[i].EffectivePosition;

        float minLeft = float.MaxValue;
        float maxRight = float.MinValue;
        float minBottom = float.MaxValue;
        float maxTop = float.MinValue;
        for (int i = 0; i < selected.Count; i++)
        {
            PlayerMapRoomSnapshot room = selected[i];
            float halfW = HalfWidth(room);
            float halfH = HalfHeight(room);
            minLeft = Math.Min(minLeft, room.EffectivePosition.x - halfW);
            maxRight = Math.Max(maxRight, room.EffectivePosition.x + halfW);
            minBottom = Math.Min(minBottom, room.EffectivePosition.y - halfH);
            maxTop = Math.Max(maxTop, room.EffectivePosition.y + halfH);
        }

        switch (operation)
        {
            case LayoutOperation.AlignLeft:
                for (int i = 0; i < selected.Count; i++)
                    targets[selected[i].RoomIndex] = new Vector2(minLeft + HalfWidth(selected[i]), selected[i].EffectivePosition.y);
                break;
            case LayoutOperation.AlignCenterX:
            {
                float center = (minLeft + maxRight) * 0.5f;
                for (int i = 0; i < selected.Count; i++)
                    targets[selected[i].RoomIndex] = new Vector2(center, selected[i].EffectivePosition.y);
                break;
            }
            case LayoutOperation.AlignRight:
                for (int i = 0; i < selected.Count; i++)
                    targets[selected[i].RoomIndex] = new Vector2(maxRight - HalfWidth(selected[i]), selected[i].EffectivePosition.y);
                break;
            case LayoutOperation.AlignBottom:
                for (int i = 0; i < selected.Count; i++)
                    targets[selected[i].RoomIndex] = new Vector2(selected[i].EffectivePosition.x, minBottom + HalfHeight(selected[i]));
                break;
            case LayoutOperation.AlignCenterY:
            {
                float center = (minBottom + maxTop) * 0.5f;
                for (int i = 0; i < selected.Count; i++)
                    targets[selected[i].RoomIndex] = new Vector2(selected[i].EffectivePosition.x, center);
                break;
            }
            case LayoutOperation.AlignTop:
                for (int i = 0; i < selected.Count; i++)
                    targets[selected[i].RoomIndex] = new Vector2(selected[i].EffectivePosition.x, maxTop - HalfHeight(selected[i]));
                break;
            case LayoutOperation.SpaceX:
                DistributeX(selected, targets);
                break;
            case LayoutOperation.SpaceY:
                DistributeY(selected, targets);
                break;
        }

        int[] ids = new int[selected.Count];
        Vector2[] positions = new Vector2[selected.Count];
        for (int i = 0; i < selected.Count; i++) ids[i] = selected[i].RoomIndex;
        Array.Sort(ids);
        for (int i = 0; i < ids.Length; i++) positions[i] = targets[ids[i]];

        PlayerMapGroupCommandQueue.EnqueueExact(new PlayerMapGroupMoveCommand(
            ids,
            positions,
            Label(operation)));
    }

    private static void DistributeX(
        List<PlayerMapRoomSnapshot> selected,
        Dictionary<int, Vector2> targets)
    {
        List<PlayerMapRoomSnapshot> ordered = new(selected);
        ordered.Sort((a, b) =>
        {
            float leftA = a.EffectivePosition.x - HalfWidth(a);
            float leftB = b.EffectivePosition.x - HalfWidth(b);
            int compare = leftA.CompareTo(leftB);
            return compare != 0 ? compare : a.RoomIndex.CompareTo(b.RoomIndex);
        });

        float left = ordered[0].EffectivePosition.x - HalfWidth(ordered[0]);
        float right = ordered[ordered.Count - 1].EffectivePosition.x + HalfWidth(ordered[ordered.Count - 1]);
        float totalWidth = 0f;
        for (int i = 0; i < ordered.Count; i++) totalWidth += HalfWidth(ordered[i]) * 2f;
        float gap = (right - left - totalWidth) / Math.Max(1, ordered.Count - 1);
        float cursor = left;
        for (int i = 0; i < ordered.Count; i++)
        {
            PlayerMapRoomSnapshot room = ordered[i];
            float half = HalfWidth(room);
            targets[room.RoomIndex] = new Vector2(cursor + half, room.EffectivePosition.y);
            cursor += half * 2f + gap;
        }
    }

    private static void DistributeY(
        List<PlayerMapRoomSnapshot> selected,
        Dictionary<int, Vector2> targets)
    {
        List<PlayerMapRoomSnapshot> ordered = new(selected);
        ordered.Sort((a, b) =>
        {
            float bottomA = a.EffectivePosition.y - HalfHeight(a);
            float bottomB = b.EffectivePosition.y - HalfHeight(b);
            int compare = bottomA.CompareTo(bottomB);
            return compare != 0 ? compare : a.RoomIndex.CompareTo(b.RoomIndex);
        });

        float bottom = ordered[0].EffectivePosition.y - HalfHeight(ordered[0]);
        float top = ordered[ordered.Count - 1].EffectivePosition.y + HalfHeight(ordered[ordered.Count - 1]);
        float totalHeight = 0f;
        for (int i = 0; i < ordered.Count; i++) totalHeight += HalfHeight(ordered[i]) * 2f;
        float gap = (top - bottom - totalHeight) / Math.Max(1, ordered.Count - 1);
        float cursor = bottom;
        for (int i = 0; i < ordered.Count; i++)
        {
            PlayerMapRoomSnapshot room = ordered[i];
            float half = HalfHeight(room);
            targets[room.RoomIndex] = new Vector2(room.EffectivePosition.x, cursor + half);
            cursor += half * 2f + gap;
        }
    }

    private static float HalfWidth(PlayerMapRoomSnapshot room) =>
        room.Bake.Width * PlayerMapCoordinateSystem.CanonPixelsPerTile * 0.5f;

    private static float HalfHeight(PlayerMapRoomSnapshot room) =>
        room.Bake.Height * PlayerMapCoordinateSystem.CanonPixelsPerTile * 0.5f;

    private static string Label(LayoutOperation operation) => operation switch
    {
        LayoutOperation.AlignLeft => "Align player-map rooms left",
        LayoutOperation.AlignCenterX => "Align player-map rooms center X",
        LayoutOperation.AlignRight => "Align player-map rooms right",
        LayoutOperation.AlignBottom => "Align player-map rooms bottom",
        LayoutOperation.AlignCenterY => "Align player-map rooms center Y",
        LayoutOperation.AlignTop => "Align player-map rooms top",
        LayoutOperation.SpaceX => "Distribute player-map rooms horizontally",
        LayoutOperation.SpaceY => "Distribute player-map rooms vertically",
        _ => "Layout player-map rooms"
    };

}
