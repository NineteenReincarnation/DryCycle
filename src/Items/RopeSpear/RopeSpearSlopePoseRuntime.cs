using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.Items.RopeSpear;

/// <summary>
/// Render-only angle adaptation for RopeSpear traversal that still relies on
/// vanilla beam topology underneath. Shallow spears adapt the standing-on-beam
/// pose; steep spears adapt vertical pole climbing/hanging poses. Gameplay physics,
/// collision and vanilla beam state remain untouched.
/// </summary>
internal static class RopeSpearSlopePoseRuntime
{
    private const float MinSlopeY = 0.08f;
    private const float MaxSlopeY = 0.72f;
    private const float MinVerticalSlopeX = 0.08f;
    private const float MaxVerticalSlopeX = 0.72f;
    private const float ShaftHalfLength = 31f;
    private const float MaxVerticalGap = 34f;
    private const float MaxVerticalBeamDistance = 42f;
    private const float LowerSurfaceOffset = 5f;
    private const float VerticalBodySideOffset = 5f;
    private const float MaxTiltBlend = 0.72f;
    private const float MaxVerticalTiltBlend = 0.96f;
    private const float PoseBlendStep = 0.22f;

    private enum PoseMode
    {
        StandOnSlope,
        ClimbVerticalSlope
    }

    private sealed class PoseState
    {
        internal float Blend;
    }

