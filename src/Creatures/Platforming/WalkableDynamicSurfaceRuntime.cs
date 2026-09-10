using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.Creatures.Platforming;

/// <summary>
/// Shared player-side moving-ground integration. Providers supply continuous geometry only;
/// this runtime owns rider state, vanilla ground injection, carry, one-way landing and the
/// post-Room-physics reconciliation pass.
/// </summary>
internal static class WalkableDynamicSurfaceRuntime
{
    private const float TargetFootGap = 0.55f;
    private const float AcquireAboveTolerance = 4f;
    private const float AcquireBelowLimit = 48f;
    private const float MaintainGapTolerance = 9f;
    private const float MaintainPenetrationTolerance = 11f;
    private const float PreGroundInjectionTolerance = 2.5f;
    private const float ResolvedContactTolerance = 2f;
    private const float JumpDetachVelocity = 2.25f;
    private const float JumpDetachGap = 0.9f;
    private const float MaxAcquireUpwardVelocity = 2.5f;
    private const float MaxCarryPerPhase = 28f;
    private const float MaxInheritedSurfaceSpeed = 12f;
    private const float MinimumWalkableNormalY = 0.48f;
    private const int JumpDetachCooldown = 6;
    private const int EdgeDetachCooldown = 2;

    private sealed class RiderState
    {
        internal IWalkableDynamicSurface Surface;
        internal float Coordinate;
        internal Vector2 LastSurfacePoint;
        internal Vector2 LastSurfaceVelocity;
        internal float DistanceBeforeMovement;
        internal int DetachCooldown;
    }

    private sealed class FinalizerHolder
    {
        internal readonly Action Finalizer;
        internal FinalizerHolder(Action finalizer) => Finalizer = finalizer;
    }

    private static ConditionalWeakTable<Player, RiderState> states = new();
    private static readonly ConditionalWeakTable<object, FinalizerHolder> finalizers = new();
    private static readonly List<IWalkableDynamicSurface> surfaceBuffer = new();
    private static readonly List<WeakReference<Player>> activeRiders = new();
    private static bool enabled;

    internal static void Enable()
    {
        if (enabled) return;
        On.Player.Jump += Player_Jump;
        On.Player.MovementUpdate += Player_MovementUpdate;
        On.Room.Update += Room_Update;
        enabled = true;
    }

    internal static void Disable()
    {
        if (!enabled) return;
        On.Room.Update -= Room_Update;
        On.Player.MovementUpdate -= Player_MovementUpdate;
        On.Player.Jump -= Player_Jump;

        // Rider velocities live in the moving-surface frame. Restore world velocity before
        // dropping state so disabling/reloading the mod cannot leave a player in that frame.
        List<Player> ridersToDetach = new();
        for (int i = 0; i < activeRiders.Count; i++)
            if (activeRiders[i].TryGetTarget(out Player player) && player != null)
                ridersToDetach.Add(player);
        activeRiders.Clear();

        for (int i = 0; i < ridersToDetach.Count; i++)
            if (states.TryGetValue(ridersToDetach[i], out RiderState state) && state.Surface != null)
                Detach(ridersToDetach[i], state, default, 0, inheritSurfaceVelocity: true);

        states = new ConditionalWeakTable<Player, RiderState>();
        WalkableSurfaceRoomRegistry.Reset();
        enabled = false;
    }

    internal static void RegisterSurface(IWalkableDynamicSurface surface) =>
        WalkableSurfaceRoomRegistry.Register(surface);

    internal static void UnregisterSurface(IWalkableDynamicSurface surface) =>
        WalkableSurfaceRoomRegistry.Unregister(surface);

    internal static void RegisterPostPhysicsFinalizer(IWalkableDynamicSurface surface, Action finalizer)
    {
        if (surface == null || finalizer == null) return;
        object key = surface;
        finalizers.Remove(key);
        finalizers.Add(key, new FinalizerHolder(finalizer));
    }

    private static void Player_Jump(On.Player.orig_Jump orig, Player self)
    {
        if (states.TryGetValue(self, out RiderState state) && state.Surface != null)
        {
            // Jump() is the authoritative vanilla ground-jump event, including buffered jumps.
            // Leave the injected ContactPoint intact for the remainder of this MovementUpdate just
            // like a normal tile jump, but exit the moving velocity frame before vanilla applies
            // its jump impulse.
            Detach(self, state, default, JumpDetachCooldown, inheritSurfaceVelocity: true);
        }

        orig(self);
    }

