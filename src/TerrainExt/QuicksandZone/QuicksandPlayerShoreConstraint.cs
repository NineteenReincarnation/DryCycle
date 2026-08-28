using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

/// <summary>
/// Prevents a genuinely immersed player from leaking sideways through the open edge
/// of a quicksand material interval without turning that edge into a hard wall.
///
/// Shallow contact is intentionally permissive: at <= 20% maximum body immersion the
/// player may simply leave the interval. A rising/jumping player gets the same freedom
/// up to 28% immersion, preserving a reaction window. Deeper players are kept just
/// inside the authored material edge; outward travel is converted into a small,
/// depth-dependent whole-body lift instead of creating a ContactPoint or wall jump.
/// </summary>
internal static class QuicksandPlayerShoreConstraint
{
    private const float ActualContactImmersion = 0.05f;
    private const float FreeExitImmersion = 0.20f;
    private const float JumpExitImmersion = 0.28f;
    private const float JumpExitVelocity = 0.35f;

    private const float ShoreAcquireDistance = 6.0f;
    private const float ShoreDisengageDistance = 18.0f;
    private const float CenterBoundaryInset = 0.20f;
    private const float PreUpdateSafetyInset = 1.20f;

    private const float StrongShoreImmersion = 0.20f;
    private const float DeepShoreImmersion = 0.72f;
    private const float ShallowClimbEfficiency = 0.38f;
    private const float DeepClimbEfficiency = 0.045f;
    private const float ShallowClimbCap = 0.24f;
    private const float DeepClimbCap = 0.055f;

    private sealed class State
    {
        internal bool Active;
        internal QuicksandZone Zone;
        internal float LeftX;
        internal float RightX;
        internal int ShoreSide;
        internal bool ContactSeen;
        internal float MaxImmersion;
    }