    private static readonly ConditionalWeakTable<Player, PoseState> States = new();
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.PlayerGraphics.Update += PlayerGraphics_Update;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.PlayerGraphics.Update -= PlayerGraphics_Update;
        _enabled = false;
    }

    private static void PlayerGraphics_Update(
        On.PlayerGraphics.orig_Update orig,
        PlayerGraphics self)
    {
        Player player = self?.player;
        if (player == null ||
            player.bodyChunks == null ||
            player.bodyChunks.Length < 2 ||
            !TryFindDiagonalBeamSupport(
                player,
                out PoseMode poseMode,
                out Vector2 shaftPoint,
                out Vector2 shaftDirection,
                out Vector2 supportNormal))
        {
            if (player != null)
            {
                States.GetOrCreateValue(player).Blend = 0f;
            }

            orig(self);
            return;
        }

        PoseState state = States.GetOrCreateValue(player);
        state.Blend = Mathf.Min(1f, state.Blend + PoseBlendStep);

        BodyChunk upper = player.bodyChunks[0];
        BodyChunk lower = player.bodyChunks[1];
        Vector2 originalUpper = upper.pos;
        Vector2 originalLower = lower.pos;

        float bodyLength = Mathf.Max(8f, Vector2.Distance(originalUpper, originalLower));
        Vector2 currentUp = originalUpper - originalLower;
        if (currentUp.sqrMagnitude < 0.0001f)
        {
            currentUp = Vector2.up;
        }
        else
        {
            currentUp.Normalize();
            if (currentUp.y < 0f)
            {
                currentUp = -currentUp;
            }
        }

        Vector2 visualUp;
        float poseBlend;
        Vector2 upperTarget;
        Vector2 lowerTarget;
        Vector2 handShaftUp = shaftDirection;
        Vector2 handSideNormal = supportNormal;

        if (poseMode == PoseMode.StandOnSlope)
        {
            float slopeAmount = Mathf.InverseLerp(
                MinSlopeY,
                MaxSlopeY,
                Mathf.Abs(shaftDirection.y));
            float tiltBlend = slopeAmount * MaxTiltBlend;
            visualUp = Vector2.Lerp(currentUp, supportNormal, tiltBlend);
            if (visualUp.sqrMagnitude < 0.0001f)
            {
                visualUp = supportNormal;
            }
            else
            {
                visualUp.Normalize();
            }

            lowerTarget = shaftPoint + supportNormal * LowerSurfaceOffset;
            upperTarget = lowerTarget + visualUp * bodyLength;
            poseBlend = state.Blend * Mathf.Lerp(0.72f, 1f, slopeAmount);
        }
        else
        {
            Vector2 shaftUp = shaftDirection;
            if (shaftUp.y < 0f)
            {
                shaftUp = -shaftUp;
            }

            Vector2 sideNormal = new(-shaftUp.y, shaftUp.x);
            if (Vector2.Dot(originalUpper - shaftPoint, sideNormal) < 0f)
            {
                sideNormal = -sideNormal;
            }

            float slopeAmount = Mathf.InverseLerp(
                MinVerticalSlopeX,
                MaxVerticalSlopeX,
                Mathf.Abs(shaftUp.x));
            float tiltBlend = slopeAmount * MaxVerticalTiltBlend;
            visualUp = Vector2.Lerp(currentUp, shaftUp, tiltBlend);
            if (visualUp.sqrMagnitude < 0.0001f)
            {
                visualUp = shaftUp;
            }
            else
            {
                visualUp.Normalize();
            }

            // Vanilla ClimbOnBeam keeps the upper body roughly five pixels to one
            // side of the hidden vertical beam. Recreate that relationship against
            // the real angled shaft, then rotate the rear chunk along the shaft.
            upperTarget = shaftPoint + sideNormal * VerticalBodySideOffset;
            lowerTarget = upperTarget - visualUp * bodyLength;
            poseBlend = state.Blend * Mathf.Lerp(0.78f, 1f, slopeAmount);
            handShaftUp = shaftUp;
            handSideNormal = sideNormal;
        }

        upper.pos = Vector2.Lerp(originalUpper, upperTarget, poseBlend);
        lower.pos = Vector2.Lerp(originalLower, lowerTarget, poseBlend);

        try
        {
            // PlayerGraphics.Update derives drawPositions, head, tail, hands and legs
            // from the body chunks. Feeding it the temporary pose gives a complete
            // visual rotation without changing player collision, velocity or beam
            // traversal logic.
            orig(self);

            Vector2 desiredLegDirection = -visualUp;
            if (self.legsDirection.sqrMagnitude < 0.0001f)
            {
                self.legsDirection = desiredLegDirection;
            }
            else
            {
                self.legsDirection = Vector2.Lerp(
                    self.legsDirection.normalized,
                    desiredLegDirection,
                    poseBlend * 0.85f).normalized;
            }

            if (poseMode == PoseMode.ClimbVerticalSlope)
            {
                PrepareVerticalClimbHands(
                    self,
                    shaftPoint,
                    handShaftUp,
                    handSideNormal,
                    poseBlend);
            }
        }
        finally
        {
            upper.pos = originalUpper;
            lower.pos = originalLower;
        }
    }

    private static void PrepareVerticalClimbHands(
        PlayerGraphics graphics,
        Vector2 shaftPoint,
        Vector2 shaftUp,
        Vector2 sideNormal,
        float poseBlend)
    {
        Player player = graphics?.player;
        if (player == null || graphics.hands == null)
        {
            return;
        }

        for (int i = 0; i < 2 && i < graphics.hands.Length; i++)
        {
            SlugcatHand hand = graphics.hands[i];
            if (hand == null)
            {
                continue;
            }

            Vector2 target;
            if (player.animation == Player.AnimationIndex.ClimbOnBeam)
            {
                float cycle = (float)player.animationFrame / 20f * Mathf.PI * 2f;
                float wave = (i == 1 != (player.flipDirection == 1))
                    ? Mathf.Cos(cycle)
                    : Mathf.Sin(cycle);
                bool trailingHand = i == 1 == (player.flipDirection == 1);
                float along = (trailingHand ? -3f : 3f) + 6f * wave;
                float side = trailingHand ? -1f : 1f;
                target = shaftPoint + shaftUp * along + sideNormal * side;
            }
            else if (player.animation == Player.AnimationIndex.HangUnderVerticalBeam)
            {
                target = shaftPoint +
                         shaftUp * (i == 0 ? 20f : 25f) +
                         sideNormal * (i == 0 ? -0.5f : 0.5f);
            }
            else
            {
                // GetUpToBeamTip/BeamTip already use useful vanilla hand behaviour;
                // the temporary body rotation is enough for those transition poses.
                continue;
            }

            hand.mode = Limb.Mode.HuntAbsolutePosition;
            hand.absoluteHuntPos = target;
            hand.huntSpeed = player.animation == Player.AnimationIndex.HangUnderVerticalBeam
                ? 10f
                : 8f;
            hand.quickness = player.animation == Player.AnimationIndex.HangUnderVerticalBeam
                ? 1f
                : 0.65f;

            // Hands are graphical body parts, not gameplay collision. Nudge their
            // current visual position as well so the first tilted frame does not show
            // them lingering on the invisible vertical beam while the torso rotates.
            hand.pos = Vector2.Lerp(hand.pos, target, poseBlend * 0.55f);
        }
    }

    private static bool TryFindDiagonalBeamSupport(
        Player player,
        out PoseMode poseMode,
        out Vector2 shaftPoint,
        out Vector2 shaftDirection,
        out Vector2 supportNormal)
    {
        poseMode = PoseMode.StandOnSlope;
        shaftPoint = Vector2.zero;
        shaftDirection = Vector2.right;
        supportNormal = Vector2.up;

        if (player?.room?.physicalObjects == null ||
            player.bodyMode != Player.BodyModeIndex.ClimbingOnBeam)
        {
            return false;
        }

        if (UsesTopOfBeamPose(player.animation) &&
            TryFindHorizontalTopologySupport(
                player,
                out shaftPoint,
                out shaftDirection,
                out supportNormal))
        {
            poseMode = PoseMode.StandOnSlope;
            return true;
        }

        if (UsesVerticalBeamPose(player.animation) &&
            TryFindVerticalTopologySupport(
                player,
                out shaftPoint,
                out shaftDirection,
                out supportNormal))
        {
            poseMode = PoseMode.ClimbVerticalSlope;
            return true;
        }

        return false;
    }

    private static bool TryFindHorizontalTopologySupport(
        Player player,
        out Vector2 shaftPoint,
        out Vector2 shaftDirection,
        out Vector2 supportNormal)
    {
        shaftPoint = Vector2.zero;
        shaftDirection = Vector2.right;
        supportNormal = Vector2.up;

        BodyChunk lower = player.bodyChunks[1];
        IntVector2 lowerTile = player.room.GetTilePosition(lower.pos);
        float bestScore = float.MaxValue;
        bool found = false;

        for (int layer = 0; layer < player.room.physicalObjects.Length; layer++)
        {
            var objects = player.room.physicalObjects[layer];
            for (int i = 0; i < objects.Count; i++)
            {
                if (objects[i] is not RopeSpear spear ||
                    spear.slatedForDeletetion ||
                    spear.mode != Weapon.Mode.StuckInWall ||
                    spear.abstractSpear == null ||
                    spear.abstractSpear.stuckInWallCycles <= 0 ||
                    !spear.stuckInWall.HasValue)
                {
                    continue;
                }

                Vector2 direction = spear.rotation;
                if (direction.sqrMagnitude < 0.0001f)
                {
                    continue;
                }
                direction.Normalize();

                // Positive stuckInWallCycles means vanilla created horizontal beam
                // tiles. Only shallow/45-degree RopeSpears need this adapter.
                if (Mathf.Abs(direction.y) < MinSlopeY ||
                    Mathf.Abs(direction.x) < Mathf.Abs(direction.y) ||
                    Mathf.Abs(direction.x) < 0.001f)
                {
                    continue;
                }

                IntVector2 anchorTile = player.room.GetTilePosition(spear.stuckInWall.Value);
                if (lowerTile.y != anchorTile.y ||
                    Mathf.Abs(lowerTile.x - anchorTile.x) > 1)
                {
                    continue;
                }

                Vector2 center = spear.firstChunk.pos;
                float along = (lower.pos.x - center.x) / direction.x;
                if (Mathf.Abs(along) > ShaftHalfLength)
                {
                    continue;
                }

                Vector2 point = center + direction * along;
                float verticalGap = Mathf.Abs(lower.pos.y - point.y);
                if (verticalGap > MaxVerticalGap)
                {
                    continue;
                }

                Vector2 normal = new(-direction.y, direction.x);
                if (normal.y < 0f)
                {
                    normal = -normal;
                }

                if (Vector2.Dot(player.mainBodyChunk.pos - point, normal) < -4f)
                {
                    continue;
                }

                float score = verticalGap + Mathf.Abs(along) * 0.05f;
                if (score >= bestScore)
                {
                    continue;
                }

                bestScore = score;
                shaftPoint = point;
                shaftDirection = direction;
                supportNormal = normal;
                found = true;
            }
        }

        return found;
    }

    private static bool TryFindVerticalTopologySupport(
        Player player,
        out Vector2 shaftPoint,
        out Vector2 shaftDirection,
        out Vector2 supportNormal)
    {
        shaftPoint = Vector2.zero;
        shaftDirection = Vector2.up;
        supportNormal = Vector2.right;

        Vector2 samplePosition = player.mainBodyChunk.pos;
        IntVector2 bodyTile = player.room.GetTilePosition(samplePosition);
        float bestScore = float.MaxValue;
        bool found = false;

        for (int layer = 0; layer < player.room.physicalObjects.Length; layer++)
        {
            var objects = player.room.physicalObjects[layer];
            for (int i = 0; i < objects.Count; i++)
            {
                if (objects[i] is not RopeSpear spear ||
                    spear.slatedForDeletetion ||
                    spear.mode != Weapon.Mode.StuckInWall ||
                    spear.abstractSpear == null ||
                    spear.abstractSpear.stuckInWallCycles >= 0 ||
                    !spear.stuckInWall.HasValue)
                {
                    continue;
                }

                Vector2 direction = spear.rotation;
                if (direction.sqrMagnitude < 0.0001f)
                {
                    continue;
                }
                direction.Normalize();

                // Negative stuckInWallCycles means vanilla generated vertical beam
                // tiles. Purely vertical spears already match vanilla, so only apply
                // this visual correction when the real shaft has a horizontal slope.
                if (Mathf.Abs(direction.x) < MinVerticalSlopeX ||
                    Mathf.Abs(direction.y) <= Mathf.Abs(direction.x) ||
                    Mathf.Abs(direction.y) < 0.001f)
                {
                    continue;
                }

                IntVector2 anchorTile = player.room.GetTilePosition(spear.stuckInWall.Value);
                if (Mathf.Abs(bodyTile.x - anchorTile.x) > 1 ||
                    Mathf.Abs(bodyTile.y - anchorTile.y) > 2)
                {
                    continue;
                }

                Vector2 center = spear.firstChunk.pos;
                Vector2 a = center - direction * ShaftHalfLength;
                Vector2 b = center + direction * ShaftHalfLength;
                ClosestPointOnSegment(samplePosition, a, b, out float t, out Vector2 point);

                float distance = Vector2.Distance(samplePosition, point);
                if (distance > MaxVerticalBeamDistance)
                {
                    continue;
                }

                // Prefer the spear whose real shaft is closest to the torso, with a
                // tiny endpoint penalty so a nearby middle section wins over a more
                // distant tip when multiple RopeSpears overlap the same beam tile.
                float endpointPenalty = Mathf.Min(t, 1f - t) < 0.05f ? 3f : 0f;
                float score = distance + endpointPenalty;
                if (score >= bestScore)
                {
                    continue;
                }

                Vector2 shaftUp = direction.y < 0f ? -direction : direction;
                Vector2 normal = new(-shaftUp.y, shaftUp.x);
                if (Vector2.Dot(samplePosition - point, normal) < 0f)
                {
                    normal = -normal;
                }

                bestScore = score;
                shaftPoint = point;
                shaftDirection = direction;
                supportNormal = normal;
                found = true;
            }
        }

        return found;
    }

    private static void ClosestPointOnSegment(
        Vector2 position,
        Vector2 a,
        Vector2 b,
        out float t,
        out Vector2 point)
    {
        Vector2 delta = b - a;
        float denominator = delta.sqrMagnitude;
        if (denominator <= 0.0001f)
        {
            t = 0f;
            point = a;
            return;
        }

        t = Mathf.Clamp01(Vector2.Dot(position - a, delta) / denominator);
        point = Vector2.Lerp(a, b, t);
    }

    private static bool UsesTopOfBeamPose(Player.AnimationIndex animation)
    {
        return animation == Player.AnimationIndex.StandOnBeam ||
               animation == Player.AnimationIndex.BeamTip ||
               animation == Player.AnimationIndex.GetUpOnBeam ||
               animation == Player.AnimationIndex.GetUpToBeamTip;
    }

    private static bool UsesVerticalBeamPose(Player.AnimationIndex animation)
    {
        return animation == Player.AnimationIndex.ClimbOnBeam ||
               animation == Player.AnimationIndex.HangUnderVerticalBeam ||
               animation == Player.AnimationIndex.GetUpToBeamTip ||
               animation == Player.AnimationIndex.BeamTip;
    }
}