    private static void Player_MovementUpdate(On.Player.orig_MovementUpdate orig, Player self, bool eu)
    {
        RiderState state = states.GetValue(self, _ => new RiderState());
        if (state.DetachCooldown > 0) state.DetachCooldown--;

        PrepareBeforeVanillaMovement(self, state);
        orig(self, eu);
        ResolveAfterVanillaMovement(self, state);
    }

    private static void Room_Update(On.Room.orig_Update orig, Room self)
    {
        orig(self);
        FinalizeRoomPhysics(self);
    }

    private static void PrepareBeforeVanillaMovement(Player player, RiderState state)
    {
        if (state.Surface == null)
        {
            if (state.DetachCooldown == 0 && CanRide(player))
            {
                BodyChunk acquisitionFeet = Feet(player);
                if (acquisitionFeet != null && TryAcquireSurface(player, acquisitionFeet, state))
                {
                    if (state.Surface != null &&
                        state.Surface.TrySample(state.Coordinate, out WalkableSurfaceSample acquiredSample))
                        InjectVanillaGroundState(player, state.Surface, acquiredSample);
                    else
                        Detach(player, state, default, EdgeDetachCooldown, inheritSurfaceVelocity: true);
                }
            }
            return;
        }

        if (!CanRide(player) || !SurfaceStillValid(player, state.Surface) ||
            !state.Surface.TrySample(state.Coordinate, out WalkableSurfaceSample carriedSample) ||
            carriedSample.Normal.y < MinimumWalkableNormalY ||
            !IsSurfaceSpeedRideable(carriedSample.Velocity))
        {
            Detach(player, state, default, EdgeDetachCooldown, inheritSurfaceVelocity: true);
            return;
        }

        Vector2 carry = carriedSample.Point - state.LastSurfacePoint;
        if (carry.magnitude > MaxCarryPerPhase)
        {
            Detach(player, state, carriedSample, EdgeDetachCooldown, inheritSurfaceVelocity: true);
            return;
        }

        TranslatePlayerAndResolveTerrain(player, carry);

        BodyChunk feet = Feet(player);
        if (feet == null || !state.Surface.TrySample(feet.pos, out WalkableSurfaceSample sample) ||
            sample.Normal.y < MinimumWalkableNormalY)
        {
            Detach(player, state, carriedSample, EdgeDetachCooldown, inheritSurfaceVelocity: true);
            return;
        }

        float distance = SignedFootDistance(feet, sample);
        if (!NearGroundInjectionContact(distance))
        {
            Detach(player, state, sample, EdgeDetachCooldown, inheritSurfaceVelocity: true);
            return;
        }

        state.Coordinate = sample.Coordinate;
        state.LastSurfacePoint = sample.Point;
        state.LastSurfaceVelocity = EffectiveSurfaceVelocity(sample.Velocity);
        state.DistanceBeforeMovement = distance;
        InjectVanillaGroundState(player, state.Surface, sample);
    }

    private static void ResolveAfterVanillaMovement(Player player, RiderState state)
    {
        if (!CanRide(player))
        {
            Detach(player, state, default, 0, inheritSurfaceVelocity: true);
            return;
        }

        BodyChunk feet = Feet(player);
        if (feet == null) return;

        if (state.Surface != null)
        {
            if (!SurfaceStillValid(player, state.Surface) ||
                !state.Surface.TrySample(feet.pos, out WalkableSurfaceSample sample) ||
                !IsSurfaceSpeedRideable(sample.Velocity))
            {
                Detach(player, state, default, EdgeDetachCooldown, inheritSurfaceVelocity: true);
                return;
            }

            float distance = SignedFootDistance(feet, sample);
            float riderNormalVelocity = RiderNormalVelocity(feet, sample);
            if (ShouldDetachAfterMovement(state.DistanceBeforeMovement, distance, riderNormalVelocity))
            {
                Detach(player, state, sample, JumpDetachCooldown, inheritSurfaceVelocity: true);
                return;
            }

            if (sample.Normal.y < MinimumWalkableNormalY || !WithinMaintainedContact(distance))
            {
                Detach(player, state, sample, EdgeDetachCooldown, inheritSurfaceVelocity: true);
                return;
            }

            ResolveSupportedContact(player, feet, state.Surface, sample, distance);
            if (!state.Surface.TrySample(feet.pos, out WalkableSurfaceSample resolvedSample) ||
                !NearResolvedContact(SignedFootDistance(feet, resolvedSample)))
            {
                Detach(player, state, sample, EdgeDetachCooldown, inheritSurfaceVelocity: true);
                return;
            }

            state.Coordinate = resolvedSample.Coordinate;
            state.LastSurfacePoint = resolvedSample.Point;
            state.LastSurfaceVelocity = EffectiveSurfaceVelocity(resolvedSample.Velocity);
            ClearOwnedJumpChunk(player, state.Surface);
            return;
        }

        if (state.DetachCooldown == 0)
            TryAcquireSurface(player, feet, state);
    }

