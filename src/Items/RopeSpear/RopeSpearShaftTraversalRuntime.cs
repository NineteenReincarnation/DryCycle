using System.Reflection;
using System.Runtime.CompilerServices;
using RWCustom;
using UnityEngine;

namespace DryCycle.Items.RopeSpear;

/// <summary>
/// Traversal for non-cardinal RopeSpears. Vanilla beam tiles can only represent
/// horizontal/vertical poles, so diagonal spears use their real rendered shaft as
/// the climb/stand surface instead of creating fake axis-aligned beam geometry.
/// </summary>
internal static class RopeSpearShaftTraversalRuntime
{
    private const float AcquireRadius = 20f;
    private const float StandAcquireRadius = 16f;
    private const float StandFeetOffset = 5.5f;
    private const float StandBodyOffset = 18f;
    private const float ClimbBodyOffset = 2.5f;
    private const float WalkSpeed = 1.45f;
    private const float ClimbSpeed = 1.8f;
    private const float PositionFollow = 0.42f;
    private const float VelocityFollow = 0.13f;
    private const float TailReleasePadding = 4f;
    private const int ReattachCooldown = 8;
    private const int RopeMountGraceFrames = 6;

    private enum TraversalMode
    {
        Climb,
        Stand
    }

    private sealed class TraversalState
    {
        internal RopeSpear Spear;
        internal TraversalMode Mode;
        internal float Position;
        internal int Cooldown;
        internal int StepCounter;
        internal int MountGrace;
    }

    private static readonly ConditionalWeakTable<Player, TraversalState> States = new();

    // RopeSpearHooks intentionally keeps rope-grab state private. This small bridge
    // is only used for one transition: when the player reaches the rope's spear end,
    // clear the flexible-rope grab before taking control of the rigid diagonal shaft.
    private static readonly FieldInfo RopeGrabStatesField = typeof(RopeSpearHooks).GetField(
        "RopeGrabStates",
        BindingFlags.Static | BindingFlags.NonPublic);
    private static object _ropeGrabTable;
    private static MethodInfo _ropeGrabTryGetValue;
    private static FieldInfo _ropeGrabSpearField;
    private static FieldInfo _ropeGrabDelayField;
    private static bool _ropeReflectionWarningIssued;

    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        On.Player.Update += Player_Update;
        On.Player.GrabUpdate += Player_GrabUpdate;
        On.PlayerGraphics.Update += PlayerGraphics_Update;
        _enabled = true;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.Player.Update -= Player_Update;
        On.Player.GrabUpdate -= Player_GrabUpdate;
        On.PlayerGraphics.Update -= PlayerGraphics_Update;
        _enabled = false;
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

        TraversalState state = States.GetOrCreateValue(self);
        if (state.Cooldown > 0)
        {
            state.Cooldown--;
        }

        if (!CanUseSpearTraversal(self))
        {
            ClearState(state, ReattachCooldown);
            return;
        }

        if (state.Spear != null)
        {
            if (!UpdateAttachedPlayer(self, state))
            {
                ClearState(state, ReattachCooldown);
            }
            return;
        }

        if (state.Cooldown > 0)
        {
            return;
        }

        Player.InputPackage input = self.input[0];
        bool pickupPressed = input.pckp &&
                             (self.input.Length < 2 || !self.input[1].pckp);
        bool upHeld = input.y > 0;

        if ((upHeld || pickupPressed) &&
            TryAcquireClimb(self, out RopeSpear climbSpear, out float climbPosition))
        {
            state.Spear = climbSpear;
            state.Mode = TraversalMode.Climb;
            state.Position = climbPosition;
            state.StepCounter = 0;
            state.MountGrace = 0;
            self.wantToPickUp = 0;
            return;
        }

