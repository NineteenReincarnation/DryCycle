using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Single room-local traffic policy for BatHive approaches.
///
/// Returning bats still use Rain World's native BatHive Dijkstra maps, but only a small number
/// may occupy the final ingress corridor for each hive at once. Other bats hold at distributed,
/// non-hive shelter points until a slot opens. This prevents weather, travel and injury systems
/// from independently funneling the whole colony into one physical entrance.
/// </summary>
internal static class DB_HiveTraffic
{
    private const int NormalIngressCapacity = 1;
    private const int UrgentIngressCapacity = 2;
    private const int ClaimStaleTicks = 160;
    private const int StallReleaseTicks = 220;
    private const int RetryMinTicks = 70;
    private const int RetryMaxTicks = 150;
    private const float EntranceKeepoutPixels = 150f;
    private const float FallbackHoldMinPixels = 190f;
    private const float FallbackHoldMaxPixels = 310f;

    private sealed class Claim
    {
        internal DB_Creature Bat;
        internal Room Room;
        internal int HiveIndex;
        internal bool Admitted;
        internal int LastSeenTick;
        internal int LastProgressTick;
        internal int RetryAfterTick;
        internal int BestPathDistance = int.MaxValue;
    }

    private sealed class RoomState
    {
        internal readonly List<Claim> Claims = new(12);
    }

    private static ConditionalWeakTable<Room, RoomState> rooms = new();
    private static ConditionalWeakTable<DB_Creature, Claim> claims = new();

    internal static void Reset()
    {
        rooms = new ConditionalWeakTable<Room, RoomState>();
        claims = new ConditionalWeakTable<DB_Creature, Claim>();
    }

    internal static void Forget(DB_Creature bat)
    {
        if (bat == null || !claims.TryGetValue(bat, out Claim claim)) return;
        if (claim.Room != null && rooms.TryGetValue(claim.Room, out RoomState state))
            state.Claims.Remove(claim);
        claims.Remove(bat);
    }

    /// <summary>
    /// Filters a movement request before DB_FlightMotor writes localGoal. The return value tells
    /// the caller whether the requested BatHive corridor was admitted; movement itself is still
    /// allowed when false, but the goal is replaced by a distributed holding point and the hive
    /// Dijkstra map must be cleared for that frame.
    /// </summary>
    internal static bool FilterGoal(
        DB_Creature bat,
        DB_BehaviorOwner owner,
        Vector2 requestedGoal,
        bool preserveDijkstra,
        out Vector2 effectiveGoal,
        out bool keepDijkstra)
    {
        effectiveGoal = requestedGoal;
        keepDijkstra = preserveDijkstra;
        if (bat?.room == null || bat.AI == null || bat.mainBodyChunk == null)
            return true;

        if (!IsHiveIngressOwner(owner))
        {
            Forget(bat);
            return true;
        }

        if (TryFollowedHiveIndex(bat, out int hiveIndex))
        {
            if (TryAdmit(bat, hiveIndex, IsUrgent(bat, owner)))
                return true;

            keepDijkstra = false;
            bat.AI.followingDijkstraMap = -1;
            effectiveGoal = ChooseHoldingPoint(bat, hiveIndex, owner);
            return false;
        }

        // Sandstorm shelter anchors are allowed to describe physically protected geometry, but
        // they must not become an implicit second BatHive queue. If a non-ingress Environment
        // goal lands in the entrance keepout area, redirect it to another protected anchor.
        if (IsEnvironmentOwner(owner) &&
            ShouldKeepEnvironmentalShelterOffEntrance(bat, requestedGoal, out hiveIndex))
        {
            Forget(bat);
            keepDijkstra = false;
            effectiveGoal = ChooseHoldingPoint(bat, hiveIndex, owner);
            return false;
        }

        Forget(bat);
        return true;
    }

    internal static bool IsAdmitted(DB_Creature bat)
        => bat != null && claims.TryGetValue(bat, out Claim claim) && claim.Admitted;

    private static bool IsHiveIngressOwner(DB_BehaviorOwner owner)
        => owner is DB_BehaviorOwner.InjuryRecovery or
                    DB_BehaviorOwner.Travel or
                    DB_BehaviorOwner.EnvironmentHardSurvival or
                    DB_BehaviorOwner.EnvironmentLocalSurvival;

    private static bool IsEnvironmentOwner(DB_BehaviorOwner owner)
        => owner is DB_BehaviorOwner.EnvironmentHardSurvival or
                    DB_BehaviorOwner.EnvironmentLocalSurvival;

