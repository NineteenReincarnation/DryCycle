using System.Runtime.CompilerServices;
using RWCustom;
using UnityEngine;

namespace DryCycle.Items.RopeSpear;

/// <summary>
/// Keeps vanilla VineGrab responsible for ordinary RopeSpear climbing, but owns the
/// endpoint handoff onto the spear shaft. The handoff is deliberately conservative:
/// it only enters a vanilla beam animation when that animation's own tile/distance
/// requirements are already true, and it restores VineGrab if another hook starts an
/// invalid beam transition that vanilla would immediately cancel.
/// </summary>
internal static class RopeSpearDiagonalClimbRuntime
{
    private const float InputDeadZone = 0.05f;
    private const float MinAlongCursor = 12f;
    private const float MaxCursor = 30f;
    private const float PreservedSwingFactor = 0.35f;

    private const float MountRemainingRopeDistance = 58f;
    private const float MountBodyToTailDistance = 72f;
    private const float HorizontalVanillaTargetDistance = 24f;
    private const int MountBeamSearchX = 4;
    private const int MountBeamSearchY = 2;
    private const int EndpointRecoveryFrames = 12;
    private const float EndpointRecoveryRopeDistance = 72f;
    private const float EndpointRecoveryBodyDistance = 84f;

    private sealed class EndpointRecoveryState
    {
        internal RopeSpear Spear;
        internal float FloatPos;
        internal int FramesLeft;
    }

    private static readonly ConditionalWeakTable<Player, EndpointRecoveryState> EndpointRecovery = new();
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
        EndpointRecoveryState recovery = self == null
            ? null
            : EndpointRecovery.GetOrCreateValue(self);

        // RopeSpearHooks still contains an older horizontal-only post-update mount
        // path. Depending on HookGen ordering that path can run after this hook and
        // leave the player in GetUpOnBeam even though vanilla's own 25 px / beam-tile
        // requirements are false. Repair that state before the next vanilla update.
        TryRecoverInvalidEndpointState(self, recovery);

        bool intentionalRelease = IsIntentionalVineJump(self);
        CaptureEndpointRecovery(self, recovery);

        bool mountedBeforeVanilla = TryAssistMountOntoShaft(self);
        if (!mountedBeforeVanilla)
        {
            BiasVineCursorAlongRope(self);
        }

        orig(self, eu);

        // If vanilla or another hook discarded VineGrab while the player was merely
        // holding Up at the spear endpoint, restore the same rope position instead of
        // allowing an unexplained fall. Jump remains an intentional release and is
        // never recovered.
        if (!intentionalRelease)
        {
            TryRecoverInvalidEndpointState(self, recovery);
        }
        else
        {
            ClearRecovery(recovery);
            return;
        }

        // Vanilla movement during this frame may have brought the body into a safe
        // beam tile. Hand off now if possible; otherwise remain on VineGrab and try
        // again next frame.
        if (self?.animation == Player.AnimationIndex.VineGrab &&
            self.vinePos?.vine is RopeSpear)
        {
            CaptureEndpointRecovery(self, recovery);
            TryAssistMountOntoShaft(self);
        }
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

        float alongInput = Vector2.Dot(input, tangent);
        float alongMagnitude = Mathf.Abs(alongInput);
        if (alongMagnitude <= InputDeadZone)
        {
            return;
        }

