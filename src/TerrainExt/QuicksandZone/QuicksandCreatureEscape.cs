using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

/// <summary>
/// Post-entry quicksand behavior for non-player creatures.
///
/// Escape direction and destination remain AI-owned, while physical escape uses a
/// curve-aware virtual soft floor instead of translating the whole creature. Each
/// immersed BodyChunk temporarily ignores the authored solid terrain, then receives
/// a local floor contact against a support surface that slowly sinks below the real
/// quicksand curve. Native creature locomotion therefore remains responsible for
/// turning, body pose, legs and flight.
/// </summary>
internal static class QuicksandCreatureEscape
{
    private const float EnterImmersion = 0.01f;
    private const float ExitImmersion = 0.005f;
    private const float PanicImmersion = 0.65f;
    private const float FullSubmergeClearance = 1.5f;
    private const float EscapeMargin = 40f;
    private const float FlyingEscapeHeight = 80f;
    private const int ExitConfirmTicks = 8;
    private const int DeathConfirmTicks = 10;
    private const int DeadCleanupTicks = 30;
    private const int StallSwitchTicks = 90;

    // The support surface itself sinks. Land creatures get a noticeably faster
    // collapse than flyers; flyers are intentionally much easier to lift clear.
    private const float LandSurfaceSinkRate = 0.050f;
    private const float LandDeepSinkRate = 0.030f;
    private const float FlySurfaceSinkRate = 0.012f;
    private const float FlyDeepSinkRate = 0.022f;

