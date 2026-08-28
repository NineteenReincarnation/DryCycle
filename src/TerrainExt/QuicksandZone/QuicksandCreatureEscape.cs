using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

/// <summary>
/// Post-entry quicksand behavior for non-player creatures. AI hazard code prevents
/// entry when possible; this layer makes an immersed creature commit to an escape,
/// gives flying creatures an easier upward recovery, and owns creature death once
/// the complete body has remained below the quicksand surface.
/// </summary>
internal static class QuicksandCreatureEscape
{
    private const float EnterImmersion = 0.01f;
    private const float ExitImmersion = 0.005f;
    private const float PanicImmersion = 0.65f;
    private const float FullSubmergeClearance = 1.5f;
    private const float EscapeMargin = 40f;
    private const int ExitConfirmTicks = 8;
    private const int DeathConfirmTicks = 10;
    private const int DeadCleanupTicks = 30;
    private const int StallSwitchTicks = 45;

    private const float LandSurfaceSink = 0.055f;
    private const float LandDeepSink = 0.035f;
    private const float LandMinX = 0.38f;
    private const float LandMaxX = 0.90f;
    private const float LandXVelocity = 0.90f;

    // Flying creatures are deliberately much easier to recover: shallow contact
    // produces a strong upward whole-body allowance, while deep immersion still
    // leaves a smaller upward escape instead of converting flight into land sinking.
    private const float FlySurfaceUp = 0.85f;
    private const float FlyDeepUp = 0.18f;
    private const float FlyMinX = 0.55f;
    private const float FlyMaxX = 1.15f;
    private const float FlyXVelocity = 1.20f;
    private const float FlySurfaceUpVelocity = 1.40f;
    private const float FlyDeepUpVelocity = 0.30f;

    private sealed class State
    {
        internal bool Active;
        internal QuicksandZone Zone;
        internal int EscapeDirection;
        internal int ExitTicks;
        internal int FullySubmergedTicks;
        internal int DeadSubmergedTicks;
        internal int StallTicks;
        internal bool ReleasedGrasps;
        internal float LastDanger;
        internal float StartX;
        internal float StartY;
        internal bool HasSnapshot;
        internal bool[] OriginalCollision;
        internal bool[] CollisionOverridden;
    }

