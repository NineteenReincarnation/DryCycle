using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

/// <summary>
/// Prevents a deeply immersed player from leaving a quicksand material interval
/// sideways through its open edge.
///
/// This is deliberately not a wall/contact-point simulation. The player's native
/// locomotion still owns pose and animation. Near a shore, outward travel is held at
/// the authored material boundary and a depth-dependent fraction of that attempted
/// travel is converted into a small whole-body upward climb. Shallow players can
/// leave after a short confirmed shore-top hold; deep players continue to sink if
/// the climb cannot overcome the normal sink rate.
/// </summary>
internal static class QuicksandPlayerShoreConstraint
{
    private const float ActualContactImmersion = 0.05f;
    private const float ExitReleaseImmersion = 0.14f;
    private const float StrongShoreImmersion = 0.20f;
    private const float DeepShoreImmersion = 0.72f;

    // Acquire the shore early enough that the inner player update cannot cross the
    // material edge in one tick. The actual clamp is at the edge, not this distance.
    private const float ShoreAcquireDistance = 7.0f;
    private const float ShoreDisengageDistance = 18.0f;
    private const float CenterBoundaryInset = 0.35f;
    private const float PreUpdateSafetyInset = 1.80f;

    private const float ShoreTopTolerance = 3.0f;
    private const int ExitConfirmTicks = 4;

    // Very small non-quicksand gaps are treated as one physical pool so two shore
    // constraints cannot fight over a few pixels of material painting.
    private const float MergeMaterialGapWorld = 8.0f;

    private const float ShallowClimbEfficiency = 0.38f;
    private const float DeepClimbEfficiency = 0.045f;
    private const float ShallowClimbCap = 0.24f;
    private const float DeepClimbCap = 0.055f;

    private sealed class State
    {
        internal bool Active;
        internal QuicksandZone Zone;
        internal float StartU;
        internal float EndU;
        internal float LeftX;
        internal float RightX;
        internal int ShoreSide;
        internal bool ContactSeen;
        internal bool ExitUnlocked;
        internal int ExitTicks;
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
            out QuicksandZone sinkZoneBefore,
            out _);

