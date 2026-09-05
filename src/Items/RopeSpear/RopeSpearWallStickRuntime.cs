using System.Runtime.CompilerServices;
using RWCustom;
using UnityEngine;

namespace DryCycle.Items.RopeSpear;

/// <summary>
/// Gives RopeSpear deterministic terrain sticking at the real projectile angle.
/// Vanilla Spear wall logic is cardinal and probabilistic; this runtime uses the
/// continuous flight vector, preserves that angle through save/load, and delegates
/// non-cardinal traversal to RopeSpearShaftTraversalRuntime.
/// </summary>
internal static class RopeSpearWallStickRuntime
{
    private const float SpearTipReach = 22f;
    private const float TraceStep = 2f;
    private const float EmbedOffset = 5f;
    private const float NonCardinalComponentThreshold = 0.015f;
    private const float ShaftTailReach = 27f;
    private const float ShaftWallReach = 6f;
    private const float StandableSlopeLimit = 0.80f;
    private const float LowSpeedThreshold = 1.5f;
    private const int LowSpeedReleaseFrames = 18;

    private sealed class FlightState
    {
        internal bool InSpearUpdate;
        internal Vector2 StartPosition;
        internal Vector2 Velocity;
        internal Vector2 Direction;
        internal int LowSpeedFrames;
    }

