using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

/// <summary>
/// Owns player quicksand draw ordering.
///
/// Players are deliberately excluded from QuicksandZoneHooks' generic MoveToBack
/// path. This hook waits until the rendered feet touch the authored front curve,
/// then moves only player sprites that normally belong to the body/midground layer
/// behind TerrainCurve's deep-fill mesh. Foreground, Items, HUD and Bloom sprites are
/// left in their native containers, and every moved sprite is restored to the exact
/// container it had before quicksand occlusion.
/// </summary>
internal static class QuicksandPlayerRenderContact
{
    private const int PlayerLegSpriteIndex = 4;
    private const float ContactEpsilon = 0.02f;
    private const float ReleaseClearance = 2.0f;
    private const float EdgeSampleSpacing = 3.0f;
    private const int MaxEdgeSamples = 32;

    private sealed class State
    {
        internal bool SurfaceContactLatched;
        internal bool Occluding;
        internal bool[] OccludableSprites;
        internal FContainer[] NativeContainers;
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
        orig(self, timeStacker, rCam, camPos);

        if (self == null || self.sprites == null || rCam?.room == null)
        {
            return;
        }

        State state = States.GetOrCreateValue(self);
        Player player = ResolvePlayer(self.drawableObject);
        if (player == null ||
            player.room != rCam.room ||
            !QuicksandSinkRateLimiter.TryGetPlayerQuicksandState(
                player,
                out QuicksandZone zone,
                out _) ||
            !IsUsableZone(zone))
        {
            RestoreMovedSprites(self, state);
            ResetState(state);
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
            // The physics state may remain active for a few frames after a low jump.
            // Once the rendered feet are clearly above the curve, stop clipping.
            state.SurfaceContactLatched = false;
        }

        if (!state.SurfaceContactLatched)
        {
            RestoreMovedSprites(self, state);
            return;
        }

        if (!PlacePlayerBehindDeepFill(self, rCam, zone, state))
        {
            // Missing terrain leaser/container should fail visible, not hide the cat.
            RestoreMovedSprites(self, state);
        }
    }

    private static bool PlacePlayerBehindDeepFill(
        RoomCamera.SpriteLeaser playerLeaser,
        RoomCamera rCam,
        QuicksandZone zone,
        State state)
    {
        if (playerLeaser?.sprites == null ||
            rCam?.spriteLeasers == null ||
            state == null ||
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
        FContainer midground = rCam.ReturnFContainer("Midground");
        FSprite deepFill = zoneLeaser.sprites[2];
        if (sand == null || midground == null || deepFill.container != sand)
        {
            return false;
        }

        if (!state.Occluding ||
            state.OccludableSprites == null ||
            state.NativeContainers == null ||
            state.OccludableSprites.Length != playerLeaser.sprites.Length ||
            state.NativeContainers.Length != playerLeaser.sprites.Length)
        {
            RestoreMovedSprites(playerLeaser, state);
            CaptureOccludableSprites(playerLeaser, midground, state);
        }

        bool movedAny = false;
        for (int i = 0; i < playerLeaser.sprites.Length; i++)
        {
            if (!state.OccludableSprites[i])
            {
                continue;
            }

            FSprite sprite = playerLeaser.sprites[i];
            if (sprite == null)
            {
                continue;
            }

            if (sprite.container != sand)
            {
                sand.AddChild(sprite);
            }

            // Deep fill begins at the authored front curve. Placing the body directly
            // behind it clips only the portion actually below the curve; the 50 px
            // surface strip remains behind the player instead of hiding half the body.
            sprite.MoveBehindOtherNode(deepFill);
            movedAny = true;
        }

        state.Occluding = movedAny;
        return movedAny;
    }

    private static void CaptureOccludableSprites(
        RoomCamera.SpriteLeaser playerLeaser,
        FContainer midground,
        State state)
    {
        int count = playerLeaser.sprites.Length;
        state.OccludableSprites = new bool[count];
        state.NativeContainers = new FContainer[count];

        IMuddableGraphics muddable = playerLeaser.drawableObject as IMuddableGraphics;
        for (int i = 0; i < count; i++)
        {
            FSprite sprite = playerLeaser.sprites[i];
            if (sprite == null || sprite.container == null)
            {
                continue;
            }

            // Native Midground catches body-adjacent extras such as the face/gills;
            // IMuddableGraphics catches the canonical physical body sprites even if
            // another graphics mod has placed them in a custom container.
            bool bodySprite = sprite.container == midground ||
                              (muddable != null && muddable.MuddableSprite(playerLeaser, i));
            if (!bodySprite)
            {
                continue;
            }

            state.OccludableSprites[i] = true;
            state.NativeContainers[i] = sprite.container;
        }
    }

    private static void RestoreMovedSprites(
        RoomCamera.SpriteLeaser playerLeaser,
        State state)
    {
        if (playerLeaser?.sprites == null ||
            state == null ||
            !state.Occluding ||
            state.OccludableSprites == null ||
            state.NativeContainers == null)
        {
            return;
        }

        int count = Mathf.Min(
            playerLeaser.sprites.Length,
            Mathf.Min(state.OccludableSprites.Length, state.NativeContainers.Length));

        for (int i = 0; i < count; i++)
        {
            if (!state.OccludableSprites[i])
            {
                continue;
            }

            FSprite sprite = playerLeaser.sprites[i];
            FContainer nativeContainer = state.NativeContainers[i];
            if (sprite != null && nativeContainer != null && sprite.container != nativeContainer)
            {
                nativeContainer.AddChild(sprite);
            }
        }

        state.Occluding = false;
        state.OccludableSprites = null;
        state.NativeContainers = null;
    }

    private static void ResetState(State state)
    {
        if (state == null)
        {
            return;
        }

        state.SurfaceContactLatched = false;
        state.Occluding = false;
        state.OccludableSprites = null;
        state.NativeContainers = null;
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
