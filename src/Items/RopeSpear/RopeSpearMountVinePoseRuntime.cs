using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.Items.RopeSpear;

/// <summary>
/// Keeps the diagonal RopeSpear endpoint bridge physically driven by vanilla
/// GetUpOnBeam, but renders that short bridge using the vanilla VineGrab pose.
/// This prevents the transition from looking like a wall/beam pull-up while still
/// preserving the already working non-teleport mounting physics underneath.
/// </summary>
internal static class RopeSpearMountVinePoseRuntime
{
    private const float MinSlopeY = 0.08f;
    private const float MaxSlopeRatio = 1f;
    private const float MaxTailDistance = 104f;
    private const float MaxPullupTargetDistance = 120f;
    private const float VineHandTravel = 20f;

    private sealed class VisualState
    {
        internal int ClimbFrame;
    }

    private readonly struct SavedPlayerVisualState
    {
        internal readonly Player.AnimationIndex Animation;
        internal readonly Player.BodyModeIndex BodyMode;
        internal readonly ClimbableVinesSystem.VinePosition VinePos;
        internal readonly int AnimationFrame;
        internal readonly bool Standing;

        internal SavedPlayerVisualState(Player player)
        {
            Animation = player.animation;
            BodyMode = player.bodyMode;
            VinePos = player.vinePos;
            AnimationFrame = player.animationFrame;
            Standing = player.standing;
        }

        internal void Restore(Player player)
        {
            player.animation = Animation;
            player.bodyMode = BodyMode;
            player.vinePos = VinePos;
            player.animationFrame = AnimationFrame;
            player.standing = Standing;
        }
    }

    private static readonly ConditionalWeakTable<Player, VisualState> States = new();
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.PlayerGraphics.Update += PlayerGraphics_Update;
        On.PlayerGraphics.DrawSprites += PlayerGraphics_DrawSprites;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.PlayerGraphics.DrawSprites -= PlayerGraphics_DrawSprites;
        On.PlayerGraphics.Update -= PlayerGraphics_Update;
        _enabled = false;
    }

    private static void PlayerGraphics_Update(
        On.PlayerGraphics.orig_Update orig,
        PlayerGraphics self)
    {
        Player player = self?.player;
        if (!TryResolveMountVineVisual(player, out RopeSpear spear, out float visualFloatPos))
        {
            if (player != null)
            {
                States.GetOrCreateValue(player).ClimbFrame = 0;
            }

            orig(self);
            return;
        }

        VisualState state = States.GetOrCreateValue(player);
        state.ClimbFrame++;
        if (state.ClimbFrame > 30)
        {
            state.ClimbFrame = 0;
        }

        SavedPlayerVisualState saved = new(player);
        ApplyVineVisual(player, spear, visualFloatPos, state.ClimbFrame);

        try
        {
            // SlugcatHand.Update is called from PlayerGraphics.Update. Temporarily
            // presenting this bridge as VineGrab makes it execute Rain World's own
            // alternating vine-hand logic instead of GetUpOnBeam's terrain-hand pose.
            orig(self);
        }
        finally
        {
            saved.Restore(player);
        }
    }

    private static void PlayerGraphics_DrawSprites(
        On.PlayerGraphics.orig_DrawSprites orig,
        PlayerGraphics self,
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        float timeStacker,
        Vector2 camPos)
    {
        Player player = self?.player;
        if (!TryResolveMountVineVisual(player, out RopeSpear spear, out float visualFloatPos))
        {
            orig(self, sLeaser, rCam, timeStacker, camPos);
            return;
        }

        VisualState state = States.GetOrCreateValue(player);
        SavedPlayerVisualState saved = new(player);
        ApplyVineVisual(player, spear, visualFloatPos, state.ClimbFrame);

        try
        {
            // DrawSprites also branches on bodyMode/animation for leg sprites and the
            // special "hands on terrain" overlays. Keep the temporary VineGrab view
            // active here as well so Update and rendering agree for the whole frame.
            orig(self, sLeaser, rCam, timeStacker, camPos);
        }
        finally
        {
            saved.Restore(player);
        }
    }

    private static void ApplyVineVisual(
        Player player,
        RopeSpear spear,
        float visualFloatPos,
        int climbFrame)
    {
        player.animation = Player.AnimationIndex.VineGrab;
        player.bodyMode = Player.BodyModeIndex.Default;
        player.vinePos = new ClimbableVinesSystem.VinePosition(spear, visualFloatPos);
        player.animationFrame = climbFrame;
        player.standing = false;
    }

    private static bool TryResolveMountVineVisual(
        Player player,
        out RopeSpear spear,
        out float visualFloatPos)
    {
        spear = null;
        visualFloatPos = 0f;

        if (player?.room?.physicalObjects == null ||
            player.room.climbableVines == null ||
            player.animation != Player.AnimationIndex.GetUpOnBeam ||
            player.bodyMode != Player.BodyModeIndex.ClimbingOnBeam)
        {
            return false;
        }

        float bestScore = float.MaxValue;
        float bestLength = 0f;

        for (int layer = 0; layer < player.room.physicalObjects.Length; layer++)
        {
            var objects = player.room.physicalObjects[layer];
            for (int i = 0; i < objects.Count; i++)
            {
                if (objects[i] is not RopeSpear candidate ||
                    candidate.slatedForDeletetion ||
                    candidate.mode != Weapon.Mode.StuckInWall ||
                    candidate.abstractSpear == null ||
                    candidate.abstractSpear.stuckInWallCycles <= 0 ||
                    !candidate.CurrentlyClimbable() ||
                    !player.room.climbableVines.vines.Contains(candidate))
                {
                    continue;
                }

                Vector2 direction = candidate.rotation;
                if (direction.sqrMagnitude < 0.0001f)
                {
                    continue;
                }
                direction.Normalize();

                float absX = Mathf.Abs(direction.x);
                float absY = Mathf.Abs(direction.y);
                if (absY < MinSlopeY ||
                    absX < absY ||
                    absY / Mathf.Max(0.001f, absX) > MaxSlopeRatio)
                {
                    continue;
                }

                int last = candidate.TotalPositions() - 1;
                if (last < 0)
                {
                    continue;
                }

                Vector2 tail = candidate.Pos(last);
                float bodyDistance = Vector2.Distance(player.mainBodyChunk.pos, tail);
                if (bodyDistance > MaxTailDistance)
                {
                    continue;
                }

                float targetDistance = Vector2.Distance(
                    player.upOnHorizontalBeamPos,
                    candidate.firstChunk.pos);
                if (targetDistance > MaxPullupTargetDistance)
                {
                    continue;
                }

                float totalLength = player.room.climbableVines.TotalLength(candidate);
                if (totalLength <= 0.001f)
                {
                    continue;
                }

                float score = bodyDistance + targetDistance * 0.25f;
                if (score >= bestScore)
                {
                    continue;
                }

                bestScore = score;
                bestLength = totalLength;
                spear = candidate;
            }
        }

        if (spear == null)
        {
            return false;
        }

        // Vanilla SlugcatHand samples about +/-20 px along the current vine while
        // alternating the two hands. Put the visual cursor one hand-span below the
        // endpoint so that complete motion stays on the last section of real rope,
        // rather than extrapolating beyond floatPos 1 into invalid vine space.
        float handSpan = Mathf.Clamp(VineHandTravel / bestLength, 0.005f, 0.25f);
        visualFloatPos = Mathf.Clamp01(1f - handSpan);
        return true;
    }
}
