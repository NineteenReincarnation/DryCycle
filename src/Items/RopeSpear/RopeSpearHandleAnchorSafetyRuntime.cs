using System.Runtime.CompilerServices;
using RWCustom;
using UnityEngine;

namespace DryCycle.Items.RopeSpear;

/// <summary>
/// Owns Alt+Throw while a player is holding a RopeSpear handle. Alt+Throw is an
/// anchor command, never an ordinary throw. Endpoint anchoring also arms a short
/// post-Player.Update catch window so the hanging player is transferred to the
/// rope only after vanilla has finished processing the release frame.
/// </summary>
internal static class RopeSpearHandleAnchorSafetyRuntime
{
    private const float AutoCatchSearchRadius = 72f;
    private const float EndpointFallbackDistance = 78f;
    private const int PendingCatchFrames = 5;

    private sealed class PendingCatchState
    {
        internal RopeSpear Spear;
        internal RopeHandle Handle;
        internal int FramesLeft;
    }

    private static readonly ConditionalWeakTable<Player, PendingCatchState> PendingCatches = new();
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.Player.ThrowObject += Player_ThrowObject;
        On.Player.Update += Player_Update;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.Player.ThrowObject -= Player_ThrowObject;
        On.Player.Update -= Player_Update;
        _enabled = false;
    }

    /// <summary>
    /// Called by RopeHandle itself when an endpoint becomes anchored. Keeping this
    /// notification at the actual state transition makes auto-catch independent of
    /// ThrowObject hook ordering: even the older RopeSpearHooks anchor path cannot
    /// bypass the safety transfer.
    /// </summary>
    internal static void NotifyHandleAnchored(RopeHandle handle, Player holder)
    {
        if (!_enabled || handle == null || holder == null)
        {
            return;
        }

        RopeSpear parentSpear = FindParentSpear(holder, handle);
        if (!ShouldAutoCatch(holder, parentSpear))
        {
            return;
        }

        PendingCatchState pending = PendingCatches.GetOrCreateValue(holder);
        pending.Spear = parentSpear;
        pending.Handle = handle;
        pending.FramesLeft = PendingCatchFrames;
    }

    private static void Player_ThrowObject(
        On.Player.orig_ThrowObject orig,
        Player self,
        int grasp,
        bool eu)
    {
        if (!AltHeld() ||
            self?.grasps == null ||
            grasp < 0 ||
            grasp >= self.grasps.Length ||
            self.grasps[grasp]?.grabbed is not RopeHandle handle)
        {
            orig(self, grasp, eu);
            return;
        }

        // Alt reserves Throw for endpoint anchoring. Even when there is no nearby
        // terrain, consume the input rather than falling through to vanilla ThrowObject;
        // otherwise a failed anchor attempt launches the handle away from the player.
        if (!handle.TryAnchorToNearbyTerrain())
        {
            return;
        }

        // TryAnchorToNearbyTerrain arms the pending catch while the player is still
        // the holder. Release the hand now; the actual VineGrab transfer happens in
        // the post-Update pass below, after vanilla can no longer overwrite it.
        self.ReleaseGrasp(grasp);
    }

    private static void Player_Update(
        On.Player.orig_Update orig,
        Player self,
        bool eu)
    {
        orig(self, eu);

        if (self == null ||
            !PendingCatches.TryGetValue(self, out PendingCatchState pending) ||
            pending.FramesLeft <= 0)
        {
            return;
        }

        if (self.dead ||
            !self.Consious ||
            self.inShortcut ||
            self.enteringShortCut.HasValue ||
            pending.Spear == null ||
            pending.Handle == null ||
            pending.Spear.slatedForDeletetion ||
            pending.Handle.slatedForDeletetion ||
            pending.Spear.room != self.room ||
            pending.Handle.room != self.room ||
            pending.Spear.mode != Weapon.Mode.StuckInWall ||
            !pending.Handle.Anchored)
        {
            ClearPending(pending);
            return;
        }

        if (self.animation == Player.AnimationIndex.VineGrab &&
            self.vinePos?.vine == pending.Spear)
        {
            ClearPending(pending);
            return;
        }

        if (TryAttachPlayerToRope(self, pending.Spear, pending.Handle))
        {
            ClearPending(pending);
            return;
        }

        pending.FramesLeft--;
        if (pending.FramesLeft <= 0)
        {
            ClearPending(pending);
        }
    }

    private static void ClearPending(PendingCatchState pending)
    {
        if (pending == null)
        {
            return;
        }

        pending.Spear = null;
        pending.Handle = null;
        pending.FramesLeft = 0;
    }

    private static bool AltHeld()
    {
        return Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
    }

    private static RopeSpear FindParentSpear(Player player, RopeHandle handle)
    {
        if (player?.room?.physicalObjects == null ||
            handle?.abstractPhysicalObject == null)
        {
            return null;
        }

        EntityID parentId = handle.ParentSpearID;
        for (int layer = 0; layer < player.room.physicalObjects.Length; layer++)
        {
            var objects = player.room.physicalObjects[layer];
            for (int i = 0; i < objects.Count; i++)
            {
                if (objects[i] is RopeSpear spear &&
                    !spear.slatedForDeletetion &&
                    spear.abstractPhysicalObject != null &&
                    spear.abstractPhysicalObject.ID == parentId)
                {
                    return spear;
                }
            }
        }

        return null;
    }

    private static bool ShouldAutoCatch(Player player, RopeSpear spear)
    {
        if (player == null ||
            spear == null ||
            player.room == null ||
            spear.room != player.room ||
            spear.mode != Weapon.Mode.StuckInWall ||
            player.dead ||
            !player.Consious ||
            player.inShortcut ||
            player.enteringShortCut.HasValue ||
            player.animation == Player.AnimationIndex.VineGrab)
        {
            return false;
        }

        // Do not trust Player.standing/canJump here. While the player is physically
        // suspended from a held RopeHandle those fields can remain stale for a frame,
        // which was exactly why the vertical hanging case failed to auto-catch.
        // Reject only states that have real, current support.
        return !HasStableSupport(player);
    }

    private static bool HasStableSupport(Player player)
    {
        if (player.bodyMode == Player.BodyModeIndex.ClimbingOnBeam ||
            player.animation == Player.AnimationIndex.ClimbOnBeam ||
            player.animation == Player.AnimationIndex.HangFromBeam ||
            player.animation == Player.AnimationIndex.StandOnBeam ||
            player.animation == Player.AnimationIndex.BeamTip ||
            player.animation == Player.AnimationIndex.GetUpOnBeam ||
            player.animation == Player.AnimationIndex.GetUpToBeamTip ||
            player.animation == Player.AnimationIndex.HangUnderVerticalBeam)
        {
            return true;
        }

        if (player.bodyChunks == null)
        {
            return false;
        }

        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            BodyChunk chunk = player.bodyChunks[i];
            if (chunk != null && chunk.ContactPoint.y < 0)
            {
                return true;
            }
        }

        // ContactPoint can lag one frame at tile boundaries. Probe directly below
        // the lower body chunk as a second check for genuine floor support.
        if (player.room != null && player.bodyChunks.Length > 1)
        {
            BodyChunk lower = player.bodyChunks[1];
            if (lower != null)
            {
                Vector2 probe = lower.pos + Vector2.down * (lower.rad + 3f);
                if (player.room.GetTile(probe).Solid)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryAttachPlayerToRope(
        Player player,
        RopeSpear spear,
        RopeHandle anchoredHandle)
    {
        if (player?.room?.climbableVines == null ||
            spear == null ||
            anchoredHandle == null ||
            !anchoredHandle.Anchored ||
            spear.mode != Weapon.Mode.StuckInWall)
        {
            return false;
        }

        ClimbableVinesSystem.VinePosition vinePosition =
            player.room.climbableVines.VineOverlap(
                player.mainBodyChunk.pos,
                player.mainBodyChunk.rad + 22f);

        if (vinePosition?.vine != spear)
        {
            if (spear.TryFindNearestRopePoint(
                    player.mainBodyChunk.pos,
                    AutoCatchSearchRadius,
                    out float normalizedPosition,
                    out _))
            {
                vinePosition = new ClimbableVinesSystem.VinePosition(
                    spear,
                    normalizedPosition);
            }
            else if (Custom.DistLess(
                         player.mainBodyChunk.pos,
                         anchoredHandle.firstChunk.pos,
                         EndpointFallbackDistance))
            {
                // RopeSpearRopeSystem is authored handle -> spear, so floatPos 0 is
                // the endpoint the player just released. This fallback also covers
                // the few frames before the rope solver has refreshed its node chain.
                vinePosition = new ClimbableVinesSystem.VinePosition(spear, 0f);
            }
            else
            {
                return false;
            }
        }

        player.animation = Player.AnimationIndex.VineGrab;
        player.vinePos = vinePosition;
        player.vineGrabDelay = 0;
        player.vineClimbCursor = Vector2.zero;
        player.wantToGrab = 0;
        player.wantToPickUp = 0;
        player.bodyMode = Player.BodyModeIndex.Default;
        player.standing = false;
        player.ledgeGrabCounter = 0;
        player.wallSlideCounter = 0;

        // Preserve swing/fall momentum. VineGrab makes the physical connection on
        // its normal next update; there is no endpoint teleport and no velocity reset.
        return true;
    }
}
