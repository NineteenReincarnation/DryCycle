using System.Reflection;
using RWCustom;
using UnityEngine;

namespace DryCycle.Items.RopeSpear;

/// <summary>
/// Custom RopeSpear climbing. The rope remains our own constrained Verlet rope;
/// this controller only borrows the useful player-side ideas from vanilla VineGrab:
/// attach the main body chunk to the rope centre line, use vineClimbCursor to let
/// the two body chunks counter-rotate naturally, and drive free hands toward the
/// rope rather than suspending the entire slugcat below an invisible spring point.
/// </summary>
internal static class RopeSpearClimbController
{
    private const float GrabConnectionRadius = 2.35f;
    private const float MaxGrabSeparation = 62f;
    private const float ClimbSpeed = 2.65f;
    private const float SpearMountRange = 44f;
    private const float RopeReaction = 0.12f;

    private static readonly FieldInfo RopeSystemField = typeof(RopeSpear).GetField(
        "_ropeSystem",
        BindingFlags.Instance | BindingFlags.NonPublic);

    internal static bool Update(
        Player player,
        RopeSpear spear,
        ref float normalizedPosition,
        ref float poseCycle)
    {
        RopeSpearRopeSystem rope = GetRopeSystem(spear);
        if (rope == null ||
            !rope.Ready ||
            !spear.RopeActive ||
            spear.mode == Weapon.Mode.Thrown ||
            player == null ||
            player.room != spear.room ||
            player.dead ||
            player.inShortcut ||
            player.enteringShortCut.HasValue)
        {
            ResetVinePose(player);
            return false;
        }

        if (player.input == null || player.input.Length == 0)
        {
            ResetVinePose(player);
            return false;
        }

        Player.InputPackage input = player.input[0];
        Player.InputPackage previousInput = player.input.Length > 1
            ? player.input[1]
            : default;

        if (input.jmp && !previousInput.jmp)
        {
            // Match the useful part of vanilla VineGrab's jump release: preserve
            // the current swing and add only a modest launch away from the rope.
            Vector2 releaseDirection = new Vector2(input.x, Mathf.Max(0.45f, input.y));
            if (releaseDirection.sqrMagnitude < 0.01f)
            {
                releaseDirection = Vector2.up;
            }
            releaseDirection.Normalize();

            player.mainBodyChunk.vel += releaseDirection * 3.5f;
            if (player.bodyChunks != null && player.bodyChunks.Length > 1)
            {
                player.bodyChunks[1].vel += releaseDirection * 3f;
            }

            player.vineClimbCursor *= 0.35f;
            return false;
        }

        if (input.y != 0)
        {
            normalizedPosition = AdvanceGrabPosition(
                rope,
                normalizedPosition,
                input.y);
            poseCycle += 0.22f * Mathf.Abs(input.y);
        }
        else
        {
            poseCycle += 0.035f * Mathf.Abs(input.x);
        }

        if (input.y > 0 && TryMountSpearFromRope(player, spear, rope, normalizedPosition))
        {
            player.vineClimbCursor *= 0.2f;
            return false;
        }

        Vector2 ropePoint = rope.GetPoint(normalizedPosition);
        if (!Custom.DistLess(player.mainBodyChunk.pos, ropePoint, MaxGrabSeparation))
        {
            ResetVinePose(player);
            return false;
        }

        // This is the important vanilla VineGrab posture behaviour. The main chunk
        // sits on the vine/rope centreline while the rear body chunk remains free to
        // hang and swing. The old RopeSpear code instead targeted ropePoint-(0,11),
        // which visually suspended the whole slugcat underneath a point.
        ConnectMainChunkToRope(player.mainBodyChunk, rope, normalizedPosition, ropePoint);

        Vector2 inputDirection = player.SwimDir(normalize: true);
        if (inputDirection.sqrMagnitude > 0.0001f)
        {
            Vector2 cursorDirection = player.vineClimbCursor.sqrMagnitude > 0.0001f
                ? player.vineClimbCursor.normalized
                : inputDirection;
            float cursorAcceleration = Custom.LerpMap(
                Vector2.Dot(inputDirection, cursorDirection),
                -1f,
                1f,
                10f,
                3f);
            player.vineClimbCursor = Vector2.ClampMagnitude(
                player.vineClimbCursor + inputDirection * cursorAcceleration,
                30f);
        }
        else
        {
            player.vineClimbCursor *= 0.8f;
        }

        // Vanilla applies these opposite impulses to the two chunks. This small
        // counter-rotation is what makes the slugcat look like it is climbing a
        // flexible object rather than being dragged around by its torso.
        player.mainBodyChunk.vel += player.vineClimbCursor / 190f;
        if (player.bodyChunks != null && player.bodyChunks.Length > 1)
        {
            player.bodyChunks[1].vel -= player.vineClimbCursor / 190f;
        }

        // Preserve useful horizontal swing authority, but keep it weaker than the
        // rope constraint so it cannot pull the body away from the centreline.
        if (input.x != 0)
        {
            float swing = input.x * 0.085f;
            player.mainBodyChunk.vel.x += swing;
            if (player.bodyChunks != null && player.bodyChunks.Length > 1)
            {
                player.bodyChunks[1].vel.x += swing * 0.7f;
            }
        }

        // RegionKit's climbable rope uses the same guards because ordinary player
        // ledge/wall states otherwise fight a flexible-rope attachment at edges.
        player.bodyMode = Player.BodyModeIndex.Default;
        player.standing = true;
        player.ledgeGrabCounter = 0;
        player.wallSlideCounter = 0;

        rope.ApplyExternalPull(
            normalizedPosition,
            player.mainBodyChunk.pos,
            0.075f);
        return true;
    }

