using System;
using System.Reflection;
using DryCycle.Weather.Scheduling;
using DryCycle.Weather.Spatial;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Gives severe DryCycle DenseFog enough temporary shelter pressure for Task09's existing
/// EmergencyRefuge path to become available without turning fog into permanent migration
/// pressure. The source remains DesertBatflyWeatherEcology, so RoomSettings/default fog
/// effects can never activate this bridge.
/// </summary>
internal static class DesertBatflyEnvironmentalDenseFogBridge
{
    internal const float DenseFogShelterDemand = 0.56f;
    internal const float DenseFogDisplacementStartIntensity = 0.88f;

    private delegate DesertBatflyWeatherEcologySample SampleOrig(World world, AbstractRoom room);
    private delegate DesertBatflyWeatherEcologySample SampleDetour(
        SampleOrig orig,
        World world,
        AbstractRoom room);

    private delegate float ShelterDemandOrig(WeatherScheduleEventKind kind, string id);
    private delegate float ShelterDemandDetour(
        ShelterDemandOrig orig,
        WeatherScheduleEventKind kind,
        string id);

    private static Hook sampleHook;
    private static Hook shelterDemandHook;

    internal static bool Installed => sampleHook != null && shelterDemandHook != null;

    internal static void Enable()
    {
        if (Installed) return;
        Disable();
        try
        {
            MethodInfo sample = typeof(DesertBatflyWeatherEcology).GetMethod(
                "Sample",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                null,
                new[] { typeof(World), typeof(AbstractRoom) },
                null);
            MethodInfo demand = typeof(DesertBatflyWeatherEcology).GetMethod(
                "HazardShelterDemand",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                null,
                new[] { typeof(WeatherScheduleEventKind), typeof(string) },
                null);
            if (sample == null || demand == null) return;

            sampleHook = new Hook(sample, (SampleDetour)SampleHook);
            shelterDemandHook = new Hook(demand, (ShelterDemandDetour)ShelterDemandHook);
        }
        catch
        {
            Disable();
        }
    }

    internal static void Disable()
    {
        try { shelterDemandHook?.Dispose(); } catch { }
        try { sampleHook?.Dispose(); } catch { }
        shelterDemandHook = null;
        sampleHook = null;
    }

    private static DesertBatflyWeatherEcologySample SampleHook(
        SampleOrig orig,
        World world,
        AbstractRoom room)
    {
        DesertBatflyWeatherEcologySample sample = orig(world, room);
        if (!IsDenseFog(sample.HazardKind, sample.HazardId) || sample.ActiveIntensity <= 0f)
            return sample;

        // Ordinary/weak DenseFog contracts local activity but does not justify crossing
        // rooms. Only severe realized DenseFog may reach Task09's EmergencyRefuge gate.
        float severe = Mathf.InverseLerp(
            DenseFogDisplacementStartIntensity,
            1f,
            sample.ActiveIntensity);
        float temporaryShelter = Mathf.Lerp(
            sample.ShelterUrgency,
            DenseFogShelterDemand,
            severe);
        float travelExposure = Mathf.Max(
            sample.TravelExposure,
            Mathf.Lerp(0.12f, 0.28f, severe));

        return new DesertBatflyWeatherEcologySample(
            sample.HazardKind,
            sample.HazardId,
            sample.ActiveIntensity,
            sample.ImmediateDanger,
            temporaryShelter,
            sample.MigrationStress,
            travelExposure,
            sample.TimeUntilDangerTicks);
    }

    private static float ShelterDemandHook(
        ShelterDemandOrig orig,
        WeatherScheduleEventKind kind,
        string id)
    {
        float original = orig(kind, id);
        return IsDenseFog(kind, id) ? Mathf.Max(original, DenseFogShelterDemand) : original;
    }

    private static bool IsDenseFog(WeatherScheduleEventKind kind, string id)
        => kind == WeatherScheduleEventKind.Weather &&
           string.Equals(
               WeatherSpatialCatalog.NormalizeId(id),
               "DENSEFOG",
               StringComparison.Ordinal);
}