        Vector2 normal = new(-tangent.y, tangent.x);
        float preservedSwing =
            Vector2.Dot(player.vineClimbCursor, normal) * PreservedSwingFactor;

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
            data.stuckInWallCycles == 0 ||
            !HasFixedClimbAnchors(spear))
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

        // Spear.ChangeMode encodes its generated traversal topology in the sign of
        // stuckInWallCycles: positive = horizontalBeam, negative = verticalBeam.
        // The previous implementation returned immediately for negative values and
        // then searched only horizontalBeam tiles, so steep/vertical RopeSpears could
        // never hand off at all and VineGrab eventually dropped the player.
        if (data.stuckInWallCycles > 0)
        {
            return TryEnterHorizontalShaft(player, spear, spearTail);
        }

        return TryEnterVerticalShaft(player, spear);
    }

    private static bool TryEnterHorizontalShaft(
        Player player,
        RopeSpear spear,
        Vector2 spearTail)
    {
        if (!TryFindHorizontalMountBeam(
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

        // Vanilla GetUpOnBeam immediately cancels itself when neither body chunk is
        // on a horizontal beam OR when the main body is >=25 px from this target.
        // Do not enter the animation earlier than vanilla can actually sustain it.
        if (!BodyTouchesHorizontalBeam(player) ||
            !Custom.DistLess(
                player.mainBodyChunk.pos,
                pullupTarget,
                HorizontalVanillaTargetDistance))
        {
            return false;
        }

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

    private static bool TryEnterVerticalShaft(Player player, RopeSpear spear)
    {
        Room room = player.room;
        if (room == null)
        {
            return false;
        }

        IntVector2 bodyTile = room.GetTilePosition(player.mainBodyChunk.pos);
        Vector2 beamCenter;
        Player.AnimationIndex nextAnimation;
        bool standing;

        if (room.GetTile(bodyTile).verticalBeam)
        {
            beamCenter = room.MiddleOfTile(bodyTile);
            nextAnimation = Player.AnimationIndex.ClimbOnBeam;
            standing = true;
        }
        else
        {
            IntVector2 tileAbove = bodyTile + new IntVector2(0, 1);
            if (!room.GetTile(tileAbove).verticalBeam)
            {
                return false;
            }

            // This is exactly the condition vanilla HangUnderVerticalBeam checks on
            // every frame: the tile one cell above the main body must be a vertical
            // beam. It gives the rope endpoint a natural, non-teleport transition
            // into a steep spear from below.
            beamCenter = room.MiddleOfTile(tileAbove);
            nextAnimation = Player.AnimationIndex.HangUnderVerticalBeam;
            standing = false;
        }

        player.vinePos = null;
        player.vineGrabDelay = Mathf.Max(player.vineGrabDelay, 15);
        player.noGrabCounter = Mathf.Max(player.noGrabCounter, 15);
        player.flipDirection = player.mainBodyChunk.pos.x < beamCenter.x ? -1 : 1;
        player.animationFrame = 0;
        player.animation = nextAnimation;
        player.bodyMode = Player.BodyModeIndex.ClimbingOnBeam;
        player.standing = standing;

        if (nextAnimation == Player.AnimationIndex.ClimbOnBeam)
        {
            player.room.PlaySound(
                SoundID.Slugcat_Climb_Up_Vertical_Beam,
                player.mainBodyChunk,
                loop: false,
                0.65f,
                1f);
        }

        return true;
    }

    private static void CaptureEndpointRecovery(
        Player player,
        EndpointRecoveryState recovery)
    {
        if (recovery == null)
        {
            return;
        }

        if (player?.animation != Player.AnimationIndex.VineGrab ||
            player.vinePos?.vine is not RopeSpear spear ||
            player.room?.climbableVines == null ||
            !HasFixedClimbAnchors(spear))
        {
            if (recovery.FramesLeft > 0)
            {
                recovery.FramesLeft--;
            }
            return;
        }

        float totalLength = player.room.climbableVines.TotalLength(spear);
        if (totalLength <= 0.001f)
        {
            return;
        }

        float remaining = Mathf.Max(0f, 1f - player.vinePos.floatPos) * totalLength;
        Vector2 spearTail = spear.Pos(spear.TotalPositions() - 1);
        if (remaining > EndpointRecoveryRopeDistance ||
            !Custom.DistLess(
                player.mainBodyChunk.pos,
                spearTail,
                EndpointRecoveryBodyDistance))
        {
            if (recovery.Spear == spear)
            {
                ClearRecovery(recovery);
            }
            return;
        }

        recovery.Spear = spear;
        recovery.FloatPos = player.vinePos.floatPos;
        recovery.FramesLeft = EndpointRecoveryFrames;
    }

    private static void TryRecoverInvalidEndpointState(
        Player player,
        EndpointRecoveryState recovery)
    {
        if (player == null ||
            recovery == null ||
            recovery.FramesLeft <= 0 ||
            recovery.Spear == null)
        {
            return;
        }

        RopeSpear spear = recovery.Spear;
        if (player.dead ||
            !player.Consious ||
            player.room == null ||
            spear.room != player.room ||
            !HasFixedClimbAnchors(spear))
        {
            ClearRecovery(recovery);
            return;
        }

        if (player.animation == Player.AnimationIndex.VineGrab)
        {
            if (player.vinePos?.vine == spear)
            {
                recovery.FloatPos = player.vinePos.floatPos;
                recovery.FramesLeft = EndpointRecoveryFrames;
            }
            else
            {
                ClearRecovery(recovery);
            }
            return;
        }

        if (player.animation == Player.AnimationIndex.GetUpOnBeam)
        {
            if (HorizontalMountStateIsValid(player))
            {
                ClearRecovery(recovery);
                return;
            }

            RestoreEndpointVineGrab(player, recovery);
            return;
        }

        if (player.animation == Player.AnimationIndex.ClimbOnBeam ||
            player.animation == Player.AnimationIndex.HangUnderVerticalBeam)
        {
            if (VerticalMountStateIsValid(player))
            {
                ClearRecovery(recovery);
                return;
            }

            RestoreEndpointVineGrab(player, recovery);
            return;
        }

        // None is the failure state produced by both vanilla VineGrab's distance
        // cutoff and an invalid GetUpOnBeam/ClimbOnBeam transition. While the player
        // is in the endpoint grace window, convert that unexplained drop back into
        // the previous rope grab. Other named animations are treated as intentional
        // state changes and are left alone.
        if (player.animation == Player.AnimationIndex.None)
        {
            RestoreEndpointVineGrab(player, recovery);
            return;
        }

        ClearRecovery(recovery);
    }

    private static void RestoreEndpointVineGrab(
        Player player,
        EndpointRecoveryState recovery)
    {
        RopeSpear spear = recovery?.Spear;
        if (player?.room?.climbableVines == null ||
            spear == null ||
            !HasFixedClimbAnchors(spear))
        {
            ClearRecovery(recovery);
            return;
        }

        float totalLength = player.room.climbableVines.TotalLength(spear);
        float safeFloat = Mathf.Clamp01(recovery.FloatPos);
        if (totalLength > 0.001f)
        {
            // Never restore to exactly 1.0. ClimbOnVineSpeed has a special endpoint
            // branch that returns -1 at floatPos==1 regardless of input direction.
            safeFloat = Mathf.Min(safeFloat, 1f - 0.75f / totalLength);
        }

        player.animation = Player.AnimationIndex.VineGrab;
        player.vinePos = new ClimbableVinesSystem.VinePosition(spear, safeFloat);
        player.vineGrabDelay = 0;
        player.bodyMode = Player.BodyModeIndex.Default;
        player.standing = false;
        player.wantToGrab = 0;

        recovery.FloatPos = safeFloat;
        recovery.FramesLeft = EndpointRecoveryFrames;
    }

    private static bool HorizontalMountStateIsValid(Player player)
    {
        return player?.room != null &&
               BodyTouchesHorizontalBeam(player) &&
               Custom.DistLess(
                   player.mainBodyChunk.pos,
                   player.upOnHorizontalBeamPos,
                   25f);
    }

    private static bool VerticalMountStateIsValid(Player player)
    {
        if (player?.room == null)
        {
            return false;
        }

        if (player.animation == Player.AnimationIndex.ClimbOnBeam)
        {
            return player.room.GetTile(player.mainBodyChunk.pos).verticalBeam;
        }

        if (player.animation == Player.AnimationIndex.HangUnderVerticalBeam)
        {
            return player.room.GetTile(
                player.mainBodyChunk.pos + new Vector2(0f, 20f)).verticalBeam;
        }

        return false;
    }

    private static bool BodyTouchesHorizontalBeam(Player player)
    {
        if (player?.room == null || player.bodyChunks == null)
        {
            return false;
        }

        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            BodyChunk chunk = player.bodyChunks[i];
            if (chunk != null && player.room.GetTile(chunk.pos).horizontalBeam)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsIntentionalVineJump(Player player)
    {
        return player?.animation == Player.AnimationIndex.VineGrab &&
               player.input != null &&
               player.input.Length > 1 &&
               player.input[0].jmp &&
               !player.input[1].jmp;
    }

    private static bool HasFixedClimbAnchors(RopeSpear spear)
    {
        if (spear == null ||
            spear.mode != Weapon.Mode.StuckInWall ||
            spear.room?.physicalObjects == null ||
            spear.abstractPhysicalObject == null)
        {
            return false;
        }

        EntityID spearId = spear.abstractPhysicalObject.ID;
        for (int layer = 0; layer < spear.room.physicalObjects.Length; layer++)
        {
            var objects = spear.room.physicalObjects[layer];
            for (int i = 0; i < objects.Count; i++)
            {
                if (objects[i] is RopeHandle handle &&
                    !handle.slatedForDeletetion &&
                    handle.ParentSpearID == spearId &&
                    handle.Anchored)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void ClearRecovery(EndpointRecoveryState recovery)
    {
        if (recovery == null)
        {
            return;
        }

        recovery.Spear = null;
        recovery.FloatPos = 0f;
        recovery.FramesLeft = 0;
    }

    private static bool TryFindHorizontalMountBeam(
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

        SearchHorizontalBeamNeighborhood(
            room,
            room.GetTilePosition(spearCenter),
            spearTail,
            playerPosition,
            ref bestScore,
            ref beamCenter,
            ref found);

        SearchHorizontalBeamNeighborhood(
            room,
            room.GetTilePosition(spearTail),
            spearTail,
            playerPosition,
            ref bestScore,
            ref beamCenter,
            ref found);

        return found;
    }

    private static void SearchHorizontalBeamNeighborhood(
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