    private const float ContactTolerance = 2.5f;
    private const float SupportInfluenceRadii = 1.35f;
    private const float SupportSideMarginRadii = 0.50f;
    private const float MaximumCorrectionPerTick = 3.0f;
    private const float MinimumLandSupport = 0.72f;
    private const float MinimumFlySupport = 0.88f;

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
        internal float LastEscapeDistance;
        internal float SupportDepth;
        internal float SupportSinkRate;
        internal float CurrentDanger;
        internal float CurrentMaxImmersion;
    }

    private static readonly ConditionalWeakTable<Creature, State> States = new();
    private static bool _enabled;

    internal static bool IsEscaping(Creature creature)
    {
        return creature != null &&
               !QuicksandDrillCrabCompatibility.IsDrillCrab(creature) &&
               States.TryGetValue(creature, out State state) &&
               state.Active;
    }

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.Room.Update += Room_Update;
        On.BodyChunk.Update += BodyChunk_Update;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;
        On.Room.Update -= Room_Update;
        On.BodyChunk.Update -= BodyChunk_Update;
    }

    private static void Room_Update(On.Room.orig_Update orig, Room room)
    {
        BeforeRoomUpdate(room);
        orig(room);
        AfterRoomUpdate(room);
    }

    private static void BeforeRoomUpdate(Room room)
    {
        ForEachCreature(room, creature =>
        {
            if (!CanControl(creature))
            {
                return;
            }

            State state = States.GetValue(creature, _ => new State());
            if (!state.Active &&
                TryMeasure(creature, out QuicksandZone zone, out float average,
                    out float maximum, out _) &&
                maximum > EnterImmersion)
            {
                Activate(creature, state, zone, average, maximum);
            }

            if (state.Active && Valid(state.Zone))
            {
                SetEscapeDestination(creature, state);
            }
        });
    }

    private static void AfterRoomUpdate(Room room)
    {
        ForEachCreature(room, creature =>
        {
            if (!CanControl(creature))
            {
                return;
            }

            State state = States.GetValue(creature, _ => new State());
            if (!TryMeasure(creature, out QuicksandZone zone, out float average,
                    out float maximum, out bool fullySubmerged))
            {
                if (state.Active && ++state.ExitTicks >= ExitConfirmTicks)
                {
                    Deactivate(state);
                }
                return;
            }

            if (!state.Active && maximum > EnterImmersion)
            {
                Activate(creature, state, zone, average, maximum);
            }
            if (!state.Active)
            {
                return;
            }

            if (state.Zone != zone)
            {
                state.Zone = zone;
                state.SupportDepth = InitialSupportDepth(creature, zone);
                state.LastEscapeDistance = EscapeDistance(creature, state);
            }

            state.Zone = zone;
            state.CurrentDanger = Mathf.Clamp01(maximum * 0.65f + average * 0.35f);
            state.CurrentMaxImmersion = maximum;
            SetEscapeDestination(creature, state);

            if (maximum <= ExitImmersion)
            {
                if (++state.ExitTicks >= ExitConfirmTicks)
                {
                    Deactivate(state);
                }
                return;
            }
            state.ExitTicks = 0;

            UpdateProgress(creature, state, state.CurrentDanger);
            AdvanceSupportFloor(creature, state);

            if (!creature.dead &&
                maximum >= PanicImmersion &&
                !state.ReleasedGrasps)
            {
                creature.LoseAllGrasps();
                state.ReleasedGrasps = true;
            }

            UpdateDeath(creature, state, fullySubmerged);
        });
    }

    private static void BodyChunk_Update(On.BodyChunk.orig_Update orig, BodyChunk self)
    {
        Creature creature = self?.owner as Creature;
        if (!CanControl(creature) ||
            !States.TryGetValue(creature, out State state) ||
            !state.Active ||
            !Valid(state.Zone))
        {
            orig(self);
            return;
        }

        float radius = Mathf.Max(1f, self.rad);
        bool inInfluence = WithinSupportInfluence(state.Zone, self.pos, radius) ||
                           WithinSupportInfluence(state.Zone, self.pos + self.vel, radius);

        bool originalCollision = self.collideWithTerrain;
        if (inInfluence)
        {
            // The authored room floor must not hold the creature at the top of the
            // quicksand. Native BodyChunk physics runs without solid terrain here;
            // the local soft floor is applied immediately afterwards.
            self.collideWithTerrain = false;
        }

        try
        {
            orig(self);
        }
        finally
        {
            if (inInfluence)
            {
                self.collideWithTerrain = originalCollision;
            }
        }

        if (!inInfluence)
        {
            return;
        }

        ApplyVirtualFloor(self, creature, state);
    }

    private static void ApplyVirtualFloor(BodyChunk chunk, Creature creature, State state)
    {
        float radius = Mathf.Max(1f, chunk.rad);
        if (!TrySampleSurface(state.Zone, chunk.pos, radius, out float surfaceY, out _))
        {
            return;
        }

        float virtualFloorY = surfaceY - state.SupportDepth;
        float chunkBottomY = chunk.pos.y - radius;
        float penetration = virtualFloorY - chunkBottomY;
        float contactWindow = Mathf.Max(ContactTolerance, radius * 0.30f);

        if (penetration < -contactWindow)
        {
            // The creature has lifted clearly above the sinking support. In
            // particular, flyers are free to use their native upward locomotion.
            return;
        }

        bool flying = creature.Template != null && creature.Template.canFly;
        float minimumSupport = flying ? MinimumFlySupport : MinimumLandSupport;
        float supportStrength = Mathf.Lerp(1f, minimumSupport, Smooth(state.CurrentDanger));

        if (penetration > 0f)
        {
            float correction = Mathf.Min(penetration, MaximumCorrectionPerTick) * supportStrength;
            chunk.pos.y += correction;
        }

        // This contact survives until the chunk's next native update, so creature-
        // specific locomotion (notably Lizard locomotion) can observe a real-looking
        // floor contact during its next decision/animation pass.
        chunk.contactPoint.y = -1;

        // A descending floor carries supported chunks downward, but never cancels
        // native upward movement. This is what lets wings/jumps escape naturally.
        if (chunk.vel.y < -state.SupportSinkRate)
        {
            chunk.vel.y = -state.SupportSinkRate;
        }
    }

    private static void AdvanceSupportFloor(Creature creature, State state)
    {
        bool flying = creature.Template != null && creature.Template.canFly;
        float t = Smooth(state.CurrentDanger);
        state.SupportSinkRate = flying
            ? Mathf.Lerp(FlySurfaceSinkRate, FlyDeepSinkRate, t)
            : Mathf.Lerp(LandSurfaceSinkRate, LandDeepSinkRate, t);
        state.SupportDepth += state.SupportSinkRate;
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

    private static void Activate(Creature creature, State state, QuicksandZone zone,
        float average, float maximum)
    {
        state.Active = true;
        state.Zone = zone;
        state.EscapeDirection = NearestSide(creature, zone);
        state.ExitTicks = 0;
        state.FullySubmergedTicks = 0;
        state.DeadSubmergedTicks = 0;
        state.StallTicks = 0;
        state.ReleasedGrasps = false;
        state.CurrentDanger = Mathf.Clamp01(maximum * 0.65f + average * 0.35f);
        state.CurrentMaxImmersion = maximum;
        state.LastDanger = state.CurrentDanger;
        state.SupportDepth = InitialSupportDepth(creature, zone);
        state.SupportSinkRate = creature.Template != null && creature.Template.canFly
            ? FlySurfaceSinkRate
            : LandSurfaceSinkRate;
        state.LastEscapeDistance = EscapeDistance(creature, state);
    }

    private static void Deactivate(State state)
    {
        if (state == null)
        {
            return;
        }

        state.Active = false;
        state.Zone = null;
        state.EscapeDirection = 0;
        state.ExitTicks = 0;
        state.FullySubmergedTicks = 0;
        state.DeadSubmergedTicks = 0;
        state.StallTicks = 0;
        state.ReleasedGrasps = false;
        state.LastDanger = 0f;
        state.LastEscapeDistance = 0f;
        state.SupportDepth = 0f;
        state.SupportSinkRate = 0f;
        state.CurrentDanger = 0f;
        state.CurrentMaxImmersion = 0f;
    }

    private static void UpdateProgress(Creature creature, State state, float danger)
    {
        float distance = EscapeDistance(creature, state);
        bool outwardProgress = distance < state.LastEscapeDistance - 0.50f ||
                               danger < state.LastDanger - 0.02f;

        state.StallTicks = outwardProgress ? 0 : state.StallTicks + 1;
        state.LastEscapeDistance = distance;
        state.LastDanger = danger;

        if (state.StallTicks < StallSwitchTicks)
        {
            return;
        }

        // A chosen side stays stable while the creature is genuinely progressing.
        // Only a prolonged stall flips it, avoiding the old left/right oscillation.
        state.EscapeDirection = state.EscapeDirection == 0
            ? NearestSide(creature, state.Zone)
            : -state.EscapeDirection;
        state.StallTicks = 0;
        state.LastEscapeDistance = EscapeDistance(creature, state);
    }

    private static void SetEscapeDestination(Creature creature, State state)
    {
        if (creature.safariControlled)
        {
            return;
        }

        ArtificialIntelligence ai = creature.abstractCreature?.abstractAI?.RealAI;
        if (ai == null || creature.room == null || !Valid(state.Zone))
        {
            return;
        }

        float clearance = BodyClearance(creature);
        int direction = state.EscapeDirection == 0 ? NearestSide(creature, state.Zone) : state.EscapeDirection;
        state.EscapeDirection = direction;

        float edgeX = direction < 0 ? state.Zone.startX : state.Zone.endX;
        float targetX = direction < 0
            ? edgeX - EscapeMargin - clearance
            : edgeX + EscapeMargin + clearance;

        float edgeSurfaceY = AverageY(creature);
        TrySampleSurfaceAtX(state.Zone, edgeX, out edgeSurfaceY);

        bool flying = creature.Template != null && creature.Template.canFly;
        float targetY = flying
            ? Mathf.Max(edgeSurfaceY + clearance + FlyingEscapeHeight,
                AverageY(creature) + FlyingEscapeHeight * 0.75f)
            : edgeSurfaceY + clearance + 8f;

        ai.SetDestination(creature.room.GetWorldCoordinate(new Vector2(targetX, targetY)));
    }

    private static float InitialSupportDepth(Creature creature, QuicksandZone zone)
    {
        float deepestBottomPenetration = 0f;
        for (int i = 0; i < creature.bodyChunks.Length; i++)
        {
            BodyChunk chunk = creature.bodyChunks[i];
            if (chunk == null)
            {
                continue;
            }

            float radius = Mathf.Max(1f, chunk.rad);
            if (!TrySampleSurface(zone, chunk.pos, radius, out float surfaceY, out _))
            {
                continue;
            }

            deepestBottomPenetration = Mathf.Max(
                deepestBottomPenetration,
                surfaceY - (chunk.pos.y - radius));
        }

        // Starting the virtual floor at the deepest current contact prevents a large
        // one-frame upward snap when a falling creature first enters quicksand.
        return Mathf.Max(0f, deepestBottomPenetration);
    }

    private static float EscapeDistance(Creature creature, State state)
    {
        if (!Valid(state.Zone))
        {
            return 0f;
        }

        float x = AverageX(creature);
        if (state.EscapeDirection < 0)
        {
            return Mathf.Max(0f, x - state.Zone.startX);
        }
        return Mathf.Max(0f, state.Zone.endX - x);
    }

    private static bool TryMeasure(Creature creature, out QuicksandZone bestZone,
        out float average, out float maximum, out bool fullySubmerged)
    {
        bestZone = null;
        average = 0f;
        maximum = 0f;
        fullySubmerged = false;
        if (!CanControl(creature) || creature.room.updateList == null)
        {
            return false;
        }

        float bestDanger = 0f;
        for (int i = 0; i < creature.room.updateList.Count; i++)
        {
            if (creature.room.updateList[i] is not QuicksandZone zone || !Valid(zone))
            {
                continue;
            }
            if (!MeasureZone(creature, zone, out float zoneAverage,
                    out float zoneMaximum, out bool zoneFullySubmerged))
            {
                continue;
            }

            float danger = zoneMaximum * 0.65f + zoneAverage * 0.35f;
            if (danger <= bestDanger)
            {
                continue;
            }

            bestDanger = danger;
            bestZone = zone;
            average = zoneAverage;
            maximum = zoneMaximum;
            fullySubmerged = zoneFullySubmerged;
        }

        return bestZone != null;
    }

    private static bool MeasureZone(Creature creature, QuicksandZone zone,
        out float average, out float maximum, out bool fullySubmerged)
    {
        average = 0f;
        maximum = 0f;
        fullySubmerged = true;
        float total = 0f;
        int count = 0;
        bool touched = false;

        for (int i = 0; i < creature.bodyChunks.Length; i++)
        {
            BodyChunk chunk = creature.bodyChunks[i];
            if (chunk == null)
            {
                fullySubmerged = false;
                continue;
            }

            float radius = Mathf.Max(1f, chunk.rad);
            if (!TrySampleSurface(zone, chunk.pos, radius, out float surfaceY, out float depth))
            {
                fullySubmerged = false;
                continue;
            }

            float immersion = Mathf.Clamp01((depth + radius) / (radius * 2f));
            total += immersion;
            count++;
            maximum = Mathf.Max(maximum, immersion);
            touched |= immersion > EnterImmersion;

            // Creature death follows the player-style rule: every physical chunk has
            // to be completely under its own local curved surface for several ticks.
            if (chunk.pos.y + radius > surfaceY - FullSubmergeClearance)
            {
                fullySubmerged = false;
            }
        }

        if (count == 0 || !touched)
        {
            fullySubmerged = false;
            return false;
        }

        average = Mathf.Clamp01(total / count);
        return true;
    }

    private static bool WithinSupportInfluence(QuicksandZone zone, Vector2 position, float radius)
    {
        if (!TrySampleSurface(zone, position, radius, out float surfaceY, out float depth))
        {
            return false;
        }

        float bottomY = zone.PlacedObject.pos.y - zone.Data.BottomDepth;
        return depth >= -radius * SupportInfluenceRadii &&
               position.y >= bottomY - radius;
    }

    private static bool TrySampleSurface(QuicksandZone zone, Vector2 position, float radius,
        out float surfaceY, out float depth)
    {
        surfaceY = 0f;
        depth = 0f;
        if (!Valid(zone) ||
            position.x < zone.startX - radius * SupportSideMarginRadii ||
            position.x > zone.endX + radius * SupportSideMarginRadii)
        {
            return false;
        }

        float x = Mathf.Clamp(position.x, zone.startX, zone.endX);
        if (!TrySampleSurfaceAtX(zone, x, out surfaceY))
        {
            return false;
        }

        depth = surfaceY - position.y;
        return depth >= -radius * SupportInfluenceRadii;
    }

    private static bool TrySampleSurfaceAtX(QuicksandZone zone, float x, out float surfaceY)
    {
        surfaceY = 0f;
        if (!Valid(zone))
        {
            return false;
        }

        float clampedX = Mathf.Clamp(x, zone.startX, zone.endX);
        float u = zone.MaterialUAtWorldX(clampedX);
        if (!zone.Data.IsQuicksand(u) ||
            !zone.TrySampleSurfaceFrame(u, out Vector2 surface, out _, out _, out _))
        {
            return false;
        }

        surfaceY = surface.y;
        return true;
    }

    private static int NearestSide(Creature creature, QuicksandZone zone)
    {
        float x = AverageX(creature);
        return Mathf.Abs(x - zone.startX) <= Mathf.Abs(zone.endX - x) ? -1 : 1;
    }

    private static float AverageX(Creature creature)
    {
        float total = 0f;
        int count = 0;
        for (int i = 0; i < creature.bodyChunks.Length; i++)
        {
            BodyChunk chunk = creature.bodyChunks[i];
            if (chunk == null)
            {
                continue;
            }
            total += chunk.pos.x;
            count++;
        }
        return count > 0 ? total / count : 0f;
    }

    private static float AverageY(Creature creature)
    {
        float total = 0f;
        int count = 0;
        for (int i = 0; i < creature.bodyChunks.Length; i++)
        {
            BodyChunk chunk = creature.bodyChunks[i];
            if (chunk == null)
            {
                continue;
            }
            total += chunk.pos.y;
            count++;
        }
        return count > 0 ? total / count : 0f;
    }

    private static float BodyClearance(Creature creature)
    {
        float result = 6f;
        for (int i = 0; i < creature.bodyChunks.Length; i++)
        {
            if (creature.bodyChunks[i] != null)
            {
                result = Mathf.Max(result, creature.bodyChunks[i].rad);
            }
        }
        return result;
    }

    private static float Smooth(float value)
    {
        float t = Mathf.Clamp01(value);
        return t * t * (3f - 2f * t);
    }

    private static bool CanControl(Creature creature) =>
        creature != null &&
        creature is not Player &&
        !QuicksandDrillCrabCompatibility.IsDrillCrab(creature) &&
        !creature.slatedForDeletetion &&
        creature.room != null &&
        creature.bodyChunks != null &&
        creature.bodyChunks.Length > 0;

    private static bool Valid(QuicksandZone zone) =>
        zone != null &&
        !zone.slatedForDeletetion &&
        zone.PlacedObject != null &&
        zone.PlacedObject.active &&
        zone.Data != null;

    private static void ForEachCreature(Room room, System.Action<Creature> action)
    {
        if (room?.physicalObjects == null)
        {
            return;
        }

        for (int layer = 0; layer < room.physicalObjects.Length; layer++)
        {
            var objects = room.physicalObjects[layer];
            if (objects == null)
            {
                continue;
            }

            for (int i = 0; i < objects.Count; i++)
            {
                if (objects[i] is Creature creature && creature is not Player)
                {
                    action(creature);
                }
            }
        }
    }
}
