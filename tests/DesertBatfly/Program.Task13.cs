using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

internal static partial class Program
{
    private static void RunTask13()
    {
        Type phase = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentPhase", true);
        string[] phases = Enum.GetNames(phase);
        string[] expectedPhases = { "Calm", "Advisory", "Preparation", "Sheltering", "Acute", "Recovery" };
        Check(phases.Length == 6 && expectedPhases.All(phases.Contains),
            "Task13 environmental context exposes exactly six approved phases");

        Type weather = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentWeather", true);
        foreach (string name in new[]
                 {
                     "None", "LightRain", "Fog", "DenseFog", "HeavyRain", "HeatWave",
                     "IntenseHeat", "Sandstorm", "DeathSandstorm", "DeathRain"
                 })
            Check(Enum.GetNames(weather).Contains(name), "Task13 weather profile includes " + name);

        Type context = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentContext", true);
        foreach (string name in new[]
                 {
                     "WeatherSourceValid", "Weather", "WeatherId", "ActiveIntensity",
                     "ImmediateDanger", "ShelterUrgency", "TravelExposure", "ForecastTicks",
                     "Phase", "PhaseReason"
                 })
            Check(context.GetField(name, Flags) != null, "Task13 room context contains " + name);

        Type influence = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentInfluence", true);
        foreach (string name in new[]
                 {
                     "ShelterDrive", "OpenExposureAversion", "RoostMultiplier", "HarassMultiplier",
                     "SocialMultiplier", "PlayMultiplier", "GroupCohesionMultiplier",
                     "ActivityRadiusMultiplier", "VisibilityConfidence", "NavigationUncertainty",
                     "ObstacleAnticipationScale", "HomeReturnDrive", "BurrowDrive",
                     "MigrationSuppression", "HeatAgitation", "HeatShelterDrive",
                     "ThermalExhaustion", "DamageAttackPermission", "HardSurvival",
                     "PreferredShelterPoint", "PreferredShelterQuality", "CommitmentTicks",
                     "RecoveryProgress", "DecisionReason"
                 })
            Check(influence.GetField(name, Flags) != null, "Task13 influence contains " + name);

        Type anchor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_ShelterAnchor", true);
        Check(anchor.GetField("Exposure", Flags) != null &&
              anchor.GetField("RoostCompatible", Flags) != null &&
              anchor.GetField("Crowding", Flags) != null,
            "Task13 keeps shelter quality, Roost compatibility and crowding as separate anchor data");

        Type roomRuntime = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentRoomRuntime", true);
        Check((int)roomRuntime.GetField("WeatherSampleInterval", Flags).GetRawConstantValue() == 20,
            "Task13 room weather sample is low-frequency");
        Check((int)roomRuntime.GetField("CrowdingSampleInterval", Flags).GetRawConstantValue() == 20,
            "Task13 crowding refresh is low-frequency");
        Check((int)roomRuntime.GetField("MaxAnchors", Flags).GetRawConstantValue() == 8,
            "Task13 room shelter anchor cache is bounded");
        Check(roomRuntime.GetMethod("TryChooseAnchor", Flags) != null &&
              roomRuntime.GetMethod("WeatherQuality", Flags) != null,
            "Task13 room runtime owns multi-anchor selection and weather-specific shelter quality");

        Type behavior = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentRuntime", true);
        Check((int)behavior.GetField("DecisionBaseInterval", Flags).GetRawConstantValue() >= 8,
            "Task13 individual environmental decisions are staggered/low-frequency");
        Check((float)behavior.GetField("AnchorSwitchMargin", Flags).GetRawConstantValue() >= 0.10f,
            "Task13 shelter anchor switching has hysteresis margin");
        Check(behavior.GetMethod("AllowsEnvironmentalDamageAttack", Flags) != null &&
              behavior.GetMethod("MigrationSuppression", Flags) != null &&
              behavior.GetMethod("VisibilityScale", Flags) != null,
            "Task13 exposes heat attack, migration suppression and visibility influences to existing systems");

        Type profile = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentProfile", true);
        int advisory = (int)profile.GetField("SandstormAdvisoryTicks", Flags).GetRawConstantValue();
        int preparation = (int)profile.GetField("SandstormPreparationTicks", Flags).GetRawConstantValue();
        int strong = (int)profile.GetField("SandstormStrongPreparationTicks", Flags).GetRawConstantValue();
        Check(advisory > preparation && preparation > strong && strong > 0,
            "Task13 Sandstorm has species-specific staged early anticipation");
        Check(profile.GetMethod("HeatAgitation", Flags) != null &&
              profile.GetMethod("HeatShelterDrive", Flags) != null &&
              profile.GetMethod("ThermalExhaustion", Flags) != null,
            "Task13 Heat keeps agitation, shelter drive and exhaustion as separate axes");

        // Ordinary Fog is an activity/visibility modifier only. Even full-intensity DryCycle
        // Fog must not silently acquire DenseFog's Home/refuge escalation semantics.
        Type ecologySample = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_WeatherEcologySample", true);
        Type eventKind = mod.GetType("DryCycle.Weather.Scheduling.WeatherScheduleEventKind", true);
        object weatherKind = Enum.Parse(eventKind, "Weather");
        object fullFogSample = Activator.CreateInstance(
            ecologySample,
            Flags,
            null,
            new object[] { weatherKind, "FOG", 1f, 0.02f, 0.05f, 0f, 0.05f, int.MaxValue },
            null);
        MethodInfo resolvePhase = profile.GetMethod("ResolvePhase", Flags);
        object[] fogPhaseArgs =
        {
            Enum.Parse(weather, "Fog"),
            fullFogSample,
            Enum.Parse(phase, "Calm"),
            null
        };
        object resolvedFogPhase = resolvePhase.Invoke(null, fogPhaseArgs);
        Check(resolvedFogPhase.ToString() == "Advisory",
            "ordinary Fog has a hard Advisory ceiling; DenseFog owns shelter/displacement escalation");

        Check(mod.GetType(
                "DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalDenseFogBridge", false) == null,
            "Task13 DenseFog behavior-neutral RuntimeDetour shim is retired in R5");

        Type environmentalPolicy = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentalPolicy", true);
        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalTask09Bridge", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalSurvivalBridge", false) == null,
            "Task13 R5 retires Task09/native-survival RuntimeDetour bridges");
        Check(roomRuntime.GetMethod("TryGetShelterFailureDebug", Flags) != null,
            "Task13 realized-room runtime owns LocalShelterFailure evidence directly");
        Check((int)roomRuntime.GetField("ShelterFailureMinTicks", Flags).GetRawConstantValue() >= 300 &&
              (int)roomRuntime.GetField("ShelterFailureReportCooldownTicks", Flags).GetRawConstantValue() >= 1200,
            "Task13 LocalShelterFailure remains sustained and cooldown-bounded");
        Check(environmentalPolicy.GetMethod("ShouldSuppressNewMigration", Flags) != null &&
              environmentalPolicy.GetMethod("ShouldRecallHomeForSandstorm", Flags) != null &&
              environmentalPolicy.GetMethod("CanConsiderSandstormOutwardRefuge", Flags) != null &&
              environmentalPolicy.GetMethod("AcceptSandstormEmergencyRefuge", Flags) != null,
            "Task09 directly consumes explicit Sandstorm environment policy");
        Check((int)environmentalPolicy.GetField("SandstormEmergencyMaxHops", Flags).GetRawConstantValue() <= 2 &&
              (int)environmentalPolicy.GetField("DeathSandstormEmergencyMaxHops", Flags).GetRawConstantValue() <= 1,
            "Task13 Sandstorm outward emergency exception remains deliberately short-range");
        Check((int)environmentalPolicy.GetField("SandstormHomeRecallMinimumLeadTicks", Flags).GetRawConstantValue() < advisory &&
              (int)environmentalPolicy.GetField("DeathSandstormHomeRecallMinimumLeadTicks", Flags).GetRawConstantValue() < advisory,
            "Task13 Sandstorm Home recall remains inside the bounded species forecast horizon");
        Check(behavior.GetMethod("ApplyOwnedBehavior", Flags) != null,
            "Task13 same-room Home/Hive/Burrow executes through the Environment-owned behavior path");

        Type visibilityPolicy = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_VisibilityPolicy", true);
        Type weaponPerception = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_WeaponPerception", true);
        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalSignalBridge", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalThreatBridge", false) == null,
            "Task13 visual signal/threat bridges are retired after R2 central perception adoption");
        Check(visibilityPolicy.GetMethod("CanObserve", Flags) != null &&
              weaponPerception.GetMethod("TryFindIncomingProjectileFrom", Flags) != null,
            "Task13 Fog/DenseFog visibility and close projectile recognition now use shared R2 perception policy");
        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalVengeanceBridge", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalSocialBridge", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalIntegration", false) == null,
            "Task13 R5 retires internal Environment RuntimeDetour integration layers");
        Check(environmentalPolicy.GetMethod("AggressionAuthorized", Flags) != null &&
              environmentalPolicy.GetMethod("CombatMotivation", Flags) != null &&
              environmentalPolicy.GetMethod("AllowsHarassCandidate", Flags) != null &&
              environmentalPolicy.GetMethod("AdjustRoostDuration", Flags) != null &&
              environmentalPolicy.GetMethod("BlocksNeutralSocial", Flags) != null &&
              environmentalPolicy.GetMethod("FogNavigationFamiliarityScale", Flags) != null,
            "Task13 environmental effects are consumed through an explicit cross-domain policy API");

        Type hooks = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_RainWorldHooks", true);
        MethodInfo hooksEnable = hooks.GetMethod("Enable", Flags);
        MethodInfo hooksDisable = hooks.GetMethod("Disable", Flags);
        Check(MethodCallOffset(hooksEnable, behavior, "Reset") >= 0 &&
              MethodCallOffset(hooksEnable, roomRuntime, "Reset") >= 0 &&
              MethodCallOffset(hooksDisable, behavior, "Reset") >= 0 &&
              MethodCallOffset(hooksDisable, roomRuntime, "Reset") >= 0,
            "Task13 direct behavior/room runtimes remain wired into DesertBatfly lifecycle");

        Type signalKind = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SignalKind", true);
        Check(Enum.GetNames(signalKind).Length == 6,
            "Task13 does not add weather signal kinds to Task12 V1");

        Type threat = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyThreatRuntime", true);
        Type signals = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SignalRuntime", true);
        Type social = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySocialLife", true);
        MethodInfo updateAI = hooks.GetMethod("UpdateAI", Flags);
        int task11 = MethodCallOffset(updateAI, threat, "Update");
        int task12 = MethodCallOffset(updateAI, signals, "Update");
        int task13 = MethodCallOffset(updateAI, behavior, "Update");
        int task10 = MethodCallOffset(updateAI, social, "Update");
        Check(task11 >= 0 && task12 > task11 && task13 > task12 && task10 > task13,
            "realized pipeline stays Task11 -> Task12 -> Task13 -> Task10 after Task09 travel refusal");

        Type state = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_State", true);
        Check(state.GetFields(Flags).All(f =>
                f.Name.IndexOf("Environmental", StringComparison.OrdinalIgnoreCase) < 0 &&
                f.Name.IndexOf("HeatAgitation", StringComparison.OrdinalIgnoreCase) < 0 &&
                f.Name.IndexOf("FogMemory", StringComparison.OrdinalIgnoreCase) < 0 &&
                f.Name.IndexOf("SandstormMemory", StringComparison.OrdinalIgnoreCase) < 0),
            "Task13 current environmental state is not persisted in DB_State");

        foreach (Type type in new[]
                 {
                     behavior, roomRuntime, profile, environmentalPolicy,
                     visibilityPolicy, weaponPerception,
                     mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentExposure", true)
                 })
            Check(!TypeCallsTask13Forbidden(type),
                "Task13 type " + type.Name + " has no RoomSettings/Input/BodyChunk.vel or cross-room LeaveRoom ownership");

        Type debug = mod.GetType("DryCycle.Debugging.AI.DB_EnvironmentDebugSource", true);
        Check(debug != null,
            "Task13 Observatory source exists for weather/phase/anchor/heat/failure inspection");

        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySocialRoles", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyRoleScores", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.ExpressedSocialRole", false) == null,
            "Task13 does not restore rejected Task02 role runtime");

        Console.WriteLine(
            "Task 13 environment: six phases, DryCycle-only source boundary, bounded anchors, Fog ceiling, severe DenseFog temporary Task09 handoff, Heat axes, Sandstorm Home-retention policy, native Home/Burrow, pipeline, persistence and forbidden-ownership guards verified.");
    }

    private static bool TypeCallsTask13Forbidden(Type type)
    {
        foreach (MethodInfo method in type.GetMethods(
                     BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
                     BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            byte[] il = method.GetMethodBody()?.GetILAsByteArray();
            if (il == null || il.Length == 0) continue;
            int offset = 0;
            while (offset < il.Length)
            {
                OpCode opcode;
                byte first = il[offset++];
                if (first == 0xFE)
                {
                    if (offset >= il.Length) break;
                    opcode = MultiByteOpCode(il[offset++]);
                }
                else opcode = SingleByteOpCode(first);

                int operandOffset = offset;
                int operandSize = OperandSize(opcode.OperandType, il, operandOffset);
                if ((opcode == OpCodes.Call || opcode == OpCodes.Callvirt) && operandSize >= 4)
                {
                    try
                    {
                        MethodBase called = method.Module.ResolveMethod(BitConverter.ToInt32(il, operandOffset));
                        string owner = called?.DeclaringType?.FullName ?? string.Empty;
                        if (owner == "UnityEngine.Input") return true;
                        if (owner.IndexOf("RoomSettings", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                        if (called?.Name == "LeaveRoom" &&
                            owner.IndexOf("FlyAI", StringComparison.Ordinal) >= 0)
                            return true;
                    }
                    catch (ArgumentException) { }
                }
                else if (opcode == OpCodes.Stfld && operandSize >= 4)
                {
                    try
                    {
                        FieldInfo field = method.Module.ResolveField(BitConverter.ToInt32(il, operandOffset));
                        if (field?.DeclaringType == typeof(BodyChunk) && field.Name == "vel") return true;
                    }
                    catch (ArgumentException) { }
                }
                offset += operandSize;
            }
        }
        return false;
    }
}
