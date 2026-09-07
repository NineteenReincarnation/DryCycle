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
    internal const int AdvisoryMinimumHoldTicks = 80;
    internal const int PreparationMinimumHoldTicks = 120;
    internal const int ShelteringMinimumHoldTicks = 180;
    internal const int AcuteMinimumHoldTicks = 120;
    internal const int ShelterFailureMinTicks = 360;
    internal const int ShelterFailureReportCooldownTicks = 1800;
    internal const float MinimumUsableAnchorQuality = 0.44f;
    internal const float SevereCrowdingPerAnchor = 5f;

    internal sealed class RoomState
    {
        internal readonly Room Room;
        internal readonly List<DesertBatflyShelterAnchor> Anchors = new(MaxAnchors);
        internal DesertBatflyEnvironmentalRoomContext Context = DesertBatflyEnvironmentalRoomContext.Calm;
        internal DesertBatflyWeatherEcologySample WeatherSample = DesertBatflyWeatherEcologySample.None;
        internal DesertBatflyTask13WeatherAxesSample WeatherAxes = DesertBatflyTask13WeatherAxesSample.None;
        internal DesertBatflyEnvironmentalWeather LastWeather = DesertBatflyEnvironmentalWeather.None;
        internal int RecoveryStartTick = -1;
        internal int RecoveryDurationTicks;
        internal int LastWeatherSampleTick = int.MinValue;
        internal int LastCrowdingSampleTick = int.MinValue;
        internal int PhaseStartTick = int.MinValue;
        internal bool AnchorsBuilt;
        internal int ShelterFailureLastTick = int.MinValue;
        internal int ShelterFailureAccumulatedTicks;
        internal int ShelterFailureLastReportTick = int.MinValue;
        internal string ShelterFailureReason = string.Empty;
        internal float ShelterFailureSeverity;

        internal RoomState(Room room)
        {
            Room = room;
        }

        internal int PhaseTicks(int tick)
            => PhaseStartTick == int.MinValue ? 0 : Mathf.Max(0, tick - PhaseStartTick);
    }

    private sealed class AnchorCandidate
    {
        internal IntVector2 Tile;
        internal DB_EnvironmentExposureSample Exposure;
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
        RoomState state = states.GetValue(room, r => new RoomState(r));
        Refresh(state);
        ObserveLocalShelterFailure(state);
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

    internal static bool TryGetShelterFailureDebug(
        Room room,
        out int accumulatedTicks,
        out float severity,
        out string reason)
    {
        accumulatedTicks = 0;
        severity = 0f;
        reason = string.Empty;
        if (room == null || !states.TryGetValue(room, out RoomState state)) return false;
        accumulatedTicks = state.ShelterFailureAccumulatedTicks;
        severity = state.ShelterFailureSeverity;
        reason = state.ShelterFailureReason;
        return accumulatedTicks > 0 || severity > 0f;
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

        string currentColony = DB_ColonyRuntime.RecordFor(bat.abstractCreature, false)?.CurrentColony;
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
            if (candidate.RoostCompatible)
                candidateScore += Mathf.Clamp01(roostPreference) * 0.11f;
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
        DB_EnvironmentExposureSample e = anchor.Exposure;
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


    private static void ObserveLocalShelterFailure(RoomState state)
    {
        Room room = state?.Room;
        if (room?.abstractRoom == null || room.world == null || room.game == null) return;

        int tick = room.game.clock;
        int elapsed = state.ShelterFailureLastTick == int.MinValue
            ? 1
            : Mathf.Clamp(tick - state.ShelterFailureLastTick, 1, 60);
        state.ShelterFailureLastTick = tick;

        DesertBatflyEnvironmentalRoomContext context = state.Context;
        if (!SeriousShelterFailureWeather(context))
        {
            state.ShelterFailureAccumulatedTicks = Mathf.Max(
                0, state.ShelterFailureAccumulatedTicks - elapsed * 3);
            state.ShelterFailureSeverity = 0f;
            state.ShelterFailureReason = "no serious active Task13 shelter demand";
            return;
        }

        float bestQuality = 0f;
        bool anyUsable = false;
        bool allCrowded = state.Anchors.Count > 0;
        for (int i = 0; i < state.Anchors.Count; i++)
        {
            DesertBatflyShelterAnchor anchor = state.Anchors[i];
            float quality = WeatherQuality(anchor, context.Weather);
            bestQuality = Mathf.Max(bestQuality, quality);
            if (quality >= MinimumUsableAnchorQuality && anchor.Crowding < SevereCrowdingPerAnchor)
                anyUsable = true;
            if (anchor.Crowding < SevereCrowdingPerAnchor)
                allCrowded = false;
        }

        bool noAnchors = state.Anchors.Count == 0;
        bool badQuality = bestQuality < MinimumUsableAnchorQuality;
        bool failing = noAnchors || !anyUsable || allCrowded;
        if (!failing)
        {
            state.ShelterFailureAccumulatedTicks = Mathf.Max(
                0, state.ShelterFailureAccumulatedTicks - elapsed * 2);
            state.ShelterFailureSeverity = 0f;
            state.ShelterFailureReason = "usable realized shelter anchor available";
            return;
        }

        float severity = noAnchors
            ? 0.72f
            : allCrowded
                ? 0.55f
                : Mathf.Clamp01(0.48f + (MinimumUsableAnchorQuality - bestQuality));
        severity *= Mathf.Lerp(0.72f, 1f,
            Mathf.Max(context.ActiveIntensity, context.ImmediateDanger));
        state.ShelterFailureSeverity = Mathf.Clamp01(severity);
        state.ShelterFailureReason = noAnchors
            ? "serious weather: no realized shelter anchors"
            : allCrowded
                ? "serious weather: all usable shelter anchors overcrowded"
                : badQuality
                    ? "serious weather: available anchors have inadequate weather protection"
                    : "serious weather: no sufficiently protected uncrowded anchor";
        state.ShelterFailureAccumulatedTicks = Mathf.Min(
            ShelterFailureMinTicks * 2,
            state.ShelterFailureAccumulatedTicks + elapsed);

        if (state.ShelterFailureAccumulatedTicks < ShelterFailureMinTicks) return;
        if (state.ShelterFailureLastReportTick != int.MinValue &&
            tick - state.ShelterFailureLastReportTick < ShelterFailureReportCooldownTicks)
            return;

        DB_ColonyState colony = DB_ColonyRuntime.TryGetColony(room.abstractRoom);
        if (colony == null) return;
        DB_ColonyRuntime.ReportExternalRefuge(
            colony.RoomName,
            null,
            Mathf.Clamp(state.ShelterFailureSeverity, 0.12f, 0.80f));
        state.ShelterFailureLastReportTick = tick;
        state.ShelterFailureAccumulatedTicks = 0;
    }

    private static bool SeriousShelterFailureWeather(
        in DesertBatflyEnvironmentalRoomContext context)
    {
        if (!context.WeatherSourceValid) return false;
        if (context.Weather is DesertBatflyEnvironmentalWeather.LightRain or
            DesertBatflyEnvironmentalWeather.Fog)
            return false;
        return context.Phase is DesertBatflyEnvironmentalPhase.Sheltering or
                   DesertBatflyEnvironmentalPhase.Acute ||
               (context.Phase == DesertBatflyEnvironmentalPhase.Preparation &&
                context.ShelterUrgency >= 0.68f);
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
        state.WeatherAxes = room.world != null && room.abstractRoom != null
            ? DesertBatflyWeatherEcology.SampleTask13Axes(room.world, room.abstractRoom)
            : DesertBatflyTask13WeatherAxesSample.None;

        DesertBatflyEnvironmentalWeather weather = DB_EnvironmentProfile.Classify(sample);
        DesertBatflyWeatherEcologySample phaseSample = sample;

        if (weather == DesertBatflyEnvironmentalWeather.LightRain && state.WeatherAxes.DenseFogIntensity > 0f)
        {
            weather = DesertBatflyEnvironmentalWeather.DenseFog;
            phaseSample = Reprofile(sample, "DENSEFOG", state.WeatherAxes.DenseFogIntensity);
        }
        else if (weather == DesertBatflyEnvironmentalWeather.LightRain && state.WeatherAxes.FogIntensity > 0f)
        {
            weather = DesertBatflyEnvironmentalWeather.Fog;
            phaseSample = Reprofile(sample, "FOG", state.WeatherAxes.FogIntensity);
        }

        DesertBatflyEnvironmentalPhase previous = state.Context.Phase;

        if (weather != DesertBatflyEnvironmentalWeather.None)
        {
            state.LastWeather = weather;
            state.RecoveryStartTick = -1;
            state.RecoveryDurationTicks = 0;
            DesertBatflyEnvironmentalPhase candidate = DB_EnvironmentProfile.ResolvePhase(
                weather, phaseSample, previous, out string reason);
            DesertBatflyEnvironmentalPhase phase = ApplyPhaseHold(state, candidate, tick, ref reason);
            SetContext(state, new DesertBatflyEnvironmentalRoomContext(
                true,
                weather,
                phaseSample.HazardKind,
                phaseSample.HazardId,
                phaseSample.ActiveIntensity,
                phaseSample.ImmediateDanger,
                phaseSample.ShelterUrgency,
                phaseSample.TravelExposure,
                phaseSample.TimeUntilDangerTicks,
                phase,
                reason), tick);
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
            SetContext(state, new DesertBatflyEnvironmentalRoomContext(
                false,
                state.LastWeather,
                sample.HazardKind,
                string.Empty,
                0f, 0f, 0f, 0f, int.MaxValue,
                DesertBatflyEnvironmentalPhase.Recovery,
                "staggered recovery from last authorized DryCycle weather"), tick);
            return;
        }

        state.RecoveryStartTick = -1;
        state.RecoveryDurationTicks = 0;
        state.LastWeather = DesertBatflyEnvironmentalWeather.None;
        SetContext(state, DesertBatflyEnvironmentalRoomContext.Calm, tick);
    }

    private static DesertBatflyEnvironmentalPhase ApplyPhaseHold(
        RoomState state,
        DesertBatflyEnvironmentalPhase candidate,
        int tick,
        ref string reason)
    {
        DesertBatflyEnvironmentalPhase current = state.Context.Phase;
        if (candidate == current) return current;
        if (candidate == DesertBatflyEnvironmentalPhase.Acute ||
            PhaseRank(candidate) > PhaseRank(current))
            return candidate;
        if (current is DesertBatflyEnvironmentalPhase.Calm or DesertBatflyEnvironmentalPhase.Recovery)
            return candidate;

        int hold = MinimumHoldTicks(current);
        int elapsed = state.PhaseTicks(tick);
        if (elapsed >= hold) return candidate;
        reason += $"; holding {current} for hysteresis ({elapsed}/{hold})";
        return current;
    }

    private static void SetContext(
        RoomState state,
        in DesertBatflyEnvironmentalRoomContext context,
        int tick)
    {
        if (state.Context.Phase != context.Phase || state.PhaseStartTick == int.MinValue)
            state.PhaseStartTick = tick;
        state.Context = context;
    }

    private static int MinimumHoldTicks(DesertBatflyEnvironmentalPhase phase)
        => phase switch
        {
            DesertBatflyEnvironmentalPhase.Advisory => AdvisoryMinimumHoldTicks,
            DesertBatflyEnvironmentalPhase.Preparation => PreparationMinimumHoldTicks,
            DesertBatflyEnvironmentalPhase.Sheltering => ShelteringMinimumHoldTicks,
            DesertBatflyEnvironmentalPhase.Acute => AcuteMinimumHoldTicks,
            _ => 0
        };

    private static int PhaseRank(DesertBatflyEnvironmentalPhase phase)
        => phase switch
        {
            DesertBatflyEnvironmentalPhase.Calm => 0,
            DesertBatflyEnvironmentalPhase.Recovery => 0,
            DesertBatflyEnvironmentalPhase.Advisory => 1,
            DesertBatflyEnvironmentalPhase.Preparation => 2,
            DesertBatflyEnvironmentalPhase.Sheltering => 3,
            DesertBatflyEnvironmentalPhase.Acute => 4,
            _ => 0
        };

    private static DesertBatflyWeatherEcologySample Reprofile(
        in DesertBatflyWeatherEcologySample aggregate,
        string weatherId,
        float activeIntensity)
    {
        return new DesertBatflyWeatherEcologySample(
            Weather.Scheduling.WeatherScheduleEventKind.Weather,
            weatherId,
            activeIntensity,
            aggregate.ImmediateDanger,
            aggregate.ShelterUrgency,
            aggregate.MigrationStress,
            aggregate.TravelExposure,
            aggregate.TimeUntilDangerTicks);
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

            DB_EnvironmentExposureSample exposure =
                DB_EnvironmentExposure.Sample(room, tile, 1f);
            bool roost = DB_EnvironmentExposure.RoostCompatibilityHint(room, tile);
            bool hive = DB_EnvironmentExposure.NearHive(room, tile);
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
