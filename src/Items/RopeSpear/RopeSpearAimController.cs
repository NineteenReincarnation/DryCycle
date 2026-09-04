using System.Reflection;
using System.Runtime.CompilerServices;
using DryCycle.DayNight;
using UnityEngine;

namespace DryCycle.Items.RopeSpear;

/// <summary>
/// RopeSpear input controller: eight-direction hold-to-aim throwing plus long/short
/// rope mode. Quick taps remain ordinary horizontal throws.
/// </summary>
internal static class RopeSpearAimController
{
    // Two separate Alt presses inside this window toggle rope mode. Holding Alt is
    // counted as one press, so Alt+Up/Down reeling can never oscillate the mode.
    private const int DoubleAltWindowFrames = 12;
    private const float ShortModeRopeThickness = 1.15f;

    private sealed class AimState
    {
        internal RopeSpear Spear;
        internal int GraspIndex = -1;
        internal int Facing = 1;
        internal int HoldFrames;
        internal bool Charging;
        internal Vector2 AimDirection = Vector2.right;
        internal RopeSpearAimIndicator Indicator;
    }

    private sealed class AltTapState
    {
        internal bool AltWasHeld;
        internal int SecondTapWindow;
    }

    private sealed class RopeModeState
    {
        // New RopeSpears are long mode by default.
        internal bool LongMode = true;
        internal bool ShortFlightActive;
        internal float LockedLength = AbstractRopeSpear.DefaultRopeLength;
    }

    private static readonly ConditionalWeakTable<Player, AimState> States = new();
    private static readonly ConditionalWeakTable<Player, AltTapState> AltTapStates = new();
    private static readonly ConditionalWeakTable<RopeSpear, RopeModeState> RopeModes = new();