        if (hadSinkState)
        {
            EnsureInterval(player, state, sinkZoneBefore);
            UpdateMeasuredImmersion(player, state);
            UpdateContactAndShoreChoice(player, state);

            if (ShouldConstrainAtShore(player, state))
            {
                // Keep all physics centers just inside the material edge before the
                // inner update. This is only a ~2 px safety reserve; it is not a wide
                // invisible bank and therefore does not create an "air wall" inside
                // the visible quicksand.
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

        // Installed after the sink/locomotion hooks, so orig() lets all native and
        // existing quicksand movement finish before the final shore correction.
        orig(player, eu);

        bool hasSinkState = QuicksandSinkRateLimiter.TryGetPlayerQuicksandState(
            player,
            out QuicksandZone sinkZoneAfter,
            out _);

        if (!hasSinkState)
        {
            Reset(state);
            return;
        }

        EnsureInterval(player, state, sinkZoneAfter);
        UpdateMeasuredImmersion(player, state);
        UpdateContactAndShoreChoice(player, state);

        if (!state.ContactSeen || state.ShoreSide == 0)
        {
            state.ExitTicks = 0;
            state.ExitUnlocked = false;
            return;
        }

        float attemptedOutwardTravel = Mathf.Max(
            0f,
            state.ShoreSide * (AverageX(player) - startAverageX));

        bool atShore = DistanceToShore(player, state, state.ShoreSide) <=
                       CenterBoundaryInset + 0.75f;

        if (!state.ExitUnlocked)
        {
            ClampPlayerCentersToShore(
                player,
                state,
                CenterBoundaryInset,
                out float blockedByClamp);

            attemptedOutwardTravel = Mathf.Max(attemptedOutwardTravel, blockedByClamp);
            atShore = atShore || blockedByClamp > 0.0001f;
        }

        if (state.ExitUnlocked)
        {
            // Once the player has proved they are shallow and at the shore top, stop
            // interfering. QuicksandSinkRateLimiter will naturally deactivate when
            // the body centers really leave the authored quicksand material.
            if (!HasOutwardInput(player, state.ShoreSide))
            {
                state.ExitUnlocked = false;
                state.ExitTicks = 0;
            }
            return;
        }

        if (!atShore)
        {
            state.ExitTicks = 0;
            return;
        }

        if (state.MaxImmersion <= ExitReleaseImmersion)
        {
            // Do not instantly release on a single threshold crossing. The player
            // must remain shallow, keep pushing toward the bank and have the lowest
            // main body edge at the local shore top for several ticks.
            if (HasOutwardInput(player, state.ShoreSide) &&
                BodyAtShoreTop(player, state))
            {
                state.ExitTicks++;
                if (state.ExitTicks >= ExitConfirmTicks)
                {
                    state.ExitUnlocked = true;
                    state.ExitTicks = 0;
                }
            }
            else
            {
                state.ExitTicks = 0;
            }

            return;
        }

        state.ExitTicks = 0;

        if (!HasOutwardInput(player, state.ShoreSide) ||
            attemptedOutwardTravel <= 0.0001f)
        {
            return;
        }

        // Convert only the player's attempted outward travel into climbing. External
        // upward impulses are never cancelled or replaced. At high immersion the
        // assist is intentionally smaller than the existing 0.10 px/tick sink rate,
        // so simply holding toward the bank cannot save a deeply buried player.
        float lift = ResolveShoreLift(state.MaxImmersion, attemptedOutwardTravel);
        if (lift > 0.0001f)
        {
            TranslatePlayerY(player, lift);
        }
    }

    private static bool ShouldConstrainAtShore(Player player, State state)
    {
        return state.Active &&
               state.ContactSeen &&
               !state.ExitUnlocked &&
               state.ShoreSide != 0 &&
               state.MaxImmersion > ExitReleaseImmersion &&
               DistanceToShore(player, state, state.ShoreSide) <= ShoreAcquireDistance;
    }

    private static void UpdateContactAndShoreChoice(Player player, State state)
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
        if (movingBackIntoPool && distance > ShoreAcquireDistance * 0.75f ||
            distance > ShoreDisengageDistance)
        {
            state.ShoreSide = 0;
            state.ExitTicks = 0;
            state.ExitUnlocked = false;
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

    private static bool BodyAtShoreTop(Player player, State state)
    {
        if (!TryGetShoreSurface(state, out float surfaceY))
        {
            return false;
        }

        float lowestBottom = float.PositiveInfinity;
        bool found = false;
        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            BodyChunk chunk = player.bodyChunks[i];
            if (chunk == null)
            {
                continue;
            }

            lowestBottom = Mathf.Min(
                lowestBottom,
                chunk.pos.y - Mathf.Max(1f, chunk.rad));
            found = true;
        }

        return found && lowestBottom >= surfaceY - ShoreTopTolerance;
    }

    private static bool TryGetShoreSurface(State state, out float surfaceY)
    {
        surfaceY = 0f;
        if (!Valid(state?.Zone) || state.ShoreSide == 0)
        {
            return false;
        }

        float u = state.ShoreSide < 0 ? state.StartU : state.EndU;
        if (!state.Zone.TrySampleSurfaceFrame(
                u,
                out Vector2 surface,
                out _,
                out _,
                out _))
        {
            return false;
        }

        surfaceY = surface.y;
        return true;
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
            float immersion = Mathf.Clamp01((depth + radius) / (radius * 2f));
            maximum = Mathf.Max(maximum, immersion);
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
        if (!Valid(zone) || !TryResolvePhysicalInterval(
                player,
                zone,
                out float startU,
                out float endU,
                out float leftX,
                out float rightX))
        {
            return;
        }

        state.Active = true;
        state.Zone = zone;
        state.StartU = startU;
        state.EndU = endU;
        state.LeftX = leftX;
        state.RightX = rightX;
    }

    private static bool TryResolvePhysicalInterval(
        Player player,
        QuicksandZone zone,
        out float bestStartU,
        out float bestEndU,
        out float bestLeftX,
        out float bestRightX)
    {
        bestStartU = 0f;
        bestEndU = 0f;
        bestLeftX = 0f;
        bestRightX = 0f;

        if (!CanTrack(player) || !Valid(zone))
        {
            return false;
        }

        List<Vector2> intervals = new();
        zone.Data.FillQuicksandIntervals(intervals);
        if (intervals.Count == 0)
        {
            return false;
        }

        float playerX = AverageX(player);
        float bestDistance = float.PositiveInfinity;

        float mergedStartU = intervals[0].x;
        float mergedEndU = intervals[0].y;

        for (int i = 1; i <= intervals.Count; i++)
        {
            bool mergeNext = false;
            if (i < intervals.Count &&
                TrySurfaceX(zone, mergedEndU, out float currentRightX) &&
                TrySurfaceX(zone, intervals[i].x, out float nextLeftX))
            {
                mergeNext = nextLeftX - currentRightX <= MergeMaterialGapWorld;
            }

            if (mergeNext)
            {
                mergedEndU = intervals[i].y;
                continue;
            }

            if (TrySurfaceX(zone, mergedStartU, out float leftX) &&
                TrySurfaceX(zone, mergedEndU, out float rightX))
            {
                if (rightX < leftX)
                {
                    (leftX, rightX) = (rightX, leftX);
                }

                float distance = playerX < leftX
                    ? leftX - playerX
                    : playerX > rightX
                        ? playerX - rightX
                        : 0f;

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestStartU = mergedStartU;
                    bestEndU = mergedEndU;
                    bestLeftX = leftX;
                    bestRightX = rightX;
                }
            }

            if (i < intervals.Count)
            {
                mergedStartU = intervals[i].x;
                mergedEndU = intervals[i].y;
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

    private static float LeftmostCenter(Player player)
    {
        float result = float.PositiveInfinity;
        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            if (player.bodyChunks[i] != null)
            {
                result = Mathf.Min(result, player.bodyChunks[i].pos.x);
            }
        }

        return result;
    }

    private static float RightmostCenter(Player player)
    {
        float result = float.NegativeInfinity;
        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            if (player.bodyChunks[i] != null)
            {
                result = Mathf.Max(result, player.bodyChunks[i].pos.x);
            }
        }

        return result;
    }

    private static void TranslatePlayerY(Player player, float deltaY)
    {
        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            if (player.bodyChunks[i] != null)
            {
                player.bodyChunks[i].pos.y += deltaY;
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
        state.StartU = 0f;
        state.EndU = 0f;
        state.LeftX = 0f;
        state.RightX = 0f;
        state.ShoreSide = 0;
        state.ContactSeen = false;
        state.ExitUnlocked = false;
        state.ExitTicks = 0;
        state.MaxImmersion = 0f;
    }
}