    private static bool TryAcquireSurface(Player player, BodyChunk feet, RiderState state)
    {
        WalkableSurfaceRoomRegistry.CopyActive(player.room, surfaceBuffer);

        IWalkableDynamicSurface bestSurface = null;
        WalkableSurfaceSample bestSample = default;
        float bestScore = float.MaxValue;

        for (int i = 0; i < surfaceBuffer.Count; i++)
        {
            IWalkableDynamicSurface surface = surfaceBuffer[i];
            if (!SurfaceStillValid(player, surface) || ReferenceEquals(SurfaceOwner(surface), player) ||
                !surface.TrySample(feet.pos, out WalkableSurfaceSample sample) ||
                sample.Normal.y < MinimumWalkableNormalY ||
                !IsSurfaceSpeedRideable(sample.Velocity))
                continue;

            float distance = SignedFootDistance(feet, sample);
            float previousDistance = PreviousSignedFootDistance(feet, sample);
            float relativeNormalVelocity = RelativeNormalVelocity(feet, sample);
            if (!ShouldAcquireContact(previousDistance, distance, relativeNormalVelocity))
                continue;

            float score = Mathf.Abs(distance) + Mathf.Max(0f, relativeNormalVelocity) * 0.5f;
            if (score >= bestScore) continue;
            bestScore = score;
            bestSurface = surface;
            bestSample = sample;
        }

        if (bestSurface == null) return false;

        float bestDistance = SignedFootDistance(feet, bestSample);
        ResolveAcquiredContact(player, feet, bestSurface, bestSample, bestDistance);
        if (!bestSurface.TrySample(feet.pos, out WalkableSurfaceSample resolvedSample) ||
            !NearResolvedContact(SignedFootDistance(feet, resolvedSample)))
        {
            // ResolveAcquiredContact has already converted velocity into the moving frame. If
            // terrain rejection prevents ownership from being committed, restore world velocity.
            InheritSurfaceVelocity(player, bestSample.Velocity);
            return false;
        }

        state.Surface = bestSurface;
        state.Coordinate = resolvedSample.Coordinate;
        state.LastSurfacePoint = resolvedSample.Point;
        state.LastSurfaceVelocity = EffectiveSurfaceVelocity(resolvedSample.Velocity);
        state.DistanceBeforeMovement = SignedFootDistance(feet, resolvedSample);
        TrackRider(player);
        return true;
    }

    private static void FinalizeRoomPhysics(Room room)
    {
        if (room?.game == null) return;

        WalkableSurfaceRoomRegistry.CopyActive(room, surfaceBuffer);
        for (int i = 0; i < surfaceBuffer.Count; i++)
        {
            IWalkableDynamicSurface surface = surfaceBuffer[i];
            if (surface is IPostRoomPhysicsWalkableSurface finalizer)
            {
                finalizer.FinalizeSurfacePhysics();
                continue;
            }

            if (finalizers.TryGetValue(surface, out FinalizerHolder holder))
                holder.Finalizer();
        }

        if (room.updateList == null) return;
        for (int i = 0; i < room.updateList.Count; i++)
        {
            if (room.updateList[i] is Player player && states.TryGetValue(player, out RiderState state) &&
                state.Surface != null)
                FinalizeRiderAfterRoomPhysics(player, state);
        }
    }

