using UnityEngine;

namespace DryCycle.Items.RopeSpear;

/// <summary>
/// Owns Alt+Throw while a player is holding a RopeSpear handle. Alt+Throw is an
/// anchor command, never an ordinary throw. When an airborne player successfully
/// fixes the handle while the spear end is already embedded in terrain, immediately
/// transfer the player from the handle to the rope's vanilla VineGrab state so the
/// release frame cannot make them fall before they can press Up again.
/// </summary>
internal static class RopeSpearHandleAnchorSafetyRuntime
{
    private const float AutoCatchSearchRadius = 58f;
    private const float EndpointFallbackDistance = 62f;

    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.Player.ThrowObject += Player_ThrowObject;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.Player.ThrowObject -= Player_ThrowObject;
        _enabled = false;
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

        RopeSpear parentSpear = FindParentSpear(self, handle);
        bool autoCatch = ShouldAutoCatch(self, parentSpear);

        // Do not use Player.ReleaseObject here. This is not a lay-down action and the
        // handle must stay exactly at its newly authored anchor point. Releasing the
        // grasp directly also avoids an extra item-drop state between anchor and catch.
        self.ReleaseGrasp(grasp);

        if (autoCatch)
        {
            TryAttachPlayerToRope(self, parentSpear, handle);
        }
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

        // Restrict the automatic transfer to the hanging/falling case requested by
        // the player. Anchoring an endpoint while safely standing on terrain or a beam
        // should simply anchor it and leave the player's movement state alone.
        if (player.standing ||
            player.canJump > 0 ||
            player.bodyMode == Player.BodyModeIndex.ClimbingOnBeam ||
            player.animation == Player.AnimationIndex.ClimbOnBeam ||
            player.animation == Player.AnimationIndex.HangFromBeam ||
            player.animation == Player.AnimationIndex.StandOnBeam ||
            player.animation == Player.AnimationIndex.BeamTip ||
            player.animation == Player.AnimationIndex.GetUpOnBeam ||
            player.animation == Player.AnimationIndex.GetUpToBeamTip)
        {
            return false;
        }

        if (player.bodyChunks != null)
        {
            for (int i = 0; i < player.bodyChunks.Length; i++)
            {
                BodyChunk chunk = player.bodyChunks[i];
                if (chunk != null && chunk.ContactPoint.y < 0)
                {
                    return false;
                }
            }
        }

        return true;
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
                player.mainBodyChunk.rad + 18f);

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
                // the endpoint the player just released. This fallback covers the
                // same-frame case before the rope solver has refreshed its node chain.
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

        // Preserve the player's existing swing/fall momentum. Vanilla VineGrab will
        // connect the main body chunk to the rope during the normal update path; no
        // teleport or velocity reset is needed here.
        return true;
    }
}