    private static bool IsUrgent(DB_Creature bat, DB_BehaviorOwner owner)
    {
        if (owner == DB_BehaviorOwner.InjuryRecovery ||
            owner == DB_BehaviorOwner.EnvironmentHardSurvival)
            return true;
        if (owner == DB_BehaviorOwner.Travel && DB_EnvironmentRuntime.HardSurvival(bat))
            return true;
        return false;
    }

    private static bool TryAdmit(DB_Creature bat, int hiveIndex, bool urgent)
    {
        Room room = bat.room;
        if (!ValidHive(room, hiveIndex))
        {
            Forget(bat);
            return true;
        }

        int tick = Math.Max(0, room.game?.clock ?? 0);
        RoomState state = rooms.GetValue(room, _ => new RoomState());
        Prune(state, room, tick);

        int pathDistance = HivePathDistance(bat, hiveIndex);
        bool alreadyInsideHive = room.GetTile(bat.mainBodyChunk.pos).hive;

        if (claims.TryGetValue(bat, out Claim existing))
        {
            if (!ReferenceEquals(existing.Room, room))
            {
                Forget(bat);
                existing = null;
            }
            else
            {
                existing.LastSeenTick = tick;
                if (existing.HiveIndex != hiveIndex)
                {
                    existing.HiveIndex = hiveIndex;
                    existing.Admitted = false;
                    existing.BestPathDistance = int.MaxValue;
                    existing.LastProgressTick = tick;
                    existing.RetryAfterTick = tick;
                }

                if (existing.Admitted)
                {
                    ObserveProgress(existing, pathDistance, tick);
                    if (alreadyInsideHive) return true;
                    if (pathDistance > 2 && tick - existing.LastProgressTick >= StallReleaseTicks)
                    {
                        existing.Admitted = false;
                        existing.RetryAfterTick = tick + RetryDelay(bat, hiveIndex, tick);
                    }
                    else
                    {
                        return true;
                    }
                }

                if (alreadyInsideHive)
                {
                    existing.Admitted = true;
                    existing.LastProgressTick = tick;
                    existing.BestPathDistance = Math.Max(0, pathDistance);
                    return true;
                }
                if (tick < existing.RetryAfterTick)
                    return false;
            }
        }

        int capacity = urgent ? UrgentIngressCapacity : NormalIngressCapacity;
        int occupied = 0;
        for (int i = 0; i < state.Claims.Count; i++)
        {
            Claim claim = state.Claims[i];
            if (claim.Admitted && claim.HiveIndex == hiveIndex)
                occupied++;
        }

        if (existing == null)
        {
            existing = new Claim
            {
                Bat = bat,
                Room = room,
                HiveIndex = hiveIndex,
                LastSeenTick = tick,
                LastProgressTick = tick,
                RetryAfterTick = tick,
                BestPathDistance = pathDistance < 0 ? int.MaxValue : pathDistance
            };
            state.Claims.Add(existing);
            claims.Add(bat, existing);
        }

        if (occupied >= capacity)
        {
            existing.Admitted = false;
            existing.RetryAfterTick = Math.Max(
                existing.RetryAfterTick,
                tick + RetryDelay(bat, hiveIndex, tick));
            return false;
        }

        existing.Admitted = true;
        existing.LastSeenTick = tick;
        existing.LastProgressTick = tick;
        existing.BestPathDistance = pathDistance < 0 ? int.MaxValue : pathDistance;
        return true;
    }

    private static void ObserveProgress(Claim claim, int pathDistance, int tick)
    {
        if (pathDistance < 0) return;
        if (claim.BestPathDistance == int.MaxValue || pathDistance < claim.BestPathDistance)
        {
            claim.BestPathDistance = pathDistance;
            claim.LastProgressTick = tick;
        }
    }

    private static void Prune(RoomState state, Room room, int tick)
    {
        for (int i = state.Claims.Count - 1; i >= 0; i--)
        {
            Claim claim = state.Claims[i];
            DB_Creature bat = claim.Bat;
            bool stale = bat == null || bat.dead || bat.slatedForDeletetion ||
                         bat.DesertState.InHive || !ReferenceEquals(bat.room, room) ||
                         tick - claim.LastSeenTick > ClaimStaleTicks;
            if (!stale) continue;
            state.Claims.RemoveAt(i);
            if (bat != null) claims.Remove(bat);
        }
    }

    private static int HivePathDistance(DB_Creature bat, int hiveIndex)
    {
        if (bat?.room?.aimap == null || !ValidHive(bat.room, hiveIndex)) return -1;
        int map = bat.room.exitAndDenIndex.Length + hiveIndex;
        return bat.room.aimap.ExitDistanceForCreature(
            bat.room.GetTilePosition(bat.mainBodyChunk.pos),
            map,
            bat.Template);
    }