    /// <summary>
    /// A freshly thrown RopeSpear normally puts its RopeHandle into the thrower's
    /// newly-free hand. When that same player deliberately climbs onto the rope,
    /// transfer from the handle to the rope by dropping only the associated handle.
    /// This mirrors physically letting the free end dangle before climbing.
    /// </summary>
    internal static void ReleaseAssociatedHandleForClimb(Player player, RopeSpear spear)
    {
        if (player?.grasps == null || spear?.abstractPhysicalObject == null)
        {
            return;
        }

        EntityID spearId = spear.abstractPhysicalObject.ID;
        for (int i = player.grasps.Length - 1; i >= 0; i--)
        {
            if (player.grasps[i]?.grabbed is RopeHandle handle &&
                handle.ParentSpearID == spearId)
            {
                player.ReleaseGrasp(i);
            }
        }
    }

    internal static void PrepareHands(
        PlayerGraphics graphics,
        RopeSpear spear,
        float normalizedPosition,
        float poseCycle)
    {
        if (graphics?.player == null || graphics.hands == null)
        {
            return;
        }

        RopeSpearRopeSystem rope = GetRopeSystem(spear);
        if (rope == null || !rope.Ready || !spear.RopeActive)
        {
            return;
        }

        GetRopePose(rope, normalizedPosition, out Vector2 point, out Vector2 tangent);

        // Alternate the free hands slightly along the rope as the player climbs.
        // This is deliberately graphics-only; physical attachment still comes from
        // the body chunk constraint above.
        float stride = Mathf.Sin(poseCycle) * 2.6f;
        float[] offsets = { -6.5f + stride, 6.5f - stride };

        for (int i = 0; i < 2 && i < graphics.hands.Length; i++)
        {
            if (graphics.player.grasps != null &&
                i < graphics.player.grasps.Length &&
                graphics.player.grasps[i] != null)
            {
                continue;
            }

            SlugcatHand hand = graphics.hands[i];
            if (hand == null)
            {
                continue;
            }

            hand.mode = Limb.Mode.HuntAbsolutePosition;
            hand.reachingForObject = true;
            hand.absoluteHuntPos = point + tangent * offsets[i];
            hand.huntSpeed = 16f;
            hand.quickness = 0.85f;
        }
    }

    internal static void ResetVinePose(Player player)
    {
        if (player != null)
        {
            player.vineClimbCursor *= 0.35f;
        }
    }

    private static RopeSpearRopeSystem GetRopeSystem(RopeSpear spear)
    {
        return spear == null
            ? null
            : RopeSystemField?.GetValue(spear) as RopeSpearRopeSystem;
    }

    private static void ConnectMainChunkToRope(
        BodyChunk chunk,
        RopeSpearRopeSystem rope,
        float normalizedPosition,
        Vector2 ropePoint)
    {
        float distance = Vector2.Distance(chunk.pos, ropePoint);
        if (distance <= GrabConnectionRadius)
        {
            return;
        }

        Vector2 before = chunk.pos;
        Vector2 direction = Custom.DirVec(chunk.pos, ropePoint);
        Vector2 correction = direction * ((distance - GrabConnectionRadius) * 0.72f);

        chunk.pos += correction;
        chunk.vel += correction;

        // The complementary part of the correction goes into nearby rope nodes.
        // ApplyExternalPull spreads this over several nodes so the player actually
        // loads the rope instead of merely being teleported onto a visual line.
        rope.ApplyExternalPull(
            normalizedPosition,
            before,
            RopeReaction);
    }