    // Short mode intentionally restores the older fixed-length projectile behaviour
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
        out Vector2 direction)
    {
        direction = Vector2.right;

        if (!_enabled ||
            !RegionDayNightOptions.RopeSpearEightWayThrowEnabled ||
            player == null ||
            !States.TryGetValue(player, out AimState state) ||
            !state.Charging ||
            !AimStateStillValid(player, state))
        {
            return false;
        }

        direction = state.AimDirection.sqrMagnitude > 0.0001f
            ? state.AimDirection.normalized
            : new Vector2(state.Facing, 0f);
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
        bool altHeld = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);

        if (!RegionDayNightOptions.RopeSpearModeSwitchEnabled)
        {
            tap.AltWasHeld = altHeld;
            tap.SecondTapWindow = 0;

            // The setting is live. If it is disabled while a short-mode spear is in
            // the player's hand, immediately return that spear to the safe default.
            if (TryFindHeldRopeSpear(self, out RopeSpear heldSpear))
            {
                RopeModeState heldMode = RopeModes.GetOrCreateValue(heldSpear);
                heldMode.LongMode = true;
                heldMode.ShortFlightActive = false;
            }
            return;
        }

        if (tap.SecondTapWindow > 0)
        {
            tap.SecondTapWindow--;
        }

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
        mode.LongMode = !mode.LongMode;
        mode.ShortFlightActive = false;

        if (self.room != null)
        {
            self.room.AddObject(new RopeSpearModeFlash(self, mode.LongMode));
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

            if (RegionDayNightOptions.RopeSpearModeSwitchEnabled && !mode.LongMode)
            {
                mode.LockedLength = spear.abstractPhysicalObject is AbstractRopeSpear data
                    ? data.RopeLength
                    : AbstractRopeSpear.DefaultRopeLength;
                mode.LockedLength = Mathf.Clamp(
                    mode.LockedLength,
                    AbstractRopeSpear.MinRopeLength,
                    AbstractRopeSpear.MaxRopeLength);
                mode.ShortFlightActive = true;
            }
            else
            {
                mode.LongMode = true;
                mode.ShortFlightActive = false;
            }
        }

        orig(self, grasp, eu);

        if (spear == null || mode == null || mode.LongMode || !mode.ShortFlightActive)
        {
            return;
        }

        // RopeSpear itself pays out in long mode during Thrown.Update. Restoring the
        // authored length here establishes the cap before the first room update.
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
                    // Long-mode Update may have paid out this frame. Short mode
                    // always restores the exact length captured at release.
                    data.RopeLength = mode.LockedLength;
                }

                ForceShortRopePhysics(self, spear, mode.LockedLength);

                // The first settled frame in RopeSpear clears its internal payout
                // latch. After that, ordinary Alt+Up/Down reeling is allowed to
                // change the short rope normally; only the launch length is fixed.
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

        if (!RegionDayNightOptions.RopeSpearEightWayThrowEnabled)
        {
            if (state.Spear != null)
            {
                ResetState(state);
            }
            orig(self, eu);
            return;
        }

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
        // throws during the same GrabUpdate, which would make hold-to-aim impossible.
        self.wantToThrow = 0;

        if (throwHeld)
        {
            state.HoldFrames++;
            int threshold = RegionDayNightOptions.RopeSpearAimHoldFrames;
            if (!state.Charging && state.HoldFrames >= threshold)
            {
                state.Charging = true;
                UpdateEightWayDirection(self, state);
            }
            else if (state.Charging)
            {
                UpdateEightWayDirection(self, state);
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

        // Releasing before the threshold is a normal flat throw. Releasing after
        // the threshold uses the last of the eight directions selected by movement
        // input. Direction release does not cancel the selection.
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
            // overwrite the selected direction regardless of HookGen ordering.
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
        // GetHeldItemDirection. Reapply our selected direction afterwards.
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
        state.Charging = false;

        int facing = player.ThrowDirection;
        if (facing == 0)
        {
            facing = player.flipDirection;
        }
        state.Facing = facing < 0 ? -1 : 1;
        state.AimDirection = new Vector2(state.Facing, 0f);
    }

    private static void EnsureAimIndicator(Player player, AimState state)
    {
        if (!RegionDayNightOptions.RopeSpearAimIndicatorEnabled)
        {
            if (state.Indicator != null)
            {
                state.Indicator.Destroy();
                state.Indicator = null;
            }
            return;
        }

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

    private static void UpdateEightWayDirection(Player player, AimState state)
    {
        if (player?.input == null || player.input.Length == 0 || state == null)
        {
            return;
        }

        int x = player.input[0].x;
        int y = player.input[0].y;
        if (x == 0 && y == 0)
        {
            // Keep the last selected direction. This lets the player choose a ray,
            // release the D-pad/stick, then release Throw without snapping horizontal.
            return;
        }

        x = x < 0 ? -1 : x > 0 ? 1 : 0;
        y = y < 0 ? -1 : y > 0 ? 1 : 0;

        Vector2 direction = new(x, y);
        if (direction.sqrMagnitude < 0.0001f)
        {
            return;
        }

        direction.Normalize();
        state.AimDirection = direction;
        if (x != 0)
        {
            state.Facing = x;
        }
    }

    private static Vector2 GetAimDirection(AimState state)
    {
        if (state == null || state.AimDirection.sqrMagnitude < 0.0001f)
        {
            return new Vector2(state?.Facing < 0 ? -1f : 1f, 0f);
        }

        return state.AimDirection.normalized;
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
        if (player == null ||
            spear?.firstChunk == null ||
            direction.sqrMagnitude < 0.0001f)
        {
            return;
        }

        direction.Normalize();

        // Preserve vanilla's calculated throw force, including weakness and other
        // character modifiers, but rotate that velocity to the selected direction.
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
        // rotate that offset with the selected direction. If a downward/backward
        // offset would start inside terrain, retain vanilla's already-valid position.
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

        // Vanilla impact code expects a cardinal IntVector2. The any-angle wall-stick
        // runtime uses the real flight vector; this dominant-axis value only keeps
        // vanilla weapon bookkeeping coherent for the rest of the engine.
        if (Mathf.Abs(direction.x) >= Mathf.Abs(direction.y))
        {
            spear.throwDir = new IntVector2(direction.x < 0f ? -1 : 1, 0);
        }
        else
        {
            spear.throwDir = new IntVector2(0, direction.y < 0f ? -1 : 1);
        }

        // Vanilla already applied horizontal recoil. Rotate that contribution toward
        // the requested direction, but cap the corrective delta so a 180-degree aim
        // cannot create an accidental movement-tech-sized impulse by itself.
        Vector2 recoilCorrection = direction - vanillaDirection;
        Vector2 mainCorrection = Vector2.ClampMagnitude(recoilCorrection * 8f, 10f);
        Vector2 rearCorrection = Vector2.ClampMagnitude(recoilCorrection * 4f, 5f);
        player.mainBodyChunk.vel += mainCorrection;
        if (player.bodyChunks != null && player.bodyChunks.Length > 1)
        {
            player.bodyChunks[1].vel -= rearCorrection;
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
        state.AimDirection = Vector2.right;
    }
}