    private static void FinalizeRiderAfterRoomPhysics(Player player, RiderState state)
    {
        if (!CanRide(player) || !SurfaceStillValid(player, state.Surface) ||
            !state.Surface.TrySample(state.Coordinate, out WalkableSurfaceSample coordinateSample) ||
            !IsSurfaceSpeedRideable(coordinateSample.Velocity))
        {
            Detach(player, state, default, EdgeDetachCooldown, inheritSurfaceVelocity: true);
            return;
        }

        Vector2 residualCarry = coordinateSample.Point - state.LastSurfacePoint;
        if (residualCarry.magnitude > MaxCarryPerPhase)
        {
            Detach(player, state, coordinateSample, EdgeDetachCooldown, inheritSurfaceVelocity: true);
            return;
        }

        TranslatePlayerAndResolveTerrain(player, residualCarry);

        BodyChunk feet = Feet(player);
        if (feet == null || !state.Surface.TrySample(feet.pos, out WalkableSurfaceSample sample) ||
            !IsSurfaceSpeedRideable(sample.Velocity))
        {
            Detach(player, state, coordinateSample, EdgeDetachCooldown, inheritSurfaceVelocity: true);
            return;
        }

        float distance = SignedFootDistance(feet, sample);
        float riderNormalVelocity = RiderNormalVelocity(feet, sample);
        if (sample.Normal.y < MinimumWalkableNormalY ||
            ShouldDetachAfterMovement(state.DistanceBeforeMovement, distance, riderNormalVelocity) ||
            !WithinMaintainedContact(distance))
        {
            Detach(player, state, sample,
                riderNormalVelocity > JumpDetachVelocity ? JumpDetachCooldown : EdgeDetachCooldown,
                inheritSurfaceVelocity: true);
            return;
        }

        ResolveSupportedContact(player, feet, state.Surface, sample, distance);
        if (!state.Surface.TrySample(feet.pos, out WalkableSurfaceSample resolvedSample) ||
            !NearResolvedContact(SignedFootDistance(feet, resolvedSample)))
        {
            Detach(player, state, sample, EdgeDetachCooldown, inheritSurfaceVelocity: true);
            return;
        }

        state.Coordinate = resolvedSample.Coordinate;
        state.LastSurfacePoint = resolvedSample.Point;
        state.LastSurfaceVelocity = EffectiveSurfaceVelocity(resolvedSample.Velocity);
        ClearOwnedJumpChunk(player, state.Surface);
    }

    private static void ResolveAcquiredContact(
        Player player,
        BodyChunk feet,
        IWalkableDynamicSurface surface,
        WalkableSurfaceSample sample,
        float signedDistance)
    {
        // Acquisition is already proven one-way by previous/current signed distances, so a
        // fast fall may require a larger correction than ordinary maintained contact.
        float correction = TargetFootGap - signedDistance;
        if (Mathf.Abs(correction) > 0.01f)
            TranslatePlayerAndResolveTerrain(player, sample.Normal * correction);

        // While attached, BodyChunk velocities are stored relative to the moving surface because
        // the surface transform itself is applied as positional carry. Convert world velocity once
        // on acquisition, then add the current surface velocity back exactly once on detach.
        Vector2 surfaceVelocity = EffectiveSurfaceVelocity(sample.Velocity);
        for (int i = 0; i < player.bodyChunks.Length; i++)
            if (player.bodyChunks[i] != null)
                player.bodyChunks[i].vel = ToRiderVelocity(player.bodyChunks[i].vel, surfaceVelocity);

        float riderNormalVelocity = RiderNormalVelocity(feet, sample);
        if (riderNormalVelocity < 0f)
        {
            Vector2 cancel = sample.Normal * -riderNormalVelocity;
            for (int i = 0; i < player.bodyChunks.Length; i++)
                if (player.bodyChunks[i] != null)
                    player.bodyChunks[i].vel += cancel;
        }

        ClearOwnedJumpChunk(player, surface);
    }

    private static void ResolveSupportedContact(
        Player player,
        BodyChunk feet,
        IWalkableDynamicSurface surface,
        WalkableSurfaceSample sample,
        float signedDistance)
    {
        float correction = ContactCorrection(signedDistance);
        if (Mathf.Abs(correction) > 0.01f)
            TranslatePlayerAndResolveTerrain(player, sample.Normal * correction);

        float riderNormalVelocity = RiderNormalVelocity(feet, sample);
        if (riderNormalVelocity < 0f)
        {
            Vector2 cancel = sample.Normal * -riderNormalVelocity;
            for (int i = 0; i < player.bodyChunks.Length; i++)
                if (player.bodyChunks[i] != null)
                    player.bodyChunks[i].vel += cancel;
        }

        ClearOwnedJumpChunk(player, surface);
    }

