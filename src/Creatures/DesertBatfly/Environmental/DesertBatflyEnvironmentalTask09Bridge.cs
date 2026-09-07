using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Narrow Task13 -> Task09 bridge. Task13 may suppress the *start* of a migration
/// during sandstorm conditions, request Task09's existing Home-return path while the
/// forecast still leaves a useful travel window, filter new outward Sandstorm refuge
/// attempts, and report bounded realized shelter failure.
///
/// Task13 never creates a destination, route, ReturnHome or ColonyMigration intent.
/// All cross-room intent creation remains inside DesertBatflyTravelNavigation/Task09.
/// </summary>
internal static class DesertBatflyEnvironmentalTask09Bridge
{
    internal const int ShelterFailureMinTicks = 360;
    internal const int ShelterFailureReportCooldownTicks = 1800;
    internal const float MinimumUsableAnchorQuality = 0.44f;
    internal const float SevereCrowdingPerAnchor = 5f;

    // Sandstorm is the species-specific early-warning weather. Return-home starts only
    // while there is still enough forecast time to make leaving a current room sensible.
    // Once serious wind/sand is active, Task13 prefers local shelter instead of creating
    // a new long cross-room trip.
    internal const int SandstormHomeRecallMinimumLeadTicks = 2800;
    internal const int DeathSandstormHomeRecallMinimumLeadTicks = 3600;
    internal const int SandstormEmergencyMinimumLeadTicks = 4200;
    internal const int DeathSandstormEmergencyMinimumLeadTicks = 5200;
    internal const int SandstormEmergencyMaxHops = 2;
    internal const int DeathSandstormEmergencyMaxHops = 1;

    private delegate void ScheduleMigrationOrig(World world, DesertBatflyColonyState source, int cycle);
    private delegate void ScheduleMigrationDetour(
        ScheduleMigrationOrig orig,
        World world,
        DesertBatflyColonyState source,
        int cycle);

    private delegate void RoomUpdateOrig(Room room);
    private delegate void RoomUpdateDetour(RoomUpdateOrig orig, Room room);

    private delegate void EvaluateWeatherOrig(World world);
    private delegate void EvaluateWeatherDetour(EvaluateWeatherOrig orig, World world);

    private delegate bool FindEmergencyRefugeOrig(
        World world,
        AbstractRoom home,
        CreatureTemplate template,
        DesertBatflyWeatherEcologySample hazard,
        float physicalCapability,
        string knownRefuge,
        Func<AbstractRoom, float> predatorRisk,
        Func<AbstractRoom, float> crowding,
        out DesertBatflyRefugeTarget target);

    private delegate bool FindEmergencyRefugeDetour(
        FindEmergencyRefugeOrig orig,
        World world,
        AbstractRoom home,
        CreatureTemplate template,
        DesertBatflyWeatherEcologySample hazard,
        float physicalCapability,
        string knownRefuge,
        Func<AbstractRoom, float> predatorRisk,
        Func<AbstractRoom, float> crowding,
        out DesertBatflyRefugeTarget target);

    private sealed class FailureState
    {
        internal int LastTick = int.MinValue;
        internal int AccumulatedTicks;
        internal int LastReportTick = int.MinValue;
        internal string LastReason = string.Empty;
        internal float LastSeverity;
    }

    private static Hook scheduleMigrationHook;
    private static Hook roomUpdateHook;
    private static Hook evaluateWeatherHook;
    private static Hook findEmergencyRefugeHook;
    private static MethodInfo endEvacuationAndReturn;
    private static ConditionalWeakTable<Room, FailureState> failures = new();

    internal static bool Installed =>
        scheduleMigrationHook != null && roomUpdateHook != null &&
        evaluateWeatherHook != null && findEmergencyRefugeHook != null;

