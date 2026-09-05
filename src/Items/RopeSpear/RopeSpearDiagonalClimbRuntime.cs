using RWCustom;
using UnityEngine;

namespace DryCycle.Items.RopeSpear;

/// <summary>
/// Restores RopeSpear's historical diagonal climbing input while keeping the
/// vanilla VineGrab state responsible for body placement, hand posing, swinging,
/// jump release, and vine attachment.
/// </summary>
internal static class RopeSpearDiagonalClimbRuntime
{
    private const float InputDeadZone = 0.05f;
    private const float MinAlongCursor = 12f;
    private const float MaxCursor = 30f;
    private const float PreservedSwingFactor = 0.35f;

    // Rope -> shallow shaft handoff. The old handoff required the slugcat to already
    // be very close to the hidden horizontal-beam target even though VineGrab keeps
    // the torso hanging below the rope endpoint. That creates a dead zone exactly at
    // the pose seen when the hands have reached the spear tail but the body cannot
    // move any farther upward. These values only widen the transition envelope; the
    // actual pull-up is still vanilla GetUpOnBeam and never teleports body chunks.
    private const float MountRemainingRopeDistance = 52f;
    private const float MountBodyToTailDistance = 62f;
    private const float MountBodyToBeamTargetDistance = 60f;
    private const int MountBeamSearchX = 4;
    private const int MountBeamSearchY = 2;

    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.Player.Update += Player_Update;
        RopeSpearSlopePoseRuntime.Enable();
        RopeSpearHandleAnchorSafetyRuntime.Enable();
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        RopeSpearHandleAnchorSafetyRuntime.Disable();
        RopeSpearSlopePoseRuntime.Disable();
        On.Player.Update -= Player_Update;
        _enabled = false;
    }

    private static void Player_Update(
        On.Player.orig_Update orig,
        Player self,
        bool eu)
    {
        // IMPORTANT: attempt the rope -> shaft handoff before vanilla VineGrab runs.
        // Vanilla disconnects a vine grab when the main body is farther than
        // 40 + VineRad from the current vine point. At the RopeSpear tail the hands
        // can reach the endpoint while the torso still hangs below that threshold,
        // so waiting until after orig() means animation has already become None and
        // there is nothing left to hand off. This pre-pass catches that exact frame.
        if (TryAssistMountOntoShaft(self))
        {
            orig(self, eu);
            return;
        }

        BiasVineCursorAlongRope(self);
        orig(self, eu);

        // Keep the post-pass as well. It covers cases where vanilla movement during
        // this frame moved the player into the mount envelope without first crossing
        // the disconnect threshold.
        TryAssistMountOntoShaft(self);
    }

    private static void BiasVineCursorAlongRope(Player player)
    {
        if (player?.animation != Player.AnimationIndex.VineGrab ||
            player.vinePos?.vine is not RopeSpear ||
            player.room?.climbableVines == null ||
            player.input == null ||
            player.input.Length == 0)
        {
            return;
        }

        Vector2 input = new(player.input[0].x, player.input[0].y);
        if (input.sqrMagnitude < 0.01f)
        {
            return;
        }

        if (input.sqrMagnitude > 1f)
        {
            input.Normalize();
        }

        Vector2 tangent = player.room.climbableVines.VineDir(player.vinePos);
        if (tangent.sqrMagnitude < 0.0001f)
        {
            return;
        }
        tangent.Normalize();

        // This is the important part of the pre-VineGrab implementation: project
        // world-space input directly onto the visible rope tangent. Up, Right, or a
        // diagonal combination can therefore advance on a sloped rope according to
        // the direction actually pressed instead of depending on vanilla's
        // goal-position angle heuristic.
        float alongInput = Vector2.Dot(input, tangent);
        float alongMagnitude = Mathf.Abs(alongInput);
        if (alongMagnitude <= InputDeadZone)
        {
            return;
        }

        Vector2 normal = new(-tangent.y, tangent.x);
        float preservedSwing =
            Vector2.Dot(player.vineClimbCursor, normal) * PreservedSwingFactor;

        // Keep enough tangent authority to make ClimbOnVineSpeed unambiguous even
        // when the body is hanging slightly off the rope. Vanilla Player.Update will
        // still add the ordinary SwimDir contribution and perform the actual climb.
        float tangentCursor = Mathf.Sign(alongInput) *
                              Mathf.Max(MinAlongCursor, MaxCursor * alongMagnitude);

        player.vineClimbCursor = Vector2.ClampMagnitude(
            tangent * tangentCursor + normal * preservedSwing,
            MaxCursor);
    }

    private static bool TryAssistMountOntoShaft(Player player)
    {
        if (player?.animation != Player.AnimationIndex.VineGrab ||
            player.vinePos?.vine is not RopeSpear spear ||
            player.room?.climbableVines == null ||
            player.input == null ||
            player.input.Length == 0 ||
            player.input[0].y <= 0 ||
            spear.mode != Weapon.Mode.StuckInWall ||
            spear.abstractPhysicalObject is not AbstractRopeSpear data ||
            data.stuckInWallCycles < 0)
        {
            return false;
        }

        float totalLength = player.room.climbableVines.TotalLength(spear);
        if (totalLength <= 0.001f)
        {
            return false;
        }

        float remaining = Mathf.Max(0f, 1f - player.vinePos.floatPos) * totalLength;
        if (remaining > MountRemainingRopeDistance)
        {
            return false;
        }

        int last = spear.TotalPositions() - 1;
        if (last < 0)
        {
            return false;
        }

        Vector2 spearTail = spear.Pos(last);
        if (!Custom.DistLess(
                player.mainBodyChunk.pos,
                spearTail,
                MountBodyToTailDistance))
        {
            return false;
        }

        if (!TryFindMountBeam(
                player.room,
                spear.firstChunk.pos,
                spearTail,
                player.mainBodyChunk.pos,
                out Vector2 beamCenter))
        {
            return false;
        }

        Vector2 pullupTarget = new(
            beamCenter.x,
            player.room.MiddleOfTile(beamCenter).y + 20f);

        if (!Custom.DistLess(
                player.mainBodyChunk.pos,
                pullupTarget,
                MountBodyToBeamTargetDistance))
        {
            return false;
        }

        // Relinquish the rope explicitly before starting the beam pull-up. Keeping
        // vinePos alive after changing animation can let later vine code reclaim the
        // same endpoint on the next frame and causes the familiar up/down loop.
        player.vinePos = null;
        player.vineGrabDelay = Mathf.Max(player.vineGrabDelay, 15);
        player.noGrabCounter = Mathf.Max(player.noGrabCounter, 15);
        player.flipDirection = spear.rotation.x >= 0f ? -1 : 1;
        player.pullupSoftlockSafety = 0;
        player.straightUpOnHorizontalBeam = true;
        player.forceFeetToHorizontalBeamTile = 20;
        player.upOnHorizontalBeamPos = pullupTarget;
        player.animation = Player.AnimationIndex.GetUpOnBeam;
        player.bodyMode = Player.BodyModeIndex.ClimbingOnBeam;
        player.standing = false;

        player.room.PlaySound(
            SoundID.Slugcat_Get_Up_On_Horizontal_Beam,
            player.mainBodyChunk,
            loop: false,
            0.75f,
            1f);
        return true;
    }

    private static bool TryFindMountBeam(
        Room room,
        Vector2 spearCenter,
        Vector2 spearTail,
        Vector2 playerPosition,
        out Vector2 beamCenter)
    {
        beamCenter = Vector2.zero;
        if (room == null)
        {
            return false;
        }

        float bestScore = float.MaxValue;
        bool found = false;

        SearchBeamNeighborhood(
            room,
            room.GetTilePosition(spearCenter),
            spearTail,
            playerPosition,
            ref bestScore,
            ref beamCenter,
            ref found);

        // Diagonal spears can leave their exposed tail one tile away from the
        // cardinal beam topology generated by Spear.ChangeMode. Searching around
        // the tail as well as the embedded center makes that layout mountable.
        SearchBeamNeighborhood(
            room,
            room.GetTilePosition(spearTail),
            spearTail,
            playerPosition,
            ref bestScore,
            ref beamCenter,
            ref found);

        return found;
    }

    private static void SearchBeamNeighborhood(
        Room room,
        IntVector2 origin,
        Vector2 spearTail,
        Vector2 playerPosition,
        ref float bestScore,
        ref Vector2 beamCenter,
        ref bool found)
    {
        for (int x = -MountBeamSearchX; x <= MountBeamSearchX; x++)
        {
            for (int y = -MountBeamSearchY; y <= MountBeamSearchY; y++)
            {
                IntVector2 tilePosition = origin + new IntVector2(x, y);
                Room.Tile tile = room.GetTile(tilePosition);
                if (!tile.horizontalBeam || tile.Solid)
                {
                    continue;
                }

                Vector2 center = room.MiddleOfTile(tilePosition);
                float score = Vector2.Distance(playerPosition, center) +
                              Vector2.Distance(spearTail, center) * 0.35f;
                if (score >= bestScore)
                {
                    continue;
                }

                bestScore = score;
                beamCenter = center;
                found = true;
            }
        }
    }
}