    private static void InjectVanillaGroundState(
        Player player,
        IWalkableDynamicSurface surface,
        WalkableSurfaceSample sample)
    {
        BodyChunk feet = Feet(player);
        if (feet == null) return;

        // MovementUpdate itself derives canJump and Stand/Crawl from ContactPoint. Do not write
        // player.standing or canJump here: those are locomotion state, not platform state.
        feet.contactPoint.y = -1;
        feet.terrainCurveNormal = sample.Normal;
        ClearOwnedJumpChunk(player, surface);
    }

    private static void TranslatePlayerAndResolveTerrain(Player player, Vector2 delta)
    {
        if (delta.sqrMagnitude < 0.000001f || player?.bodyChunks == null) return;

        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            BodyChunk chunk = player.bodyChunks[i];
            if (chunk == null) continue;
            chunk.pos += delta;
        }

        // Carry occurs after the normal BodyChunk terrain pass, so re-run the public collision
        // probes immediately. lastPos remains the true previous-frame position for rendering.
        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            BodyChunk chunk = player.bodyChunks[i];
            if (chunk == null || !chunk.collideWithTerrain || chunk.buried) continue;
            chunk.onSlope = 0;
            chunk.terrainCurveNormal = Vector2.zero;
            chunk.CheckVerticalCollision();
            if (chunk.collideWithSlopes)
                chunk.checkAgainstSlopesVertically();
            chunk.CheckHorizontalCollision();
        }
    }

    private static void Detach(
        Player player,
        RiderState state,
        WalkableSurfaceSample sample,
        int cooldown,
        bool inheritSurfaceVelocity)
    {
        IWalkableDynamicSurface surface = state.Surface;
        if (surface != null && inheritSurfaceVelocity)
        {
            Vector2 velocity = sample.Surface != null
                ? EffectiveSurfaceVelocity(sample.Velocity)
                : state.LastSurfaceVelocity;
            InheritSurfaceVelocity(player, velocity);
        }

        if (surface != null)
            ClearOwnedJumpChunk(player, surface);

        state.Surface = null;
        state.Coordinate = 0f;
        state.LastSurfacePoint = Vector2.zero;
        state.LastSurfaceVelocity = Vector2.zero;
        state.DistanceBeforeMovement = 0f;
        state.DetachCooldown = Math.Max(state.DetachCooldown, cooldown);
        UntrackRider(player);
    }

    private static void TrackRider(Player player)
    {
        if (player == null) return;
        for (int i = activeRiders.Count - 1; i >= 0; i--)
        {
            if (!activeRiders[i].TryGetTarget(out Player existing) || existing == null)
            {
                activeRiders.RemoveAt(i);
                continue;
            }
            if (ReferenceEquals(existing, player)) return;
        }
        activeRiders.Add(new WeakReference<Player>(player));
    }

    private static void UntrackRider(Player player)
    {
        if (player == null) return;
        for (int i = activeRiders.Count - 1; i >= 0; i--)
        {
            if (!activeRiders[i].TryGetTarget(out Player existing) || existing == null ||
                ReferenceEquals(existing, player))
                activeRiders.RemoveAt(i);
        }
    }

    private static void InheritSurfaceVelocity(Player player, Vector2 surfaceVelocity)
    {
        if (player?.bodyChunks == null) return;
        Vector2 inherited = EffectiveSurfaceVelocity(surfaceVelocity);
        for (int i = 0; i < player.bodyChunks.Length; i++)
            if (player.bodyChunks[i] != null)
                player.bodyChunks[i].vel = ToWorldVelocity(player.bodyChunks[i].vel, inherited);
    }

    private static void ClearOwnedJumpChunk(Player player, IWalkableDynamicSurface surface)
    {
        PhysicalObject owner = SurfaceOwner(surface);
        if (player?.jumpChunk == null || owner == null ||
            !ReferenceEquals(player.jumpChunk.owner, owner))
            return;

        player.jumpChunk = null;
        player.jumpChunkCounter = 0;
    }

    private static BodyChunk Feet(Player player) =>
        player?.bodyChunks != null && player.bodyChunks.Length > 1 ? player.bodyChunks[1] : null;

    private static float SignedFootDistance(BodyChunk feet, WalkableSurfaceSample sample) =>
        Vector2.Dot(feet.pos - sample.Point, sample.Normal) - feet.rad;

    private static float PreviousSignedFootDistance(BodyChunk feet, WalkableSurfaceSample sample) =>
        Vector2.Dot(feet.lastPos - sample.PreviousPoint, sample.Normal) - feet.rad;

    private static float RelativeNormalVelocity(BodyChunk feet, WalkableSurfaceSample sample) =>
        Vector2.Dot(feet.vel - sample.Velocity, sample.Normal);

    private static float RiderNormalVelocity(BodyChunk feet, WalkableSurfaceSample sample) =>
        Vector2.Dot(feet.vel, sample.Normal);

    private static Vector2 EffectiveSurfaceVelocity(Vector2 surfaceVelocity) =>
        Vector2.ClampMagnitude(surfaceVelocity, MaxInheritedSurfaceSpeed);

    internal static Vector2 ToRiderVelocity(Vector2 worldVelocity, Vector2 surfaceVelocity) =>
        worldVelocity - surfaceVelocity;

    internal static Vector2 ToWorldVelocity(Vector2 riderVelocity, Vector2 surfaceVelocity) =>
        riderVelocity + surfaceVelocity;

    internal static bool IsSurfaceSpeedRideable(Vector2 surfaceVelocity) =>
        surfaceVelocity.magnitude <= MaxInheritedSurfaceSpeed;

    private static bool SurfaceStillValid(Player player, IWalkableDynamicSurface surface) =>
        surface != null && surface.SurfaceEnabled && surface.SurfaceRoom == player.room &&
        SurfaceOwner(surface) != null && !SurfaceOwner(surface).slatedForDeletetion;

    private static PhysicalObject SurfaceOwner(IWalkableDynamicSurface surface)
    {
        if (surface is IWalkableDynamicSurfaceOwner ownerProvider)
            return ownerProvider.SurfaceOwner;
        return surface as PhysicalObject;
    }

    private static bool CanRide(Player player)
    {
        if (player?.room == null || Feet(player) == null || player.dead || !player.Consious ||
            player.inShortcut || player.enteringShortCut.HasValue || player.grabbedBy.Count > 0 ||
            player.Submersion > 0.55f)
            return false;

        return IsGroundCompatibleBodyMode(player.bodyMode);
    }

    internal static bool IsGroundCompatibleBodyMode(Player.BodyModeIndex bodyMode) =>
        bodyMode == Player.BodyModeIndex.Default ||
        bodyMode == Player.BodyModeIndex.Stand ||
        bodyMode == Player.BodyModeIndex.Crawl;

    internal static bool ShouldAcquireContact(
        float previousDistance,
        float currentDistance,
        float relativeNormalVelocity)
    {
        if (previousDistance < -1.5f || currentDistance > AcquireAboveTolerance ||
            currentDistance < -AcquireBelowLimit || relativeNormalVelocity > MaxAcquireUpwardVelocity)
            return false;

        bool sweptDownThroughSurface = previousDistance >= -1.5f && currentDistance <= TargetFootGap;
        bool restingNearSurface = currentDistance >= -1.5f && currentDistance <= AcquireAboveTolerance;
        return sweptDownThroughSurface || restingNearSurface;
    }

    internal static bool ShouldDetachAfterMovement(
        float previousDistance,
        float currentDistance,
        float riderNormalVelocity) =>
        riderNormalVelocity > JumpDetachVelocity &&
        currentDistance > JumpDetachGap &&
        currentDistance > previousDistance + 0.25f;

    internal static float ContactCorrection(float signedDistance) =>
        Mathf.Clamp(TargetFootGap - signedDistance, -MaintainGapTolerance, MaintainPenetrationTolerance);

    private static bool NearGroundInjectionContact(float distance) =>
        Mathf.Abs(distance - TargetFootGap) <= PreGroundInjectionTolerance;

    private static bool NearResolvedContact(float distance) =>
        Mathf.Abs(distance - TargetFootGap) <= ResolvedContactTolerance;

    private static bool WithinMaintainedContact(float distance) =>
        distance <= MaintainGapTolerance && distance >= -MaintainPenetrationTolerance;
}
