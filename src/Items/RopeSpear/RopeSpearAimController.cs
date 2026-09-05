using System.Reflection;
using System.Runtime.CompilerServices;
using DryCycle.Misc;
using UnityEngine;

namespace DryCycle.Items.RopeSpear;

/// <summary>
/// RopeSpear input controller: configurable sweep/eight-direction aiming plus long/short/ultra-short rope modes.
/// </summary>
internal static class RopeSpearAimController
{
    // Rain World updates gameplay at roughly 40 Hz. Eight frames is long enough to
    // distinguish an intentional hold while keeping ordinary taps responsive in
    // the default continuous-sweep throwing mode.
    private const int HoldThresholdFrames = 8;

    // Deliberately slow enough to let the player release on a chosen angle instead
    // of having the spear race through the arc. At ~40 Hz this is ~50 degrees/sec:
    // horizontal -> straight up takes about 1.8 seconds.
    private const float SweepDegreesPerFrame = 1.25f;
    private const float MinAimAngle = -90f;
    private const float MaxAimAngle = 90f;

    // Two separate Alt presses inside this window advance rope mode. Holding Alt is
    // counted as one press, so Alt+Up/Down reeling can never oscillate the mode.
    private const int DoubleAltWindowFrames = 12;
    private const float ShortModeRopeThickness = 1.15f;
    private const float UltraShortLengthMultiplier = 0.25f;

    private sealed class AimState
    {
        internal RopeSpear Spear;
        internal int GraspIndex = -1;
        internal int Facing = 1;
        internal int HoldFrames;
        internal bool Charging;
        internal bool EightDirection;
        internal float AngleDegrees;
        internal int SweepDirection = 1;
        internal RopeSpearAimIndicator Indicator;
    }

    private sealed class AltTapState
    {
        internal bool AltWasHeld;
        internal int SecondTapWindow;
    }

    private sealed class RopeModeState
    {
        // New RopeSpears are long mode by default. Double-Alt cycles:
        // Long -> Short -> UltraShort -> Long.
        internal bool LongMode = true;
        internal bool UltraShortMode;
        internal bool ShortFlightActive;
        internal float LockedLength = AbstractRopeSpear.DefaultRopeLength;
    }

    private static readonly ConditionalWeakTable<Player, AimState> States = new();
    private static readonly ConditionalWeakTable<Player, AltTapState> AltTapStates = new();
    private static readonly ConditionalWeakTable<RopeSpear, RopeModeState> RopeModes = new();

