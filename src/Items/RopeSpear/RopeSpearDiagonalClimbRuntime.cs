using System.Runtime.CompilerServices;
using RWCustom;
using UnityEngine;

namespace DryCycle.Items.RopeSpear;

/// <summary>
/// Keeps vanilla VineGrab responsible for ordinary RopeSpear climbing, but owns the
/// endpoint handoff onto the spear shaft. Horizontal diagonal spears need a short
/// staged pull-up because vanilla only creates cardinal beam tiles around the spear's
/// embedded center while the visible rope reaches the real diagonal tail.
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

    // A shallow diagonal spear can put its real tail one or even two tiles below the
    // horizontalBeam row that vanilla generated at the embedded center. Waiting for
    // BodyTouchesHorizontalBeam therefore deadlocks: VineGrab cannot move past the
    // rope endpoint, but GetUpOnBeam refuses to start until a body chunk is already
    // in that hidden row. The bridge keeps the vanilla GetUpOnBeam animation and its
    // body physics, but temporarily exposes only the main body's current tile as a
    // beam and advances the pull-up target in <=20 px steps until the real beam row
    // is reached. No body chunk is teleported.
    private const int HorizontalBridgeFrames = 80;
    private const float HorizontalBridgeStepTargetDistance = 20f;
    private const float HorizontalBridgeMaxDistance = 96f;

    private const int EndpointRecoveryFrames = 12;
    private const float EndpointRecoveryRopeDistance = 72f;
    private const float EndpointRecoveryBodyDistance = 84f;

    private sealed class EndpointRecoveryState
    {
        internal RopeSpear Spear;
        internal float FloatPos;
        internal int FramesLeft;
    }

    private sealed class HorizontalMountBridgeState
    {
        internal RopeSpear Spear;
        internal Vector2 BeamCenter;
        internal Vector2 FinalTarget;
        internal int FramesLeft;
        internal bool ProxyBeamInjected;
        internal IntVector2 ProxyBeamTile;
        internal bool ProxyBeamWasHorizontal;
    }

    private static readonly ConditionalWeakTable<Player, EndpointRecoveryState> EndpointRecovery = new();
    private static readonly ConditionalWeakTable<Player, HorizontalMountBridgeState> HorizontalMountBridges = new();
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
        HorizontalMountBridgeState bridge = self == null
            ? null
            : HorizontalMountBridges.GetOrCreateValue(self);

        // Never leave a synthetic beam flag behind if a previous frame ended early.
        RestoreProxyHorizontalBeam(self, bridge);

        // Once the endpoint bridge has started it owns this update until the real
        // vanilla beam row is reached or the player cancels. Running endpoint
        // recovery before this would mistake the intentionally staged GetUpOnBeam
        // state for an invalid transition and put the player back on the rope.
        if (BridgeActive(self, bridge))
        {
            if (ShouldCancelHorizontalBridge(self, bridge))
            {
                CancelHorizontalBridge(self, bridge, recovery, restoreRope: !IsIntentionalBridgeJump(self));
                orig(self, eu);
                return;
            }

            PrepareHorizontalBridgeForVanilla(self, bridge);
            orig(self, eu);
            RestoreProxyHorizontalBeam(self, bridge);
            FinishHorizontalBridgeFrame(self, bridge, recovery);
            return;
        }

        // RopeSpearHooks still contains an older horizontal-only post-update mount
        // path. Depending on HookGen ordering that path can leave the player in
        // GetUpOnBeam even though vanilla's own requirements are false. Repair that
        // state before the next ordinary update. A staged bridge is excluded above.
        TryRecoverInvalidEndpointState(self, recovery);

        bool intentionalRelease = IsIntentionalVineJump(self);
        CaptureEndpointRecovery(self, recovery);

        bool mountedBeforeVanilla = TryAssistMountOntoShaft(self);

        // TryAssistMountOntoShaft can start the staged horizontal bridge this frame.
        // Prepare its proxy beam before vanilla sees GetUpOnBeam.
        if (BridgeActive(self, bridge))
        {
            PrepareHorizontalBridgeForVanilla(self, bridge);
            orig(self, eu);
            RestoreProxyHorizontalBeam(self, bridge);
            FinishHorizontalBridgeFrame(self, bridge, recovery);
            return;
        }

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

        // Vanilla movement during this frame may have brought the player into the
        // endpoint envelope. Start a direct handoff or the staged horizontal bridge.
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

        // If vanilla can already sustain GetUpOnBeam, enter it directly.
        if (BodyTouchesHorizontalBeam(player) &&
            Custom.DistLess(
                player.mainBodyChunk.pos,
                pullupTarget,
                HorizontalVanillaTargetDistance))
        {
            EnterHorizontalGetUpOnBeam(player, spear, pullupTarget, playSound: true);
            return true;
        }

        // This is the missing diagonal-tail case. The real spear tail is reachable,
        // but the cardinal horizontalBeam row may be above/behind it. Start a staged
        // vanilla pull-up rather than requiring the impossible condition that the
        // player already be standing in that hidden row while still on VineGrab.
        if (!Custom.DistLess(
                player.mainBodyChunk.pos,
                pullupTarget,
                HorizontalBridgeMaxDistance))
        {
            return false;
        }

        BeginHorizontalMountBridge(player, spear, beamCenter, pullupTarget);
        return true;
    }

    private static void EnterHorizontalGetUpOnBeam(
        Player player,
        RopeSpear spear,
        Vector2 pullupTarget,
        bool playSound)
    {
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

        if (playSound)
        {
            player.room.PlaySound(
                SoundID.Slugcat_Get_Up_On_Horizontal_Beam,
                player.mainBodyChunk,
                loop: false,
                0.75f,
                1f);
        }
    }

    private static void BeginHorizontalMountBridge(
        Player player,
        RopeSpear spear,
        Vector2 beamCenter,
        Vector2 pullupTarget)
    {
        HorizontalMountBridgeState bridge = HorizontalMountBridges.GetOrCreateValue(player);
        RestoreProxyHorizontalBeam(player, bridge);

        bridge.Spear = spear;
        bridge.BeamCenter = beamCenter;
        bridge.FinalTarget = pullupTarget;
        bridge.FramesLeft = HorizontalBridgeFrames;

        EnterHorizontalGetUpOnBeam(player, spear, pullupTarget, playSound: true);
    }

    private static bool BridgeActive(Player player, HorizontalMountBridgeState bridge)
    {
        return player != null &&
               bridge != null &&
               bridge.FramesLeft > 0 &&
               bridge.Spear != null;
    }

    private static bool ShouldCancelHorizontalBridge(
        Player player,
        HorizontalMountBridgeState bridge)
    {
        if (!BridgeActive(player, bridge) ||
            player.dead ||
            !player.Consious ||
            player.room == null ||
            bridge.Spear.room != player.room ||
            bridge.Spear.mode != Weapon.Mode.StuckInWall ||
            !HasFixedClimbAnchors(bridge.Spear))
        {
            return true;
        }

        if (player.input == null || player.input.Length == 0)
        {
            return true;
        }

        if (IsIntentionalBridgeJump(player))
        {
            return true;
        }

        // Releasing Up means "stop mounting" rather than "fall off". The caller
        // restores the endpoint VineGrab when possible.
        return player.input[0].y <= 0;
    }

    private static bool IsIntentionalBridgeJump(Player player)
    {
        return player?.input != null &&
               player.input.Length > 1 &&
               player.input[0].jmp &&
               !player.input[1].jmp;
    }

    private static void PrepareHorizontalBridgeForVanilla(
        Player player,
        HorizontalMountBridgeState bridge)
    {
        if (!BridgeActive(player, bridge) || player.room == null)
        {
            return;
        }

        RopeSpear spear = bridge.Spear;

        // Once the real beam prerequisites are true, stop faking anything. Vanilla
        // can take the final GetUpOnBeam step by itself.
        if (BodyTouchesHorizontalBeam(player) &&
            Custom.DistLess(
                player.mainBodyChunk.pos,
                bridge.FinalTarget,
                HorizontalVanillaTargetDistance))
        {
            EnterHorizontalGetUpOnBeam(player, spear, bridge.FinalTarget, playSound: false);
            return;
        }

        IntVector2 proxyPosition = player.room.GetTilePosition(player.mainBodyChunk.pos);
        Room.Tile proxyTile = player.room.GetTile(proxyPosition);
        if (!proxyTile.Solid)
        {
            bridge.ProxyBeamTile = proxyPosition;
            bridge.ProxyBeamWasHorizontal = proxyTile.horizontalBeam;
            bridge.ProxyBeamInjected = true;
            proxyTile.horizontalBeam = true;
        }

        Vector2 delta = bridge.FinalTarget - player.mainBodyChunk.pos;
        Vector2 stagedTarget = bridge.FinalTarget;
        if (delta.sqrMagnitude > HorizontalBridgeStepTargetDistance * HorizontalBridgeStepTargetDistance)
        {
            stagedTarget = player.mainBodyChunk.pos +
                           delta.normalized * HorizontalBridgeStepTargetDistance;
        }

        EnterHorizontalGetUpOnBeam(player, spear, stagedTarget, playSound: false);
        player.pullupSoftlockSafety = 0;
    }

    private static void RestoreProxyHorizontalBeam(
        Player player,
        HorizontalMountBridgeState bridge)
    {
        if (bridge == null || !bridge.ProxyBeamInjected)
        {
            return;
        }

        Room room = player?.room ?? bridge.Spear?.room;
        if (room != null)
        {
            Room.Tile tile = room.GetTile(bridge.ProxyBeamTile);
            tile.horizontalBeam = bridge.ProxyBeamWasHorizontal;
        }

        bridge.ProxyBeamInjected = false;
        bridge.ProxyBeamWasHorizontal = false;
    }

    private static void FinishHorizontalBridgeFrame(
        Player player,
        HorizontalMountBridgeState bridge,
        EndpointRecoveryState recovery)
    {
        if (!BridgeActive(player, bridge))
        {
            return;
        }

        bridge.FramesLeft--;

        if (recovery != null && recovery.Spear == bridge.Spear)
        {
            recovery.FramesLeft = EndpointRecoveryFrames;
        }

        bool actualBeamReady =
            player?.room != null &&
            BodyTouchesHorizontalBeam(player) &&
            Custom.DistLess(
                player.mainBodyChunk.pos,
                bridge.FinalTarget,
                HorizontalVanillaTargetDistance);

        // Vanilla may have completed the pull-up during this frame.
        if (actualBeamReady && player.animation == Player.AnimationIndex.StandOnBeam)
        {
            ClearHorizontalBridge(bridge);
            ClearRecovery(recovery);
            return;
        }

        if (actualBeamReady)
        {
            EnterHorizontalGetUpOnBeam(
                player,
                bridge.Spear,
                bridge.FinalTarget,
                playSound: false);
            ClearHorizontalBridge(bridge);
            ClearRecovery(recovery);
            return;
        }

        // A proxy tile can occasionally make vanilla think the rear body chunk has
        // already reached a beam and briefly switch to StandOnBeam. The proxy is gone
        // now, so that is not a real completion; continue the staged GetUpOnBeam.
        if ((player.animation == Player.AnimationIndex.None ||
             player.animation == Player.AnimationIndex.StandOnBeam) &&
            bridge.FramesLeft > 0)
        {
            EnterHorizontalGetUpOnBeam(
                player,
                bridge.Spear,
                bridge.FinalTarget,
                playSound: false);
        }

        if (bridge.FramesLeft <= 0 ||
            player == null ||
            player.room == null ||
            !Custom.DistLess(
                player.mainBodyChunk.pos,
                bridge.FinalTarget,
                HorizontalBridgeMaxDistance))
        {
            CancelHorizontalBridge(player, bridge, recovery, restoreRope: true);
        }
    }

    private static void CancelHorizontalBridge(
        Player player,
        HorizontalMountBridgeState bridge,
        EndpointRecoveryState recovery,
        bool restoreRope)
    {
        RestoreProxyHorizontalBeam(player, bridge);
        ClearHorizontalBridge(bridge);

        if (restoreRope &&
            recovery != null &&
            recovery.Spear != null &&
            recovery.FramesLeft > 0)
        {
            RestoreEndpointVineGrab(player, recovery);
            return;
        }

        if (player != null)
        {
            player.animation = Player.AnimationIndex.None;
            player.vinePos = null;
            player.vineGrabDelay = Mathf.Max(player.vineGrabDelay, 10);
            player.noGrabCounter = Mathf.Max(player.noGrabCounter, 5);
            player.standing = false;
        }

        ClearRecovery(recovery);
    }

    private static void ClearHorizontalBridge(HorizontalMountBridgeState bridge)
    {
        if (bridge == null)
        {
            return;
        }

        bridge.Spear = null;
        bridge.BeamCenter = Vector2.zero;
        bridge.FinalTarget = Vector2.zero;
        bridge.FramesLeft = 0;
        bridge.ProxyBeamInjected = false;
        bridge.ProxyBeamWasHorizontal = false;
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
