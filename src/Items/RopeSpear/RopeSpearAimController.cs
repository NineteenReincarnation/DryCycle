using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.Items.RopeSpear;

/// <summary>
/// Adds a RopeSpear-only throw input state machine.
/// A quick tap of throw is delayed until release and remains a normal horizontal
/// throw. Holding throw past a short threshold enters an aiming sweep from level
/// to straight up, then down through level to straight down, bouncing continuously
/// until the button is released.
/// </summary>
internal static class RopeSpearAimController
{
    // Rain World updates gameplay at roughly 40 Hz. Eight frames is long enough to
    // distinguish an intentional hold while keeping ordinary taps responsive.
    private const int HoldThresholdFrames = 8;
    private const float SweepDegreesPerFrame = 4f;
    private const float MinAimAngle = -90f;
    private const float MaxAimAngle = 90f;

    private sealed class AimState
    {
        internal RopeSpear Spear;
        internal int GraspIndex = -1;
        internal int Facing = 1;
        internal int HoldFrames;
        internal bool Charging;
        internal float AngleDegrees;
        internal int SweepDirection = 1;

        // Set only around the explicit ThrowObject call made on button release.
        // Player_ThrowObject consumes this after vanilla has created the projectile.
        internal bool PendingDirectionalThrow;
        internal Vector2 PendingDirection;
    }

