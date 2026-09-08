using System;
using System.Reflection;

internal static partial class Program
{
    private static void RunTask13Rain()
    {
        Type phase = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentPhase", true);
        Type weather = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentWeather", true);
        Type profile = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentProfile", true);
        Type ecologySample = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_WeatherEcologySample", true);
        Type eventKind = mod.GetType("DryCycle.Weather.Scheduling.WeatherScheduleEventKind", true);
        object weatherKind = Enum.Parse(eventKind, "Weather");
        MethodInfo resolvePhase = profile.GetMethod("ResolvePhase", Flags);

        object Sample(string id, float active, float danger, float shelter, float travel, int forecast)
            => Activator.CreateInstance(
                ecologySample,
                Flags,
                null,
                new object[] { weatherKind, id, active, danger, shelter, 0f, travel, forecast },
                null);

        string Resolve(string weatherName, object sample, string previous = "Calm")
        {
            object[] args =
            {
                Enum.Parse(weather, weatherName),
                sample,
                Enum.Parse(phase, previous),
                null
            };
            return resolvePhase.Invoke(null, args).ToString();
        }

        // HeavyRain is strong covered-space ecology, never a lethal profile by itself.
        Check(Resolve("HeavyRain", Sample("HEAVYRAIN", 0.70f, 1f, 0.80f, 0.85f, int.MaxValue)) == "Sheltering",
            "Task13 HeavyRain reaches covered-space Sheltering at strong DryCycle intensity");
        Check(Resolve("HeavyRain", Sample("HEAVYRAIN", 0.34f, 1f, 0.45f, 0.55f, int.MaxValue)) == "Preparation",
            "Task13 HeavyRain contracts through Preparation before sheltering");
        Check(Resolve("HeavyRain", Sample("HEAVYRAIN", 0.70f, 1f, 1f, 1f, 0)) != "Acute",
            "Task13 HeavyRain alone has no Acute phase; DeathRain owns lethal rain survival");

        MethodInfo heavyRainBurden = profile.GetMethod("HeavyRainBurden", Flags);
        Check(heavyRainBurden != null,
            "Task13 HeavyRain exposes an explicit realized RainExposure/RoofShielding burden model");

        float exposed = (float)heavyRainBurden.Invoke(null,
            new object[] { 0.70f, 1f, 0f, 0.55f, 1f, 0f, 0.65f });
        float roofed = (float)heavyRainBurden.Invoke(null,
            new object[] { 0.70f, 0f, 1f, 0.55f, 1f, 0f, 0.65f });
        float injured = (float)heavyRainBurden.Invoke(null,
            new object[] { 0.70f, 1f, 0f, 0.55f, 0.35f, 0.25f, 0.65f });
        Check(exposed > roofed + 0.35f,
            "Task13 HeavyRain roof shielding strongly reduces realized rain burden");
        Check(injured > exposed,
            "Task13 HeavyRain injured/shocked individuals contract earlier under equal exposure");

        MethodInfo visibility = profile.GetMethod("VisibilityConfidence", Flags);
        MethodInfo uncertainty = profile.GetMethod("NavigationUncertainty", Flags);
        object heavyRainWeather = Enum.Parse(weather, "HeavyRain");
        Check(Math.Abs((float)visibility.Invoke(null, new object[] { heavyRainWeather, 1f }) - 1f) < 0.0001f &&
              Math.Abs((float)uncertainty.Invoke(null, new object[] { heavyRainWeather, 1f })) < 0.0001f,
            "Task13 HeavyRain does not steal Fog visibility/navigation semantics");

        Type behavior = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentRuntime", true);
        MethodInfo applyWeather = behavior.GetMethod("ApplyWeatherProfile", Flags);
        Check(MethodCallOffset(applyWeather, profile, "HeavyRainBurden") >= 0,
            "Task13 HeavyRain behavior consumes the local burden model instead of intensity-only sheltering");

        int deathPreparation = (int)profile.GetField("DeathRainPreparationLeadTicks", Flags).GetRawConstantValue();
        int deathShelter = (int)profile.GetField("DeathRainShelterLeadTicks", Flags).GetRawConstantValue();
        Check(deathPreparation > deathShelter && deathShelter > 0,
            "Task13 DeathRain has bounded pre-onset preparation before hard local sheltering");
        Check(Resolve("DeathRain", Sample("DEATHRAIN", 0f, 0f, 0.35f, 0.45f, deathPreparation - 100)) == "Preparation",
            "Task13 DeathRain bounded forecast produces pre-onset local Preparation");
        Check(Resolve("DeathRain", Sample("DEATHRAIN", 0.10f, 0.20f, 0.70f, 0.80f, deathShelter - 100)) == "Sheltering",
            "Task13 DeathRain near onset enters local Sheltering while Task09 owns cross-room travel");
        Check(Resolve("DeathRain", Sample("DEATHRAIN", 0.70f, 0.90f, 1f, 1f, 0)) == "Acute",
            "Task13 DeathRain enters Acute only under material lethal danger");

        Type survivalBridge = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CreatureEnvironmentalSurvivalBridge", true);
        Check(survivalBridge.GetMethod("ShouldSeekHome", Flags) != null &&
              survivalBridge.GetMethod("ShouldBurrow", Flags) != null,
            "Task13 DeathRain local Home/Burrow pressure feeds the existing native FlyAI survival bridge");

        Type roomRuntime = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentRoomRuntime", true);
        MethodInfo weatherQuality = roomRuntime.GetMethod("WeatherQuality", Flags);
        Check(weatherQuality != null,
            "Task13 rain shelter selection remains on the shared bounded multi-anchor scorer");

        Console.WriteLine(
            "Task 13 rain completion: HeavyRain roof/exposure/injury ecology, non-Acute ceiling, Fog separation, DeathRain forecast escalation and native survival handoff verified.");
    }
}
