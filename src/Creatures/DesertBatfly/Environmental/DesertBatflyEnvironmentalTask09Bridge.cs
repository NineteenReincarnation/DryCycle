using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Narrow Task13 -> Task09 bridge. Task13 may suppress the *start* of a migration
/// during sandstorm conditions and may report bounded realized shelter failure.
/// It never creates a destination, route, ReturnHome or ColonyMigration intent.
/// </summary>
internal static class DesertBatflyEnvironmentalTask09Bridge
{
    internal const int ShelterFailureMinTicks = 360;
    internal const int ShelterFailureReportCooldownTicks = 1800;
    internal const float MinimumUsableAnchorQuality = 0.44f;
    internal const float SevereCrowdingPerAnchor = 5f;

    private delegate void ScheduleMigrationOrig(World world, DesertBatflyColonyState source, int cycle);
    private delegate void ScheduleMigrationDetour(
        ScheduleMigrationOrig orig,
        World world,
        DesertBatflyColonyState source,
        int cycle);

    private delegate void RoomUpdateOrig(Room room);
    private delegate void RoomUpdateDetour(RoomUpdateOrig orig, Room room);

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
    private static ConditionalWeakTable<Room, FailureState> failures = new();

    internal static bool Installed => scheduleMigrationHook != null && roomUpdateHook != null;

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
            if (schedule == null || updateRoom == null) return;

            scheduleMigrationHook = new Hook(schedule, (ScheduleMigrationDetour)ScheduleMigrationHook);
            roomUpdateHook = new Hook(updateRoom, (RoomUpdateDetour)RoomUpdateHook);
        }
        catch
        {
            Disable();
        }
    }

    internal static void Disable()
    {
        try { roomUpdateHook?.Dispose(); } catch { }
        try { scheduleMigrationHook?.Dispose(); } catch { }
        roomUpdateHook = null;
        scheduleMigrationHook = null;
        failures = new ConditionalWeakTable<Room, FailureState>();
    }

    internal static bool ShouldSuppressNewMigration(World world, DesertBatflyColonyState source)
    {
        if (world == null || source == null) return false;
        AbstractRoom room = DesertBatflyColonyRuntime.FindRoom(world, source.RoomName);
        if (room == null) return false;

        DesertBatflyWeatherEcologySample sample = DesertBatflyWeatherEcology.Sample(world, room);
        DesertBatflyEnvironmentalWeather weather = DesertBatflyEnvironmentalProfile.Classify(sample);
        if (weather != DesertBatflyEnvironmentalWeather.Sandstorm &&
            weather != DesertBatflyEnvironmentalWeather.DeathSandstorm)
            return false;

        float suppression = DesertBatflyEnvironmentalProfile.SandstormMigrationSuppression(weather, sample);
        return suppression >= 0.70f;
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
}
