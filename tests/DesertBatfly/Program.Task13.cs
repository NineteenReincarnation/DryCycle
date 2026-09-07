using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

internal static partial class Program
{
    private static void RunTask13()
    {
        Type phase = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalPhase", true);
        string[] phases = Enum.GetNames(phase);
        string[] expectedPhases = { "Calm", "Advisory", "Preparation", "Sheltering", "Acute", "Recovery" };
        Check(phases.Length == 6 && expectedPhases.All(phases.Contains),
            "Task13 environmental context exposes exactly six approved phases");

        Type weather = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalWeather", true);
        foreach (string name in new[]
                 {
                     "None", "LightRain", "Fog", "DenseFog", "HeavyRain", "HeatWave",
                     "IntenseHeat", "Sandstorm", "DeathSandstorm", "DeathRain"
                 })
            Check(Enum.GetNames(weather).Contains(name), "Task13 weather profile includes " + name);

        Type context = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalRoomContext", true);
        foreach (string name in new[]
                 {
                     "WeatherSourceValid", "Weather", "WeatherId", "ActiveIntensity",
                     "ImmediateDanger", "ShelterUrgency", "TravelExposure", "ForecastTicks",
                     "Phase", "PhaseReason"
                 })
            Check(context.GetField(name, Flags) != null, "Task13 room context contains " + name);

        Type influence = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalInfluence", true);
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

        Type anchor = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyShelterAnchor", true);
        Check(anchor.GetField("Exposure", Flags) != null &&
              anchor.GetField("RoostCompatible", Flags) != null &&
              anchor.GetField("Crowding", Flags) != null,
            "Task13 keeps shelter quality, Roost compatibility and crowding as separate anchor data");

        Type roomRuntime = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalRoomRuntime", true);
        Check((int)roomRuntime.GetField("WeatherSampleInterval", Flags).GetRawConstantValue() == 20,
            "Task13 room weather sample is low-frequency");
        Check((int)roomRuntime.GetField("CrowdingSampleInterval", Flags).GetRawConstantValue() == 20,
            "Task13 crowding refresh is low-frequency");
        Check((int)roomRuntime.GetField("MaxAnchors", Flags).GetRawConstantValue() == 8,
            "Task13 room shelter anchor cache is bounded");
        Check(roomRuntime.GetMethod("TryChooseAnchor", Flags) != null &&
              roomRuntime.GetMethod("WeatherQuality", Flags) != null,
            "Task13 room runtime owns multi-anchor selection and weather-specific shelter quality");

        Type behavior = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalBehavior", true);
        Check((int)behavior.GetField("DecisionBaseInterval", Flags).GetRawConstantValue() >= 8,
            "Task13 individual environmental decisions are staggered/low-frequency");
        Check((float)behavior.GetField("AnchorSwitchMargin", Flags).GetRawConstantValue() >= 0.10f,
            "Task13 shelter anchor switching has hysteresis margin");
        Check(behavior.GetMethod("AllowsEnvironmentalDamageAttack", Flags) != null &&
              behavior.GetMethod("MigrationSuppression", Flags) != null &&
              behavior.GetMethod("VisibilityScale", Flags) != null,
            "Task13 exposes heat attack, migration suppression and visibility influences to existing systems");

        Type profile = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalProfile", true);
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
        Type ecologySample = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyWeatherEcologySample", true);
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

        Type denseFogBridge = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalDenseFogBridge", true);
        Check((float)denseFogBridge.GetField("DenseFogShelterDemand", Flags).GetRawConstantValue() >= 0.50f &&
              (float)denseFogBridge.GetField("DenseFogDisplacementStartIntensity", Flags).GetRawConstantValue() >= 0.80f,
            "Task13 DenseFog only hands severe realized fog to Task09 temporary refuge semantics");
        Check(denseFogBridge.GetMethod("SampleHook", Flags) != null &&
              denseFogBridge.GetMethod("ShelterDemandHook", Flags) != null,
            "Task13 DenseFog bridge modifies temporary shelter semantics without editing persistent migration memory");

        Type task09Bridge = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalTask09Bridge", true);
        Check(task09Bridge.GetMethod("ShouldSuppressNewMigration", Flags) != null &&
              task09Bridge.GetMethod("TryGetShelterFailureDebug", Flags) != null,
            "Task13 has a narrow Task09 bridge for migration timing and realized shelter failure");
        Check((int)task09Bridge.GetField("ShelterFailureMinTicks", Flags).GetRawConstantValue() >= 300,
            "Task13 LocalShelterFailure requires sustained realized failure");
        Check((int)task09Bridge.GetField("ShelterFailureReportCooldownTicks", Flags).GetRawConstantValue() >= 1200,
            "Task13 LocalShelterFailure reporting is bounded by cooldown");
        Check(task09Bridge.GetMethod("ShouldRecallHomeForSandstorm", Flags) != null &&
              task09Bridge.GetMethod("CanConsiderSandstormOutwardRefuge", Flags) != null &&
              task09Bridge.GetMethod("AcceptSandstormEmergencyRefuge", Flags) != null,
            "Task13 Sandstorm Task09 policy distinguishes early Home recall from narrow outward emergency refuge");
        Check((int)task09Bridge.GetField("SandstormEmergencyMaxHops", Flags).GetRawConstantValue() <= 2 &&
              (int)task09Bridge.GetField("DeathSandstormEmergencyMaxHops", Flags).GetRawConstantValue() <= 1,
            "Task13 Sandstorm outward emergency exception is deliberately short-range");
        Check((int)task09Bridge.GetField("SandstormHomeRecallMinimumLeadTicks", Flags).GetRawConstantValue() < advisory &&
              (int)task09Bridge.GetField("DeathSandstormHomeRecallMinimumLeadTicks", Flags).GetRawConstantValue() < advisory,
            "Task13 Sandstorm Home recall operates inside the bounded species forecast horizon");

        Type survivalBridge = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalSurvivalBridge", true);
        Check(survivalBridge.GetMethod("ShouldSeekHome", Flags) != null &&
              survivalBridge.GetMethod("ShouldBurrow", Flags) != null &&
              survivalBridge.GetMethod("HigherPriorityOwnsLocalGoal", Flags) != null,
            "Task13 HomeReturn/Burrow uses native FlyAI while preserving higher-priority local goals");

        Type socialBridge = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalSocialBridge", true);
        Type signalBridge = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalSignalBridge", true);
        Type threatBridge = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalThreatBridge", true);
        Type vengeanceBridge = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalVengeanceBridge", true);
        Check(signalBridge.GetMethod("TryPerceiveHook", Flags) != null,
            "Task13 Fog/DenseFog reduces only Task12 visual signal perception");
        Check(threatBridge.GetMethod("NearestVisiblePlayerHook", Flags) != null,
            "Task13 Fog/DenseFog reduces Task11 current visual recognition while keeping close projectile fallback");
        Check(vengeanceBridge.GetMethod("UpdateHook", Flags) != null,
            "Task13 hard survival suspends rather than clears Vengeance");

        Type integration = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalIntegration", true);
        Check(integration.GetMethod("CanHarassHook", Flags) != null &&
              integration.GetMethod("SteerHook", Flags) != null,
            "Task13 integrates environmental aggression and Fog navigation through existing DesertBatflyAI gates");
        Check(integration.GetMethod("FogNavigationFamiliarityScale", Flags) != null,
            "Task13 DenseFog navigation has an explicit Home/Hive familiarity mitigation path");

        Type hooks = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyHooks", true);
        MethodInfo hooksEnable = hooks.GetMethod("Enable", Flags);
        MethodInfo hooksDisable = hooks.GetMethod("Disable", Flags);
        Check(MethodCallOffset(hooksEnable, denseFogBridge, "Enable") >= 0 &&
              MethodCallOffset(hooksEnable, task09Bridge, "Enable") >= 0 &&
              MethodCallOffset(hooksEnable, survivalBridge, "Enable") >= 0,
            "Task13 DenseFog, Task09 and native survival bridges are wired into DesertBatfly lifecycle");
        Check(MethodCallOffset(hooksDisable, denseFogBridge, "Disable") >= 0 &&
              MethodCallOffset(hooksDisable, task09Bridge, "Disable") >= 0 &&
              MethodCallOffset(hooksDisable, survivalBridge, "Disable") >= 0,
            "Task13 auxiliary bridges are removed with DesertBatfly lifecycle");

        Type signalKind = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalKind", true);
        Check(Enum.GetNames(signalKind).Length == 6,
            "Task13 does not add weather signal kinds to Task12 V1");

        Type threat = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyThreatRuntime", true);
        Type signals = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalRuntime", true);
        Type social = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySocialLife", true);
        MethodInfo updateAI = hooks.GetMethod("UpdateAI", Flags);
        int task11 = MethodCallOffset(updateAI, threat, "Update");
        int task12 = MethodCallOffset(updateAI, signals, "Update");
        int task13 = MethodCallOffset(updateAI, behavior, "Update");
        int task10 = MethodCallOffset(updateAI, social, "Update");
        Check(task11 >= 0 && task12 > task11 && task13 > task12 && task10 > task13,
            "realized pipeline stays Task11 -> Task12 -> Task13 -> Task10 after Task09 travel refusal");

        Type state = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyState", true);
        Check(state.GetFields(Flags).All(f =>
                f.Name.IndexOf("Environmental", StringComparison.OrdinalIgnoreCase) < 0 &&
                f.Name.IndexOf("HeatAgitation", StringComparison.OrdinalIgnoreCase) < 0 &&
                f.Name.IndexOf("FogMemory", StringComparison.OrdinalIgnoreCase) < 0 &&
                f.Name.IndexOf("SandstormMemory", StringComparison.OrdinalIgnoreCase) < 0),
            "Task13 current environmental state is not persisted in DesertBatflyState");

        foreach (Type type in new[]
                 {
                     behavior, roomRuntime, profile, integration, socialBridge, signalBridge,
                     threatBridge, vengeanceBridge, denseFogBridge, task09Bridge, survivalBridge,
                     mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalExposure", true)
                 })
            Check(!TypeCallsTask13Forbidden(type),
                "Task13 type " + type.Name + " has no RoomSettings/Input/BodyChunk.vel or cross-room LeaveRoom ownership");

        Type debug = mod.GetType("DryCycle.Debugging.AI.DesertBatflyTask13DebugSource", true);
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
