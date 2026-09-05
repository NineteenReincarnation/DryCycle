using System;
using System.Collections.Generic;
using System.Reflection;
using DryCycle.Creatures.DesertBatfly;
using ImGuiNET;
using UnityEngine;
using Num = System.Numerics;

namespace DryCycle.Debugging.AI;

internal static class AIDebugAdvancedOverlay
{
    private static readonly List<AIDebugCandidate> CandidateScratch = new(64);
    private static readonly List<DesertBatfly> AttackersScratch = new(4);
    private static readonly List<DesertBatfly> WaitingScratch = new(8);

    private static readonly FieldInfo VisibleThreatField = typeof(DesertBatflySocialRoles)
        .GetField("visibleThreat", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo ClearSightField = typeof(DesertBatflySocialRoles)
        .GetField("clearSightTicks", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo WatchTicksField = typeof(DesertBatflySocialRoles)
        .GetField("watchTicks", BindingFlags.Instance | BindingFlags.NonPublic);

    internal static void Draw(RainWorldGame game, AbstractCreature selected, bool frozen,
        AIDebugTraceFrame frozenFrame)
    {
        if (game?.cameras == null || game.cameras.Length == 0) return;
        RoomCamera camera = frozen
            ? AIDebugCameraUtil.ForRoomName(game, frozenFrame.Room)
            : AIDebugCameraUtil.ForCreature(game, selected);
        if (camera?.room == null) return;

        Creature realized = selected?.realizedCreature;
        if (!frozen && realized?.room != camera.room) return;

        ImDrawListPtr draw = ImGui.GetBackgroundDrawList();
        using (AIDebugProfiler.Begin(AIDebugProfileCategory.Overlay))
        {
            if (frozen)
            {
                DrawFrozen(draw, camera, frozenFrame);
                return;
            }

            if (AIDebugSettings.OverlayAImap) DrawAImap(draw, camera, selected);
            if (AIDebugSettings.OverlayPhysics) DrawPhysics(draw, camera, realized);
            if (AIDebugSettings.OverlayMovement) DrawMovement(draw, camera, realized);
            if (realized is DesertBatfly bat)
            {
                if (AIDebugSettings.OverlaySocial) DrawDesertSocial(draw, camera, bat);
                if (AIDebugSettings.OverlayCombat) DrawDesertCombat(draw, camera, bat);
            }
            DrawCandidates(draw, camera, selected);
        }
    }

    private static void DrawFrozen(ImDrawListPtr draw, RoomCamera camera, AIDebugTraceFrame frame)
    {
        if (!string.Equals(frame.Room, camera.room.abstractRoom.name, StringComparison.Ordinal)) return;
        uint ghost = Col(0.72f, 0.82f, 1f, 0.85f);
        uint goal = Col(0.26f, 0.82f, 0.96f, 0.85f);
        Num.Vector2 p = World(camera, frame.Position);
        Num.Vector2 g = World(camera, frame.LocalGoal);
        draw.AddCircle(p, 13f, ghost, 24, 2f);
        draw.AddLine(p + new Num.Vector2(-9f, 0f), p + new Num.Vector2(9f, 0f), ghost, 1f);
        draw.AddLine(p + new Num.Vector2(0f, -9f), p + new Num.Vector2(0f, 9f), ghost, 1f);
        Arrow(draw, p, g, goal, 2f);
        if (AIDebugSettings.OverlayLabels)
        {
            draw.AddText(p + new Num.Vector2(15f, -10f), ghost,
                $"HISTORY {frame.Frame} · {frame.Mode} · {frame.Role}");
            draw.AddText(g + new Num.Vector2(8f, -8f), goal, "localGoal");
        }
    }

    private static void DrawPhysics(ImDrawListPtr draw, RoomCamera camera, Creature creature)
    {
        if (creature?.bodyChunks == null) return;
        uint chunkColor = Col(0.90f, 0.90f, 0.94f, 0.72f);
        uint velocityColor = Col(0.30f, 0.78f, 0.98f, 0.80f);
        float scale = AIDebugCameraUtil.ScreenScale(camera);
        for (int i = 0; i < creature.bodyChunks.Length; i++)
        {
            BodyChunk chunk = creature.bodyChunks[i];
            if (chunk == null) continue;
            Num.Vector2 p = World(camera, chunk.pos);
            draw.AddCircle(p, Mathf.Max(2f, chunk.rad * scale), chunkColor, 20, 1.4f);
            Arrow(draw, p, World(camera, chunk.pos + chunk.vel * 8f), velocityColor, 1.2f);
        }

        if (creature.grasps == null) return;
        uint grasp = Col(0.96f, 0.66f, 0.26f, 0.82f);
        for (int i = 0; i < creature.grasps.Length; i++)
        {
            Creature.Grasp g = creature.grasps[i];
            if (g?.grabbed?.firstChunk == null || creature.mainBodyChunk == null) continue;
            draw.AddLine(World(camera, creature.mainBodyChunk.pos),
                World(camera, g.grabbed.firstChunk.pos), grasp, 1.4f);
        }
    }

    private static void DrawMovement(ImDrawListPtr draw, RoomCamera camera, Creature creature)
    {
        if (creature?.mainBodyChunk == null) return;
        uint velocity = Col(0.32f, 0.86f, 0.98f, 0.90f);
        Num.Vector2 p = World(camera, creature.mainBodyChunk.pos);
        Arrow(draw, p, World(camera, creature.mainBodyChunk.pos + creature.mainBodyChunk.vel * 12f), velocity, 1.8f);

        if (creature is DesertBatfly bat && bat.AI != null)
        {
            uint local = Col(0.20f, 0.78f, 0.96f, 0.95f);
            Num.Vector2 g = World(camera, bat.AI.localGoal);
            Arrow(draw, p, g, local, 2f);
            draw.AddCircleFilled(g, 4f, local, 12);
            if (AIDebugSettings.OverlayLabels) draw.AddText(g + new Num.Vector2(6f, -7f), local, "localGoal");
        }
    }

    private static void DrawAImap(ImDrawListPtr draw, RoomCamera camera, AbstractCreature selected)
    {
        Room room = camera.room;
        if (room?.aimap == null || selected?.creatureTemplate == null) return;
        using (AIDebugProfiler.Begin(AIDebugProfileCategory.AImap))
        {
            float visibleBottom = camera.pos.y;
            float visibleTop = camera.pos.y + camera.sSize.y;
            if (camera.splitScreenMode)
            {
                visibleBottom += camera.sSize.y * 0.25f;
                visibleTop -= camera.sSize.y * 0.25f;
            }
            int x0 = Mathf.Clamp(Mathf.FloorToInt(camera.pos.x / 20f) - 1, 0, room.TileWidth - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(visibleBottom / 20f) - 1, 0, room.TileHeight - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt((camera.pos.x + camera.sSize.x) / 20f) + 1, 0, room.TileWidth - 1);
            int y1 = Mathf.Clamp(Mathf.CeilToInt(visibleTop / 20f) + 1, 0, room.TileHeight - 1);
            uint allowed = Col(0.22f, 0.78f, 0.40f, 0.11f);
            uint blocked = Col(0.92f, 0.28f, 0.26f, 0.08f);
            for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            {
                bool accessible;
                try { accessible = room.aimap.TileAccessibleToCreature(x, y, selected.creatureTemplate); }
                catch { continue; }
                Vector2 bottomLeft = new(x * 20f, y * 20f);
                Vector2 topRight = bottomLeft + new Vector2(20f, 20f);
                Num.Vector2 a = World(camera, new Vector2(bottomLeft.x, topRight.y));
                Num.Vector2 b = World(camera, new Vector2(topRight.x, bottomLeft.y));
                draw.AddRectFilled(a, b, accessible ? allowed : blocked);
            }

            Creature realized = selected.realizedCreature;
            if (realized?.mainBodyChunk == null) return;
            IntVector2 tile = room.GetTilePosition(realized.mainBodyChunk.pos);
            AItile aiTile = room.aimap.getAItile(tile);
            if (aiTile?.outgoingPaths == null) return;
            uint connection = Col(0.94f, 0.78f, 0.24f, 0.78f);
            int count = Mathf.Min(24, aiTile.outgoingPaths.Count);
            for (int i = 0; i < count; i++)
            {
                MovementConnection c = aiTile.outgoingPaths[i];
                bool allowedConnection;
                try { allowedConnection = room.aimap.IsConnectionAllowedForCreature(c, selected.creatureTemplate); }
                catch { allowedConnection = false; }
                if (!allowedConnection) continue;
                Num.Vector2 a = World(camera, room.MiddleOfTile(c.StartTile));
                Num.Vector2 b = World(camera, room.MiddleOfTile(c.DestTile));
                Arrow(draw, a, b, connection, 1.2f);
                if (AIDebugSettings.OverlayLabels)
                    draw.AddText((a + b) * 0.5f, connection, c.type.ToString());
            }
        }
    }

    private static void DrawDesertSocial(ImDrawListPtr draw, RoomCamera camera, DesertBatfly bat)
    {
        if (bat.room == null || !DesertSwarmRoom.TryGet(bat.room, out DesertSwarmRoom colony)) return;
        DesertBatflySocialRoles roles = bat.DesertAI.Roles;
        Num.Vector2 center = World(camera, colony.Flock.Center);
        float scale = AIDebugCameraUtil.ScreenScale(camera);
        uint social = Col(0.46f, 0.88f, 0.62f, 0.88f);
        uint watch = Col(0.72f, 0.56f, 0.96f, 0.92f);
        draw.AddCircle(center, 11f, social, 24, 1.6f);

        ExpressedSocialRole role = roles.Expressed;
        if (role == ExpressedSocialRole.Sentinel)
        {
            draw.AddCircle(center, 190f * scale, watch, 64, 1.3f);
            Creature threat = VisibleThreatField?.GetValue(roles) as Creature;
            if (threat?.room == bat.room && threat.mainBodyChunk != null)
                Arrow(draw, World(camera, bat.mainBodyChunk.pos), World(camera, threat.mainBodyChunk.pos), watch, 1.6f);
            if (AIDebugSettings.OverlayLabels)
            {
                int watchTicks = WatchTicksField?.GetValue(roles) is int w ? w : 0;
                draw.AddText(World(camera, bat.mainBodyChunk.pos) + new Num.Vector2(12f, -22f), watch,
                    $"SENTINEL conf={roles.SentinelAlertConfidence:0.00} watch={watchTicks}");
            }
        }
        else if (role == ExpressedSocialRole.Opportunist || roles.OpportunistRecoveryActive)
        {
            draw.AddCircle(center, 95f * scale, watch, 48, 1.3f);
            if (AIDebugSettings.OverlayLabels)
            {
                int safe = ClearSightField?.GetValue(roles) is int c ? c : 0;
                draw.AddText(World(camera, bat.mainBodyChunk.pos) + new Num.Vector2(12f, -22f), watch,
                    $"OPPORTUNIST window={roles.OpportunityTicks} safe={safe}/40 recovery={roles.OpportunistRecoveryActive}");
            }
        }
        else if (role == ExpressedSocialRole.Bully && AIDebugSettings.OverlayLabels)
        {
            draw.AddText(World(camera, bat.mainBodyChunk.pos) + new Num.Vector2(12f, -22f), watch, "BULLY");
        }
    }

    private static void DrawDesertCombat(ImDrawListPtr draw, RoomCamera camera, DesertBatfly selected)
    {
        Creature target = selected.DesertAI.Target;
        if (target?.room != selected.room || target.mainBodyChunk == null ||
            selected.room?.abstractRoom?.creatures == null) return;

        AttackersScratch.Clear();
        WaitingScratch.Clear();
        foreach (AbstractCreature abs in selected.room.abstractRoom.creatures)
        {
            if (abs?.realizedCreature is not DesertBatfly bat || bat.DesertAI.Target != target ||
                bat.mainBodyChunk == null) continue;
            if (bat.DesertAI.FormalAttack) AttackersScratch.Add(bat);
            else WaitingScratch.Add(bat);
        }

        uint attack = Col(0.96f, 0.38f, 0.28f, 0.95f);
        uint wait = Col(0.96f, 0.72f, 0.24f, 0.82f);
        for (int i = 0; i < AttackersScratch.Count; i++)
        {
            Num.Vector2 p = World(camera, AttackersScratch[i].mainBodyChunk.pos);
            if (AIDebugSettings.OverlayLabels) draw.AddText(p + new Num.Vector2(10f, 6f), attack, $"SLOT {i + 1}");
        }
        for (int i = 0; i < WaitingScratch.Count; i++)
        {
            Num.Vector2 p = World(camera, WaitingScratch[i].mainBodyChunk.pos);
            if (AIDebugSettings.OverlayLabels) draw.AddText(p + new Num.Vector2(10f, 6f), wait, "WAIT");
        }
        Num.Vector2 t = World(camera, target.mainBodyChunk.pos);
        draw.AddCircle(t, 11f, attack, 24, 2f);
        if (AttackersScratch.Count > DesertBatflyTuning.AttackSlots && AIDebugSettings.OverlayLabels)
            draw.AddText(t + new Num.Vector2(14f, -16f), attack,
                $"ATTACK SLOT VIOLATION {AttackersScratch.Count}/{DesertBatflyTuning.AttackSlots}");
    }

    private static void DrawCandidates(ImDrawListPtr draw, RoomCamera camera, AbstractCreature owner)
    {
        if (owner == null || AIDebugCandidateRegistry.Copy(DebugEntityKey.From(owner), CandidateScratch) == 0) return;
        uint valid = Col(0.30f, 0.86f, 0.54f, 0.88f);
        uint invalid = Col(0.92f, 0.34f, 0.30f, 0.70f);
        uint winner = Col(0.98f, 0.82f, 0.24f, 0.98f);
        for (int i = 0; i < CandidateScratch.Count; i++)
        {
            AIDebugCandidate c = CandidateScratch[i];
            if (!c.HasPosition) continue;
            Num.Vector2 p = World(camera, c.Position);
            uint color = c.Winner ? winner : c.Valid ? valid : invalid;
            draw.AddCircle(p, c.Winner ? 8f : 5f, color, 18, c.Winner ? 2f : 1.3f);
            if (AIDebugSettings.OverlayLabels)
                draw.AddText(p + new Num.Vector2(7f, -8f), color,
                    $"{c.Name} {c.Score:0.00}{(c.Valid ? string.Empty : " ×")}");
        }
    }

    private static Num.Vector2 World(RoomCamera camera, Vector2 world) =>
        AIDebugCameraUtil.WorldToImGui(camera, world);

    private static void Arrow(ImDrawListPtr draw, Num.Vector2 from, Num.Vector2 to, uint color, float thickness)
    {
        draw.AddLine(from, to, color, thickness);
        Num.Vector2 delta = to - from;
        float length = Mathf.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
        if (length < 2f) return;
        Num.Vector2 dir = delta / length;
        Num.Vector2 side = new(-dir.Y, dir.X);
        draw.AddTriangleFilled(to, to - dir * 9f + side * 4.5f, to - dir * 9f - side * 4.5f, color);
    }

    private static uint Col(float r, float g, float b, float a) =>
        ImGui.ColorConvertFloat4ToU32(new Num.Vector4(r, g, b, a));
}