    private static readonly ConditionalWeakTable<Player, State> States = new();
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.Player.Update += Player_Update;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;
        On.Player.Update -= Player_Update;
    }

    private static void Player_Update(
        On.Player.orig_Update orig,
        Player player,
        bool eu)
    {
        if (!CanTrack(player))
        {
            orig(player, eu);
            return;
        }

        State state = States.GetValue(player, _ => new State());
        bool hadSinkState = QuicksandSinkRateLimiter.TryGetPlayerQuicksandState(
            player,
            out QuicksandZone zoneBefore,
            out _);

        if (hadSinkState)
        {
            EnsureInterval(player, state, zoneBefore);
            UpdateMeasuredImmersion(player, state);
            UpdateShoreChoice(player, state);

            // Deep sideways travel is stopped before the inner update so one fast
            // frame cannot tunnel through the material edge. Shallow players, and a
            // deliberate shallow jump toward the bank, are not pre-clamped.
            if (ShouldBlockBeforeUpdate(player, state))
            {
                ClampPlayerCentersToShore(
                    player,
                    state,
                    PreUpdateSafetyInset,
                    out _);
            }
        }
        else if (state.Active)
        {
            Reset(state);
        }

        float startAverageX = AverageX(player);
        orig(player, eu);

        bool hasSinkState = QuicksandSinkRateLimiter.TryGetPlayerQuicksandState(
            player,
            out QuicksandZone zoneAfter,
            out _);

        if (!hasSinkState)
        {
            Reset(state);
            return;
        }

        EnsureInterval(player, state, zoneAfter);
        UpdateMeasuredImmersion(player, state);
        UpdateShoreChoice(player, state);

        if (!state.ContactSeen || state.ShoreSide == 0)
        {
            return;
        }

        bool outwardInput = HasOutwardInput(player, state.ShoreSide);
        bool atShore = DistanceToShore(player, state, state.ShoreSide) <=
                       CenterBoundaryInset + 0.75f;

        // Ordinary shallow movement can leave freely. This is deliberately simpler
        // than the previous 0.14 + four-tick shore-top gate and gives immediate player
        // agency without reopening the deep side-exit exploit.
        if (state.MaxImmersion <= FreeExitImmersion)
        {
            return;
        }

        // A real upward jump/pull gets a slightly wider shallow window. The held-jump
        // boost is handled by QuicksandPlayerStruggleControl; this layer only refrains
        // from blocking a valid outward rising trajectory.
        if (atShore &&
            outwardInput &&
            state.MaxImmersion <= JumpExitImmersion &&
            AverageVelocityY(player) >= JumpExitVelocity)
        {
            return;
        }

        float attemptedOutwardTravel = Mathf.Max(
            0f,
            state.ShoreSide * (AverageX(player) - startAverageX));

        ClampPlayerCentersToShore(
            player,
            state,
            CenterBoundaryInset,
            out float blockedByClamp);

        attemptedOutwardTravel = Mathf.Max(attemptedOutwardTravel, blockedByClamp);
        atShore = atShore || blockedByClamp > 0.0001f;

        if (!atShore || !outwardInput || attemptedOutwardTravel <= 0.0001f)
        {
            return;
        }

        float lift = ResolveShoreLift(state.MaxImmersion, attemptedOutwardTravel);
        if (lift > 0.0001f)
        {
            TranslatePlayerY(player, lift);
        }
    }

    private static bool ShouldBlockBeforeUpdate(Player player, State state)
    {
        if (!state.Active ||
            !state.ContactSeen ||
            state.ShoreSide == 0 ||
            state.MaxImmersion <= FreeExitImmersion ||
            DistanceToShore(player, state, state.ShoreSide) > ShoreAcquireDistance)
        {
            return false;
        }

        bool outwardInput = HasOutwardInput(player, state.ShoreSide);
        bool shallowJumpIntent = outwardInput &&
                                 state.MaxImmersion <= JumpExitImmersion &&
                                 JumpHeld(player);

        return !shallowJumpIntent;
    }

    private static void UpdateShoreChoice(Player player, State state)
    {
        if (!state.Active)
        {
            return;
        }

        if (state.MaxImmersion >= ActualContactImmersion)
        {
            state.ContactSeen = true;
        }

        if (!state.ContactSeen)
        {
            state.ShoreSide = 0;
            return;
        }

        int inputDirection = HorizontalInputDirection(player);
        if (state.ShoreSide == 0)
        {
            if (inputDirection < 0 &&
                DistanceToShore(player, state, -1) <= ShoreAcquireDistance)
            {
                state.ShoreSide = -1;
            }
            else if (inputDirection > 0 &&
                     DistanceToShore(player, state, 1) <= ShoreAcquireDistance)
            {
                state.ShoreSide = 1;
            }
            else if (LeftmostCenter(player) < state.LeftX ||
                     RightmostCenter(player) > state.RightX)
            {
                state.ShoreSide = LeftmostCenter(player) < state.LeftX ? -1 : 1;
            }

            return;
        }

        float distance = DistanceToShore(player, state, state.ShoreSide);
        bool movingBackIntoPool = inputDirection == -state.ShoreSide;
        if ((movingBackIntoPool && distance > ShoreAcquireDistance * 0.75f) ||
            distance > ShoreDisengageDistance)
        {
            state.ShoreSide = 0;
        }
    }

    private static float ResolveShoreLift(float maxImmersion, float outwardTravel)
    {
        float t = Mathf.InverseLerp(
            StrongShoreImmersion,
            DeepShoreImmersion,
            Mathf.Clamp(maxImmersion, StrongShoreImmersion, DeepShoreImmersion));
        t = t * t * (3f - 2f * t);

        float efficiency = Mathf.Lerp(
            ShallowClimbEfficiency,
            DeepClimbEfficiency,
            t);
        float cap = Mathf.Lerp(ShallowClimbCap, DeepClimbCap, t);
        return Mathf.Min(cap, outwardTravel * efficiency);
    }

    private static void UpdateMeasuredImmersion(Player player, State state)
    {
        state.MaxImmersion = MeasureMaxImmersion(player, state);
    }

    private static float MeasureMaxImmersion(Player player, State state)
    {
        if (!state.Active || !Valid(state.Zone))
        {
            return 0f;
        }

        float maximum = 0f;
        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            BodyChunk chunk = player.bodyChunks[i];
            if (chunk == null)
            {
                continue;
            }

            float radius = Mathf.Max(1f, chunk.rad);
            if (chunk.pos.x < state.LeftX - radius * 1.5f ||
                chunk.pos.x > state.RightX + radius * 1.5f)
            {
                continue;
            }

            float sampleX = Mathf.Clamp(chunk.pos.x, state.LeftX, state.RightX);
            float u = state.Zone.MaterialUAtWorldX(sampleX);
            if (!state.Zone.TrySampleSurfaceFrame(
                    u,
                    out Vector2 surface,
                    out _,
                    out _,
                    out _))
            {
                continue;
            }

            float depth = surface.y - chunk.pos.y;
            maximum = Mathf.Max(
                maximum,
                Mathf.Clamp01((depth + radius) / (radius * 2f)));
        }

        return maximum;
    }

    private static void EnsureInterval(Player player, State state, QuicksandZone zone)
    {
        if (state.Active && state.Zone == zone && Valid(zone))
        {
            return;
        }

        Reset(state);
        if (!Valid(zone) ||
            !TryResolveInterval(player, zone, out float leftX, out float rightX))
        {
            return;
        }

        state.Active = true;
        state.Zone = zone;
        state.LeftX = leftX;
        state.RightX = rightX;
    }

    private static bool TryResolveInterval(
        Player player,
        QuicksandZone zone,
        out float bestLeftX,
        out float bestRightX)
    {
        bestLeftX = 0f;
        bestRightX = 0f;
        if (!CanTrack(player) || !Valid(zone))
        {
            return false;
        }

        var boundaries = zone.Data.MaterialBoundaries;
        float playerX = AverageX(player);
        float bestDistance = float.PositiveInfinity;
        bool quicksand = false;
        float intervalStartU = 0f;

        for (int i = 0; i <= boundaries.Count; i++)
        {
            float boundaryU = i < boundaries.Count ? boundaries[i] : 1f;
            if (quicksand &&
                boundaryU > intervalStartU + 0.0001f &&
                TrySurfaceX(zone, intervalStartU, out float leftX) &&
                TrySurfaceX(zone, boundaryU, out float rightX))
            {
                if (rightX < leftX)
                {
                    float swap = leftX;
                    leftX = rightX;
                    rightX = swap;
                }

                float distance = playerX < leftX
                    ? leftX - playerX
                    : playerX > rightX
                        ? playerX - rightX
                        : 0f;

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestLeftX = leftX;
                    bestRightX = rightX;
                }
            }

            if (i < boundaries.Count)
            {
                quicksand = !quicksand;
                intervalStartU = boundaryU;
            }
        }

        return bestDistance < float.PositiveInfinity;
    }

    private static bool TrySurfaceX(QuicksandZone zone, float u, out float x)
    {
        x = 0f;
        if (!Valid(zone) ||
            !zone.TrySampleSurfaceFrame(
                Mathf.Clamp01(u),
                out Vector2 surface,
                out _,
                out _,
                out _))
        {
            return false;
        }

        x = surface.x;
        return true;
    }

    private static void ClampPlayerCentersToShore(
        Player player,
        State state,
        float inset,
        out float blockedTravel)
    {
        blockedTravel = 0f;
        if (!state.Active || state.ShoreSide == 0)
        {
            return;
        }

        float correction = 0f;
        if (state.ShoreSide < 0)
        {
            float minimumX = state.LeftX + inset;
            float leftmost = LeftmostCenter(player);
            if (leftmost < minimumX)
            {
                correction = minimumX - leftmost;
                blockedTravel = correction;
            }
        }
        else
        {
            float maximumX = state.RightX - inset;
            float rightmost = RightmostCenter(player);
            if (rightmost > maximumX)
            {
                correction = maximumX - rightmost;
                blockedTravel = -correction;
            }
        }

        if (Mathf.Abs(correction) <= 0.000001f)
        {
            return;
        }

        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            if (player.bodyChunks[i] != null)
            {
                player.bodyChunks[i].pos.x += correction;
            }
        }
    }

    private static float DistanceToShore(Player player, State state, int side)
    {
        if (!state.Active || side == 0)
        {
            return float.PositiveInfinity;
        }

        return side < 0
            ? Mathf.Max(0f, LeftmostCenter(player) - state.LeftX)
            : Mathf.Max(0f, state.RightX - RightmostCenter(player));
    }

    private static bool HasOutwardInput(Player player, int shoreSide)
    {
        return shoreSide != 0 && HorizontalInputDirection(player) == shoreSide;
    }

    private static bool JumpHeld(Player player)
    {
        return player?.input != null &&
               player.input.Length > 0 &&
               player.input[0].jmp;
    }

    private static int HorizontalInputDirection(Player player)
    {
        if (player?.input == null || player.input.Length == 0)
        {
            return 0;
        }

        return player.input[0].x < 0 ? -1 : player.input[0].x > 0 ? 1 : 0;
    }

    private static float AverageX(Player player)
    {
        float total = 0f;
        int count = 0;
        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            BodyChunk chunk = player.bodyChunks[i];
            if (chunk == null)
            {
                continue;
            }

            total += chunk.pos.x;
            count++;
        }

        return count > 0 ? total / count : 0f;
    }

    private static float AverageVelocityY(Player player)
    {
        float total = 0f;
        int count = 0;
        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            BodyChunk chunk = player.bodyChunks[i];
            if (chunk == null)
            {
                continue;
            }

            total += chunk.vel.y;
            count++;
        }

        return count > 0 ? total / count : 0f;
    }

    private static float LeftmostCenter(Player player)
    {
        float result = float.PositiveInfinity;
        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            BodyChunk chunk = player.bodyChunks[i];
            if (chunk != null)
            {
                result = Mathf.Min(result, chunk.pos.x);
            }
        }

        return result;
    }

    private static float RightmostCenter(Player player)
    {
        float result = float.NegativeInfinity;
        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            BodyChunk chunk = player.bodyChunks[i];
            if (chunk != null)
            {
                result = Mathf.Max(result, chunk.pos.x);
            }
        }

        return result;
    }

    private static void TranslatePlayerY(Player player, float deltaY)
    {
        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            BodyChunk chunk = player.bodyChunks[i];
            if (chunk != null)
            {
                chunk.pos.y += deltaY;
            }
        }
    }

    private static bool CanTrack(Player player)
    {
        return player != null &&
               player.room != null &&
               player.bodyChunks != null &&
               player.bodyChunks.Length > 0 &&
               !player.slatedForDeletetion;
    }

    private static bool Valid(QuicksandZone zone)
    {
        return zone != null &&
               !zone.slatedForDeletetion &&
               zone.PlacedObject != null &&
               zone.PlacedObject.active &&
               zone.Data != null;
    }

    private static void Reset(State state)
    {
        if (state == null)
        {
            return;
        }

        state.Active = false;
        state.Zone = null;
        state.LeftX = 0f;
        state.RightX = 0f;
        state.ShoreSide = 0;
        state.ContactSeen = false;
        state.MaxImmersion = 0f;
    }
}
