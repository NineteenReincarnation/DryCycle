using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.Creatures.Platforming;

/// <summary>
/// Shared player-side moving-platform integration for any IWalkableDynamicSurface provider.
/// Providers supply geometry only; this runtime carries riders, injects vanilla ground state,
/// resolves the top surface after ordinary collisions, and releases riders at jumps/edges.
/// </summary>
internal static class WalkableDynamicSurfaceRuntime
{
    private const float AcquireAboveTolerance = 7f;
    private const float AcquirePenetrationTolerance = 5f;
    private const float MaintainGapTolerance = 11f;
    private const float MaintainPenetrationTolerance = 9f;
    private const float MaxCarryPerFrame = 28f;
    private const float MaxInheritedSurfaceSpeed = 12f;
    private const float MinimumWalkableNormalY = 0.48f;
    private const int JumpDetachCooldown = 6;
    private const int EdgeDetachCooldown = 2;

    private sealed class RiderState
    {
        internal IWalkableDynamicSurface Surface;
        internal float Coordinate;
        internal Vector2 LastSurfacePoint;
        internal int DetachCooldown;
    }

    private static ConditionalWeakTable<Player, RiderState> states = new();
    private static bool enabled;

    internal static void Enable()
    {
        if (enabled) return;
        On.Player.MovementUpdate += Player_MovementUpdate;
        enabled = true;
    }

    internal static void Disable()
    {
        if (!enabled) return;
        On.Player.MovementUpdate -= Player_MovementUpdate;
        states = new ConditionalWeakTable<Player, RiderState>();
        enabled = false;
    }

    private static void Player_MovementUpdate(On.Player.orig_MovementUpdate orig, Player self, bool eu)
    {
        RiderState state = states.GetValue(self, _ => new RiderState());
        PrepareBeforeVanillaMovement(self, state);
        orig(self, eu);
        ResolveAfterVanillaMovement(self, state);
    }

    private static void PrepareBeforeVanillaMovement(Player player, RiderState state)
    {
        if (state.DetachCooldown > 0) state.DetachCooldown--;

        if (!CanRide(player))
        {
            ClearRide(state, 0);
            return;
        }

        if (state.Surface == null) return;
        if (!SurfaceStillValid(player, state.Surface) ||
            !state.Surface.TrySample(state.Coordinate, out WalkableSurfaceSample sample))
        {
            ClearRide(state, EdgeDetachCooldown);
            return;
        }

        Vector2 carry = sample.Point - state.LastSurfacePoint;
        if (carry.magnitude > MaxCarryPerFrame)
        {
            ClearRide(state, EdgeDetachCooldown);
            return;
        }

        // Moving ground translates both the current and interpolation positions. Player velocity
        // remains the player's own relative locomotion; platform velocity is inherited on detach.
        TranslatePlayer(player, carry, translateLastPos: true);
        state.LastSurfacePoint = sample.Point;
        InjectVanillaGroundState(player);
    }

    private static void ResolveAfterVanillaMovement(Player player, RiderState state)
    {
        if (!CanRide(player))
        {
            ClearRide(state, 0);
            return;
        }

        BodyChunk feet = player.bodyChunks?[1];
        if (feet == null) return;

        if (state.Surface != null)
        {
            if (!SurfaceStillValid(player, state.Surface))
            {
                ClearRide(state, EdgeDetachCooldown);
                return;
            }

            if (!state.Surface.TrySample(feet.pos, out WalkableSurfaceSample sample))
            {
                if (state.Surface.TrySample(state.Coordinate, out WalkableSurfaceSample edgeSample))
                    InheritSurfaceVelocity(player, edgeSample.Velocity);
                ClearRide(state, EdgeDetachCooldown);
                return;
            }

            float distance = SignedFootDistance(feet, sample);
            float relativeNormalVelocity = Vector2.Dot(feet.vel - sample.Velocity, sample.Normal);
            bool jumpPressed = player.input != null && player.input.Length > 1 &&
                               player.input[0].jmp && !player.input[1].jmp;
            if (jumpPressed && relativeNormalVelocity > 1.5f)
            {
                InheritSurfaceVelocity(player, sample.Velocity);
                ClearRide(state, JumpDetachCooldown);
                return;
            }

            if (sample.Normal.y < MinimumWalkableNormalY ||
                distance > MaintainGapTolerance || distance < -MaintainPenetrationTolerance)
            {
                InheritSurfaceVelocity(player, sample.Velocity);
                ClearRide(state, EdgeDetachCooldown);
                return;
            }

            ResolveSupportedContact(player, feet, sample, distance);
            state.Coordinate = sample.Coordinate;
            state.LastSurfacePoint = sample.Point;
            InjectVanillaGroundState(player);
            return;
        }

        if (state.DetachCooldown > 0) return;
        TryAcquireSurface(player, feet, state);
    }