    private static bool TryFollowedHiveIndex(DB_Creature bat, out int hiveIndex)
    {
        hiveIndex = -1;
        if (bat?.room == null || bat.AI == null || bat.AI.followingDijkstraMap < 0 ||
            bat.room.hives == null || bat.room.hives.Length == 0)
            return false;
        int candidate = bat.AI.followingDijkstraMap - bat.room.exitAndDenIndex.Length;
        if (!ValidHive(bat.room, candidate)) return false;
        hiveIndex = candidate;
        return true;
    }

    private static bool ShouldKeepEnvironmentalShelterOffEntrance(
        DB_Creature bat,
        Vector2 goal,
        out int nearestHive)
    {
        nearestHive = -1;
        if (!DB_EnvironmentRuntime.TryGetInfluence(bat, out DB_EnvironmentInfluence influence) ||
            influence.Weather is not (DB_EnvironmentWeather.Sandstorm or
                DB_EnvironmentWeather.DeathSandstorm) ||
            influence.Phase is DB_EnvironmentPhase.Calm or DB_EnvironmentPhase.Recovery)
            return false;

        float best = EntranceKeepoutPixels * EntranceKeepoutPixels;
        for (int h = 0; h < bat.room.hives.Length; h++)
        {
            IntVector2[] hive = bat.room.hives[h];
            if (hive == null) continue;
            for (int i = 0; i < hive.Length; i++)
            {
                float sq = (bat.room.MiddleOfTile(hive[i]) - goal).sqrMagnitude;
                if (sq >= best) continue;
                best = sq;
                nearestHive = h;
            }
        }
        return nearestHive >= 0;
    }

    private static Vector2 ChooseHoldingPoint(
        DB_Creature bat,
        int hiveIndex,
        DB_BehaviorOwner owner)
    {
        if (TryChooseProtectedHoldingAnchor(bat, hiveIndex, out Vector2 protectedPoint))
            return protectedPoint;

        Room room = bat.room;
        Vector2 center = HiveCenter(room, hiveIndex);
        int seed = bat.Personality?.VisualSeed ?? bat.abstractCreature?.ID.RandomSeed ?? 0;
        int baseOrdinal = StableInt(seed ^ (hiveIndex * 0x45d9f3b), 12);
        float personalRadius = Mathf.Lerp(
            FallbackHoldMinPixels,
            FallbackHoldMaxPixels,
            Stable01(seed ^ 0x63D83595));

        Vector2 best = bat.mainBodyChunk.pos;
        float bestScore = float.NegativeInfinity;
        for (int n = 0; n < 12; n++)
        {
            int ordinal = (baseOrdinal + n * 5) % 12;
            float angle = ordinal * 30f + Stable01(seed ^ (n * 7919)) * 12f - 6f;
            Vector2 dir = new(
                Mathf.Cos(angle * Mathf.Deg2Rad),
                Mathf.Sin(angle * Mathf.Deg2Rad));
            Vector2 candidate = center + dir * personalRadius;
            candidate.x = Mathf.Clamp(candidate.x, 24f, room.PixelWidth - 24f);
            candidate.y = Mathf.Clamp(candidate.y, 24f, room.PixelHeight - 24f);
            if (!UsableHoldingPoint(room, candidate)) continue;
            if (DistanceToAnyHiveSquared(room, candidate) < EntranceKeepoutPixels * EntranceKeepoutPixels)
                continue;

            float score = room.VisualContact(bat.mainBodyChunk.pos, candidate) ? 0.30f : 0f;
            score -= Mathf.Clamp01(Vector2.Distance(bat.mainBodyChunk.pos, candidate) / 700f) * 0.16f;
            score += Stable01(seed ^ (ordinal * 104729) ^ (int)owner * 8191) * 0.08f;
            if (score <= bestScore) continue;
            bestScore = score;
            best = candidate;
        }

        if (bestScore > float.NegativeInfinity)
            return best;

        Vector2 away = bat.mainBodyChunk.pos - center;
        if (away.sqrMagnitude < 0.01f)
        {
            float angle = Stable01(seed ^ 0x27D4EB2D) * 360f;
            away = new Vector2(
                Mathf.Cos(angle * Mathf.Deg2Rad),
                Mathf.Sin(angle * Mathf.Deg2Rad));
        }
        away.Normalize();
        Vector2 fallback = bat.mainBodyChunk.pos + away * 95f + Vector2.up * 25f;
        fallback.x = Mathf.Clamp(fallback.x, 24f, room.PixelWidth - 24f);
        fallback.y = Mathf.Clamp(fallback.y, 24f, room.PixelHeight - 24f);
        return fallback;
    }

