using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

/// <summary>
/// Keeps player quicksand occlusion tied to the authored front curve rather than
/// the full TerrainCurve surface band. QuicksandZoneHooks may move the player into
/// Sand, but this outer hook delays that move until the rendered feet touch and then
/// reorders the player immediately behind the deep-fill mesh (sprite 2), leaving the
/// 50 px surface strip behind the player. This avoids the whole body disappearing as
/// soon as the player first touches quicksand while preserving the original curve
/// rendering and shader setup.
/// </summary>
internal static class QuicksandPlayerRenderContact
{
    // PlayerGraphics keeps the ordinary legs sprite at index 4 for the standard
    // slugcat graphics layout. Custom graphics fall back to lower BodyChunk contact.
    private const int PlayerLegSpriteIndex = 4;
    private const float ContactEpsilon = 0.02f;
    private const float ReleaseClearance = 2.0f;
    private const float EdgeSampleSpacing = 3.0f;
    private const int MaxEdgeSamples = 32;

    private sealed class State
    {
        internal bool SurfaceContactLatched;
    }

    private static readonly ConditionalWeakTable<RoomCamera.SpriteLeaser, State> States = new();
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.RoomCamera.SpriteLeaser.Update += SpriteLeaser_Update;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;
        On.RoomCamera.SpriteLeaser.Update -= SpriteLeaser_Update;
    }

    private static void SpriteLeaser_Update(
        On.RoomCamera.SpriteLeaser.orig_Update orig,
        RoomCamera.SpriteLeaser self,
        float timeStacker,
        RoomCamera rCam,
        Vector2 camPos)
    {
        // This hook is enabled after QuicksandZoneHooks, so orig() first lets the
        // existing quicksand renderer make its normal container decision. We only
        // undo that decision for a player whose feet have not reached the surface.
        orig(self, timeStacker, rCam, camPos);

        if (self == null || self.sprites == null || rCam?.room == null)
        {
            return;
        }

        State state = States.GetOrCreateValue(self);
        Player player = ResolvePlayer(self.drawableObject);
        if (player == null ||
            player.room != rCam.room ||
            !QuicksandSinkRateLimiter.TryGetVisualSink(
                player,
                out _,
                out QuicksandZone zone,
                out _) ||
            !IsUsableZone(zone))
        {
            state.SurfaceContactLatched = false;
            return;
        }

        float penetration = MeasurePlayerFootPenetration(
            self,
            player,
            zone,
            camPos);

        if (!state.SurfaceContactLatched)
        {
            if (penetration >= ContactEpsilon)
            {
                state.SurfaceContactLatched = true;
            }
        }
        else if (penetration < -ReleaseClearance)
        {
            // A real upward jump should become fully visible again once the rendered
            // feet have clearly left the sand, instead of remaining clipped merely
            // because the physics sink state has not cleared yet.
            state.SurfaceContactLatched = false;
        }

        if (!state.SurfaceContactLatched)
        {
            // Until the feet actually touch, keep the player in the native containers.
            // The inner hook may have already moved the sprites into Sand this frame;
            // restore them here without changing any terrain geometry.
            self.AddSpritesToContainer(null, rCam);
            return;
        }

        // TerrainCurve uses sprite 1 for a roughly 50 px surface strip and sprite 2
        // for the deep fill from the authored front curve downward. Moving a player
        // to the absolute back of Sand puts the whole body behind both meshes, which
        // is why first contact could hide half the slugcat immediately. Instead place
        // the player between those two terrain meshes: in front of the surface strip,
        // but directly behind the deep fill. The deep fill then occludes only pixels
        // that are actually below the front curve.
        if (!PlacePlayerBehindDeepFill(self, rCam, zone))
        {
            // If the terrain leaser is unavailable for a frame, prefer a fully visible
            // player over the old severe over-occlusion.
            self.AddSpritesToContainer(null, rCam);
        }
    }

    private static bool PlacePlayerBehindDeepFill(
        RoomCamera.SpriteLeaser playerLeaser,
        RoomCamera rCam,
        QuicksandZone zone)
    {
        if (playerLeaser?.sprites == null ||
            rCam?.spriteLeasers == null ||
            !IsUsableZone(zone))
        {
            return false;
        }

        RoomCamera.SpriteLeaser zoneLeaser = null;
        for (int i = 0; i < rCam.spriteLeasers.Count; i++)
        {
            RoomCamera.SpriteLeaser candidate = rCam.spriteLeasers[i];
            if (candidate != null && candidate.drawableObject == zone)
            {
                zoneLeaser = candidate;
                break;
            }
        }

        if (zoneLeaser?.sprites == null ||
            zoneLeaser.sprites.Length <= 2 ||
            zoneLeaser.sprites[2] == null)
        {
            return false;
        }

        FContainer sand = rCam.ReturnFContainer("Sand");
        FSprite deepFill = zoneLeaser.sprites[2];
        if (sand == null || deepFill.container != sand)
        {
            return false;
        }

        for (int i = 0; i < playerLeaser.sprites.Length; i++)
        {
            FSprite sprite = playerLeaser.sprites[i];
            if (sprite == null)
            {
                continue;
            }

            if (sprite.container != sand)
            {
                sand.AddChild(sprite);
            }

            sprite.MoveBehindOtherNode(deepFill);
        }

        if (playerLeaser.containers != null)
        {
            for (int i = 0; i < playerLeaser.containers.Length; i++)
            {
                FContainer container = playerLeaser.containers[i];
                if (container == null)
                {
                    continue;
                }

                if (container.container != sand)
                {
                    sand.AddChild(container);
                }

                container.MoveBehindOtherNode(deepFill);
            }
        }

        return true;
    }

    private static Player ResolvePlayer(IDrawable drawable)
    {
        if (drawable is GraphicsModule graphicsModule)
        {
            return graphicsModule.owner as Player;
        }

        return drawable as Player;
    }

    private static float MeasurePlayerFootPenetration(
        RoomCamera.SpriteLeaser sLeaser,
        Player player,
        QuicksandZone zone,
        Vector2 camPos)
    {
        if (sLeaser?.sprites != null &&
            sLeaser.sprites.Length > PlayerLegSpriteIndex)
        {
            float spritePenetration = MeasureSpritePenetration(
                sLeaser.sprites[PlayerLegSpriteIndex],
                zone,
                camPos);

            if (spritePenetration > float.NegativeInfinity)
            {
                return spritePenetration;
            }
        }

        return MeasureLowerBodyPenetration(player, zone);
    }

    private static float MeasureSpritePenetration(
        FSprite sprite,
        QuicksandZone zone,
        Vector2 camPos)
    {
        if (sprite == null ||
            !sprite.isVisible ||
            sprite.alpha <= 0.001f ||
            !IsUsableZone(zone))
        {
            return float.NegativeInfinity;
        }

        float deepest = float.NegativeInfinity;
        bool sampled = false;

        if (sprite is TriangleMesh mesh &&
            mesh.vertices != null &&
            mesh.triangles != null &&
            mesh.vertices.Length > 0)
        {
            for (int i = 0; i < mesh.triangles.Length; i++)
            {
                TriangleMesh.Triangle triangle = mesh.triangles[i];
                SampleEdge(sprite, mesh.vertices[triangle.a], mesh.vertices[triangle.b],
                    zone, camPos, ref deepest, ref sampled);
                SampleEdge(sprite, mesh.vertices[triangle.b], mesh.vertices[triangle.c],
                    zone, camPos, ref deepest, ref sampled);
                SampleEdge(sprite, mesh.vertices[triangle.c], mesh.vertices[triangle.a],
                    zone, camPos, ref deepest, ref sampled);
            }

            return sampled ? deepest : float.NegativeInfinity;
        }

        Vector2[] localVertices = sprite._localVertices;
        if (localVertices == null || localVertices.Length < 2)
        {
            return float.NegativeInfinity;
        }

        for (int i = 0; i < localVertices.Length; i++)
        {
            SampleEdge(
                sprite,
                localVertices[i],
                localVertices[(i + 1) % localVertices.Length],
                zone,
                camPos,
                ref deepest,
                ref sampled);
        }

        return sampled ? deepest : float.NegativeInfinity;
    }

    private static void SampleEdge(
        FSprite sprite,
        Vector2 localStart,
        Vector2 localEnd,
        QuicksandZone zone,
        Vector2 camPos,
        ref float deepest,
        ref bool sampled)
    {
        Vector2 worldStart = sprite.LocalToStage(localStart) + camPos;
        Vector2 worldEnd = sprite.LocalToStage(localEnd) + camPos;
        float edgeLength = Vector2.Distance(worldStart, worldEnd);
        int sampleCount = Mathf.Clamp(
            Mathf.CeilToInt(edgeLength / EdgeSampleSpacing),
            1,
            MaxEdgeSamples);

        for (int i = 0; i <= sampleCount; i++)
        {
            Vector2 worldPoint = Vector2.Lerp(
                worldStart,
                worldEnd,
                (float)i / sampleCount);

            if (!TryGetSurfacePenetration(zone, worldPoint, out float penetration))
            {
                continue;
            }

            sampled = true;
            deepest = Mathf.Max(deepest, penetration);
        }
    }

    private static float MeasureLowerBodyPenetration(Player player, QuicksandZone zone)
    {
        if (player?.bodyChunks == null ||
            player.bodyChunks.Length == 0 ||
            !IsUsableZone(zone))
        {
            return float.NegativeInfinity;
        }

        BodyChunk lowerBody = player.bodyChunks.Length > 1
            ? player.bodyChunks[1]
            : player.bodyChunks[0];

        if (lowerBody == null)
        {
            return float.NegativeInfinity;
        }

        Vector2 bottomPoint = lowerBody.pos +
                              Vector2.down * Mathf.Max(1f, lowerBody.rad);

        return TryGetSurfacePenetration(zone, bottomPoint, out float penetration)
            ? penetration
            : float.NegativeInfinity;
    }

    private static bool TryGetSurfacePenetration(
        QuicksandZone zone,
        Vector2 worldPoint,
        out float penetration)
    {
        penetration = 0f;
        if (!IsUsableZone(zone) ||
            worldPoint.x < zone.startX ||
            worldPoint.x > zone.endX)
        {
            return false;
        }

        float u = zone.MaterialUAtWorldX(worldPoint.x);
        if (!zone.Data.IsQuicksand(u) ||
            !zone.TrySampleSurfaceFrame(
                u,
                out Vector2 surfacePoint,
                out _,
                out _,
                out _))
        {
            return false;
        }

        penetration = surfacePoint.y - worldPoint.y;
        return true;
    }

    private static bool IsUsableZone(QuicksandZone zone)
    {
        return zone != null &&
               !zone.slatedForDeletetion &&
               zone.PlacedObject != null &&
               zone.PlacedObject.active &&
               zone.Data != null;
    }
}
