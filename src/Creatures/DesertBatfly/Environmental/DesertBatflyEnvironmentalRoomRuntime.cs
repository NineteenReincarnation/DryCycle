using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal static class DesertBatflyEnvironmentalRoomRuntime
{
    internal const int WeatherSampleInterval = 20;
    internal const int CrowdingSampleInterval = 20;
    internal const int MaxAnchors = 8;
    internal const int MinAnchorSeparationTiles = 5;

    internal sealed class RoomState
    {
        internal readonly Room Room;
        internal readonly List<DesertBatflyShelterAnchor> Anchors = new(MaxAnchors);
        internal DesertBatflyEnvironmentalRoomContext Context = DesertBatflyEnvironmentalRoomContext.Calm;
        internal DesertBatflyWeatherEcologySample WeatherSample = DesertBatflyWeatherEcologySample.None;
        internal DesertBatflyEnvironmentalWeather LastWeather = DesertBatflyEnvironmentalWeather.None;
        internal int RecoveryStartTick = -1;
        internal int RecoveryDurationTicks;
        internal int LastWeatherSampleTick = int.MinValue;
        internal int LastCrowdingSampleTick = int.MinValue;
        internal bool AnchorsBuilt;

        internal RoomState(Room room)
        {
            Room = room;
        }
    }

    private sealed class AnchorCandidate
    {
        internal IntVector2 Tile;
        internal DesertBatflyEnvironmentalExposureSample Exposure;
        internal bool Roost;
        internal bool Hive;
        internal float Score;
    }

    private static ConditionalWeakTable<Room, RoomState> states = new();

    internal static void Reset()
    {
        states = new ConditionalWeakTable<Room, RoomState>();
    }

    internal static RoomState For(Room room)
    {
        if (room == null) return null;
        RoomState state = states.GetValue(room, r => new RoomState(r));
        Refresh(state);
        return state;
    }

    internal static void Update(Room room)
    {
        if (room == null) return;
        Refresh(states.GetValue(room, r => new RoomState(r)));
    }

    internal static bool TryGetContext(Room room, out DesertBatflyEnvironmentalRoomContext context)
    {
        RoomState state = For(room);
        if (state == null)
        {
            context = DesertBatflyEnvironmentalRoomContext.Calm;
            return false;
        }
        context = state.Context;
        return true;
    }

    internal static bool TryChooseAnchor(
        DesertBatfly bat,
        DesertBatflyEnvironmentalWeather weather,
        float roostPreference,
        out DesertBatflyShelterAnchor anchor,
        out float score)
    {
        anchor = null;
        score = float.NegativeInfinity;
        if (bat?.room == null) return false;
        RoomState state = For(bat.room);
        if (state == null || state.Anchors.Count == 0) return false;

        string currentColony = DesertBatflyColonyRuntime.RecordFor(bat.abstractCreature, false)?.CurrentColony;
        bool homeRoom = !string.IsNullOrEmpty(currentColony) &&
            string.Equals(currentColony, bat.room.abstractRoom?.name, StringComparison.OrdinalIgnoreCase);
        float injury = 1f - bat.Injury.PhysicalCapability;

        for (int i = 0; i < state.Anchors.Count; i++)
        {
            DesertBatflyShelterAnchor candidate = state.Anchors[i];
            float quality = WeatherQuality(candidate, weather);
            float distance01 = Mathf.Clamp01(Vector2.Distance(
                bat.mainBodyChunk.pos,
                candidate.Position) / 720f);
            float candidateScore = quality;
            candidateScore -= distance01 * Mathf.Lerp(0.24f, 0.42f, injury);
            candidateScore -= Mathf.Clamp01(candidate.Crowding / 5f) * 0.22f;
            candidateScore += candidate.RoostCompatible * Mathf.Clamp01(roostPreference) * 0.11f;
            if (homeRoom && candidate.NearHive) candidateScore += 0.14f;
            candidateScore += StablePreference(bat.Personality.VisualSeed, candidate.Id) * 0.055f;

            if (candidateScore <= score) continue;
            score = candidateScore;
            anchor = candidate;
        }

        return anchor != null;
    }

    internal static float WeatherQuality(
        DesertBatflyShelterAnchor anchor,
        DesertBatflyEnvironmentalWeather weather)
    {
        if (anchor == null) return 0f;
        DesertBatflyEnvironmentalExposureSample e = anchor.Exposure;
        return weather switch
        {
            DesertBatflyEnvironmentalWeather.LightRain => e.RoofShielding * 0.75f + e.Enclosure * 0.25f,
            DesertBatflyEnvironmentalWeather.HeavyRain or DesertBatflyEnvironmentalWeather.DeathRain =>
                e.RoofShielding * 0.72f + e.Enclosure * 0.28f,
            DesertBatflyEnvironmentalWeather.Fog => e.Enclosure * 0.52f + (1f - e.Exposure) * 0.20f,
            DesertBatflyEnvironmentalWeather.DenseFog =>
                e.Enclosure * 0.58f + e.RoofShielding * 0.17f + (anchor.NearHive ? 0.18f : 0f),
            DesertBatflyEnvironmentalWeather.HeatWave or DesertBatflyEnvironmentalWeather.IntenseHeat =>
                e.Shade * 0.68f + e.Enclosure * 0.32f,
            DesertBatflyEnvironmentalWeather.Sandstorm or DesertBatflyEnvironmentalWeather.DeathSandstorm =>
                e.RoofShielding * 0.34f + e.SideShielding * 0.34f + e.Enclosure * 0.32f,
            _ => 1f - e.Exposure
        };
    }

    internal static float RecoveryProgress(RoomState state, int tick)
    {
        if (state == null || state.RecoveryStartTick < 0 || state.RecoveryDurationTicks <= 0) return 1f;
        return Mathf.Clamp01((tick - state.RecoveryStartTick) / (float)state.RecoveryDurationTicks);
    }

    private static void Refresh(RoomState state)
    {
        Room room = state.Room;
        if (room == null) return;
        int tick = room.game?.clock ?? 0;

        if (!state.AnchorsBuilt && room.TileWidth > 2 && room.TileHeight > 2)
            BuildAnchors(state);

        if (state.LastWeatherSampleTick == int.MinValue || tick - state.LastWeatherSampleTick >= WeatherSampleInterval)
        {
            state.LastWeatherSampleTick = tick;
            RefreshWeather(state, tick);
        }

        if (state.LastCrowdingSampleTick == int.MinValue || tick - state.LastCrowdingSampleTick >= CrowdingSampleInterval)
        {
            state.LastCrowdingSampleTick = tick;
            RefreshCrowding(state);
        }
    }

    private static void RefreshWeather(RoomState state, int tick)
    {
        Room room = state.Room;
        DesertBatflyWeatherEcologySample sample = room.world != null && room.abstractRoom != null
            ? DesertBatflyWeatherEcology.Sample(room.world, room.abstractRoom)
            : DesertBatflyWeatherEcologySample.None;
        state.WeatherSample = sample;

        DesertBatflyEnvironmentalWeather weather = DesertBatflyEnvironmentalProfile.Classify(sample);
        DesertBatflyEnvironmentalPhase previous = state.Context.Phase;

        if (weather != DesertBatflyEnvironmentalWeather.None)
        {
            state.LastWeather = weather;
            state.RecoveryStartTick = -1;
            state.RecoveryDurationTicks = 0;
            DesertBatflyEnvironmentalPhase phase = DesertBatflyEnvironmentalProfile.ResolvePhase(
                weather, sample, previous, out string reason);
            state.Context = new DesertBatflyEnvironmentalRoomContext(
                true,
                weather,
                sample.HazardKind,
                sample.HazardId,
                sample.ActiveIntensity,
                sample.ImmediateDanger,
                sample.ShelterUrgency,
                sample.TravelExposure,
                sample.TimeUntilDangerTicks,
                phase,
                reason);
            return;
        }

        if (previous != DesertBatflyEnvironmentalPhase.Calm &&
            previous != DesertBatflyEnvironmentalPhase.Recovery &&
            state.LastWeather != DesertBatflyEnvironmentalWeather.None)
        {
            state.RecoveryStartTick = tick;
            state.RecoveryDurationTicks = RecoveryDuration(state.LastWeather);
        }

        if (state.RecoveryStartTick >= 0 &&
            tick - state.RecoveryStartTick < state.RecoveryDurationTicks)
        {
            state.Context = new DesertBatflyEnvironmentalRoomContext(
                false,
                state.LastWeather,
                sample.HazardKind,
                string.Empty,
                0f, 0f, 0f, 0f, int.MaxValue,
                DesertBatflyEnvironmentalPhase.Recovery,
                "staggered recovery from last authorized DryCycle weather");
            return;
        }

        state.RecoveryStartTick = -1;
        state.RecoveryDurationTicks = 0;
        state.LastWeather = DesertBatflyEnvironmentalWeather.None;
        state.Context = DesertBatflyEnvironmentalRoomContext.Calm;
    }

    private static void BuildAnchors(RoomState state)
    {
        Room room = state.Room;
        state.AnchorsBuilt = true;
        List<AnchorCandidate> candidates = new(96);
        const int stride = 3;

        for (int y = 2; y < room.TileHeight - 2; y += stride)
        for (int x = 2; x < room.TileWidth - 2; x += stride)
        {
            IntVector2 tile = new(x, y);
            Room.Tile current = room.GetTile(tile);
            if (current.Solid || current.AnyWater) continue;

            DesertBatflyEnvironmentalExposureSample exposure =
                DesertBatflyEnvironmentalExposure.Sample(room, tile, 1f);
            bool roost = DesertBatflyEnvironmentalExposure.RoostCompatibilityHint(room, tile);
            bool hive = DesertBatflyEnvironmentalExposure.NearHive(room, tile);
            float score = 1f - exposure.Exposure;
            score += exposure.Enclosure * 0.22f;
            if (roost) score += 0.08f;
            if (hive) score += 0.10f;
            if (score < 0.18f) continue;

            candidates.Add(new AnchorCandidate
            {
                Tile = tile,
                Exposure = exposure,
                Roost = roost,
                Hive = hive,
                Score = score
            });
        }

        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        int separationSq = MinAnchorSeparationTiles * MinAnchorSeparationTiles;
        for (int i = 0; i < candidates.Count && state.Anchors.Count < MaxAnchors; i++)
        {
            AnchorCandidate candidate = candidates[i];
            bool tooClose = false;
            for (int n = 0; n < state.Anchors.Count; n++)
            {
                IntVector2 other = state.Anchors[n].Tile;
                int dx = candidate.Tile.x - other.x;
                int dy = candidate.Tile.y - other.y;
                if (dx * dx + dy * dy < separationSq)
                {
                    tooClose = true;
                    break;
                }
            }
            if (tooClose) continue;

            state.Anchors.Add(new DesertBatflyShelterAnchor(
                state.Anchors.Count + 1,
                candidate.Tile,
                room.MiddleOfTile(candidate.Tile),
                candidate.Exposure,
                candidate.Roost,
                candidate.Hive));
        }
    }

    private static void RefreshCrowding(RoomState state)
    {
        for (int i = 0; i < state.Anchors.Count; i++) state.Anchors[i].Crowding = 0f;
        if (state.Anchors.Count == 0) return;

        Room room = state.Room;
        var flies = DesertSwarmRoom.For(room).Hive.flies;
        for (int i = 0; i < flies.Count; i++)
        {
            if (flies[i] is not DesertBatfly bat || bat.dead || bat.slatedForDeletetion || bat.room != room)
                continue;
            for (int n = 0; n < state.Anchors.Count; n++)
            {
                if (Vector2.Distance(bat.mainBodyChunk.pos, state.Anchors[n].Position) <= 105f)
                    state.Anchors[n].Crowding += 1f;
            }
        }
    }

    private static int RecoveryDuration(DesertBatflyEnvironmentalWeather weather)
        => weather switch
        {
            DesertBatflyEnvironmentalWeather.LightRain => 140,
            DesertBatflyEnvironmentalWeather.Fog => 240,
            DesertBatflyEnvironmentalWeather.DenseFog => 440,
            DesertBatflyEnvironmentalWeather.HeatWave => 360,
            DesertBatflyEnvironmentalWeather.IntenseHeat => 680,
            DesertBatflyEnvironmentalWeather.Sandstorm => 520,
            DesertBatflyEnvironmentalWeather.DeathSandstorm => 760,
            DesertBatflyEnvironmentalWeather.HeavyRain => 420,
            DesertBatflyEnvironmentalWeather.DeathRain => 720,
            _ => 260
        };

    private static float StablePreference(int seed, int anchorId)
    {
        unchecked
        {
            uint x = (uint)(seed * 1103515245 + anchorId * 0x45d9f3b);
            x ^= x >> 16;
            x *= 0x7feb352d;
            x ^= x >> 15;
            return (x & 0x00ffffffu) / 16777215f;
        }
    }
}
