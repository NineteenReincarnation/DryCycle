using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal static class DB_EnvironmentRoomRuntime
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
        internal readonly List<DB_ShelterAnchor> Anchors = new(MaxAnchors);
        internal DB_EnvironmentContext Context = DB_EnvironmentContext.Calm;
        internal DB_WeatherEcologySample WeatherSample = DB_WeatherEcologySample.None;
        internal DB_WeatherAxesSample WeatherAxes = DB_WeatherAxesSample.None;
        internal DB_EnvironmentWeather LastWeather = DB_EnvironmentWeather.None;
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

    internal static bool TryGetContext(Room room, out DB_EnvironmentContext context)
    {
        RoomState state = For(room);
        if (state == null)
        {
            context = DB_EnvironmentContext.Calm;
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
        DB_Creature bat,
        DB_EnvironmentWeather weather,
        float roostPreference,
        out DB_ShelterAnchor anchor,
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
            DB_ShelterAnchor candidate = state.Anchors[i];
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
        DB_ShelterAnchor anchor,
        DB_EnvironmentWeather weather)
    {
        if (anchor == null) return 0f;
        DB_EnvironmentExposureSample e = anchor.Exposure;
        return weather switch
        {
            DB_EnvironmentWeather.LightRain => e.RoofShielding * 0.75f + e.Enclosure * 0.25f,
            DB_EnvironmentWeather.HeavyRain or DB_EnvironmentWeather.DeathRain =>
                e.RoofShielding * 0.72f + e.Enclosure * 0.28f,
            DB_EnvironmentWeather.Fog => e.Enclosure * 0.52f + (1f - e.Exposure) * 0.20f,
            DB_EnvironmentWeather.DenseFog =>
                e.Enclosure * 0.58f + e.RoofShielding * 0.17f + (anchor.NearHive ? 0.18f : 0f),
            DB_EnvironmentWeather.HeatWave or DB_EnvironmentWeather.IntenseHeat =>
                e.Shade * 0.68f + e.Enclosure * 0.32f,
            DB_EnvironmentWeather.Sandstorm or DB_EnvironmentWeather.DeathSandstorm =>
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

        DB_EnvironmentContext context = state.Context;
        if (!SeriousShelterFailureWeather(context))
        {
            state.ShelterFailureAccumulatedTicks = Mathf.Max(
                0, state.ShelterFailureAccumulatedTicks - elapsed * 3);
            state.ShelterFailureSeverity = 0f;
            state.ShelterFailureReason = "no serious active environmental shelter demand";
            return;
        }

        float bestQuality = 0f;
        bool anyUsable = false;
        bool allCrowded = state.Anchors.Count > 0;
        for (int i = 0; i < state.Anchors.Count; i++)
        {
            DB_ShelterAnchor anchor = state.Anchors[i];
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
        in DB_EnvironmentContext context)
    {
        if (!context.WeatherSourceValid) return false;
        if (context.Weather is DB_EnvironmentWeather.LightRain or
            DB_EnvironmentWeather.Fog)
            return false;
        return context.Phase is DB_EnvironmentPhase.Sheltering or
                   DB_EnvironmentPhase.Acute ||
               (context.Phase == DB_EnvironmentPhase.Preparation &&
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
        DB_WeatherEcologySample sample = room.world != null && room.abstractRoom != null
            ? DB_WeatherEcology.Sample(room.world, room.abstractRoom)
            : DB_WeatherEcologySample.None;
        state.WeatherSample = sample;
        state.WeatherAxes = room.world != null && room.abstractRoom != null
            ? DB_WeatherEcology.SampleEnvironmentAxes(room.world, room.abstractRoom)
            : DB_WeatherAxesSample.None;

        DB_EnvironmentWeather weather = DB_EnvironmentProfile.Classify(sample);
        DB_WeatherEcologySample phaseSample = sample;

        if (weather == DB_EnvironmentWeather.LightRain && state.WeatherAxes.DenseFogIntensity > 0f)
        {
            weather = DB_EnvironmentWeather.DenseFog;
            phaseSample = Reprofile(sample, "DENSEFOG", state.WeatherAxes.DenseFogIntensity);
        }
        else if (weather == DB_EnvironmentWeather.LightRain && state.WeatherAxes.FogIntensity > 0f)
        {
            weather = DB_EnvironmentWeather.Fog;
            phaseSample = Reprofile(sample, "FOG", state.WeatherAxes.FogIntensity);
        }

        DB_EnvironmentPhase previous = state.Context.Phase;

        if (weather != DB_EnvironmentWeather.None)
        {
            state.LastWeather = weather;
            state.RecoveryStartTick = -1;
            state.RecoveryDurationTicks = 0;
            DB_EnvironmentPhase candidate = DB_EnvironmentProfile.ResolvePhase(
                weather, phaseSample, previous, out string reason);
            DB_EnvironmentPhase phase = ApplyPhaseHold(state, candidate, tick, ref reason);
            SetContext(state, new DB_EnvironmentContext(
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

        if (previous != DB_EnvironmentPhase.Calm &&
            previous != DB_EnvironmentPhase.Recovery &&
            state.LastWeather != DB_EnvironmentWeather.None)
        {
            state.RecoveryStartTick = tick;
            state.RecoveryDurationTicks = RecoveryDuration(state.LastWeather);
        }

        if (state.RecoveryStartTick >= 0 &&
            tick - state.RecoveryStartTick < state.RecoveryDurationTicks)
        {
            SetContext(state, new DB_EnvironmentContext(
                false,
                state.LastWeather,
                sample.HazardKind,
                string.Empty,
                0f, 0f, 0f, 0f, int.MaxValue,
                DB_EnvironmentPhase.Recovery,
                "staggered recovery from last authorized DryCycle weather"), tick);
            return;
        }

        state.RecoveryStartTick = -1;
        state.RecoveryDurationTicks = 0;
        state.LastWeather = DB_EnvironmentWeather.None;
        SetContext(state, DB_EnvironmentContext.Calm, tick);
    }

    private static DB_EnvironmentPhase ApplyPhaseHold(
        RoomState state,
        DB_EnvironmentPhase candidate,
        int tick,
        ref string reason)
    {
        DB_EnvironmentPhase current = state.Context.Phase;
        if (candidate == current) return current;
        if (candidate == DB_EnvironmentPhase.Acute ||
            PhaseRank(candidate) > PhaseRank(current))
            return candidate;
        if (current is DB_EnvironmentPhase.Calm or DB_EnvironmentPhase.Recovery)
            return candidate;

        int hold = MinimumHoldTicks(current);
        int elapsed = state.PhaseTicks(tick);
        if (elapsed >= hold) return candidate;
        reason += $"; holding {current} for hysteresis ({elapsed}/{hold})";
        return current;
    }

    private static void SetContext(
        RoomState state,
        in DB_EnvironmentContext context,
        int tick)
    {
        if (state.Context.Phase != context.Phase || state.PhaseStartTick == int.MinValue)
            state.PhaseStartTick = tick;
        state.Context = context;
    }

    private static int MinimumHoldTicks(DB_EnvironmentPhase phase)
        => phase switch
        {
            DB_EnvironmentPhase.Advisory => AdvisoryMinimumHoldTicks,
            DB_EnvironmentPhase.Preparation => PreparationMinimumHoldTicks,
            DB_EnvironmentPhase.Sheltering => ShelteringMinimumHoldTicks,
            DB_EnvironmentPhase.Acute => AcuteMinimumHoldTicks,
            _ => 0
        };

    private static int PhaseRank(DB_EnvironmentPhase phase)
        => phase switch
        {
            DB_EnvironmentPhase.Calm => 0,
            DB_EnvironmentPhase.Recovery => 0,
            DB_EnvironmentPhase.Advisory => 1,
            DB_EnvironmentPhase.Preparation => 2,
            DB_EnvironmentPhase.Sheltering => 3,
            DB_EnvironmentPhase.Acute => 4,
            _ => 0
        };

    private static DB_WeatherEcologySample Reprofile(
        in DB_WeatherEcologySample aggregate,
        string weatherId,
        float activeIntensity)
    {
        return new DB_WeatherEcologySample(
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

            state.Anchors.Add(new DB_ShelterAnchor(
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
        var flies = DB_SwarmRoom.For(room).Hive.flies;
        for (int i = 0; i < flies.Count; i++)
        {
            if (flies[i] is not DB_Creature bat || bat.dead || bat.slatedForDeletetion || bat.room != room)
                continue;
            for (int n = 0; n < state.Anchors.Count; n++)
            {
                if (Vector2.Distance(bat.mainBodyChunk.pos, state.Anchors[n].Position) <= 105f)
                    state.Anchors[n].Crowding += 1f;
            }
        }
    }

    private static int RecoveryDuration(DB_EnvironmentWeather weather)
        => weather switch
        {
            DB_EnvironmentWeather.LightRain => 140,
            DB_EnvironmentWeather.Fog => 240,
            DB_EnvironmentWeather.DenseFog => 440,
            DB_EnvironmentWeather.HeatWave => 360,
            DB_EnvironmentWeather.IntenseHeat => 680,
            DB_EnvironmentWeather.Sandstorm => 520,
            DB_EnvironmentWeather.DeathSandstorm => 760,
            DB_EnvironmentWeather.HeavyRain => 420,
            DB_EnvironmentWeather.DeathRain => 720,
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