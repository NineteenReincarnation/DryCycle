using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.Items.RopeSpear;

/// <summary>
/// Render-only slope adaptation for players standing on a diagonal RopeSpear that
/// still uses vanilla horizontal-beam traversal underneath. Physics remains vanilla;
/// only PlayerGraphics sees temporarily tilted body chunks so the slugcat visually
/// follows the real spear shaft instead of standing upright on the hidden beam.
/// </summary>
internal static class RopeSpearSlopePoseRuntime
{
    private const float MinSlopeY = 0.08f;
    private const float MaxSlopeY = 0.72f;
    private const float ShaftHalfLength = 31f;
    private const float MaxVerticalGap = 34f;
    private const float LowerSurfaceOffset = 5f;
    private const float MaxTiltBlend = 0.72f;
    private const float PoseBlendStep = 0.22f;

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

        float slopeAmount = Mathf.InverseLerp(
            MinSlopeY,
            MaxSlopeY,
            Mathf.Abs(shaftDirection.y));
        float tiltBlend = slopeAmount * MaxTiltBlend;
        Vector2 visualUp = Vector2.Lerp(currentUp, supportNormal, tiltBlend);
        if (visualUp.sqrMagnitude < 0.0001f)
        {
            visualUp = supportNormal;
        }
        else
        {
            visualUp.Normalize();
        }

        Vector2 lowerTarget = shaftPoint + supportNormal * LowerSurfaceOffset;
        Vector2 upperTarget = lowerTarget + visualUp * bodyLength;
        float poseBlend = state.Blend * Mathf.Lerp(0.72f, 1f, slopeAmount);

        upper.pos = Vector2.Lerp(originalUpper, upperTarget, poseBlend);
        lower.pos = Vector2.Lerp(originalLower, lowerTarget, poseBlend);

        try
        {
            // PlayerGraphics.Update derives drawPositions, head, tail, hands and legs
            // from the body chunks. Feeding it the temporary pose gives us a complete
            // visual tilt without changing player collision, velocity or beam logic.
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
        }
        finally
        {
            upper.pos = originalUpper;
            lower.pos = originalLower;
        }
    }

    private static bool TryFindDiagonalBeamSupport(
        Player player,
        out Vector2 shaftPoint,
        out Vector2 shaftDirection,
        out Vector2 supportNormal)
    {
        shaftPoint = Vector2.zero;
        shaftDirection = Vector2.right;
        supportNormal = Vector2.up;

        if (player?.room?.physicalObjects == null ||
            player.bodyMode != Player.BodyModeIndex.ClimbingOnBeam ||
            !UsesTopOfBeamPose(player.animation))
        {
            return false;
        }

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
                // tiles. Only shallow/45-degree RopeSpears need this render adapter;
                // steep spears use vertical-beam traversal instead.
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

    private static bool UsesTopOfBeamPose(Player.AnimationIndex animation)
    {
        return animation == Player.AnimationIndex.StandOnBeam ||
               animation == Player.AnimationIndex.BeamTip ||
               animation == Player.AnimationIndex.GetUpOnBeam ||
               animation == Player.AnimationIndex.GetUpToBeamTip;
    }
}
