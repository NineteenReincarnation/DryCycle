using System;
using System.Collections.Generic;
using DryCycle.Creatures.DesertBatfly;
using ImGuiNET;
using UnityEngine;
using Num = System.Numerics;

namespace DryCycle.Debugging.AI;

internal static class AIDebugWorldOverlay
{
    internal static bool TryPick(RainWorldGame game, List<AbstractCreature> entities,
        bool mouseCaptured, out AbstractCreature picked)
    {
        picked = null;
        if (game?.cameras == null || game.cameras.Length == 0 || game.cameras[0]?.room == null) return false;
        if (mouseCaptured || (!Input.GetKey(KeyCode.LeftAlt) && !Input.GetKey(KeyCode.RightAlt))) return false;
        if (!Input.GetMouseButtonDown(0)) return false;

        RoomCamera camera = game.cameras[0];
        Vector2 mouseWorld = (Vector2)Futile.mousePosition + camera.pos;
        float best = 34f;
        for (int i = 0; i < entities.Count; i++)
        {
            AbstractCreature creature = entities[i];
            Creature realized = creature?.realizedCreature;
            if (realized?.room != camera.room || realized.inShortcut || realized.slatedForDeletetion) continue;
            float distance = DistanceToCreature(mouseWorld, realized);
            if (distance >= best) continue;
            best = distance;
            picked = creature;
        }
        return picked != null;
    }

    internal static void Draw(RainWorldGame game, AbstractCreature selected,
        IEnumerable<DebugEntityKey> pinned, Func<DebugEntityKey, AbstractCreature> resolver,
        bool drawPath, bool drawPerception, bool drawLabels)
    {
        if (game?.cameras == null || game.cameras.Length == 0 || game.cameras[0]?.room == null) return;
        RoomCamera camera = game.cameras[0];
        ImDrawListPtr draw = ImGui.GetBackgroundDrawList();

        if (pinned != null)
        {
            foreach (DebugEntityKey key in pinned)
            {
                AbstractCreature creature = resolver?.Invoke(key);
                if (creature == null || ReferenceEquals(creature, selected)) continue;
                DrawCreatureMarker(draw, camera, creature, false, drawLabels);
            }
        }

        if (selected == null) return;
        DrawCreatureMarker(draw, camera, selected, true, drawLabels);
        Creature realized = selected.realizedCreature;
        if (realized?.room != camera.room) return;

        Num.Vector2 origin = WorldToImGui(camera, realized.mainBodyChunk.pos);
        uint cyan = Color(0.25f, 0.82f, 0.96f, 0.92f);
        uint yellow = Color(0.96f, 0.76f, 0.26f, 0.90f);
        uint red = Color(0.96f, 0.33f, 0.30f, 0.90f);
        uint green = Color(0.35f, 0.92f, 0.52f, 0.90f);

        if (realized is DesertBatfly bat)
        {
            if (bat.DesertAI.Target?.room == camera.room)
            {
                Num.Vector2 target = WorldToImGui(camera, bat.DesertAI.Target.mainBodyChunk.pos);
                DrawArrow(draw, origin, target, red, 2.0f);
                draw.AddCircle(target, 9f, red, 20, 2f);
                if (drawLabels) draw.AddText(target + new Num.Vector2(10f, -8f), red, "TARGET");
            }

            if (drawPath && bat.AI != null)
            {
                Num.Vector2 localGoal = WorldToImGui(camera, bat.AI.localGoal);
                DrawArrow(draw, origin, localGoal, cyan, 1.7f);
                draw.AddCircleFilled(localGoal, 4f, cyan, 12);
                if (drawLabels) draw.AddText(localGoal + new Num.Vector2(6f, -7f), cyan, "localGoal");
            }

            if (DesertSwarmRoom.TryGet(camera.room, out DesertSwarmRoom colony))
            {
                Num.Vector2 center = WorldToImGui(camera, colony.Flock.Center);
                draw.AddCircle(center, 13f, green, 24, 1.5f);
                if (drawLabels) draw.AddText(center + new Num.Vector2(14f, -8f), green, "FLOCK");
            }
        }
        else if (drawPath)
        {
            AIDebugPathState path = AIDebugAdvancedCapture.CapturePath(selected);
            if (path.HasPathfinder && path.Destination.room == camera.room.abstractRoom.index && path.Destination.TileDefined)
            {
                Vector2 destinationWorld = camera.room.MiddleOfTile(path.Destination.Tile);
                Num.Vector2 destination = WorldToImGui(camera, destinationWorld);
                DrawArrow(draw, origin, destination, cyan, 1.7f);
                draw.AddCircle(destination, 7f, cyan, 16, 1.5f);
            }
        }

        if (drawPerception)
        {
            ArtificialIntelligence ai = selected.abstractAI?.RealAI;
            Tracker tracker = ai?.tracker;
            if (tracker?.creatures != null)
            {
                int limit = Mathf.Min(12, tracker.creatures.Count);
                for (int i = 0; i < limit; i++)
                {
                    Tracker.CreatureRepresentation rep = tracker.creatures[i];
                    Creature other = rep?.representedCreature?.realizedCreature;
                    if (other?.room != camera.room) continue;
                    Num.Vector2 point = WorldToImGui(camera, other.mainBodyChunk.pos);
                    uint color = rep.VisualContact ? green : yellow;
                    draw.AddCircle(point, rep.VisualContact ? 8f : 6f, color, 16, 1.4f);
                    if (rep.VisualContact) draw.AddLine(origin, point, color, 1f);
                }
            }
        }
    }