    private static readonly ConditionalWeakTable<Player, AimState> States = new();
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.Player.GrabUpdate += Player_GrabUpdate;
        On.Player.ThrowObject += Player_ThrowObject;
        On.Player.GraphicsModuleUpdated += Player_GraphicsModuleUpdated;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.Player.GrabUpdate -= Player_GrabUpdate;
        On.Player.ThrowObject -= Player_ThrowObject;
        On.Player.GraphicsModuleUpdated -= Player_GraphicsModuleUpdated;
        _enabled = false;
    }

    private static void Player_GrabUpdate(
        On.Player.orig_GrabUpdate orig,
        Player self,
        bool eu)
    {
        if (self == null || self.input == null || self.input.Length == 0)
        {
            orig(self, eu);
            return;
        }

        AimState state = States.GetOrCreateValue(self);
        bool throwHeld = self.input[0].thrw;
        bool throwPressed = throwHeld &&
                            (self.input.Length < 2 || !self.input[1].thrw);

        if (state.Spear == null)
        {
            if (!throwPressed ||
                !CanStartAim(self) ||
                !TryFindVanillaThrowCandidate(
                    self,
                    out int graspIndex,
                    out RopeSpear ropeSpear))
            {
                orig(self, eu);
                return;
            }

            BeginAim(self, state, ropeSpear, graspIndex);
        }

        if (!AimStateStillValid(self, state))
        {
            ResetState(state);
            orig(self, eu);
            return;
        }

        // While this state exists, vanilla must never see the held throw input.
        // Vanilla converts the first rising edge directly into wantToThrow=5 and
        // throws during the same GrabUpdate, which would make long-press aiming
        // impossible. Keeping wantToThrow at zero also removes any buffered edge.
        self.wantToThrow = 0;

        if (throwHeld)
        {
            state.HoldFrames++;
            if (!state.Charging && state.HoldFrames >= HoldThresholdFrames)
            {
                // Enter aiming at exactly horizontal. The sweep starts on the next
                // held frame so crossing the tap/hold threshold never jumps angle.
                state.Charging = true;
                state.AngleDegrees = 0f;
                state.SweepDirection = 1;
            }
            else if (state.Charging)
            {
                AdvanceSweep(state);
            }

            RunGrabUpdateWithThrowMasked(orig, self, eu);
            self.wantToThrow = 0;
            ApplyHeldAimPose(state);
            return;
        }

        // Releasing before the threshold is a normal flat throw. Releasing after
        // the threshold uses the angle currently shown by the spear. Both branches
        // throw on release, which is the unavoidable small delay needed to tell a
        // tap apart from a hold without sacrificing either input.
        RunGrabUpdateWithThrowMasked(orig, self, eu);
        self.wantToThrow = 0;

        if (!AimStateStillValid(self, state))
        {
            ResetState(state);
            return;
        }

        Vector2 releaseDirection = state.Charging
            ? GetAimDirection(state)
            : new Vector2(state.Facing, 0f);

        state.PendingDirectionalThrow = true;
        state.PendingDirection = releaseDirection;

        int releaseGrasp = state.GraspIndex;
        try
        {
            self.ThrowObject(releaseGrasp, eu);
        }
        finally
        {
            state.PendingDirectionalThrow = false;
            ResetState(state);
        }
    }

    private static void Player_ThrowObject(
        On.Player.orig_ThrowObject orig,
        Player self,
        int grasp,
        bool eu)
    {
        AimState state = null;
        RopeSpear expectedSpear = null;
        Vector2 requestedDirection = Vector2.zero;
        bool directionalThrow = false;

        if (self != null &&
            States.TryGetValue(self, out state) &&
            state.PendingDirectionalThrow &&
            state.GraspIndex == grasp &&
            state.Spear != null &&
            self.grasps != null &&
            grasp >= 0 &&
            grasp < self.grasps.Length &&
            object.ReferenceEquals(self.grasps[grasp]?.grabbed, state.Spear))
        {
            expectedSpear = state.Spear;
            requestedDirection = state.PendingDirection;
            directionalThrow = requestedDirection.sqrMagnitude > 0.0001f;
            if (directionalThrow)
            {
                requestedDirection.Normalize();
            }
        }

        orig(self, grasp, eu);

        if (!directionalThrow ||
            expectedSpear == null ||
            expectedSpear.slatedForDeletetion ||
            expectedSpear.mode != Weapon.Mode.Thrown)
        {
            return;
        }

        ApplyDirectionalThrow(self, expectedSpear, requestedDirection, eu);
    }

    private static void Player_GraphicsModuleUpdated(
        On.Player.orig_GraphicsModuleUpdated orig,
        Player self,
        bool actuallyViewed,
        bool eu)
    {
        orig(self, actuallyViewed, eu);

        if (self == null ||
            !States.TryGetValue(self, out AimState state) ||
            state.Spear == null ||
            !AimStateStillValid(self, state))
        {
            return;
        }

        // Player.GraphicsModuleUpdated normally overwrites a carried weapon with
        // GetHeldItemDirection. Reapply our angle afterwards so the visible spear
        // tip actually follows the aiming sweep in the player's hand.
        ApplyHeldAimPose(state);
    }

    private static void BeginAim(
        Player player,
        AimState state,
        RopeSpear spear,
        int graspIndex)
    {
        state.Spear = spear;
        state.GraspIndex = graspIndex;
        state.HoldFrames = 0;
        state.Charging = false;
        state.AngleDegrees = 0f;
        state.SweepDirection = 1;
        state.PendingDirectionalThrow = false;
        state.PendingDirection = Vector2.zero;

        int facing = player.ThrowDirection;
        if (facing == 0)
        {
            facing = player.flipDirection;
        }
        state.Facing = facing < 0 ? -1 : 1;
    }

    private static bool CanStartAim(Player player)
    {
        if (player.dead ||
            !player.Consious ||
            player.inShortcut ||
            player.enteringShortCut.HasValue)
        {
            return false;
        }

        if (ModManager.MSC && player.monkAscension)
        {
            return false;
        }

        return true;
    }

    private static bool TryFindVanillaThrowCandidate(
        Player player,
        out int graspIndex,
        out RopeSpear ropeSpear)
    {
        graspIndex = -1;
        ropeSpear = null;
        if (player.grasps == null)
        {
            return false;
        }

        // Match vanilla's hand priority: the first throwable grasp is the object X
        // would throw. Only intercept the input when that same candidate is ours.
        for (int i = 0; i < player.grasps.Length; i++)
        {
            var grasp = player.grasps[i];
            if (grasp == null ||
                grasp.grabbed == null ||
                !player.IsObjectThrowable(grasp.grabbed))
            {
                continue;
            }

            if (grasp.grabbed is not RopeSpear spear)
            {
                return false;
            }

            graspIndex = i;
            ropeSpear = spear;
            return true;
        }

        return false;
    }

    private static bool AimStateStillValid(Player player, AimState state)
    {
        if (state?.Spear == null ||
            player.grasps == null ||
            state.GraspIndex < 0 ||
            state.GraspIndex >= player.grasps.Length ||
            player.dead ||
            !player.Consious ||
            player.inShortcut ||
            player.enteringShortCut.HasValue)
        {
            return false;
        }

        return object.ReferenceEquals(
            player.grasps[state.GraspIndex]?.grabbed,
            state.Spear);
    }

    private static void RunGrabUpdateWithThrowMasked(
        On.Player.orig_GrabUpdate orig,
        Player player,
        bool eu)
    {
        Player.InputPackage originalInput = player.input[0];
        Player.InputPackage maskedInput = originalInput;
        maskedInput.thrw = false;
        player.input[0] = maskedInput;

        try
        {
            orig(player, eu);
        }
        finally
        {
            player.input[0] = originalInput;
        }
    }

    private static void AdvanceSweep(AimState state)
    {
        state.AngleDegrees += state.SweepDirection * SweepDegreesPerFrame;

        if (state.AngleDegrees >= MaxAimAngle)
        {
            state.AngleDegrees = MaxAimAngle;
            state.SweepDirection = -1;
        }
        else if (state.AngleDegrees <= MinAimAngle)
        {
            state.AngleDegrees = MinAimAngle;
            state.SweepDirection = 1;
        }
    }

    private static Vector2 GetAimDirection(AimState state)
    {
        float radians = state.AngleDegrees * Mathf.Deg2Rad;
        Vector2 direction = new(
            Mathf.Cos(radians) * state.Facing,
            Mathf.Sin(radians));

        if (direction.sqrMagnitude < 0.0001f)
        {
            return new Vector2(state.Facing, 0f);
        }

        direction.Normalize();
        return direction;
    }

    private static void ApplyHeldAimPose(AimState state)
    {
        if (state?.Spear == null)
        {
            return;
        }

        Vector2 direction = state.Charging
            ? GetAimDirection(state)
            : new Vector2(state.Facing, 0f);

        state.Spear.setRotation = direction;
        state.Spear.rotation = direction;
        state.Spear.lastRotation = direction;
        state.Spear.rotationSpeed = 0f;
    }

    private static void ApplyDirectionalThrow(
        Player player,
        RopeSpear spear,
        Vector2 direction,
        bool eu)
    {
        if (player == null || direction.sqrMagnitude < 0.0001f)
        {
            return;
        }

        direction.Normalize();

        // Preserve vanilla's calculated throw force, including weakness and other
        // character modifiers, but rotate that velocity to the selected angle.
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
            vanillaDirection = new Vector2(StateFacingFallback(player), 0f);
        }

        spear.firstChunk.vel = direction * throwSpeed;

        // Start the projectile on the same ten-pixel release arc vanilla uses, but
        // rotate that offset with the selected angle so a vertical shot does not
        // visibly originate from the side of the slugcat.
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

        // Vanilla impact code expects an IntVector2 cardinal throwDir. Keep the
        // exact velocity/visual angle, but select the dominant axis for wall vs.
        // floor/ceiling contact so straight-up/down shots and ordinary side shots
        // retain the original spear collision path.
        if (Mathf.Abs(direction.x) >= Mathf.Abs(direction.y))
        {
            spear.throwDir = new IntVector2(direction.x < 0f ? -1 : 1, 0);
        }
        else
        {
            spear.throwDir = new IntVector2(0, direction.y < 0f ? -1 : 1);
        }

        // Vanilla already applied horizontal recoil before our angle override.
        // Rotate only that recoil component so an upward/downward cast feels like
        // it was actually thrown in the selected direction.
        Vector2 recoilCorrection = direction - vanillaDirection;
        player.mainBodyChunk.vel += recoilCorrection * 8f;
        if (player.bodyChunks != null && player.bodyChunks.Length > 1)
        {
            player.bodyChunks[1].vel -= recoilCorrection * 4f;
        }
    }

    private static int StateFacingFallback(Player player)
    {
        int facing = player.ThrowDirection;
        if (facing == 0)
        {
            facing = player.flipDirection;
        }
        return facing < 0 ? -1 : 1;
    }

    private static void ResetState(AimState state)
    {
        if (state == null)
        {
            return;
        }

        state.Spear = null;
        state.GraspIndex = -1;
        state.Facing = 1;
        state.HoldFrames = 0;
        state.Charging = false;
        state.AngleDegrees = 0f;
        state.SweepDirection = 1;
        state.PendingDirectionalThrow = false;
        state.PendingDirection = Vector2.zero;
    }
}