    private static readonly ConditionalWeakTable<Creature, State> States = new();
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled) return;
        _enabled = true;
        On.Room.Update += Room_Update;
    }

    internal static void Disable()
    {
        if (!_enabled) return;
        _enabled = false;
        On.Room.Update -= Room_Update;
    }

    private static void Room_Update(On.Room.orig_Update orig, Room room)
    {
        BeforeRoomUpdate(room);
        try
        {
            orig(room);
        }
        finally
        {
            RestoreCollision(room);
        }
        AfterRoomUpdate(room);
    }

    private static void BeforeRoomUpdate(Room room)
    {
        ForEachCreature(room, creature =>
        {
            if (!CanControl(creature)) return;

            State state = States.GetValue(creature, _ => new State());
            state.StartX = AverageX(creature);
            state.StartY = AverageY(creature);
            state.HasSnapshot = true;

            if (!state.Active &&
                TryMeasure(creature, out QuicksandZone zone, out float avg, out float max, out _) &&
                max > EnterImmersion)
            {
                Activate(creature, state, zone, avg, max);
            }

            if (!state.Active || !Valid(state.Zone)) return;
            SetEscapeDestination(creature, state);
            OverrideCollision(creature, state);
        });
    }

    private static void AfterRoomUpdate(Room room)
    {
        ForEachCreature(room, creature =>
        {
            if (!CanControl(creature)) return;

            State state = States.GetValue(creature, _ => new State());
            if (!state.HasSnapshot) return;
            state.HasSnapshot = false;

            if (!TryMeasure(creature, out QuicksandZone zone, out float avg, out float max,
                    out bool fullySubmerged))
            {
                if (state.Active && ++state.ExitTicks >= ExitConfirmTicks) Deactivate(state);
                return;
            }

            if (!state.Active && max > EnterImmersion) Activate(creature, state, zone, avg, max);
            if (!state.Active) return;

            state.Zone = zone;
            SetEscapeDestination(creature, state);

            if (max <= ExitImmersion)
            {
                if (++state.ExitTicks >= ExitConfirmTicks) Deactivate(state);
                return;
            }
            state.ExitTicks = 0;

            float danger = Mathf.Clamp01(max * 0.65f + avg * 0.35f);
            UpdateProgress(creature, state, danger);

            if (!creature.dead)
            {
                ApplyEscapeMotion(creature, state, danger, max);
                if (max >= PanicImmersion && !state.ReleasedGrasps)
                {
                    creature.LoseAllGrasps();
                    state.ReleasedGrasps = true;
                }
            }

            UpdateDeath(creature, state, fullySubmerged);
        });
    }

    private static void UpdateDeath(Creature creature, State state, bool fullySubmerged)
    {
        if (!fullySubmerged)
        {
            state.FullySubmergedTicks = 0;
            state.DeadSubmergedTicks = 0;
            return;
        }

        if (!creature.dead)
        {
            if (++state.FullySubmergedTicks >= DeathConfirmTicks)
            {
                creature.LoseAllGrasps();
                creature.Die();
                state.DeadSubmergedTicks = 0;
            }
            return;
        }

        if (++state.DeadSubmergedTicks >= DeadCleanupTicks)
        {
            QuicksandSubmersionCleanup.DeleteCreatureAfterSubmersion(creature);
            Deactivate(state);
        }
    }

    private static void Activate(Creature creature, State state, QuicksandZone zone, float avg, float max)
    {
        state.Active = true;
        state.Zone = zone;
        state.EscapeDirection = NearestSide(creature, zone);
        state.ExitTicks = state.FullySubmergedTicks = state.DeadSubmergedTicks = state.StallTicks = 0;
        state.ReleasedGrasps = false;
        state.LastDanger = Mathf.Clamp01(max * 0.65f + avg * 0.35f);
    }

    private static void Deactivate(State state)
    {
        if (state == null) return;
        state.Active = false;
        state.Zone = null;
        state.EscapeDirection = 0;
        state.ExitTicks = state.FullySubmergedTicks = state.DeadSubmergedTicks = state.StallTicks = 0;
        state.ReleasedGrasps = false;
        state.LastDanger = 0f;
    }

    private static void UpdateProgress(Creature creature, State state, float danger)
    {
        state.StallTicks = danger < state.LastDanger - 0.015f ? 0 : state.StallTicks + 1;
        state.LastDanger = danger;
        if (state.StallTicks < StallSwitchTicks) return;

        // Keep a chosen side stable under normal circumstances, but if danger has not
        // improved for long enough, try the opposite side once rather than oscillating.
        state.EscapeDirection = state.EscapeDirection == 0
            ? NearestSide(creature, state.Zone)
            : -state.EscapeDirection;
        state.StallTicks = 0;
    }

    private static void SetEscapeDestination(Creature creature, State state)
    {
        if (creature.safariControlled) return;
        ArtificialIntelligence ai = creature.abstractCreature?.abstractAI?.RealAI;
        if (ai == null || creature.room == null || !Valid(state.Zone)) return;

        float clearance = BodyClearance(creature);
        float x = state.EscapeDirection < 0
            ? state.Zone.startX - EscapeMargin - clearance
            : state.Zone.endX + EscapeMargin + clearance;
        float y = AverageY(creature) + clearance + 10f;
        ai.SetDestination(creature.room.GetWorldCoordinate(new Vector2(x, y)));
    }

    private static void ApplyEscapeMotion(Creature creature, State state, float danger, float maxImmersion)
    {
        int dir = state.EscapeDirection == 0 ? NearestSide(creature, state.Zone) : state.EscapeDirection;
        state.EscapeDirection = dir;

        float rawX = AverageX(creature) - state.StartX;
        float rawY = AverageY(creature) - state.StartY;
        bool flying = creature.Template != null && creature.Template.canFly;

        if (flying)
        {
            float t = Smooth(maxImmersion);
            float upTravel = Mathf.Lerp(FlySurfaceUp, FlyDeepUp, t);
            Translate(creature, 0f, Mathf.Max(rawY, upTravel) - rawY);

            float minX = Mathf.Lerp(FlyMinX, FlyMaxX, danger);
            float desiredX = dir * Mathf.Clamp(Mathf.Max(rawX * dir, minX), minX, FlyMaxX);
            Translate(creature, desiredX - rawX, 0f);

            float upVelocity = Mathf.Lerp(FlySurfaceUpVelocity, FlyDeepUpVelocity, t);
            RaiseAverageYVelocity(creature, upVelocity);
            RaiseDirectionalXVelocity(creature, dir, FlyXVelocity);
            return;
        }

        float sink = Mathf.Lerp(LandSurfaceSink, LandDeepSink, Smooth(danger));
        Translate(creature, 0f, -sink - rawY);

        float landMinX = Mathf.Lerp(LandMinX, LandMaxX, danger);
        float landX = dir * Mathf.Clamp(Mathf.Max(rawX * dir, landMinX), landMinX, LandMaxX);
        Translate(creature, landX - rawX, 0f);

        CapDownVelocity(creature, sink);
        RaiseDirectionalXVelocity(creature, dir, LandXVelocity);
    }

    private static bool TryMeasure(Creature creature, out QuicksandZone bestZone,
        out float average, out float maximum, out bool fullySubmerged)
    {
        bestZone = null;
        average = maximum = 0f;
        fullySubmerged = false;
        if (!CanControl(creature) || creature.room.updateList == null) return false;

        float bestDanger = 0f;
        for (int i = 0; i < creature.room.updateList.Count; i++)
        {
            if (creature.room.updateList[i] is not QuicksandZone zone || !Valid(zone)) continue;
            if (!MeasureZone(creature, zone, out float avg, out float max, out bool full)) continue;

            float danger = max * 0.65f + avg * 0.35f;
            if (danger <= bestDanger) continue;
            bestDanger = danger;
            bestZone = zone;
            average = avg;
            maximum = max;
            fullySubmerged = full;
        }
        return bestZone != null;
    }

    private static bool MeasureZone(Creature creature, QuicksandZone zone,
        out float average, out float maximum, out bool fullySubmerged)
    {
        average = maximum = 0f;
        fullySubmerged = true;
        float total = 0f;
        int count = 0;
        bool touched = false;
        float bottom = zone.PlacedObject.pos.y - zone.Data.BottomDepth;

        for (int i = 0; i < creature.bodyChunks.Length; i++)
        {
            BodyChunk chunk = creature.bodyChunks[i];
            if (chunk == null)
            {
                fullySubmerged = false;
                continue;
            }

            float rad = Mathf.Max(1f, chunk.rad);
            if (!Sample(zone, chunk.pos, rad, out float surfaceY, out float depth) ||
                chunk.pos.y < bottom - rad)
            {
                fullySubmerged = false;
                continue;
            }

            float immersion = Mathf.Clamp01((depth + rad) / (rad * 2f));
            total += immersion;
            count++;
            maximum = Mathf.Max(maximum, immersion);
            touched |= immersion > EnterImmersion;
            if (chunk.pos.y + rad > surfaceY - FullSubmergeClearance) fullySubmerged = false;
        }

        if (count == 0 || !touched)
        {
            fullySubmerged = false;
            return false;
        }

        average = Mathf.Clamp01(total / count);
        return true;
    }

    private static bool Sample(QuicksandZone zone, Vector2 pos, float rad,
        out float surfaceY, out float depth)
    {
        surfaceY = depth = 0f;
        if (!Valid(zone) || pos.x < zone.startX - rad * 0.25f || pos.x > zone.endX + rad * 0.25f)
            return false;

        float x = Mathf.Clamp(pos.x, zone.startX, zone.endX);
        float u = zone.MaterialUAtWorldX(x);
        if (!zone.Data.IsQuicksand(u) ||
            !zone.TrySampleSurfaceFrame(u, out Vector2 surface, out _, out _, out _))
            return false;

        surfaceY = surface.y;
        depth = surfaceY - pos.y;
        return depth >= -rad * 1.25f;
    }

    private static int NearestSide(Creature creature, QuicksandZone zone)
    {
        float x = AverageX(creature);
        return Mathf.Abs(x - zone.startX) <= Mathf.Abs(zone.endX - x) ? -1 : 1;
    }

    private static void OverrideCollision(Creature creature, State state)
    {
        int count = creature.bodyChunks.Length;
        if (state.OriginalCollision == null || state.OriginalCollision.Length != count)
        {
            state.OriginalCollision = new bool[count];
            state.CollisionOverridden = new bool[count];
        }

        for (int i = 0; i < count; i++)
        {
            BodyChunk chunk = creature.bodyChunks[i];
            if (chunk == null) continue;
            float rad = Mathf.Max(1f, chunk.rad);
            if (!Sample(state.Zone, chunk.pos, rad, out _, out _)) continue;
            state.OriginalCollision[i] = chunk.collideWithTerrain;
            state.CollisionOverridden[i] = true;
            chunk.collideWithTerrain = false;
        }
    }

    private static void RestoreCollision(Room room)
    {
        ForEachCreature(room, creature =>
        {
            if (!States.TryGetValue(creature, out State state) || state.CollisionOverridden == null) return;
            int count = Mathf.Min(creature.bodyChunks.Length, state.CollisionOverridden.Length);
            for (int i = 0; i < count; i++)
            {
                BodyChunk chunk = creature.bodyChunks[i];
                if (chunk != null && state.CollisionOverridden[i])
                    chunk.collideWithTerrain = state.OriginalCollision[i];
                state.CollisionOverridden[i] = false;
            }
        });
    }

    private static void CapDownVelocity(Creature creature, float sink)
    {
        float avg = AverageVelY(creature);
        if (avg < -sink) AddVelocity(creature, 0f, -sink - avg);
    }

    private static void RaiseAverageYVelocity(Creature creature, float target)
    {
        float avg = AverageVelY(creature);
        if (avg < target) AddVelocity(creature, 0f, target - avg);
    }

    private static void RaiseDirectionalXVelocity(Creature creature, int dir, float target)
    {
        float avg = AverageVelX(creature);
        float directional = avg * dir;
        if (directional < target) AddVelocity(creature, dir * (target - directional), 0f);
    }

    private static void Translate(Creature creature, float dx, float dy)
    {
        for (int i = 0; i < creature.bodyChunks.Length; i++)
        {
            BodyChunk chunk = creature.bodyChunks[i];
            if (chunk != null) chunk.pos += new Vector2(dx, dy);
        }
    }

    private static void AddVelocity(Creature creature, float dx, float dy)
    {
        for (int i = 0; i < creature.bodyChunks.Length; i++)
        {
            BodyChunk chunk = creature.bodyChunks[i];
            if (chunk != null) chunk.vel += new Vector2(dx, dy);
        }
    }

    private static float AverageX(Creature c) => Average(c, xAxis: true, velocity: false);
    private static float AverageY(Creature c) => Average(c, xAxis: false, velocity: false);
    private static float AverageVelX(Creature c) => Average(c, xAxis: true, velocity: true);
    private static float AverageVelY(Creature c) => Average(c, xAxis: false, velocity: true);

    private static float Average(Creature creature, bool xAxis, bool velocity)
    {
        float total = 0f;
        int count = 0;
        for (int i = 0; i < creature.bodyChunks.Length; i++)
        {
            BodyChunk chunk = creature.bodyChunks[i];
            if (chunk == null) continue;
            Vector2 value = velocity ? chunk.vel : chunk.pos;
            total += xAxis ? value.x : value.y;
            count++;
        }
        return count > 0 ? total / count : 0f;
    }

    private static float BodyClearance(Creature creature)
    {
        float result = 6f;
        for (int i = 0; i < creature.bodyChunks.Length; i++)
            if (creature.bodyChunks[i] != null) result = Mathf.Max(result, creature.bodyChunks[i].rad);
        return result;
    }

    private static float Smooth(float value)
    {
        float t = Mathf.Clamp01(value);
        return t * t * (3f - 2f * t);
    }

    private static bool CanControl(Creature creature) =>
        creature != null && creature is not Player && !creature.slatedForDeletetion &&
        creature.room != null && creature.bodyChunks != null && creature.bodyChunks.Length > 0;

    private static bool Valid(QuicksandZone zone) =>
        zone != null && !zone.slatedForDeletetion && zone.PlacedObject != null &&
        zone.PlacedObject.active && zone.Data != null;

    private static void ForEachCreature(Room room, System.Action<Creature> action)
    {
        if (room?.physicalObjects == null) return;
        for (int layer = 0; layer < room.physicalObjects.Length; layer++)
        {
            var objects = room.physicalObjects[layer];
            if (objects == null) continue;
            for (int i = 0; i < objects.Count; i++)
                if (objects[i] is Creature creature && creature is not Player) action(creature);
        }
    }
}