    internal static bool DrawFrozenTrace(RainWorldGame game, AIDebugTraceFrame frame, bool drawLabels)
    {
        if (game?.cameras == null || game.cameras.Length == 0 || game.cameras[0]?.room == null) return false;
        RoomCamera camera = game.cameras[0];
        if (!string.Equals(frame.Room, camera.room.abstractRoom.name, StringComparison.Ordinal)) return false;

        ImDrawListPtr draw = ImGui.GetBackgroundDrawList();
        uint ghost = Color(0.35f, 0.82f, 1f, 0.88f);
        uint goal = Color(0.96f, 0.73f, 0.30f, 0.90f);
        Num.Vector2 position = WorldToImGui(camera, frame.Position);
        Num.Vector2 localGoal = WorldToImGui(camera, frame.LocalGoal);

        draw.AddCircle(position, 13f, ghost, 24, 2.2f);
        draw.AddCircle(position, 7f, ghost, 18, 1.3f);
        draw.AddLine(position + new Num.Vector2(-15f, 0f), position + new Num.Vector2(15f, 0f), ghost, 1f);
        draw.AddLine(position + new Num.Vector2(0f, -15f), position + new Num.Vector2(0f, 15f), ghost, 1f);
        DrawArrow(draw, position, localGoal, goal, 2f);
        draw.AddCircleFilled(localGoal, 4f, goal, 12);

        if (drawLabels)
        {
            draw.AddText(position + new Num.Vector2(16f, -20f), ghost,
                $"FROZEN [{frame.Frame}] {frame.Mode}");
            draw.AddText(localGoal + new Num.Vector2(7f, -8f), goal, "localGoal@frame");
        }
        return true;
    }

    private static void DrawCreatureMarker(ImDrawListPtr draw, RoomCamera camera,
        AbstractCreature creature, bool selected, bool label)
    {
        Creature realized = creature?.realizedCreature;
        if (realized?.room != camera.room || realized.mainBodyChunk == null) return;
        Num.Vector2 point = WorldToImGui(camera, realized.mainBodyChunk.pos);
        uint color = selected ? Color(0.28f, 0.86f, 1f, 0.98f) : Color(0.72f, 0.72f, 0.76f, 0.78f);
        float radius = selected ? 12f : 7f;
        draw.AddCircle(point, radius, color, selected ? 24 : 16, selected ? 2.3f : 1.3f);
        draw.AddLine(point + new Num.Vector2(-radius, 0f), point + new Num.Vector2(radius, 0f), color, 1f);
        draw.AddLine(point + new Num.Vector2(0f, -radius), point + new Num.Vector2(0f, radius), color, 1f);
        if (!label) return;
        string type = creature.creatureTemplate?.type?.value ?? "Creature";
        draw.AddText(point + new Num.Vector2(radius + 4f, -9f), color, type + " #" + creature.ID.number);
    }

    private static float DistanceToCreature(Vector2 point, Creature creature)
    {
        float best = Vector2.Distance(point, creature.mainBodyChunk.pos);
        if (creature.bodyChunks == null) return best;
        for (int i = 0; i < creature.bodyChunks.Length; i++)
            if (creature.bodyChunks[i] != null) best = Mathf.Min(best, Vector2.Distance(point, creature.bodyChunks[i].pos));
        return best;
    }

    private static Num.Vector2 WorldToImGui(RoomCamera camera, Vector2 world)
    {
        Vector2 local = world - camera.pos;
        Vector2 virtualSize = camera.sSize;
        float sx = Screen.width / Mathf.Max(1f, virtualSize.x);
        float sy = Screen.height / Mathf.Max(1f, virtualSize.y);
        return new Num.Vector2(local.x * sx, Screen.height - local.y * sy);
    }

    private static void DrawArrow(ImDrawListPtr draw, Num.Vector2 from, Num.Vector2 to, uint color, float thickness)
    {
        draw.AddLine(from, to, color, thickness);
        Num.Vector2 delta = to - from;
        float length = Mathf.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
        if (length < 2f) return;
        Num.Vector2 dir = delta / length;
        Num.Vector2 side = new(-dir.Y, dir.X);
        Num.Vector2 tipA = to - dir * 10f + side * 5f;
        Num.Vector2 tipB = to - dir * 10f - side * 5f;
        draw.AddTriangleFilled(to, tipA, tipB, color);
    }

    private static uint Color(float r, float g, float b, float a) =>
        ImGui.ColorConvertFloat4ToU32(new Num.Vector4(r, g, b, a));
}