    private static bool TryChooseProtectedHoldingAnchor(
        DB_Creature bat,
        int hiveIndex,
        out Vector2 point)
    {
        point = default;
        if (bat?.room == null ||
            !DB_EnvironmentRuntime.TryGetInfluence(bat, out DB_EnvironmentInfluence influence) ||
            influence.Phase is DB_EnvironmentPhase.Calm or DB_EnvironmentPhase.Recovery ||
            !DB_EnvironmentRoomRuntime.TryPeekExisting(
                bat.room, out DB_EnvironmentRoomRuntime.RoomState roomState) ||
            roomState.Anchors.Count == 0)
            return false;

        int seed = bat.Personality?.VisualSeed ?? bat.abstractCreature?.ID.RandomSeed ?? 0;
        float bestScore = float.NegativeInfinity;
        for (int i = 0; i < roomState.Anchors.Count; i++)
        {
            DB_ShelterAnchor anchor = roomState.Anchors[i];
            if (anchor == null || anchor.NearHive ||
                DistanceToAnyHiveSquared(bat.room, anchor.Position) <
                    EntranceKeepoutPixels * EntranceKeepoutPixels)
                continue;

            float quality = DB_EnvironmentRoomRuntime.WeatherQuality(anchor, influence.Weather);
            float crowding = Mathf.Clamp01(
                anchor.Crowding / DB_EnvironmentRoomRuntime.SevereCrowdingPerAnchor);
            float travel = Mathf.Clamp01(
                Vector2.Distance(bat.mainBodyChunk.pos, anchor.Position) / 720f);
            float personal = Stable01(seed ^ (anchor.Id * 0x45d9f3b)) * 0.10f;
            float score = quality * 0.72f - crowding * 0.42f - travel * 0.14f + personal;
            if (score <= bestScore) continue;
            bestScore = score;
            point = anchor.Position;
        }
        return bestScore > float.NegativeInfinity;
    }

    private static bool UsableHoldingPoint(Room room, Vector2 point)
    {
        if (room == null || point.x < 20f || point.y < 20f ||
            point.x > room.PixelWidth - 20f || point.y > room.PixelHeight - 20f)
            return false;
        Room.Tile tile = room.GetTile(point);
        if (tile.Solid || tile.AnyWater || tile.hive || room.PointSubmerged(point))
            return false;
        return room.terrain == null || !room.terrain.Contains(point);
    }

    private static float DistanceToAnyHiveSquared(Room room, Vector2 point)
    {
        if (room?.hives == null) return float.PositiveInfinity;
        float best = float.PositiveInfinity;
        for (int h = 0; h < room.hives.Length; h++)
        {
            IntVector2[] hive = room.hives[h];
            if (hive == null) continue;
            for (int i = 0; i < hive.Length; i++)
                best = Mathf.Min(best, (room.MiddleOfTile(hive[i]) - point).sqrMagnitude);
        }
        return best;
    }

    private static Vector2 HiveCenter(Room room, int hiveIndex)
    {
        if (!ValidHive(room, hiveIndex)) return room?.MiddleOfTile(0, 0) ?? Vector2.zero;
        IntVector2[] tiles = room.hives[hiveIndex];
        Vector2 center = Vector2.zero;
        for (int i = 0; i < tiles.Length; i++)
            center += room.MiddleOfTile(tiles[i]);
        return center / tiles.Length;
    }

    private static bool ValidHive(Room room, int hiveIndex)
        => room?.hives != null && hiveIndex >= 0 && hiveIndex < room.hives.Length &&
           room.hives[hiveIndex] != null && room.hives[hiveIndex].Length > 0;

    private static int RetryDelay(DB_Creature bat, int hiveIndex, int tick)
    {
        int seed = bat.Personality?.VisualSeed ?? bat.abstractCreature?.ID.RandomSeed ?? 0;
        int span = RetryMaxTicks - RetryMinTicks + 1;
        return RetryMinTicks + StableInt(seed ^ (hiveIndex * 7919) ^ (tick / 40 * 104729), span);
    }

    private static int StableInt(int seed, int exclusiveMax)
    {
        if (exclusiveMax <= 1) return 0;
        return Mathf.Clamp((int)(Stable01(seed) * exclusiveMax), 0, exclusiveMax - 1);
    }

    private static float Stable01(int seed)
    {
        unchecked
        {
            uint x = (uint)seed;
            x ^= x >> 16;
            x *= 0x7feb352d;
            x ^= x >> 15;
            x *= 0x846ca68b;
            x ^= x >> 16;
            return (x & 0x00ffffffu) / 16777215f;
        }
    }
}