    private static float AdvanceGrabPosition(
        RopeSpearRopeSystem rope,
        float current,
        int verticalInput)
    {
        float referenceLength = Mathf.Max(80f, rope.RouteLength);
        float step = Mathf.Clamp(ClimbSpeed / referenceLength, 0.004f, 0.035f);
        float lowerT = Mathf.Clamp01(current - step);
        float upperT = Mathf.Clamp01(current + step);
        Vector2 lower = rope.GetPoint(lowerT);
        Vector2 upper = rope.GetPoint(upperT);

        float direction;
        if (Mathf.Abs(upper.y - lower.y) > 0.45f)
        {
            direction = upper.y > lower.y ? 1f : -1f;
        }
        else
        {
            // On a nearly horizontal section, define Up as motion toward whichever
            // endpoint is physically higher. This stays deterministic around bends.
            Vector2 handleEnd = rope.GetPoint(0f);
            Vector2 spearEnd = rope.GetPoint(1f);
            direction = spearEnd.y >= handleEnd.y ? 1f : -1f;
        }

        if (verticalInput < 0)
        {
            direction = -direction;
        }

        return Mathf.Clamp01(current + step * direction);
    }

    private static void GetRopePose(
        RopeSpearRopeSystem rope,
        float normalizedPosition,
        out Vector2 point,
        out Vector2 tangent)
    {
        point = rope.GetPoint(normalizedPosition);
        float sample = 1f / (RopeSpearRopeSystem.NodeCount - 1f);
        Vector2 before = rope.GetPoint(Mathf.Clamp01(normalizedPosition - sample));
        Vector2 after = rope.GetPoint(Mathf.Clamp01(normalizedPosition + sample));
        tangent = after - before;
        if (tangent.sqrMagnitude < 0.0001f)
        {
            tangent = Vector2.up;
        }
        else
        {
            tangent.Normalize();
        }
    }

    private static bool TryMountSpearFromRope(
        Player player,
        RopeSpear spear,
        RopeSpearRopeSystem rope,
        float normalizedPosition)
    {
        if (spear.mode != Weapon.Mode.StuckInWall ||
            spear.abstractPhysicalObject is not AbstractRopeSpear data ||
            data.stuckInWallCycles < 0 ||
            normalizedPosition < 0.88f)
        {
            return false;
        }

        Vector2 ropePoint = rope.GetPoint(normalizedPosition);
        Vector2 spearPoint = rope.GetPoint(1f);
        if (!Custom.DistLess(ropePoint, spearPoint, SpearMountRange) ||
            spearPoint.y < player.mainBodyChunk.pos.y - 14f ||
            !TryFindHorizontalSpearBeam(
                player.room,
                spear.firstChunk.pos,
                player.mainBodyChunk.pos,
                out Vector2 beamCenter))
        {
            return false;
        }

        player.noGrabCounter = Mathf.Max(player.noGrabCounter, 15);
        player.forceFeetToHorizontalBeamTile = 20;
        player.pullupSoftlockSafety = 0;
        player.straightUpOnHorizontalBeam = true;
        player.upOnHorizontalBeamPos = new Vector2(
            beamCenter.x,
            player.room.MiddleOfTile(beamCenter).y + 20f);
        player.animation = Player.AnimationIndex.GetUpOnBeam;
        player.bodyMode = Player.BodyModeIndex.ClimbingOnBeam;
        player.standing = false;

        player.mainBodyChunk.pos = beamCenter;
        player.mainBodyChunk.lastPos = beamCenter;
        player.mainBodyChunk.vel = Vector2.zero;

        if (player.bodyChunks != null && player.bodyChunks.Length > 1)
        {
            Vector2 lower = beamCenter + new Vector2(0f, -17f);
            player.bodyChunks[1].pos = lower;
            player.bodyChunks[1].lastPos = lower;
            player.bodyChunks[1].vel = Vector2.zero;
        }

        player.room.PlaySound(
            SoundID.Slugcat_Get_Up_On_Horizontal_Beam,
            player.mainBodyChunk,
            loop: false,
            0.75f,
            1f);
        return true;
    }

    private static bool TryFindHorizontalSpearBeam(
        Room targetRoom,
        Vector2 spearPosition,
        Vector2 playerPosition,
        out Vector2 beamCenter)
    {
        beamCenter = Vector2.zero;
        if (targetRoom == null)
        {
            return false;
        }

        IntVector2 origin = targetRoom.GetTilePosition(spearPosition);
        float bestDistance = float.MaxValue;
        bool found = false;

        for (int x = -2; x <= 2; x++)
        {
            for (int y = -1; y <= 1; y++)
            {
                IntVector2 tilePos = origin + new IntVector2(x, y);
                Room.Tile tile = targetRoom.GetTile(tilePos);
                if (!tile.horizontalBeam || tile.Solid)
                {
                    continue;
                }

                Vector2 center = targetRoom.MiddleOfTile(tilePos);
                float distance = Vector2.Distance(playerPosition, center);
                if (distance >= bestDistance)
                {
                    continue;
                }

                bestDistance = distance;
                beamCenter = center;
                found = true;
            }
        }

        return found;
    }
}
