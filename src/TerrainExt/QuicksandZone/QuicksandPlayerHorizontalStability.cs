using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

/// <summary>
/// Controls only whole-player horizontal momentum in quicksand.
///
/// Rules:
/// - quicksand never creates X motion;
/// - with no left/right input, passive whole-player X drift is removed;
/// - with left/right input, native walking is preserved but abnormal whole-player
///   X speed/displacement is capped;
/// - the first frame that crosses into quicksand absorbs most incoming X momentum;
/// - a swept segment test catches high-speed boundary crossings that would otherwise
///   tunnel completely through a quicksand strip in one Player.Update.
///
/// All corrections are common translations/velocity offsets applied to every player
/// body chunk, preserving the native relative body pose and walking animation.
/// </summary>
internal static class QuicksandPlayerHorizontalStability
{
    private const float NativeRunSpeedScale = 0.65f;
    private const float MinimumHorizontalCap = 1.80f;
    private const float MaximumHorizontalCap = 3.25f;
    private const float EntryVelocityRetention = 0.55f;
    private const float EntryTravelRetention = 0.35f;
    private const float SweepSampleSpacing = 3f;
    private const int MaxSweepSamples = 48;
    private const float SurfaceInfluenceRadii = 1.25f;
    private const float Epsilon = 0.000001f;

    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.Player.Update += Player_Update;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;
        On.Player.Update -= Player_Update;
    }

    private static void Player_Update(On.Player.orig_Update orig, Player self, bool eu)
    {
        if (!CanTrack(self))
        {
            orig(self, eu);
            return;
        }

        int chunkCount = self.bodyChunks.Length;
        Vector2[] startPositions = new Vector2[chunkCount];
        for (int i = 0; i < chunkCount; i++)
        {
            BodyChunk chunk = self.bodyChunks[i];
            if (chunk != null)
            {
                startPositions[i] = chunk.pos;
            }
        }

        float startAverageX = AverageChunkX(self);
        bool startedInQuicksand = TryFindCurrentQuicksand(self, out _);
        bool hadHorizontalInput = HasHorizontalInput(self);

        orig(self, eu);

        if (!CanTrack(self) ||
            (self.grabbedBy != null && self.grabbedBy.Count > 0))
        {
            return;
        }

        bool currentlyInQuicksand = TryFindCurrentQuicksand(self, out _);
        bool sweptIntoQuicksand = TryFindSweptQuicksandContact(
            self,
            startPositions,
            out _,
            out float entryT);

        if (!startedInQuicksand && !currentlyInQuicksand && !sweptIntoQuicksand)
        {
            return;
        }

        bool hasHorizontalInput = hadHorizontalInput || HasHorizontalInput(self);
        float endAverageX = AverageChunkX(self);

        if (!hasHorizontalInput)
        {
            // Scheme-D has no fake floor ContactPoint, so native ground braking does
            // not exist here. Remove only the whole-player passive drift; relative
            // chunk motion remains untouched.
            TranslatePlayerX(self, startAverageX - endAverageX);
            RemoveWholePlayerHorizontalVelocity(self);
            return;
        }

        float speedCap = ResolveHorizontalSpeedCap(self);
        float targetAverageX = endAverageX;
        bool enteringThisFrame = !startedInQuicksand &&
                                 (currentlyInQuicksand || sweptIntoQuicksand);

        if (enteringThisFrame && sweptIntoQuicksand)
        {
            // Keep travel up to the first swept contact, then permit only a small
            // amount of post-contact penetration. This absorbs a fast turn/dash at
            // the boundary instead of allowing a one-frame tunnel through the zone.
            float clampedEntryT = Mathf.Clamp01(entryT);
            float entryCenterX = Mathf.Lerp(startAverageX, endAverageX, clampedEntryT);
            float remainingTravel = endAverageX - entryCenterX;
            float postContactLimit = speedCap * EntryTravelRetention;
            targetAverageX = entryCenterX + Mathf.Clamp(
                remainingTravel,
                -postContactLimit,
                postContactLimit);
        }
        else
        {
            // Already inside quicksand: preserve active native walking, but never let
            // a special turn/slide state translate the whole player faster than the
            // quicksand walking cap in a single physics tick.
            float displacement = endAverageX - startAverageX;
            targetAverageX = startAverageX + Mathf.Clamp(
                displacement,
                -speedCap,
                speedCap);
        }

        float positionCorrectionX = targetAverageX - AverageChunkX(self);
        if (Mathf.Abs(positionCorrectionX) > Epsilon)
        {
            TranslatePlayerX(self, positionCorrectionX);
        }

        // Cap only center-of-mass X velocity. Entry gets a stronger one-time absorb;
        // ordinary in-sand walking uses the full cap. Relative chunk velocity remains
        // intact so the native leg/body cycle is not flattened.
        float velocityCap = enteringThisFrame
            ? speedCap * EntryVelocityRetention
            : speedCap;
        ClampWholePlayerHorizontalVelocity(self, velocityCap);
    }

    private static float ResolveHorizontalSpeedCap(Player player)
    {
        float nativeRunSpeed = 4.1f;

        if (player?.dynamicRunSpeed != null && player.dynamicRunSpeed.Length > 0)
        {
            nativeRunSpeed = 0f;
            for (int i = 0; i < player.dynamicRunSpeed.Length; i++)
            {
                nativeRunSpeed = Mathf.Max(nativeRunSpeed, Mathf.Abs(player.dynamicRunSpeed[i]));
            }

            if (nativeRunSpeed < 0.1f)
            {
                nativeRunSpeed = 4.1f;
            }
        }

        return Mathf.Clamp(
            nativeRunSpeed * NativeRunSpeedScale,
            MinimumHorizontalCap,
            MaximumHorizontalCap);
    }

    private static bool TryFindCurrentQuicksand(Player player, out QuicksandZone zone)
    {
        zone = null;
        if (!CanTrack(player) || player.room.updateList == null)
        {
            return false;
        }

        for (int i = 0; i < player.room.updateList.Count; i++)
        {
            if (player.room.updateList[i] is not QuicksandZone candidate ||
                !IsUsableZone(candidate))
            {
                continue;
            }

            for (int j = 0; j < player.bodyChunks.Length; j++)
            {
                BodyChunk chunk = player.bodyChunks[j];
                if (chunk != null && PointTouchesQuicksand(chunk, candidate, chunk.pos))
                {
                    zone = candidate;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryFindSweptQuicksandContact(
        Player player,
        Vector2[] startPositions,
        out QuicksandZone zone,
        out float earliestT)
    {
        zone = null;
        earliestT = 1f;

        if (!CanTrack(player) ||
            player.room.updateList == null ||
            startPositions == null)
        {
            return false;
        }

        bool found = false;

        for (int i = 0; i < player.room.updateList.Count; i++)
        {
            if (player.room.updateList[i] is not QuicksandZone candidate ||
                !IsUsableZone(candidate))
            {
                continue;
            }

            int count = Mathf.Min(player.bodyChunks.Length, startPositions.Length);
            for (int j = 0; j < count; j++)
            {
                BodyChunk chunk = player.bodyChunks[j];
                if (chunk == null)
                {
                    continue;
                }

                Vector2 start = startPositions[j];
                Vector2 end = chunk.pos;
                float distance = Vector2.Distance(start, end);
                int samples = Mathf.Clamp(
                    Mathf.CeilToInt(distance / SweepSampleSpacing),
                    1,
                    MaxSweepSamples);

                for (int sample = 1; sample <= samples; sample++)
                {
                    float t = (float)sample / samples;
                    if (!PointTouchesQuicksand(
                            chunk,
                            candidate,
                            Vector2.Lerp(start, end, t)))
                    {
                        continue;
                    }

                    if (!found || t < earliestT)
                    {
                        found = true;
                        earliestT = t;
                        zone = candidate;
                    }

                    break;
                }
            }
        }

        return found;
    }

    private static bool PointTouchesQuicksand(
        BodyChunk chunk,
        QuicksandZone zone,
        Vector2 point)
    {
        if (chunk == null || !IsUsableZone(zone))
        {
            return false;
        }

        float radius = Mathf.Max(1f, chunk.rad);
        if (point.x < zone.startX - radius * 0.15f ||
            point.x > zone.endX + radius * 0.15f)
        {
            return false;
        }

        float u = zone.MaterialUAtWorldX(point.x);
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

        float bottomY = zone.PlacedObject.pos.y - zone.Data.BottomDepth;
        float depthLength = Mathf.Max(4f, surfacePoint.y - bottomY);
        float signedDepth = surfacePoint.y - point.y;

        return signedDepth >= -radius * SurfaceInfluenceRadii &&
               signedDepth <= depthLength + radius * 0.50f;
    }

    private static bool IsUsableZone(QuicksandZone zone)
    {
        return zone != null &&
               !zone.slatedForDeletetion &&
               zone.PlacedObject != null &&
               zone.PlacedObject.active &&
               zone.Data != null;
    }

    private static bool CanTrack(Player player)
    {
        return player != null &&
               player.room != null &&
               player.bodyChunks != null &&
               player.bodyChunks.Length > 0;
    }

    private static bool HasHorizontalInput(Player player)
    {
        return player?.input != null &&
               player.input.Length > 0 &&
               player.input[0].x != 0;
    }

    private static float AverageChunkX(Player player)
    {
        float total = 0f;
        int count = 0;

        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            BodyChunk chunk = player.bodyChunks[i];
            if (chunk == null)
            {
                continue;
            }

            total += chunk.pos.x;
            count++;
        }

        return count > 0 ? total / count : 0f;
    }

    private static float AverageChunkVelocityX(Player player)
    {
        float total = 0f;
        int count = 0;

        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            BodyChunk chunk = player.bodyChunks[i];
            if (chunk == null)
            {
                continue;
            }

            total += chunk.vel.x;
            count++;
        }

        return count > 0 ? total / count : 0f;
    }

    private static void RemoveWholePlayerHorizontalVelocity(Player player)
    {
        float averageVelocityX = AverageChunkVelocityX(player);
        if (Mathf.Abs(averageVelocityX) > Epsilon)
        {
            AddPlayerVelocityX(player, -averageVelocityX);
        }
    }

    private static void ClampWholePlayerHorizontalVelocity(Player player, float maxAbsVelocity)
    {
        float averageVelocityX = AverageChunkVelocityX(player);
        float clampedVelocityX = Mathf.Clamp(
            averageVelocityX,
            -maxAbsVelocity,
            maxAbsVelocity);
        float correction = clampedVelocityX - averageVelocityX;

        if (Mathf.Abs(correction) > Epsilon)
        {
            AddPlayerVelocityX(player, correction);
        }
    }

    private static void TranslatePlayerX(Player player, float deltaX)
    {
        if (Mathf.Abs(deltaX) <= Epsilon)
        {
            return;
        }

        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            BodyChunk chunk = player.bodyChunks[i];
            if (chunk != null)
            {
                chunk.pos.x += deltaX;
            }
        }
    }

    private static void AddPlayerVelocityX(Player player, float deltaX)
    {
        if (Mathf.Abs(deltaX) <= Epsilon)
        {
            return;
        }

        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            BodyChunk chunk = player.bodyChunks[i];
            if (chunk != null)
            {
                chunk.vel.x += deltaX;
            }
        }
    }
}
