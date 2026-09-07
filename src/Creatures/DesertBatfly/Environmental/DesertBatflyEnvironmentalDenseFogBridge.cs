using System.Reflection;
using DryCycle.Weather.Scheduling;
using MonoMod.RuntimeDetour;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Compatibility lifecycle shim retained while Task13 is being finalized.
/// DenseFog ecology now lives directly in DesertBatflyWeatherEcology:
/// - DENSEFOG has high temporary shelter demand;
/// - permanent MigrationStress stays near zero;
/// - DenseFog forecast is deliberately not exposed before the fog is active.
///
/// This shim must remain behavior-neutral so it cannot double-apply or weaken those
/// semantics. It can be removed once all Task13 regression references are migrated.
/// </summary>
internal static class DesertBatflyEnvironmentalDenseFogBridge
{
    internal const float DenseFogShelterDemand = 0.90f;
    internal const float DenseFogDisplacementStartIntensity = 0.80f;

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
        => orig(world, room);

    private static float ShelterDemandHook(
        ShelterDemandOrig orig,
        WeatherScheduleEventKind kind,
        string id)
        => orig(kind, id);
}