    // Short modes intentionally restore the older fixed-length projectile behaviour
    // without changing RopeSpear's normal long-payout implementation. These cached
    // members let the post-room pass reuse the existing rope topology and tension
    // code, including wall bends, instead of implementing a second rope solver.
    private static readonly FieldInfo RopeSystemField = typeof(RopeSpear).GetField(
        "_ropeSystem",
        BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo GetHandleRopePointMethod = typeof(RopeSpear).GetMethod(
        "GetHandleRopePoint",
        BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo GetSpearRopePointMethod = typeof(RopeSpear).GetMethod(
        "GetSpearRopePoint",
        BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo ApplyRopeConstraintMethod = typeof(RopeSpear).GetMethod(
        "ApplyRopeConstraint",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private static bool _reflectionWarningIssued;
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.Player.Update += Player_Update;
        On.Player.GrabUpdate += Player_GrabUpdate;
        On.Player.ThrowObject += Player_ThrowObject_RopeMode;
        On.Player.GraphicsModuleUpdated += Player_GraphicsModuleUpdated;
        On.Room.Update += Room_Update;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.Player.Update -= Player_Update;
        On.Player.GrabUpdate -= Player_GrabUpdate;
        On.Player.ThrowObject -= Player_ThrowObject_RopeMode;
        On.Player.GraphicsModuleUpdated -= Player_GraphicsModuleUpdated;
        On.Room.Update -= Room_Update;
        _enabled = false;
    }

    internal static bool TryGetAimVisualState(
        Player player,
        out int facing,
        out float angleDegrees)
    {
        facing = 1;
        angleDegrees = 0f;

        if (!_enabled ||
            player == null ||
            !States.TryGetValue(player, out AimState state) ||
            !state.Charging ||
            !AimStateStillValid(player, state))
        {
            return false;
        }

        facing = state.Facing;
        angleDegrees = state.AngleDegrees;
        return true;
    }

    private static void Player_Update(
        On.Player.orig_Update orig,
        Player self,
        bool eu)
    {
        orig(self, eu);

        if (self == null)
        {
            return;
        }

        AltTapState tap = AltTapStates.GetOrCreateValue(self);
        if (tap.SecondTapWindow > 0)
        {
            tap.SecondTapWindow--;
        }

        bool altHeld = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
        bool altPressed = altHeld && !tap.AltWasHeld;
        tap.AltWasHeld = altHeld;

        if (!altPressed)
        {
            return;
        }

        if (!TryFindHeldRopeSpear(self, out RopeSpear spear))
        {
            tap.SecondTapWindow = 0;
            return;
        }

        if (tap.SecondTapWindow <= 0)
        {
            tap.SecondTapWindow = DoubleAltWindowFrames;
            return;
        }

        tap.SecondTapWindow = 0;
        RopeModeState mode = RopeModes.GetOrCreateValue(spear);

        if (mode.LongMode)
        {
            mode.LongMode = false;
            mode.UltraShortMode = false;
        }
        else if (!mode.UltraShortMode)
        {
            mode.UltraShortMode = true;
        }
        else
        {
            mode.LongMode = true;
            mode.UltraShortMode = false;
        }

        mode.ShortFlightActive = false;

        if (self.room != null)
        {
            self.room.AddObject(new RopeSpearModeFlash(
                self,
                mode.LongMode,
                mode.UltraShortMode));
        }
    }

    private static void Player_ThrowObject_RopeMode(
        On.Player.orig_ThrowObject orig,
        Player self,
        int grasp,
        bool eu)
    {
        RopeSpear spear = null;
        RopeModeState mode = null;

        if (self?.grasps != null &&
            grasp >= 0 &&
            grasp < self.grasps.Length &&
            self.grasps[grasp]?.grabbed is RopeSpear candidate)
        {
            spear = candidate;
            mode = RopeModes.GetOrCreateValue(spear);

            if (!mode.LongMode)
            {
                float shortLength = spear.abstractPhysicalObject is AbstractRopeSpear data
                    ? data.RopeLength
                    : AbstractRopeSpear.DefaultRopeLength;

                shortLength = Mathf.Clamp(
                    shortLength,
                    AbstractRopeSpear.MinRopeLength,
                    AbstractRopeSpear.MaxRopeLength);

                if (mode.UltraShortMode)
                {
                    // Ultra-short is defined as exactly one quarter of the length
                    // the ordinary short mode would have captured for this throw.
                    // Its own floor is therefore one quarter of Short's floor.
                    mode.LockedLength = Mathf.Clamp(
                        shortLength * UltraShortLengthMultiplier,
                        AbstractRopeSpear.MinRopeLength * UltraShortLengthMultiplier,
                        AbstractRopeSpear.MaxRopeLength);
                }
                else
                {
                    mode.LockedLength = shortLength;
                }

                mode.ShortFlightActive = true;
            }
            else
            {
                mode.ShortFlightActive = false;
            }
        }

        orig(self, grasp, eu);

        if (spear == null || mode == null || mode.LongMode || !mode.ShortFlightActive)
        {
            return;
        }

        // RopeSpear itself pays out in long mode during Thrown.Update. Restoring the
        // fixed-mode length here establishes the cap before the first room update.
        if (spear.abstractPhysicalObject is AbstractRopeSpear shortData)
        {
            shortData.RopeLength = mode.LockedLength;
        }
    }

    private static void Room_Update(On.Room.orig_Update orig, Room self)
    {
        orig(self);

        if (self?.physicalObjects == null)
        {
            return;
        }

        for (int layer = 0; layer < self.physicalObjects.Length; layer++)
        {
            var objects = self.physicalObjects[layer];
            for (int i = 0; i < objects.Count; i++)
            {
                if (objects[i] is not RopeSpear spear ||
                    !RopeModes.TryGetValue(spear, out RopeModeState mode) ||
                    mode.LongMode ||
                    !mode.ShortFlightActive ||
                    spear.slatedForDeletetion)
                {
                    continue;
                }

                if (spear.abstractPhysicalObject is AbstractRopeSpear data)
                {
                    // Long-mode Update may have paid out this frame. Fixed modes
                    // always restore the exact length captured at release.
                    data.RopeLength = mode.LockedLength;
                }

                ForceShortRopePhysics(self, spear, mode.LockedLength);

                // The first settled frame in RopeSpear clears its internal payout
                // latch. After that, ordinary Alt+Up/Down reeling is allowed to
                // change the rope normally; only the launch length is fixed.
                if (spear.mode != Weapon.Mode.Thrown)
                {
                    mode.ShortFlightActive = false;
                }
            }
        }
    }

    private static void ForceShortRopePhysics(
        Room room,
        RopeSpear spear,
        float lockedLength)
    {
        if (room == null || spear == null)
        {
            return;
        }

        if (RopeSystemField == null ||
            GetHandleRopePointMethod == null ||
            GetSpearRopePointMethod == null ||
            ApplyRopeConstraintMethod == null)
        {
            WarnMissingShortModeReflection();
            return;
        }

        try
        {
            if (RopeSystemField.GetValue(spear) is not RopeSpearRopeSystem ropeSystem)
            {
                WarnMissingShortModeReflection();
                return;
            }

            Vector2 handlePoint = (Vector2)GetHandleRopePointMethod.Invoke(
                spear,
                new object[] { 1f });
            Vector2 spearPoint = (Vector2)GetSpearRopePointMethod.Invoke(
                spear,
                new object[] { 1f });

            // Re-solve the same topology using the locked short length. This makes
            // the visible rope taut at its cap and preserves the existing corner
            // wrapping behaviour rather than measuring a naive straight line.
            ropeSystem.Update(
                room,
                handlePoint,
                spearPoint,
                lockedLength,
                ShortModeRopeThickness);

            float routeLength = ropeSystem.RouteLength;
            float stretch = routeLength - lockedLength;
            if (stretch > 0.75f)
            {
                ApplyRopeConstraintMethod.Invoke(
                    spear,
                    new object[] { stretch, routeLength });
            }
        }
        catch (System.Exception ex)
        {
            if (!_reflectionWarningIssued)
            {
                _reflectionWarningIssued = true;
                Plugin.Logger?.LogWarning($"RopeSpear short-mode constraint failed: {ex}");
            }
        }
    }

    private static void WarnMissingShortModeReflection()
    {
        if (_reflectionWarningIssued)
        {
            return;
        }

        _reflectionWarningIssued = true;
        Plugin.Logger?.LogWarning(
            "RopeSpear short mode could not bind the existing rope solver; fixed-length launch constraint is unavailable.");
    }

    private static bool TryFindHeldRopeSpear(Player player, out RopeSpear spear)
    {
        spear = null;
        if (player?.grasps == null)
        {
            return false;
        }

        for (int i = 0; i < player.grasps.Length; i++)
        {
            if (player.grasps[i]?.grabbed is RopeSpear candidate)
            {
                spear = candidate;
                return true;
            }
        }

        return false;
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
        // throws during the same GrabUpdate, which would make either custom aiming
        // scheme impossible. Keeping wantToThrow at zero also removes buffered X.
        self.wantToThrow = 0;

        if (throwHeld)
        {
            state.HoldFrames++;

            if (state.EightDirection)
            {
                // Eight-direction mode has no hold threshold and no automatic sweep.
                // X merely arms the throw; movement input selects one of the eight
                // exact digital directions in real time until X is released.
                state.Charging = true;
                UpdateEightDirectionAim(self, state);
            }
            else if (!state.Charging && state.HoldFrames >= HoldThresholdFrames)
            {
                // Default mode enters aiming at exactly horizontal. The sweep starts
                // on the next held frame so crossing the tap/hold threshold never
                // jumps angle.
                state.Charging = true;
                state.AngleDegrees = 0f;
                state.SweepDirection = 1;
            }
            else if (state.Charging)
            {
                AdvanceSweep(state);
            }

            if (state.Charging)
            {
                EnsureAimIndicator(self, state);
            }

            RunGrabUpdateWithThrowMasked(orig, self, eu);
            self.wantToThrow = 0;
            ApplyHeldAimPose(state);
            return;
        }

        // Default sweep mode keeps tap-vs-hold behavior. Eight-direction mode is
        // already charging from the first X frame, so release always uses the most
        // recent movement combination selected above.
        RunGrabUpdateWithThrowMasked(orig, self, eu);
        self.wantToThrow = 0;

        if (!AimStateStillValid(self, state))
        {
            ResetState(state);
            return;
        }

        RopeSpear releasedSpear = state.Spear;
        int releaseGrasp = state.GraspIndex;
        Vector2 releaseDirection = state.Charging
            ? GetAimDirection(state)
            : new Vector2(state.Facing, 0f);

        try
        {
            // Let vanilla and every existing ThrowObject hook finish first. The
            // directional override is deliberately applied only after this entire
            // call returns, so RopeSpearHooks' horizontal direction lock cannot
            // overwrite an angled release regardless of HookGen ordering.
            self.ThrowObject(releaseGrasp, eu);

            if (releasedSpear != null &&
                !releasedSpear.slatedForDeletetion &&
                releasedSpear.mode == Weapon.Mode.Thrown)
            {
                ApplyDirectionalThrow(self, releasedSpear, releaseDirection, eu);
            }
        }
        finally
        {
            ResetState(state);
        }
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
        // GetHeldItemDirection. Reapply our selected direction afterwards so the
        // visible spear tip follows either the sweep or the eight-direction input.
        ApplyHeldAimPose(state);
    }

    private static void BeginAim(
        Player player,
        AimState state,
        RopeSpear spear,
        int graspIndex)
    {
        if (state.Indicator != null)
        {
            state.Indicator.Destroy();
            state.Indicator = null;
        }

        state.Spear = spear;
        state.GraspIndex = graspIndex;
        state.HoldFrames = 0;
        state.EightDirection = DryCycleOptions.RopeSpearEightDirectionThrowEnabled;
        state.Charging = state.EightDirection;
        state.AngleDegrees = 0f;
        state.SweepDirection = 1;

        int facing = player.ThrowDirection;
        if (facing == 0)
        {
            facing = player.flipDirection;
        }
        state.Facing = facing < 0 ? -1 : 1;

        if (state.EightDirection)
        {
            UpdateEightDirectionAim(player, state);
        }
    }

    private static void EnsureAimIndicator(Player player, AimState state)
    {
        if (player?.room == null || !state.Charging)
        {
            return;
        }

        if (state.Indicator != null && !state.Indicator.slatedForDeletetion)
        {
            return;
        }

        state.Indicator = new RopeSpearAimIndicator(player);
        player.room.AddObject(state.Indicator);
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

            if (grasp.grabbed is not RopeSpear candidate)
            {
                return false;
            }

            graspIndex = i;
            ropeSpear = candidate;
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

    /// <summary>
    /// Select exactly one of the eight digital directions from Rain World's current
    /// movement package. Combinations are literal: Up+Right is 45 degrees up-right,
    /// Down+Left is 45 degrees down-left. With no movement input, keep a horizontal
    /// throw in the facing direction captured for this aim session.
    /// </summary>
    private static void UpdateEightDirectionAim(Player player, AimState state)
    {
        if (player?.input == null || player.input.Length == 0 || state == null)
        {
            return;
        }

        int x = player.input[0].x;
        int y = player.input[0].y;
        x = x < 0 ? -1 : x > 0 ? 1 : 0;
        y = y < 0 ? -1 : y > 0 ? 1 : 0;

        if (x == 0 && y == 0)
        {
            state.AngleDegrees = 0f;
            return;
        }

        // Horizontal input owns left/right explicitly, even if it points behind the
        // direction the slugcat happened to face when X was first pressed.
        if (x != 0)
        {
            state.Facing = x;
        }

        if (y > 0)
        {
            state.AngleDegrees = x == 0 ? 90f : 45f;
        }
        else if (y < 0)
        {
            state.AngleDegrees = x == 0 ? -90f : -45f;
        }
        else
        {
            state.AngleDegrees = 0f;
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

        if (state.Indicator != null)
        {
            state.Indicator.Destroy();
            state.Indicator = null;
        }

        state.Spear = null;
        state.GraspIndex = -1;
        state.Facing = 1;
        state.HoldFrames = 0;
        state.Charging = false;
        state.EightDirection = false;
        state.AngleDegrees = 0f;
        state.SweepDirection = 1;
    }
}