    internal static void Enable()
    {
        if (Installed) return;
        Disable();
        try
        {
            MethodInfo schedule = typeof(DesertBatflyColonyRuntime).GetMethod(
                "ScheduleMigrationBatch",
                BindingFlags.Static | BindingFlags.NonPublic,
                null,
                new[] { typeof(World), typeof(DesertBatflyColonyState), typeof(int) },
                null);
            MethodInfo updateRoom = typeof(DesertBatflyEnvironmentalRoomRuntime).GetMethod(
                "Update",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                null,
                new[] { typeof(Room) },
                null);
            MethodInfo evaluateWeather = typeof(DesertBatflyTravelNavigation).GetMethod(
                "EvaluateColonyWeather",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                null,
                new[] { typeof(World) },
                null);
            MethodInfo findEmergencyRefuge = typeof(DesertBatflyRefuge).GetMethod(
                "TryFindEmergencyRefuge",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                null,
                new[]
                {
                    typeof(World), typeof(AbstractRoom), typeof(CreatureTemplate),
                    typeof(DesertBatflyWeatherEcologySample), typeof(float), typeof(string),
                    typeof(Func<AbstractRoom, float>), typeof(Func<AbstractRoom, float>),
                    typeof(DesertBatflyRefugeTarget).MakeByRefType()
                },
                null);
            endEvacuationAndReturn = typeof(DesertBatflyTravelNavigation).GetMethod(
                "EndEvacuationAndReturn",
                BindingFlags.Static | BindingFlags.NonPublic,
                null,
                new[] { typeof(World), typeof(string), typeof(CreatureTemplate) },
                null);

            if (schedule == null || updateRoom == null || evaluateWeather == null ||
                findEmergencyRefuge == null || endEvacuationAndReturn == null)
                return;

            scheduleMigrationHook = new Hook(schedule, (ScheduleMigrationDetour)ScheduleMigrationHook);
            roomUpdateHook = new Hook(updateRoom, (RoomUpdateDetour)RoomUpdateHook);
            evaluateWeatherHook = new Hook(evaluateWeather, (EvaluateWeatherDetour)EvaluateWeatherHook);
            findEmergencyRefugeHook = new Hook(
                findEmergencyRefuge,
                (FindEmergencyRefugeDetour)FindEmergencyRefugeHook);
        }
        catch
        {
            Disable();
        }
    }

    internal static void Disable()
    {
        try { findEmergencyRefugeHook?.Dispose(); } catch { }
        try { evaluateWeatherHook?.Dispose(); } catch { }
        try { roomUpdateHook?.Dispose(); } catch { }
        try { scheduleMigrationHook?.Dispose(); } catch { }
        findEmergencyRefugeHook = null;
        evaluateWeatherHook = null;
        roomUpdateHook = null;
        scheduleMigrationHook = null;
        endEvacuationAndReturn = null;
        failures = new ConditionalWeakTable<Room, FailureState>();
    }

    internal static bool ShouldSuppressNewMigration(World world, DesertBatflyColonyState source)
    {
        if (world == null || source == null) return false;
        AbstractRoom room = DesertBatflyColonyRuntime.FindRoom(world, source.RoomName);
        if (room == null) return false;

        DesertBatflyWeatherEcologySample sample = DesertBatflyWeatherEcology.Sample(world, room);
        DesertBatflyEnvironmentalWeather weather = DesertBatflyEnvironmentalProfile.Classify(sample);
        if (!IsSandstorm(weather)) return false;

        float suppression = DesertBatflyEnvironmentalProfile.SandstormMigrationSuppression(weather, sample);
        return suppression >= 0.70f;
    }

    internal static bool ShouldRecallHomeForSandstorm(
        DesertBatflyEnvironmentalWeather weather,
        in DesertBatflyWeatherEcologySample sample)
    {
        if (!IsSandstorm(weather) || !sample.HasHazard || !sample.ForecastDanger)
            return false;

        // Recall is deliberately a pre-onset behavior. The EventEnvelope can become
        // slightly non-zero during transition, so tolerate only a very small active value.
        if (sample.ActiveIntensity > 0.08f || sample.ImmediateDanger >= 0.48f)
            return false;

        int minimumLead = weather == DesertBatflyEnvironmentalWeather.DeathSandstorm
            ? DeathSandstormHomeRecallMinimumLeadTicks
            : SandstormHomeRecallMinimumLeadTicks;
        return sample.TimeUntilDangerTicks >= minimumLead &&
               sample.TimeUntilDangerTicks <= DesertBatflyEnvironmentalProfile.SandstormAdvisoryTicks;
    }

    internal static bool CanConsiderSandstormOutwardRefuge(
        AbstractRoom home,
        DesertBatflyEnvironmentalWeather weather,
        in DesertBatflyWeatherEcologySample sample,
        out float homeQuality)
    {
        homeQuality = 1f;
        if (home == null || !IsSandstorm(weather) || !sample.HasHazard || !sample.ForecastDanger)
            return false;

        // No new outward refuge once the actual storm has materially begun. Bats that are
        // already traveling remain Task09's responsibility and can replan/suspend normally.
        if (sample.ActiveIntensity > 0.04f || sample.ImmediateDanger >= 0.38f)
            return false;

        int minimumLead = weather == DesertBatflyEnvironmentalWeather.DeathSandstorm
            ? DeathSandstormEmergencyMinimumLeadTicks
            : SandstormEmergencyMinimumLeadTicks;
        if (sample.TimeUntilDangerTicks < minimumLead) return false;

        homeQuality = DesertBatflyRefuge.HomeHiveShelterQuality(
            home, sample.HazardKind, sample.HazardId);
        float maximumAcceptableHome = weather == DesertBatflyEnvironmentalWeather.DeathSandstorm
            ? 0.24f
            : 0.30f;
        return homeQuality < maximumAcceptableHome;
    }