    private static void TryAcquireSurface(Player player, BodyChunk feet, RiderState state)
    {
        Room room = player.room;
        if (room?.updateList == null) return;

        IWalkableDynamicSurface bestSurface = null;
        WalkableSurfaceSample bestSample = default;
        float bestScore = float.MaxValue;

        for (int i = 0; i < room.updateList.Count; i++)
        {
            if (room.updateList[i] is not IWalkableDynamicSurface surface ||
                !SurfaceStillValid(player, surface) ||
                !surface.TrySample(feet.pos, out WalkableSurfaceSample sample) ||
                sample.Normal.y < MinimumWalkableNormalY)
                continue;

            float distance = SignedFootDistance(feet, sample);
            float previousDistance = Vector2.Dot(feet.lastPos - sample.PreviousPoint, sample.Normal) - feet.rad;
            float relativeNormalVelocity = Vector2.Dot(feet.vel - sample.Velocity, sample.Normal);

            // One-way top surface: the feet must have approached from the top side. Stationary
            // placement immediately above the shell is accepted, but rising through from below is not.
            if (distance > AcquireAboveTolerance || distance < -AcquirePenetrationTolerance ||
                previousDistance < -1.5f || relativeNormalVelocity > 3f)
                continue;

            float score = Mathf.Abs(distance) + Mathf.Max(0f, relativeNormalVelocity) * 0.5f;
            if (score >= bestScore) continue;
            bestScore = score;
            bestSurface = surface;
            bestSample = sample;
        }

        if (bestSurface == null) return;

        float bestDistance = SignedFootDistance(feet, bestSample);
        ResolveSupportedContact(player, feet, bestSample, bestDistance);
        state.Surface = bestSurface;
        state.Coordinate = bestSample.Coordinate;
        state.LastSurfacePoint = bestSample.Point;
        InjectVanillaGroundState(player);
    }

    private static void ResolveSupportedContact(
        Player player,
        BodyChunk feet,
        WalkableSurfaceSample sample,
        float signedDistance)
    {
        // Only correct into the surface normal. Tangential movement stays entirely under Player.
        float correction = 0.55f - signedDistance;
        if (correction > 0f)
            TranslatePlayer(player, sample.Normal * Mathf.Min(correction, 12f), translateLastPos: false);

        float relativeNormalVelocity = Vector2.Dot(feet.vel - sample.Velocity, sample.Normal);
        if (relativeNormalVelocity < 0f)
        {
            Vector2 cancel = sample.Normal * -relativeNormalVelocity;
            for (int i = 0; i < player.bodyChunks.Length; i++)
                if (player.bodyChunks[i] != null)
                    player.bodyChunks[i].vel += cancel;
        }

        // Avoid vanilla Creature-on-Creature jumpChunk behaviour while this contact is owned as ground.
        player.jumpChunk = null;
        player.jumpChunkCounter = 0;
    }

    private static void InjectVanillaGroundState(Player player)
    {
        BodyChunk feet = player.bodyChunks?[1];
        if (feet == null) return;

        feet.contactPoint.y = -1;
        feet.lastContactPoint.y = -1;
        player.standing = true;
        player.canJump = Math.Max(player.canJump, 5);
        player.jumpChunk = null;
        player.jumpChunkCounter = 0;
    }

    private static void TranslatePlayer(Player player, Vector2 delta, bool translateLastPos)
    {
        if (delta.sqrMagnitude < 0.000001f || player.bodyChunks == null) return;
        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            BodyChunk chunk = player.bodyChunks[i];
            if (chunk == null) continue;
            chunk.pos += delta;
            if (translateLastPos) chunk.lastPos += delta;
        }
    }

    private static void InheritSurfaceVelocity(Player player, Vector2 surfaceVelocity)
    {
        if (player.bodyChunks == null) return;
        Vector2 inherited = Vector2.ClampMagnitude(surfaceVelocity, MaxInheritedSurfaceSpeed);
        for (int i = 0; i < player.bodyChunks.Length; i++)
            if (player.bodyChunks[i] != null)
                player.bodyChunks[i].vel += inherited;
    }

    private static float SignedFootDistance(BodyChunk feet, WalkableSurfaceSample sample) =>
        Vector2.Dot(feet.pos - sample.Point, sample.Normal) - feet.rad;

    private static bool SurfaceStillValid(Player player, IWalkableDynamicSurface surface) =>
        surface != null && surface.SurfaceEnabled && surface.SurfaceRoom == player.room;

    private static bool CanRide(Player player)
    {
        if (player?.room == null || player.bodyChunks == null || player.bodyChunks.Length < 2 ||
            player.dead || !player.Consious || player.inShortcut || player.enteringShortCut.HasValue ||
            player.grabbedBy.Count > 0 || player.submersion > 0.55f)
            return false;

        return player.bodyMode != Player.BodyModeIndex.Swimming &&
               player.bodyMode != Player.BodyModeIndex.CorridorClimb &&
               player.bodyMode != Player.BodyModeIndex.ClimbIntoShortCut;
    }

    private static void ClearRide(RiderState state, int cooldown)
    {
        state.Surface = null;
        state.Coordinate = 0f;
        state.LastSurfacePoint = Vector2.zero;
        state.DetachCooldown = Math.Max(state.DetachCooldown, cooldown);
    }
}
