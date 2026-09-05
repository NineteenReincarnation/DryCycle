using System.Runtime.CompilerServices;
using RWCustom;
using UnityEngine;

namespace DryCycle.Items.RopeSpear;

/// <summary>
/// Makes RopeSpear wall sticking use the real flight vector instead of vanilla's
/// cardinal throwDir test. Vanilla Spear only sticks when ContactPoint == throwDir,
/// which breaks for diagonal throws because throwDir can only represent four axes.
/// </summary>
internal static class RopeSpearWallStickRuntime
{
    private const float SpearTipReach = 22f;
    private const float TraceStep = 2f;
    private const float EmbedOffset = 5f;

    private sealed class FlightState
    {
        internal bool InSpearUpdate;
        internal Vector2 StartPosition;
        internal Vector2 Velocity;
        internal Vector2 Direction;
    }

    private sealed class BeamThrowState
    {
        internal bool ThrowWasHeld;
    }

    private static readonly ConditionalWeakTable<RopeSpear, FlightState> FlightStates = new();
    private static readonly ConditionalWeakTable<Player, BeamThrowState> BeamThrowStates = new();
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.Spear.Update += Spear_Update;
        On.Weapon.HitWall += Weapon_HitWall;
        On.ClimbableVinesSystem.VineOverlap += ClimbableVinesSystem_VineOverlap;
        On.Player.Update += Player_UpdateClimbGate;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.Spear.Update -= Spear_Update;
        On.Weapon.HitWall -= Weapon_HitWall;
        On.ClimbableVinesSystem.VineOverlap -= ClimbableVinesSystem_VineOverlap;
        On.Player.Update -= Player_UpdateClimbGate;
        _enabled = false;
    }

    private static void Spear_Update(
        On.Spear.orig_Update orig,
        Spear self,
        bool eu)
    {
        if (self is not RopeSpear ropeSpear ||
            ropeSpear.mode != Weapon.Mode.Thrown ||
            ropeSpear.room == null)
        {
            orig(self, eu);
            return;
        }

        // RopeSpear is a traversal anchor, so terrain hits must not use vanilla's
        // probabilistic wall-stick roll. This is scoped strictly to RopeSpear and
        // still leaves NoSpearStickZone/border/shelter restrictions intact.
        ropeSpear.alwaysStickInWalls = true;

        // Vanilla Weapon.Update normally tumbles a thrown weapon into Mode.Free as
        // soon as its speed drops below exitThrownModeSpeed (~30). That is fine for
        // ordinary spears, but it prematurely ends RopeSpear's projectile state on
        // upward/diagonal casts near the top of the arc. Keep RopeSpear in genuine
        // projectile mode until an actual collision/stick transition ends the throw.
        ropeSpear.doNotTumbleAtLowSpeed = true;

        FlightState state = FlightStates.GetOrCreateValue(ropeSpear);
        state.StartPosition = ropeSpear.firstChunk.pos;
        state.Velocity = ropeSpear.firstChunk.vel;
        state.Direction = ResolveFlightDirection(ropeSpear, state.Velocity);
        state.InSpearUpdate = true;

        try
        {
            orig(self, eu);
        }
        finally
        {
            state.InSpearUpdate = false;
        }

        if (ropeSpear.mode == Weapon.Mode.StuckInCreature)
        {
            return;
        }

        // Vanilla may have succeeded on a cardinal-compatible face. Preserve its
        // wall/beam bookkeeping but restore the exact diagonal visual orientation.
        if (ropeSpear.mode == Weapon.Mode.StuckInWall)
        {
            RestoreExactStuckPose(ropeSpear, state.Direction);
            return;
        }

        // For a diagonal contact vanilla often leaves the spear Thrown because
        // BodyChunk.ContactPoint and cardinal throwDir differ. Sweep the real flight
        // path and perform the same wall-stick transition ourselves.
        TryStickFromFlight(ropeSpear, state);
    }

    private static void Weapon_HitWall(
        On.Weapon.orig_HitWall orig,
        Weapon self)
    {
        if (self is RopeSpear ropeSpear &&
            FlightStates.TryGetValue(ropeSpear, out FlightState state) &&
            state.InSpearUpdate &&
            ropeSpear.mode == Weapon.Mode.Thrown &&
            TryStickFromFlight(ropeSpear, state))
        {
            return;
        }

        orig(self);
    }

    private static ClimbableVinesSystem.VinePosition ClimbableVinesSystem_VineOverlap(
        On.ClimbableVinesSystem.orig_VineOverlap orig,
        ClimbableVinesSystem self,
        Vector2 pos,
        float rad)
    {
        ClimbableVinesSystem.VinePosition result = orig(self, pos, rad);
        if (result?.vine is not RopeSpear ropeSpear)
        {
            return result;
        }

        // A RopeSpear is a climbable line only after it has become a true bridge:
        // the spear end is embedded in terrain and the RopeHandle end has also been
        // explicitly anchored to terrain. A loose/held handle therefore never lets
        // vanilla VineGrab acquire the rope.
        return HasFixedClimbAnchors(ropeSpear) ? result : null;
    }

    private static void Player_UpdateClimbGate(
        On.Player.orig_Update orig,
        Player self,
        bool eu)
    {
        orig(self, eu);

        RecoverBeamSupportedThrow(self, eu);

        if (self?.animation != Player.AnimationIndex.VineGrab ||
            self.vinePos?.vine is not RopeSpear ropeSpear ||
            HasFixedClimbAnchors(ropeSpear))
        {
            return;
        }

        // If either endpoint stops being fixed while a player is already on the
        // rope, release VineGrab without erasing momentum. This keeps the same rule
        // true dynamically instead of only checking it on initial acquisition.
        self.animation = Player.AnimationIndex.None;
        self.vinePos = null;
        self.vineGrabDelay = Mathf.Max(self.vineGrabDelay, 10);
        self.noGrabCounter = Mathf.Max(self.noGrabCounter, 5);
    }

    private static void RecoverBeamSupportedThrow(Player player, bool eu)
    {
        if (player?.input == null || player.input.Length == 0)
        {
            return;
        }

        BeamThrowState state = BeamThrowStates.GetOrCreateValue(player);
        bool throwHeld = player.input[0].thrw;
        bool throwReleased = state.ThrowWasHeld && !throwHeld;
        state.ThrowWasHeld = throwHeld;

        if (!throwReleased || !IsBeamSupportedThrowState(player))
        {
            return;
        }

        if (!TryFindHeldRopeSpear(player, out int graspIndex, out RopeSpear spear))
        {
            return;
        }

        // The custom aim controller normally releases inside GrabUpdate. On the top
        // of a vertical beam Rain World's BeamTip transition can consume that release
        // frame while the aim pose remains valid, leaving the RopeSpear in the hand.
        // Retry once after Player.Update has finished. At this point all beam-state
        // movement code has run, so the ordinary Player.ThrowObject path is safe.
        Vector2 aimedDirection = spear.rotation;
        if (aimedDirection.sqrMagnitude < 0.0001f)
        {
            int facing = player.ThrowDirection;
            if (facing == 0)
            {
                facing = player.flipDirection;
            }
            aimedDirection = new Vector2(facing < 0 ? -1f : 1f, 0f);
        }
        aimedDirection.Normalize();

        player.ThrowObject(graspIndex, eu);

        if (spear.slatedForDeletetion ||
            spear.mode != Weapon.Mode.Thrown ||
            IsStillHeld(player, graspIndex, spear))
        {
            return;
        }

        ApplyRecoveredThrowDirection(player, spear, aimedDirection, eu);
    }

    private static bool IsBeamSupportedThrowState(Player player)
    {
        return player != null &&
               player.bodyMode == Player.BodyModeIndex.ClimbingOnBeam &&
               (player.animation == Player.AnimationIndex.BeamTip ||
                player.animation == Player.AnimationIndex.GetUpToBeamTip ||
                player.animation == Player.AnimationIndex.StandOnBeam);
    }

    private static bool TryFindHeldRopeSpear(
        Player player,
        out int graspIndex,
        out RopeSpear spear)
    {
        graspIndex = -1;
        spear = null;
        if (player?.grasps == null)
        {
            return false;
        }

        // Preserve vanilla throw priority. If another throwable object is in the
        // earlier hand, do not steal that release for RopeSpear recovery.
        for (int i = 0; i < player.grasps.Length; i++)
        {
            Creature.Grasp grasp = player.grasps[i];
            if (grasp?.grabbed == null || !player.IsObjectThrowable(grasp.grabbed))
            {
                continue;
            }

            if (grasp.grabbed is not RopeSpear candidate)
            {
                return false;
            }

            graspIndex = i;
            spear = candidate;
            return true;
        }

        return false;
    }

    private static bool IsStillHeld(Player player, int graspIndex, RopeSpear spear)
    {
        return player?.grasps != null &&
               graspIndex >= 0 &&
               graspIndex < player.grasps.Length &&
               ReferenceEquals(player.grasps[graspIndex]?.grabbed, spear);
    }

    private static void ApplyRecoveredThrowDirection(
        Player player,
        RopeSpear spear,
        Vector2 direction,
        bool eu)
    {
        direction.Normalize();

        float throwSpeed = spear.firstChunk.vel.magnitude;
        if (throwSpeed < 1f)
        {
            throwSpeed = 40f;
        }

        Vector2 vanillaDirection = spear.throwDir.ToVector2();
        if (vanillaDirection.sqrMagnitude > 0.0001f)
        {
            vanillaDirection.Normalize();
        }
        else
        {
            vanillaDirection = new Vector2(player.flipDirection < 0 ? -1f : 1f, 0f);
        }

        spear.firstChunk.vel = direction * throwSpeed;

        Vector2 desiredPosition = player.firstChunk.pos +
                                  direction * 10f +
                                  new Vector2(0f, 4f);
        if (player.room != null && !player.room.GetTile(desiredPosition).Solid)
        {
            spear.firstChunk.MoveFromOutsideMyUpdate(eu, desiredPosition);
            spear.thrownPos = desiredPosition;
        }

        spear.firstFrameTraceFromPos = player.mainBodyChunk.pos - direction * 10f;
        spear.setRotation = direction;
        spear.rotation = direction;
        spear.lastRotation = direction;
        spear.rotationSpeed = 0f;
        spear.changeDirCounter = 0;

        if (Mathf.Abs(direction.x) >= Mathf.Abs(direction.y))
        {
            spear.throwDir = new IntVector2(direction.x < 0f ? -1 : 1, 0);
        }
        else
        {
            spear.throwDir = new IntVector2(0, direction.y < 0f ? -1 : 1);
        }

        // Player.ThrowObject already applied horizontal vanilla recoil. Rotate only
        // that impulse into the chosen aim direction so BeamTip throws retain the
        // same recoil magnitude as throws from ordinary ground states.
        Vector2 recoilCorrection = direction - vanillaDirection;
        player.mainBodyChunk.vel += recoilCorrection * 8f;
        if (player.bodyChunks != null && player.bodyChunks.Length > 1)
        {
            player.bodyChunks[1].vel -= recoilCorrection * 4f;
        }
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

    private static Vector2 ResolveFlightDirection(RopeSpear spear, Vector2 velocity)
    {
        Vector2 direction = spear.rotation;
        if (direction.sqrMagnitude < 0.0001f)
        {
            direction = velocity;
        }

        if (direction.sqrMagnitude < 0.0001f)
        {
            direction = spear.throwDir.ToVector2();
        }

        if (direction.sqrMagnitude < 0.0001f)
        {
            direction = Vector2.right;
        }

        direction.Normalize();

        if (velocity.sqrMagnitude > 0.01f)
        {
            Vector2 velocityDirection = velocity.normalized;
            if (Vector2.Dot(direction, velocityDirection) < 0.35f)
            {
                direction = velocityDirection;
            }
        }

        return direction;
    }

    private static bool TryStickFromFlight(RopeSpear spear, FlightState state)
    {
        if (spear == null ||
            spear.room == null ||
            spear.mode == Weapon.Mode.StuckInCreature ||
            spear.mode == Weapon.Mode.StuckInWall)
        {
            return false;
        }

        Vector2 direction = state.Direction;
        if (direction.sqrMagnitude < 0.0001f)
        {
            return false;
        }
        direction.Normalize();

        if (!TryFindTerrainImpact(
                spear,
                state.StartPosition,
                state.Velocity,
                direction,
                out IntVector2 airTile,
                out IntVector2 surfaceDirection))
        {
            return false;
        }

        if (!CanStickAt(spear, airTile))
        {
            return false;
        }

        Vector2 anchorPosition = spear.room.MiddleOfTile(airTile) - direction * EmbedOffset;

        // Vanilla stores generated traversal topology in the sign of
        // stuckInWallCycles: positive creates horizontalBeam tiles, negative creates
        // verticalBeam tiles. For an arbitrary-angle RopeSpear this must follow the
        // shaft itself, not the terrain face that happened to be hit. Otherwise a
        // shallow diagonal spear striking the underside of a ledge becomes a
        // vertical pole and the historical VineGrab -> GetUpOnBeam handoff can never
        // trigger. At 45 degrees we deliberately prefer horizontal so the player can
        // finish climbing the rope and stand on the shaft; steeper spears use the
        // vanilla vertical-beam topology instead.
        int cycles = Random.Range(3, 7);
        bool horizontalTraversal = Mathf.Abs(direction.x) >= Mathf.Abs(direction.y);
        spear.abstractSpear.stuckInWallCycles = horizontalTraversal
            ? cycles
            : -cycles;
        spear.throwDir = surfaceDirection;
        spear.stuckInWall = anchorPosition;
        spear.vibrate = 10;
        spear.ChangeMode(Weapon.Mode.StuckInWall);

        // Spear.ChangeMode intentionally snaps ordinary spears to a cardinal axis.
        // Reapply the real throw vector only after its wall/beam bookkeeping ran.
        spear.stuckInWall = anchorPosition;
        spear.setRotation = direction;
        spear.rotation = direction;
        spear.lastRotation = direction;
        spear.rotationSpeed = 0f;
        spear.firstChunk.HardSetPosition(anchorPosition);
        spear.firstChunk.lastPos = anchorPosition;
        spear.firstChunk.vel = Vector2.zero;
        spear.firstChunk.collideWithTerrain = false;

        spear.room.PlaySound(
            SoundID.Spear_Stick_In_Wall,
            spear.firstChunk,
            loop: false,
            1f,
            1f);
        return true;
    }

    private static bool TryFindTerrainImpact(
        RopeSpear spear,
        Vector2 startPosition,
        Vector2 velocity,
        Vector2 direction,
        out IntVector2 airTile,
        out IntVector2 surfaceDirection)
    {
        airTile = default;
        surfaceDirection = default;
        Room room = spear.room;
        if (room == null)
        {
            return false;
        }

        // Trace the actual swept center path plus the physical spear-tip reach.
        // This works for every continuous angle and also catches fast casts before
        // the single BodyChunk has enough time to settle onto a cardinal contact.
        Vector2 endPosition = startPosition + velocity + direction * SpearTipReach;
        float distance = Vector2.Distance(startPosition, endPosition);
        int samples = Mathf.Clamp(Mathf.CeilToInt(distance / TraceStep), 1, 48);

        bool haveAir = false;
        IntVector2 previousAirTile = default;
        IntVector2 solidTile = default;
        bool hitSolid = false;

        for (int i = 0; i <= samples; i++)
        {
            float t = i / (float)samples;
            Vector2 sample = Vector2.Lerp(startPosition, endPosition, t);
            IntVector2 tile = room.GetTilePosition(sample);

            if (!room.GetTile(tile).Solid)
            {
                previousAirTile = tile;
                haveAir = true;
                continue;
            }

            if (haveAir)
            {
                solidTile = tile;
                hitSolid = true;
                break;
            }
        }

        if (!hitSolid)
        {
            // Physical terrain collision is still useful as a fallback when another
            // mod has already modified velocity/position during the same update.
            IntVector2 contact = spear.firstChunk.ContactPoint;
            if (contact.x == 0 && contact.y == 0)
            {
                return false;
            }

            Vector2 current = spear.firstChunk.pos;
            IntVector2 currentTile = room.GetTilePosition(current);
            if (room.GetTile(currentTile).Solid)
            {
                current = FindNearestAirPoint(room, current, direction);
                currentTile = room.GetTilePosition(current);
                if (room.GetTile(currentTile).Solid)
                {
                    return false;
                }
            }

            previousAirTile = currentTile;
            if (!TryResolveSurfaceDirection(
                    room,
                    previousAirTile,
                    previousAirTile,
                    contact,
                    direction,
                    out surfaceDirection))
            {
                return false;
            }

            airTile = previousAirTile;
            return true;
        }

        if (!TryResolveSurfaceDirection(
                room,
                previousAirTile,
                solidTile,
                spear.firstChunk.ContactPoint,
                direction,
                out surfaceDirection))
        {
            return false;
        }

        airTile = previousAirTile;
        return true;
    }

    private static Vector2 FindNearestAirPoint(Room room, Vector2 position, Vector2 direction)
    {
        for (int i = 1; i <= 16; i++)
        {
            Vector2 sample = position - direction * (i * 2f);
            if (!room.GetTile(sample).Solid)
            {
                return sample;
            }
        }

        return position;
    }

    private static bool TryResolveSurfaceDirection(
        Room room,
        IntVector2 airTile,
        IntVector2 solidTile,
        IntVector2 contactPoint,
        Vector2 flightDirection,
        out IntVector2 surfaceDirection)
    {
        surfaceDirection = default;

        // ContactPoint is authoritative when it names a solid neighbor.
        if (contactPoint.x != 0)
        {
            IntVector2 candidate = new(contactPoint.x < 0 ? -1 : 1, 0);
            if (room.GetTile(airTile + candidate).Solid)
            {
                surfaceDirection = candidate;
                return true;
            }
        }
        if (contactPoint.y != 0)
        {
            IntVector2 candidate = new(0, contactPoint.y < 0 ? -1 : 1);
            if (room.GetTile(airTile + candidate).Solid)
            {
                surfaceDirection = candidate;
                return true;
            }
        }

        int dx = solidTile.x - airTile.x;
        int dy = solidTile.y - airTile.y;
        if (dx != 0 && dy == 0)
        {
            surfaceDirection = new IntVector2(dx < 0 ? -1 : 1, 0);
            return true;
        }
        if (dy != 0 && dx == 0)
        {
            surfaceDirection = new IntVector2(0, dy < 0 ? -1 : 1);
            return true;
        }

        // Corner crossing: choose the solid cardinal neighbor that the real flight
        // vector points into most strongly. This keeps 45-degree casts deterministic.
        IntVector2[] candidates =
        {
            new IntVector2(1, 0),
            new IntVector2(-1, 0),
            new IntVector2(0, 1),
            new IntVector2(0, -1)
        };

        float bestScore = float.NegativeInfinity;
        bool found = false;
        for (int i = 0; i < candidates.Length; i++)
        {
            IntVector2 candidate = candidates[i];
            if (!room.GetTile(airTile + candidate).Solid)
            {
                continue;
            }

            float score = Vector2.Dot(flightDirection, candidate.ToVector2());
            if (score <= bestScore)
            {
                continue;
            }

            bestScore = score;
            surfaceDirection = candidate;
            found = true;
        }

        return found && bestScore > -0.05f;
    }

    private static bool CanStickAt(RopeSpear spear, IntVector2 airTile)
    {
        Room room = spear.room;
        if (room?.abstractRoom == null)
        {
            return false;
        }

        // Preserve vanilla's map-authoring restrictions. "Any angle" should not
        // bypass NoSpearStickZone, room borders, shelter-door safety, or allow two
        // wall spears to occupy the same anchor tile.
        if (airTile.x <= 0 ||
            airTile.y <= 0 ||
            airTile.x >= room.abstractRoom.size.x - 1 ||
            airTile.y >= room.abstractRoom.size.y - 1)
        {
            return false;
        }

        if (room.abstractRoom.entities != null)
        {
            for (int i = 0; i < room.abstractRoom.entities.Count; i++)
            {
                if (room.abstractRoom.entities[i] is not AbstractSpear other ||
                    ReferenceEquals(other, spear.abstractPhysicalObject) ||
                    other.realizedObject is not Weapon weapon ||
                    weapon.mode != Weapon.Mode.StuckInWall ||
                    other.pos.Tile != airTile)
                {
                    continue;
                }

                return false;
            }
        }

        Vector2 tileCenter = room.MiddleOfTile(airTile);
        if (room.roomSettings?.placedObjects != null)
        {
            for (int i = 0; i < room.roomSettings.placedObjects.Count; i++)
            {
                PlacedObject placed = room.roomSettings.placedObjects[i];
                if (placed.type != PlacedObject.Type.NoSpearStickZone ||
                    placed.data is not PlacedObject.ResizableObjectData data)
                {
                    continue;
                }

                if (Custom.DistLess(tileCenter, placed.pos, data.Rad))
                {
                    return false;
                }
            }
        }

        if (room.abstractRoom.shelter &&
            room.shelterDoor != null &&
            (room.shelterDoor.IsClosing || room.shelterDoor.IsOpening))
        {
            return false;
        }

        return true;
    }

    private static void RestoreExactStuckPose(RopeSpear spear, Vector2 direction)
    {
        if (spear == null ||
            spear.room == null ||
            spear.mode != Weapon.Mode.StuckInWall ||
            direction.sqrMagnitude < 0.0001f)
        {
            return;
        }

        direction.Normalize();

        if (spear.stuckInWall.HasValue)
        {
            IntVector2 tile = spear.room.GetTilePosition(spear.stuckInWall.Value);
            if (!spear.room.GetTile(tile).Solid)
            {
                Vector2 anchor = spear.room.MiddleOfTile(tile) - direction * EmbedOffset;
                spear.stuckInWall = anchor;
                spear.firstChunk.HardSetPosition(anchor);
                spear.firstChunk.lastPos = anchor;
            }
        }

        spear.setRotation = direction;
        spear.rotation = direction;
        spear.lastRotation = direction;
        spear.rotationSpeed = 0f;
        spear.firstChunk.vel = Vector2.zero;
        spear.firstChunk.collideWithTerrain = false;
    }
}