    internal static bool AcceptSandstormEmergencyRefuge(
        DesertBatflyEnvironmentalWeather weather,
        in DesertBatflyWeatherEcologySample sample,
        float homeQuality,
        in DesertBatflyRefugeTarget target)
    {
        if (!target.Valid || !IsSandstorm(weather)) return false;
        int maxHops = weather == DesertBatflyEnvironmentalWeather.DeathSandstorm
            ? DeathSandstormEmergencyMaxHops
            : SandstormEmergencyMaxHops;
        float minimumImprovement = weather == DesertBatflyEnvironmentalWeather.DeathSandstorm
            ? 0.24f
            : 0.18f;
        int extraMargin = weather == DesertBatflyEnvironmentalWeather.DeathSandstorm
            ? 1300
            : 900;

        if (target.Route.HopCount > maxHops ||
            target.ShelterQuality < homeQuality + minimumImprovement ||
            !sample.ForecastDanger || sample.TimeUntilDangerTicks == int.MaxValue)
            return false;

        long required = (long)target.EstimatedTravelTicks + extraMargin;
        return required < sample.TimeUntilDangerTicks;
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
        if (room == null || !failures.TryGetValue(room, out FailureState state)) return false;
        accumulatedTicks = state.AccumulatedTicks;
        severity = state.LastSeverity;
        reason = state.LastReason;
        return state.AccumulatedTicks > 0 || state.LastSeverity > 0f;
    }

    private static void ScheduleMigrationHook(
        ScheduleMigrationOrig orig,
        World world,
        DesertBatflyColonyState source,
        int cycle)
    {
        // Suppress only execution timing. MigrationPressure, EnvironmentalPressure and
        // ShelterFailureMemory are still settled by Task09 and remain valid history.
        if (ShouldSuppressNewMigration(world, source)) return;
        orig(world, source, cycle);
    }

    private static void EvaluateWeatherHook(EvaluateWeatherOrig orig, World world)
    {
        // Species-specific Sandstorm anticipation first recalls colony members that are
        // already outside Home while a meaningful safe travel window still exists.
        // The called method belongs to Task09 and remains the sole creator of ReturnHome
        // routes/intents; Task13 merely asks Task09 to perform its existing operation.
        RecallSandstormOutliers(world);
        orig(world);
    }

    private static bool FindEmergencyRefugeHook(
        FindEmergencyRefugeOrig orig,
        World world,
        AbstractRoom home,
        CreatureTemplate template,
        DesertBatflyWeatherEcologySample hazard,
        float physicalCapability,
        string knownRefuge,
        Func<AbstractRoom, float> predatorRisk,
        Func<AbstractRoom, float> crowding,
        out DesertBatflyRefugeTarget target)
    {
        DesertBatflyEnvironmentalWeather weather = DesertBatflyEnvironmentalProfile.Classify(hazard);
        if (!IsSandstorm(weather))
            return orig(
                world, home, template, hazard, physicalCapability, knownRefuge,
                predatorRisk, crowding, out target);

        target = default;
        if (!CanConsiderSandstormOutwardRefuge(home, weather, hazard, out float homeQuality))
            return false;

        if (!orig(
                world, home, template, hazard, physicalCapability, knownRefuge,
                predatorRisk, crowding, out DesertBatflyRefugeTarget candidate))
            return false;

        if (!AcceptSandstormEmergencyRefuge(weather, hazard, homeQuality, candidate))
            return false;

        target = candidate;
        return true;
    }

    private static void RecallSandstormOutliers(World world)
    {
        if (world?.region == null || endEvacuationAndReturn == null) return;
        CreatureTemplate template = StaticWorld.GetCreatureTemplate(DesertBatflyDefinition.CreatureType);
        if (template == null) return;

        foreach (DesertBatflyColonyState colony in DesertBatflyColonyRuntime.Colonies)
        {
            if (colony == null ||
                !string.Equals(colony.RegionName, world.region.name, StringComparison.OrdinalIgnoreCase))
                continue;
            AbstractRoom home = DesertBatflyColonyRuntime.FindRoom(world, colony.RoomName);
            if (home == null) continue;

            DesertBatflyWeatherEcologySample sample = DesertBatflyWeatherEcology.Sample(world, home);
            DesertBatflyEnvironmentalWeather weather = DesertBatflyEnvironmentalProfile.Classify(sample);
            if (!ShouldRecallHomeForSandstorm(weather, sample)) continue;

            try
            {
                endEvacuationAndReturn.Invoke(null, new object[] { world, colony.RoomName, template });
            }
            catch
            {
                // Cross-room ecology failure must never break the RainCycle update. Task09
                // will simply keep/reevaluate the current intent on its next normal tick.
            }
        }
    }