    private static readonly ConditionalWeakTable<RopeSpear, FlightState> FlightStates = new();
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.Spear.Update += Spear_Update;
        On.Spear.ChangeMode += Spear_ChangeMode;
        On.Weapon.HitWall += Weapon_HitWall;
        RopeSpearShaftTraversalRuntime.Enable();
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        RopeSpearShaftTraversalRuntime.Disable();
        On.Weapon.HitWall -= Weapon_HitWall;
        On.Spear.ChangeMode -= Spear_ChangeMode;
        On.Spear.Update -= Spear_Update;
        _enabled = false;
    }

    internal static bool TryGetTraversalSegment(
        RopeSpear spear,
        out Vector2 tail,
        out Vector2 wallEnd,
        out Vector2 direction,
        out Vector2 supportNormal,
        out bool canStand)
    {
        tail = Vector2.zero;
        wallEnd = Vector2.zero;
        direction = Vector2.right;
        supportNormal = Vector2.up;
        canStand = false;

        if (spear == null ||
            spear.room == null ||
            spear.mode != Weapon.Mode.StuckInWall ||
            spear.slatedForDeletetion ||
            !TryResolveStoredStuckDirection(spear, out direction) ||
            !IsNonCardinal(direction))
        {
            return false;
        }

        direction.Normalize();
        Vector2 center = spear.firstChunk.pos;
        tail = center - direction * ShaftTailReach;
        wallEnd = center + direction * ShaftWallReach;

        for (int i = 0; i < 7 && spear.room.GetTile(wallEnd).Solid; i++)
        {
            wallEnd -= direction * 2f;
        }

        if (spear.room.GetTile(tail).Solid)
        {
            for (int i = 0; i < 7 && spear.room.GetTile(tail).Solid; i++)
            {
                tail += direction * 2f;
            }
        }

        if (Vector2.Distance(tail, wallEnd) < 12f)
        {
            return false;
        }

        supportNormal = new Vector2(-direction.y, direction.x);
        if (supportNormal.y < 0f)
        {
            supportNormal = -supportNormal;
        }
        supportNormal.Normalize();

        canStand = Mathf.Abs(direction.y) <= StandableSlopeLimit;
        return true;
    }

    internal static bool IsNonCardinalStuckSpear(RopeSpear spear)
    {
        return spear != null &&
               spear.mode == Weapon.Mode.StuckInWall &&
               TryResolveStoredStuckDirection(spear, out Vector2 direction) &&
               IsNonCardinal(direction);
    }

    private static void Spear_Update(
        On.Spear.orig_Update orig,
        Spear self,
        bool eu)
    {
        if (self is not RopeSpear ropeSpear || ropeSpear.room == null)
        {
            orig(self, eu);
            return;
        }

        if (ropeSpear.mode == Weapon.Mode.StuckInWall)
        {
            if (TryResolveStoredStuckDirection(ropeSpear, out Vector2 storedDirection) &&
                IsNonCardinal(storedDirection))
            {
                ropeSpear.addPoles = false;
            }

            orig(self, eu);

            if (TryResolveStoredStuckDirection(ropeSpear, out storedDirection))
            {
                RestoreExactStuckPose(ropeSpear, storedDirection);
            }
            return;
        }

        if (ropeSpear.mode != Weapon.Mode.Thrown)
        {
            orig(self, eu);
            return;
        }

        ropeSpear.alwaysStickInWalls = true;
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
            state.LowSpeedFrames = 0;
            return;
        }

        if (ropeSpear.mode == Weapon.Mode.StuckInWall)
        {
            RememberStuckDirection(ropeSpear, state.Direction);
            RestoreExactStuckPose(ropeSpear, state.Direction);
            state.LowSpeedFrames = 0;
            return;
        }

        if (TryStickFromFlight(ropeSpear, state))
        {
            state.LowSpeedFrames = 0;
            return;
        }

        if (ropeSpear.mode != Weapon.Mode.Thrown)
        {
            state.LowSpeedFrames = 0;
            return;
        }

        if (ropeSpear.firstChunk.vel.magnitude < LowSpeedThreshold)
        {
            state.LowSpeedFrames++;
            if (state.LowSpeedFrames >= LowSpeedReleaseFrames)
            {
                ropeSpear.ChangeMode(Weapon.Mode.Free);
                state.LowSpeedFrames = 0;
            }
        }
        else
        {
            state.LowSpeedFrames = 0;
        }
    }

    private static void Spear_ChangeMode(
        On.Spear.orig_ChangeMode orig,
        Spear self,
        Weapon.Mode newMode)
    {
        bool wasStuckInWall = self is RopeSpear && self.mode == Weapon.Mode.StuckInWall;

        Vector2 liveDirection = Vector2.zero;
        FlightState liveState = null;
        bool haveLiveDirection = self is RopeSpear liveSpear &&
                                 FlightStates.TryGetValue(liveSpear, out liveState) &&
                                 liveState.InSpearUpdate &&
                                 liveState.Direction.sqrMagnitude > 0.25f;
        if (haveLiveDirection)
        {
            liveDirection = liveState.Direction.normalized;
        }

        orig(self, newMode);

        if (self is not RopeSpear ropeSpear)
        {
            return;
        }

        if (newMode == Weapon.Mode.StuckInWall)
        {
            Vector2 direction;
            if (haveLiveDirection)
            {
                direction = liveDirection;
            }
            else if (!TryResolveStoredStuckDirection(ropeSpear, out direction))
            {
                direction = ResolveFlightDirection(ropeSpear, ropeSpear.firstChunk.vel);
            }

            RememberStuckDirection(ropeSpear, direction);
            if (IsNonCardinal(direction))
            {
                ropeSpear.addPoles = false;
            }
            RestoreExactStuckPose(ropeSpear, direction);
        }
        else if (wasStuckInWall &&
                 ropeSpear.abstractPhysicalObject is AbstractRopeSpear data)
        {
            data.SetPersistentStuckDirection(Vector2.zero);
        }
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

    private static bool TryResolveStoredStuckDirection(
        RopeSpear spear,
        out Vector2 direction)
    {
        direction = Vector2.zero;
        if (spear?.abstractPhysicalObject is AbstractRopeSpear data &&
            data.TryGetPersistentStuckDirection(out direction))
        {
            return true;
        }

        if (spear == null || spear.rotation.sqrMagnitude <= 0.25f)
        {
            return false;
        }

        direction = spear.rotation.normalized;
        return true;
    }

    private static void RememberStuckDirection(RopeSpear spear, Vector2 direction)
    {
        if (spear?.abstractPhysicalObject is not AbstractRopeSpear data ||
            direction.sqrMagnitude <= 0.25f)
        {
            return;
        }

        data.SetPersistentStuckDirection(direction.normalized);
    }

    private static bool IsNonCardinal(Vector2 direction)
    {
        if (direction.sqrMagnitude <= 0.25f)
        {
            return false;
        }

        direction.Normalize();
        return Mathf.Abs(direction.x) > NonCardinalComponentThreshold &&
               Mathf.Abs(direction.y) > NonCardinalComponentThreshold;
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
        int cycles = Random.Range(3, 7);
        spear.abstractSpear.stuckInWallCycles = surfaceDirection.x != 0
            ? cycles
            : -cycles;
        spear.throwDir = surfaceDirection;
        spear.stuckInWall = anchorPosition;
        spear.vibrate = 10;
        RememberStuckDirection(spear, direction);
        spear.ChangeMode(Weapon.Mode.StuckInWall);

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

        Vector2 currentCenter = spear.firstChunk.pos;
        Vector2 centerEnd = Vector2.Distance(startPosition, currentCenter) > 0.5f
            ? currentCenter
            : startPosition + velocity;
        Vector2 endPosition = centerEnd + direction * SpearTipReach;
        float distance = Vector2.Distance(startPosition, endPosition);
        int samples = Mathf.Clamp(Mathf.CeilToInt(distance / TraceStep), 1, 64);

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

        if (IsNonCardinal(direction))
        {
            spear.addPoles = false;
        }

        spear.setRotation = direction;
        spear.rotation = direction;
        spear.lastRotation = direction;
        spear.rotationSpeed = 0f;
        spear.firstChunk.vel = Vector2.zero;
        spear.firstChunk.collideWithTerrain = false;
    }
}
