using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.Creatures.Platforming;

/// <summary>
/// Shared player-side moving-surface integration. Providers supply continuous geometry only;
/// this runtime owns rider state, vanilla ground/slide semantics, carry, one-way landing and the
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
    private const float MinimumContactNormalY = 0.05f;
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
                    // World-space re-sampling matters at finite curve endpoints: coordinate-only
                    // sampling has no rider position from which to reconstruct the radial cap normal.
                    if (state.Surface != null &&
                        state.Surface.TrySample(acquisitionFeet.pos, out WalkableSurfaceSample acquiredSample) &&
                        IsSurfaceContactNormal(acquiredSample.Normal))
                        ApplyVanillaSurfaceState(player, state.Surface, acquiredSample);
                    else
                        Detach(player, state, default, EdgeDetachCooldown, inheritSurfaceVelocity: true);
                }
            }
            return;
        }

        if (!CanRide(player) || !SurfaceStillValid(player, state.Surface) ||
            !state.Surface.TrySample(state.Coordinate, out WalkableSurfaceSample carriedSample) ||
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
            !IsSurfaceContactNormal(sample.Normal))
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
        ApplyVanillaSurfaceState(player, state.Surface, sample);
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
                !IsSurfaceContactNormal(sample.Normal) ||
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

            if (!WithinMaintainedContact(distance))
            {
                Detach(player, state, sample, EdgeDetachCooldown, inheritSurfaceVelocity: true);
                return;
            }

            ResolveSupportedContact(player, feet, state.Surface, sample, distance, applySlideFriction: true);
            if (!state.Surface.TrySample(feet.pos, out WalkableSurfaceSample resolvedSample) ||
                !IsSurfaceContactNormal(resolvedSample.Normal) ||
                !NearResolvedContact(SignedFootDistance(feet, resolvedSample)))
            {
                Detach(player, state, sample, EdgeDetachCooldown, inheritSurfaceVelocity: true);
                return;
            }

            state.Coordinate = resolvedSample.Coordinate;
            state.LastSurfacePoint = resolvedSample.Point;
            state.LastSurfaceVelocity = EffectiveSurfaceVelocity(resolvedSample.Velocity);
            ApplyVanillaSurfaceState(player, state.Surface, resolvedSample);
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
                !IsSurfaceContactNormal(sample.Normal) ||
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
            !IsSurfaceContactNormal(resolvedSample.Normal) ||
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
        ApplyVanillaSurfaceState(player, bestSurface, resolvedSample);
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
            !IsSurfaceContactNormal(sample.Normal) ||
            !IsSurfaceSpeedRideable(sample.Velocity))
        {
            Detach(player, state, coordinateSample, EdgeDetachCooldown, inheritSurfaceVelocity: true);
            return;
        }

        float distance = SignedFootDistance(feet, sample);
        float riderNormalVelocity = RiderNormalVelocity(feet, sample);
        if (ShouldDetachAfterMovement(state.DistanceBeforeMovement, distance, riderNormalVelocity) ||
            !WithinMaintainedContact(distance))
        {
            Detach(player, state, sample,
                riderNormalVelocity > JumpDetachVelocity ? JumpDetachCooldown : EdgeDetachCooldown,
                inheritSurfaceVelocity: true);
            return;
        }

        // Finalization exists to undo late Room collision displacement. Do not apply slide
        // friction twice in one frame; the player-side movement pass already did that.
        ResolveSupportedContact(player, feet, state.Surface, sample, distance, applySlideFriction: false);
        if (!state.Surface.TrySample(feet.pos, out WalkableSurfaceSample resolvedSample) ||
            !IsSurfaceContactNormal(resolvedSample.Normal) ||
            !NearResolvedContact(SignedFootDistance(feet, resolvedSample)))
        {
            Detach(player, state, sample, EdgeDetachCooldown, inheritSurfaceVelocity: true);
            return;
        }

        state.Coordinate = resolvedSample.Coordinate;
        state.LastSurfacePoint = resolvedSample.Point;
        state.LastSurfaceVelocity = EffectiveSurfaceVelocity(resolvedSample.Velocity);
        ApplyVanillaSurfaceState(player, state.Surface, resolvedSample);
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

        ResolveContactVelocity(player, feet, sample, applySlideFriction: true);
        ApplyVanillaSurfaceState(player, surface, sample);
    }

    private static void ResolveSupportedContact(
        Player player,
        BodyChunk feet,
        IWalkableDynamicSurface surface,
        WalkableSurfaceSample sample,
        float signedDistance,
        bool applySlideFriction)
    {
        float correction = ContactCorrection(signedDistance);
        if (Mathf.Abs(correction) > 0.01f)
            TranslatePlayerAndResolveTerrain(player, sample.Normal * correction);

        ResolveContactVelocity(player, feet, sample, applySlideFriction);
        ApplyVanillaSurfaceState(player, surface, sample);
    }

    private static void ResolveContactVelocity(
        Player player,
        BodyChunk feet,
        WalkableSurfaceSample sample,
        bool applySlideFriction)
    {
        if (player == null || feet == null) return;

        // BodyChunk's TerrainCurve collision has two deliberately different responses. A shallow
        // surface is ground: downward gravity is cancelled without being projected into downhill
        // motion, while horizontal locomotion is converted into the required vertical slope motion.
        // Only a steep surface uses normal/tangent projection and is allowed to slide.
        feet.vel = IsGroundingNormal(sample.Normal)
            ? ResolveGroundContactVelocity(feet.vel, sample.Normal, player.surfaceFriction, player.bounce, player.gravity)
            : ResolveSlidingContactVelocity(feet.vel, sample.Normal, player.surfaceFriction, player.bounce, applySlideFriction);
    }

    internal static Vector2 ResolveGroundContactVelocity(
        Vector2 velocity,
        Vector2 normal,
        float surfaceFriction,
        float bounce,
        float gravity)
    {
        if (normal.y <= 0.0001f) return velocity;

        // Mirrors BodyChunk's ordinary TerrainCurve ground branch. In particular, a pure downward
        // velocity must resolve to rest instead of gaining a horizontal component on a shallow
        // slope. Horizontal velocity alone contributes the vertical amount needed to follow it.
        float magnitude = velocity.magnitude;
        float slopeTransfer = velocity.x * -normal.x / normal.y;
        velocity.y -= slopeTransfer;
        velocity.y = Mathf.Abs(velocity.y) * bounce;
        if (velocity.y < gravity || velocity.y < 1f + 9f * (1f - bounce))
            velocity.y = 0f;
        velocity.y += slopeTransfer;
        velocity.x *= Mathf.Clamp(surfaceFriction * 2f, 0f, 1f);
        return Vector2.ClampMagnitude(velocity, magnitude);
    }

    internal static Vector2 ResolveSlidingContactVelocity(
        Vector2 velocity,
        Vector2 normal,
        float surfaceFriction,
        float bounce,
        bool applyFriction)
    {
        // Mirrors BodyChunk's steep TerrainCurve branch. Normal response prevents penetration;
        // tangential friction is applied only once per frame because Room finalization may run a
        // second contact reconciliation pass after Player movement.
        velocity -= normal * Mathf.Min(0f, Vector2.Dot(velocity, normal) * (1f + bounce * 0.2f));
        if (applyFriction)
        {
            Vector2 tangent = new(-normal.y, normal.x);
            velocity -= Vector2.Dot(velocity, tangent) *
                        Mathf.Clamp01(1f - surfaceFriction * 2f) * tangent;
        }
        return velocity;
    }

    private static void ApplyVanillaSurfaceState(
        Player player,
        IWalkableDynamicSurface surface,
        WalkableSurfaceSample sample)
    {
        BodyChunk feet = Feet(player);
        if (feet == null) return;

        // BodyChunk uses TerrainCurve.maxSlideNormalY (cos 45 degrees) to distinguish ground from
        // a steep sliding contact. Reuse that exact rule instead of inventing a creature-specific
        // angle: shallow curve sections participate in Player ground movement; steep sections keep
        // terrainCurveNormal but deliberately do not grant floor contact or a ground jump.
        feet.terrainCurveNormal = sample.Normal;
        feet.contactPoint.y = IsGroundingNormal(sample.Normal) ? -1 : 0;
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

    internal static bool IsGroundingNormal(Vector2 normal) =>
        normal.y >= TerrainCurve.maxSlideNormalY;

    internal static bool IsSurfaceContactNormal(Vector2 normal) =>
        normal.y >= MinimumContactNormalY;

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