        if (TryAcquireStanding(self, out RopeSpear standSpear, out float standPosition))
        {
            state.Spear = standSpear;
            state.Mode = TraversalMode.Stand;
            state.Position = standPosition;
            state.StepCounter = 0;
            state.MountGrace = 0;
        }
    }

    private static void Player_GrabUpdate(
        On.Player.orig_GrabUpdate orig,
        Player self,
        bool eu)
    {
        if (self == null ||
            self.input == null ||
            self.input.Length == 0 ||
            !States.TryGetValue(self, out TraversalState state) ||
            state.Spear == null)
        {
            orig(self, eu);
            return;
        }

        // Keep vanilla object/hand logic alive, but prevent Up or Pickup from being
        // interpreted by RopeSpearHooks as a request to immediately reacquire the
        // flexible rope while this player is already traversing the rigid shaft.
        Player.InputPackage original = self.input[0];
        Player.InputPackage masked = original;
        masked.y = 0;
        masked.pckp = false;
        self.input[0] = masked;

        try
        {
            orig(self, eu);
        }
        finally
        {
            self.input[0] = original;
        }
    }

    private static void PlayerGraphics_Update(
        On.PlayerGraphics.orig_Update orig,
        PlayerGraphics self)
    {
        Player player = self?.player;
        TraversalState state = null;
        if (player != null)
        {
            States.TryGetValue(player, out state);
        }

        bool climbing = state?.Spear != null && state.Mode == TraversalMode.Climb;
        if (climbing)
        {
            PrepareClimbHands(self, state);
        }

        orig(self);

        if (climbing && state.Spear != null)
        {
            PrepareClimbHands(self, state);
        }
    }

    private static bool UpdateAttachedPlayer(Player player, TraversalState state)
    {
        RopeSpear spear = state.Spear;
        if (!RopeSpearWallStickRuntime.TryGetTraversalSegment(
                spear,
                out Vector2 tail,
                out Vector2 wallEnd,
                out Vector2 direction,
                out Vector2 supportNormal,
                out bool canStand))
        {
            return false;
        }

        Player.InputPackage input = player.input[0];
        Player.InputPackage previous = player.input.Length > 1
            ? player.input[1]
            : default;

        bool jumpPressed = input.jmp && !previous.jmp;
        if (jumpPressed)
        {
            ApplyJumpRelease(player, state, direction, supportNormal, input);
            return false;
        }

        if (state.Mode == TraversalMode.Stand)
        {
            return UpdateStanding(
                player,
                state,
                tail,
                wallEnd,
                direction,
                supportNormal,
                canStand,
                input,
                previous);
        }

        return UpdateClimbing(
            player,
            state,
            tail,
            wallEnd,
            direction,
            supportNormal,
            canStand,
            input);
    }

    private static bool UpdateStanding(
        Player player,
        TraversalState state,
        Vector2 tail,
        Vector2 wallEnd,
        Vector2 direction,
        Vector2 supportNormal,
        bool canStand,
        Player.InputPackage input,
        Player.InputPackage previous)
    {
        if (!canStand)
        {
            state.Mode = TraversalMode.Climb;
            return true;
        }

        bool downPressed = input.y < 0 && previous.y >= 0;
        if (downPressed)
        {
            state.Mode = TraversalMode.Climb;
            player.standing = false;
            return true;
        }

        float length = Vector2.Distance(tail, wallEnd);
        if (length < 1f)
        {
            return false;
        }

        float tangentInput = Vector2.Dot(
            new Vector2(input.x, input.y),
            direction);

        // Horizontal control must still feel like ordinary beam walking even on a
        // diagonal spear. If the analog projection is weak, horizontal input owns
        // the direction along the spear's screen-space left/right orientation.
        if (input.x != 0 && Mathf.Abs(tangentInput) < 0.3f)
        {
            tangentInput = input.x * Mathf.Sign(direction.x);
        }

        float oldPosition = state.Position;
        state.Position += tangentInput * (WalkSpeed / length);

        // The wall side is a hard endpoint. The exposed tail is a deliberate walk-
        // off edge so players can carry momentum into a jump/fall without a special
        // detach command.
        if (state.Position < -TailReleasePadding / length)
        {
            return false;
        }
        state.Position = Mathf.Clamp(state.Position, 0f, 1f);

        Vector2 point = Vector2.Lerp(tail, wallEnd, state.Position);
        Vector2 feetTarget = point + supportNormal * StandFeetOffset;
        BodyChunk feet = GetFeetChunk(player);
        SoftFollowChunk(feet, feetTarget, PositionFollow, VelocityFollow);

        Vector2 bodyTarget = feetTarget + supportNormal * StandBodyOffset;
        SoftFollowChunk(player.mainBodyChunk, bodyTarget, 0.22f, 0.07f);

        // Only remove velocity entering the spear. Tangent momentum and velocity
        // away from the spear survive, so wall-jumps/launch tech are not sanitized.
        RemoveIntoSurfaceVelocity(feet, supportNormal);
        RemoveIntoSurfaceVelocity(player.mainBodyChunk, supportNormal);

        if (input.x != 0)
        {
            Vector2 walkImpulse = direction *
                                  (input.x * Mathf.Sign(direction.x)) *
                                  0.16f;
            player.mainBodyChunk.vel += walkImpulse;
            feet.vel += walkImpulse * 0.8f;
        }

        if (Mathf.Abs(state.Position - oldPosition) > 0.0015f)
        {
            state.StepCounter++;
            if (state.StepCounter >= 12)
            {
                state.StepCounter = 0;
                player.room?.PlaySound(
                    SoundID.Slugcat_Normal_Jump,
                    feet,
                    loop: false,
                    0.18f,
                    1.25f);
            }
        }

        player.standing = true;
        player.canJump = Mathf.Max(player.canJump, 5);
        player.ledgeGrabCounter = 0;
        player.wallSlideCounter = 0;
        return true;
    }

    private static bool UpdateClimbing(
        Player player,
        TraversalState state,
        Vector2 tail,
        Vector2 wallEnd,
        Vector2 direction,
        Vector2 supportNormal,
        bool canStand,
        Player.InputPackage input)
    {
        float length = Vector2.Distance(tail, wallEnd);
        if (length < 1f)
        {
            return false;
        }

        Vector2 movement = new(input.x, input.y);
        float along = Vector2.Dot(movement, direction);

        if (state.MountGrace > 0)
        {
            state.MountGrace--;
            // Completing a rope->shaft transfer should never make "Up" reverse off
            // the tail merely because this particular spear slopes downward toward
            // the wall. Grace is short; normal world-direction climbing resumes after.
            if (input.y > 0)
            {
                along = Mathf.Max(along, 1f);
            }
        }

        if (Mathf.Abs(along) > 0.05f)
        {
            state.Position += Mathf.Clamp(along, -1f, 1f) * (ClimbSpeed / length);
            state.StepCounter++;
        }

        if (state.Position < -TailReleasePadding / length ||
            state.Position > 1f + 2f / length)
        {
            return false;
        }
        state.Position = Mathf.Clamp01(state.Position);

        Vector2 point = Vector2.Lerp(tail, wallEnd, state.Position);
        Vector2 climbTarget = point - supportNormal * ClimbBodyOffset;
        SoftFollowChunk(player.mainBodyChunk, climbTarget, 0.48f, 0.12f);

        if (player.bodyChunks != null && player.bodyChunks.Length > 1)
        {
            Vector2 rearTarget = point - direction * 10f - supportNormal * 4f;
            SoftFollowChunk(player.bodyChunks[1], rearTarget, 0.22f, 0.06f);
        }

        if (state.StepCounter >= 10)
        {
            state.StepCounter = 0;
            player.room?.PlaySound(
                SoundID.Slugcat_Climb_Up_Vertical_Beam,
                player.mainBodyChunk,
                loop: false,
                0.45f,
                1f);
        }

        // On standable slopes, climbing from below/outside onto the exposed shaft can
        // naturally become a standing balance state once the torso reaches the top.
        if (canStand &&
            state.Position > 0.08f &&
            Vector2.Dot(player.mainBodyChunk.pos - point, supportNormal) > -2f &&
            input.y >= 0)
        {
            state.Mode = TraversalMode.Stand;
        }
        else
        {
            player.standing = false;
        }

        player.bodyMode = Player.BodyModeIndex.Default;
        player.ledgeGrabCounter = 0;
        player.wallSlideCounter = 0;
        return true;
    }

    private static void ApplyJumpRelease(
        Player player,
        TraversalState state,
        Vector2 direction,
        Vector2 supportNormal,
        Player.InputPackage input)
    {
        float tangentSign = input.x != 0
            ? input.x * Mathf.Sign(direction.x)
            : 0f;

        if (state.Mode == TraversalMode.Stand)
        {
            // Vanilla Player.Update may already have consumed a standing jump before
            // this post-pass. Do not add a second full jump; only guarantee that the
            // velocity points away from the diagonal support.
            EnsureMinimumNormalVelocity(player.mainBodyChunk, supportNormal, 3.2f);
            if (player.bodyChunks != null && player.bodyChunks.Length > 1)
            {
                EnsureMinimumNormalVelocity(player.bodyChunks[1], supportNormal, 2.7f);
            }
        }
        else
        {
            Vector2 impulse = supportNormal * 3.2f + direction * tangentSign * 1.35f;
            player.mainBodyChunk.vel += impulse;
            if (player.bodyChunks != null && player.bodyChunks.Length > 1)
            {
                player.bodyChunks[1].vel += impulse * 0.82f;
            }
        }

        player.standing = false;
    }

    private static bool TryAcquireStanding(
        Player player,
        out RopeSpear bestSpear,
        out float bestPosition)
    {
        bestSpear = null;
        bestPosition = 0f;
        if (player?.room?.physicalObjects == null)
        {
            return false;
        }

        BodyChunk feet = GetFeetChunk(player);
        float bestDistance = float.MaxValue;

        for (int layer = 0; layer < player.room.physicalObjects.Length; layer++)
        {
            var objects = player.room.physicalObjects[layer];
            for (int i = 0; i < objects.Count; i++)
            {
                if (objects[i] is not RopeSpear spear ||
                    !RopeSpearWallStickRuntime.TryGetTraversalSegment(
                        spear,
                        out Vector2 tail,
                        out Vector2 wallEnd,
                        out _,
                        out Vector2 normal,
                        out bool canStand) ||
                    !canStand)
                {
                    continue;
                }

                ClosestPointOnSegment(feet.pos, tail, wallEnd, out float t, out Vector2 point);
                Vector2 previousFeet = feet.lastPos;
                float previousSide = Vector2.Dot(previousFeet - point, normal);
                float currentSide = Vector2.Dot(feet.pos - point, normal);
                float perpendicularDistance = Mathf.Abs(currentSide);

                bool crossedDown = previousSide >= 0f && currentSide <= StandFeetOffset + 2f;
                bool approaching = Vector2.Dot(feet.vel, normal) <= 0.5f;
                bool alongSegment = t >= 0f && t <= 1f;

                if (!alongSegment ||
                    !approaching ||
                    (!crossedDown && perpendicularDistance > StandAcquireRadius) ||
                    perpendicularDistance >= bestDistance)
                {
                    continue;
                }

                bestDistance = perpendicularDistance;
                bestSpear = spear;
                bestPosition = Mathf.Clamp01(t);
            }
        }

        return bestSpear != null;
    }

    private static bool TryAcquireClimb(
        Player player,
        out RopeSpear bestSpear,
        out float bestPosition)
    {
        bestSpear = null;
        bestPosition = 0f;
        if (player?.room?.physicalObjects == null)
        {
            return false;
        }

        float bestDistance = float.MaxValue;
        for (int layer = 0; layer < player.room.physicalObjects.Length; layer++)
        {
            var objects = player.room.physicalObjects[layer];
            for (int i = 0; i < objects.Count; i++)
            {
                if (objects[i] is not RopeSpear spear ||
                    !RopeSpearWallStickRuntime.TryGetTraversalSegment(
                        spear,
                        out Vector2 tail,
                        out Vector2 wallEnd,
                        out _,
                        out _,
                        out _))
                {
                    continue;
                }

                ClosestPointOnSegment(
                    player.mainBodyChunk.pos,
                    tail,
                    wallEnd,
                    out float t,
                    out Vector2 point);
                float distance = Vector2.Distance(player.mainBodyChunk.pos, point);
                if (t < -0.08f || t > 1.05f || distance > AcquireRadius || distance >= bestDistance)
                {
                    continue;
                }

                bestDistance = distance;
                bestSpear = spear;
                bestPosition = Mathf.Clamp01(t);
            }
        }

        return bestSpear != null;
    }

    internal static bool TryMountFromRopeEndpoint(Player player, RopeSpear spear)
    {
        if (player == null ||
            spear == null ||
            player.input == null ||
            player.input.Length == 0 ||
            player.input[0].y <= 0 ||
            !RopeSpearWallStickRuntime.TryGetTraversalSegment(
                spear,
                out Vector2 tail,
                out Vector2 wallEnd,
                out _,
                out _,
                out _))
        {
            return false;
        }

        if (!Custom.DistLess(player.mainBodyChunk.pos, tail, AcquireRadius + 7f))
        {
            return false;
        }

        TraversalState state = States.GetOrCreateValue(player);
        state.Spear = spear;
        state.Mode = TraversalMode.Climb;
        state.Position = 0f;
        state.StepCounter = 0;
        state.MountGrace = DetachFlexibleRopeState(player, spear)
            ? RopeMountGraceFrames
            : 0;
        player.wantToPickUp = 0;
        return true;
    }

    private static bool DetachFlexibleRopeState(Player player, RopeSpear spear)
    {
        try
        {
            if (_ropeGrabTable == null)
            {
                _ropeGrabTable = RopeGrabStatesField?.GetValue(null);
                _ropeGrabTryGetValue = _ropeGrabTable?.GetType().GetMethod("TryGetValue");
            }

            if (_ropeGrabTable == null || _ropeGrabTryGetValue == null)
            {
                WarnRopeReflection();
                return false;
            }

            object[] args = { player, null };
            bool found = (bool)_ropeGrabTryGetValue.Invoke(_ropeGrabTable, args);
            if (!found || args[1] == null)
            {
                return false;
            }

            object ropeState = args[1];
            _ropeGrabSpearField ??= ropeState.GetType().GetField(
                "Spear",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            _ropeGrabDelayField ??= ropeState.GetType().GetField(
                "RegrabDelay",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            if (_ropeGrabSpearField == null || _ropeGrabDelayField == null)
            {
                WarnRopeReflection();
                return false;
            }

            if (!ReferenceEquals(_ropeGrabSpearField.GetValue(ropeState), spear))
            {
                return false;
            }

            _ropeGrabSpearField.SetValue(ropeState, null);
            _ropeGrabDelayField.SetValue(ropeState, ReattachCooldown);
            return true;
        }
        catch (System.Exception ex)
        {
            if (!_ropeReflectionWarningIssued)
            {
                _ropeReflectionWarningIssued = true;
                Plugin.Logger?.LogWarning(
                    $"RopeSpear diagonal shaft could not detach flexible rope state: {ex.Message}");
            }
            return false;
        }
    }

    private static void WarnRopeReflection()
    {
        if (_ropeReflectionWarningIssued)
        {
            return;
        }

        _ropeReflectionWarningIssued = true;
        Plugin.Logger?.LogWarning(
            "RopeSpear diagonal shaft could not bind RopeSpearHooks rope-grab state; rope-to-shaft transfer may require releasing and re-grabbing.");
    }

    private static void PrepareClimbHands(PlayerGraphics graphics, TraversalState state)
    {
        if (graphics?.player == null ||
            graphics.hands == null ||
            !RopeSpearWallStickRuntime.TryGetTraversalSegment(
                state.Spear,
                out Vector2 tail,
                out Vector2 wallEnd,
                out Vector2 direction,
                out _,
                out _))
        {
            return;
        }

        Vector2 point = Vector2.Lerp(tail, wallEnd, Mathf.Clamp01(state.Position));
        float[] handOffsets = { -5f, 5f };

        for (int i = 0; i < graphics.hands.Length && i < 2; i++)
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
            hand.absoluteHuntPos = point + direction * handOffsets[i];
            hand.huntSpeed = 15f;
            hand.quickness = 0.82f;
        }
    }

    private static bool CanUseSpearTraversal(Player player)
    {
        if (player == null ||
            player.dead ||
            !player.Consious ||
            player.room == null ||
            player.inShortcut ||
            player.enteringShortCut.HasValue ||
            player.input == null ||
            player.input.Length == 0)
        {
            return false;
        }

        return player.bodyMode != Player.BodyModeIndex.CorridorClimb &&
               player.bodyMode != Player.BodyModeIndex.ClimbIntoShortCut &&
               player.bodyMode != Player.BodyModeIndex.Swimming &&
               player.bodyMode != Player.BodyModeIndex.Stunned &&
               player.bodyMode != Player.BodyModeIndex.Dead;
    }

    private static BodyChunk GetFeetChunk(Player player)
    {
        if (player?.bodyChunks != null && player.bodyChunks.Length > 1)
        {
            return player.bodyChunks[1];
        }

        return player?.mainBodyChunk;
    }

    private static void SoftFollowChunk(
        BodyChunk chunk,
        Vector2 target,
        float positionShare,
        float velocityShare)
    {
        if (chunk == null)
        {
            return;
        }

        Vector2 delta = target - chunk.pos;
        if (delta.magnitude > 70f)
        {
            return;
        }

        chunk.pos += delta * positionShare;
        chunk.vel += delta * velocityShare;
    }

    private static void RemoveIntoSurfaceVelocity(BodyChunk chunk, Vector2 normal)
    {
        if (chunk == null)
        {
            return;
        }

        float into = Vector2.Dot(chunk.vel, normal);
        if (into < 0f)
        {
            chunk.vel -= normal * into;
        }
    }

    private static void EnsureMinimumNormalVelocity(
        BodyChunk chunk,
        Vector2 normal,
        float minimum)
    {
        if (chunk == null)
        {
            return;
        }

        float current = Vector2.Dot(chunk.vel, normal);
        if (current < minimum)
        {
            chunk.vel += normal * (minimum - current);
        }
    }

    private static void ClosestPointOnSegment(
        Vector2 position,
        Vector2 a,
        Vector2 b,
        out float t,
        out Vector2 point)
    {
        Vector2 ab = b - a;
        float denominator = ab.sqrMagnitude;
        if (denominator < 0.001f)
        {
            t = 0f;
            point = a;
            return;
        }

        t = Vector2.Dot(position - a, ab) / denominator;
        point = a + ab * Mathf.Clamp01(t);
    }

    private static void ClearState(TraversalState state, int cooldown)
    {
        if (state == null)
        {
            return;
        }

        state.Spear = null;
        state.Position = 0f;
        state.StepCounter = 0;
        state.MountGrace = 0;
        state.Cooldown = Mathf.Max(state.Cooldown, cooldown);
    }
}