    private static void RoomUpdateHook(RoomUpdateOrig orig, Room room)
    {
        orig(room);
        ObserveLocalShelterFailure(room);
    }

    private static void ObserveLocalShelterFailure(Room room)
    {
        if (room?.abstractRoom == null || room.world == null || room.game == null) return;
        DesertBatflyEnvironmentalRoomRuntime.RoomState roomState =
            DesertBatflyEnvironmentalRoomRuntime.For(room);
        if (roomState == null) return;

        FailureState state = failures.GetValue(room, _ => new FailureState());
        int tick = room.game.clock;
        int elapsed = state.LastTick == int.MinValue ? 1 : Mathf.Clamp(tick - state.LastTick, 1, 60);
        state.LastTick = tick;

        DesertBatflyEnvironmentalRoomContext context = roomState.Context;
        if (!SeriousWeather(context))
        {
            state.AccumulatedTicks = Mathf.Max(0, state.AccumulatedTicks - elapsed * 3);
            state.LastSeverity = 0f;
            state.LastReason = "no serious active Task13 shelter demand";
            return;
        }

        float bestQuality = 0f;
        bool anyUsable = false;
        bool allCrowded = roomState.Anchors.Count > 0;
        for (int i = 0; i < roomState.Anchors.Count; i++)
        {
            DesertBatflyShelterAnchor anchor = roomState.Anchors[i];
            float quality = DesertBatflyEnvironmentalRoomRuntime.WeatherQuality(anchor, context.Weather);
            bestQuality = Mathf.Max(bestQuality, quality);
            if (quality >= MinimumUsableAnchorQuality && anchor.Crowding < SevereCrowdingPerAnchor)
                anyUsable = true;
            if (anchor.Crowding < SevereCrowdingPerAnchor)
                allCrowded = false;
        }

        bool noAnchors = roomState.Anchors.Count == 0;
        bool badQuality = bestQuality < MinimumUsableAnchorQuality;
        bool failing = noAnchors || !anyUsable || allCrowded;
        if (!failing)
        {
            state.AccumulatedTicks = Mathf.Max(0, state.AccumulatedTicks - elapsed * 2);
            state.LastSeverity = 0f;
            state.LastReason = "usable realized shelter anchor available";
            return;
        }

        float severity = noAnchors
            ? 0.72f
            : allCrowded
                ? 0.55f
                : Mathf.Clamp01(0.48f + (MinimumUsableAnchorQuality - bestQuality));
        severity *= Mathf.Lerp(0.72f, 1f, Mathf.Max(context.ActiveIntensity, context.ImmediateDanger));
        state.LastSeverity = Mathf.Clamp01(severity);
        state.LastReason = noAnchors
            ? "serious weather: no realized shelter anchors"
            : allCrowded
                ? "serious weather: all usable shelter anchors overcrowded"
                : badQuality
                    ? "serious weather: available anchors have inadequate weather protection"
                    : "serious weather: no sufficiently protected uncrowded anchor";
        state.AccumulatedTicks = Mathf.Min(ShelterFailureMinTicks * 2, state.AccumulatedTicks + elapsed);

        if (state.AccumulatedTicks < ShelterFailureMinTicks) return;
        if (state.LastReportTick != int.MinValue &&
            tick - state.LastReportTick < ShelterFailureReportCooldownTicks)
            return;

        DesertBatflyColonyState colony = DesertBatflyColonyRuntime.TryGetColony(room.abstractRoom);
        if (colony == null) return;

        DesertBatflyColonyRuntime.ReportExternalRefuge(
            colony.RoomName,
            null,
            Mathf.Clamp(state.LastSeverity, 0.12f, 0.80f));
        state.LastReportTick = tick;
        state.AccumulatedTicks = 0;
    }

    private static bool SeriousWeather(in DesertBatflyEnvironmentalRoomContext context)
    {
        if (!context.WeatherSourceValid) return false;
        if (context.Weather is DesertBatflyEnvironmentalWeather.LightRain or DesertBatflyEnvironmentalWeather.Fog)
            return false;
        return context.Phase is DesertBatflyEnvironmentalPhase.Sheltering or DesertBatflyEnvironmentalPhase.Acute ||
               (context.Phase == DesertBatflyEnvironmentalPhase.Preparation && context.ShelterUrgency >= 0.68f);
    }

    private static bool IsSandstorm(DesertBatflyEnvironmentalWeather weather)
        => weather is DesertBatflyEnvironmentalWeather.Sandstorm or
                      DesertBatflyEnvironmentalWeather.DeathSandstorm;
}
