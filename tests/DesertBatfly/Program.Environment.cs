using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

internal static partial class Program
{
    private static void RunEnvironment()
    {
        Type phase = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentPhase", true);
        string[] phases = Enum.GetNames(phase);
        string[] expectedPhases = { "Calm", "Advisory", "Preparation", "Sheltering", "Acute", "Recovery" };
        Check(phases.Length == 6 && expectedPhases.All(phases.Contains),
            "Environment environmental context exposes exactly six approved phases");

        Type weather = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentWeather", true);
        foreach (string name in new[]
                 {
                     "None", "LightRain", "Fog", "DenseFog", "HeavyRain", "HeatWave",
                     "IntenseHeat", "Sandstorm", "DeathSandstorm", "DeathRain"
                 })
            Check(Enum.GetNames(weather).Contains(name), "Environment weather profile includes " + name);

        Type context = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentContext", true);
        foreach (string name in new[]
                 {
                     "WeatherSourceValid", "Weather", "WeatherId", "ActiveIntensity",
                     "ImmediateDanger", "ShelterUrgency", "TravelExposure", "ForecastTicks",
                     "Phase", "PhaseReason"
                 })
            Check(context.GetField(name, Flags) != null, "Environment room context contains " + name);

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
            Check(influence.GetField(name, Flags) != null, "Environment influence contains " + name);

        Type anchor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_ShelterAnchor", true);
        Check(anchor.GetField("Exposure", Flags) != null &&
              anchor.GetField("RoostCompatible", Flags) != null &&
              anchor.GetField("Crowding", Flags) != null,
            "Environment keeps shelter quality, Roost compatibility and crowding as separate anchor data");

        Type roomRuntime = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentRoomRuntime", true);
        Check((int)roomRuntime.GetField("WeatherSampleInterval", Flags).GetRawConstantValue() == 20,
            "Environment room weather sample is low-frequency");
        Check((int)roomRuntime.GetField("CrowdingSampleInterval", Flags).GetRawConstantValue() == 20,
            "Environment crowding refresh is low-frequency");
        Check((int)roomRuntime.GetField("MaxAnchors", Flags).GetRawConstantValue() == 8,
            "Environment room shelter anchor cache is bounded");
        Check(roomRuntime.GetMethod("TryChooseAnchor", Flags) != null &&
              roomRuntime.GetMethod("WeatherQuality", Flags) != null,
            "Environment room runtime owns multi-anchor selection and weather-specific shelter quality");

        Type behavior = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentRuntime", true);
        Check((int)behavior.GetField("DecisionBaseInterval", Flags).GetRawConstantValue() >= 8,
            "Environment individual environmental decisions are staggered/low-frequency");
        Check((float)behavior.GetField("AnchorSwitchMargin", Flags).GetRawConstantValue() >= 0.10f,
            "Environment shelter anchor switching has hysteresis margin");
        Check(behavior.GetMethod("AllowsEnvironmentalDamageAttack", Flags) != null &&
              behavior.GetMethod("MigrationSuppression", Flags) != null &&
              behavior.GetMethod("VisibilityScale", Flags) != null,
            "Environment exposes heat attack, migration suppression and visibility influences to existing systems");

        Type profile = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentProfile", true);
        int advisory = (int)profile.GetField("SandstormAdvisoryTicks", Flags).GetRawConstantValue();
        int preparation = (int)profile.GetField("SandstormPreparationTicks", Flags).GetRawConstantValue();
        int strong = (int)profile.GetField("SandstormStrongPreparationTicks", Flags).GetRawConstantValue();
        Check(advisory > preparation && preparation > strong && strong > 0,
            "Environment Sandstorm has species-specific staged early anticipation");
        Check(profile.GetMethod("HeatAgitation", Flags) != null &&
              profile.GetMethod("HeatShelterDrive", Flags) != null &&
              profile.GetMethod("ThermalExhaustion", Flags) != null,
            "Environment Heat keeps agitation, shelter drive and exhaustion as separate axes");

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

        Type environmentalPolicy = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentalPolicy", true);
        Check(roomRuntime.GetMethod("TryGetShelterFailureDebug", Flags) != null,
            "Environment realized-room runtime owns LocalShelterFailure evidence directly");
        Check((int)roomRuntime.GetField("ShelterFailureMinTicks", Flags).GetRawConstantValue() >= 300 &&
              (int)roomRuntime.GetField("ShelterFailureReportCooldownTicks", Flags).GetRawConstantValue() >= 1200,
            "Environment LocalShelterFailure remains sustained and cooldown-bounded");
        Check(environmentalPolicy.GetMethod("ShouldSuppressNewMigration", Flags) != null &&
              environmentalPolicy.GetMethod("ShouldRecallHomeForSandstorm", Flags) != null &&
              environmentalPolicy.GetMethod("CanConsiderSandstormOutwardRefuge", Flags) != null &&
              environmentalPolicy.GetMethod("AcceptSandstormEmergencyRefuge", Flags) != null,
            "Travel/Colony directly consumes explicit Sandstorm environment policy");
        Check((int)environmentalPolicy.GetField("SandstormEmergencyMaxHops", Flags).GetRawConstantValue() <= 2 &&
              (int)environmentalPolicy.GetField("DeathSandstormEmergencyMaxHops", Flags).GetRawConstantValue() <= 1,
            "Environment Sandstorm outward emergency exception remains deliberately short-range");
        Check((int)environmentalPolicy.GetField("SandstormHomeRecallMinimumLeadTicks", Flags).GetRawConstantValue() < advisory &&
              (int)environmentalPolicy.GetField("DeathSandstormHomeRecallMinimumLeadTicks", Flags).GetRawConstantValue() < advisory,
            "Environment Sandstorm Home recall remains inside the bounded species forecast horizon");
        Check(behavior.GetMethod("ApplyOwnedBehavior", Flags) != null,
            "Environment same-room Home/Hive/Burrow executes through the Environment-owned behavior path");

        Type visibilityPolicy = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_VisibilityPolicy", true);
        Type weaponPerception = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_WeaponPerception", true);
        Check(visibilityPolicy.GetMethod("CanObserve", Flags) != null &&
              weaponPerception.GetMethod("TryFindIncomingProjectileFrom", Flags) != null,
            "Environment Fog/DenseFog visibility and close projectile recognition now use shared R2 perception policy");
        Check(environmentalPolicy.GetMethod("AggressionAuthorized", Flags) != null &&
              environmentalPolicy.GetMethod("CombatMotivation", Flags) != null &&
              environmentalPolicy.GetMethod("AllowsHarassCandidate", Flags) != null &&
              environmentalPolicy.GetMethod("AdjustRoostDuration", Flags) != null &&
              environmentalPolicy.GetMethod("BlocksNeutralSocial", Flags) != null &&
              environmentalPolicy.GetMethod("FogNavigationFamiliarityScale", Flags) != null,
            "Environment environmental effects are consumed through an explicit cross-domain policy API");

        Type hooks = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_RainWorldHooks", true);
        MethodInfo hooksEnable = hooks.GetMethod("Enable", Flags);
        MethodInfo hooksDisable = hooks.GetMethod("Disable", Flags);
        Check(MethodCallOffset(hooksEnable, behavior, "Reset") >= 0 &&
              MethodCallOffset(hooksEnable, roomRuntime, "Reset") >= 0 &&
              MethodCallOffset(hooksDisable, behavior, "Reset") >= 0 &&
              MethodCallOffset(hooksDisable, roomRuntime, "Reset") >= 0,
            "Environment direct behavior/room runtimes remain wired into DesertBatfly lifecycle");

        Type signalKind = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SignalKind", true);
        Check(Enum.GetNames(signalKind).Length == 6,
            "Environment does not add weather signal kinds to Signals V1");

        Type threat = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_ThreatRuntime", true);
        Type signals = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SignalRuntime", true);
        Type social = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SocialRuntime", true);
        Type ai = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_AI", true);
        Type feeding = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_DehydrationFeedingRuntime", true);
        Type arbiter = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_BehaviorArbiter", true);
        MethodInfo updateAI = hooks.GetMethod("UpdateAI", Flags);
        int environmentStage = MethodCallOffset(updateAI, behavior, "RefreshInfluence");
        int aiStage = MethodCallOffset(updateAI, ai, "RefreshDecisionState");
        int threatStage = MethodCallOffset(updateAI, threat, "RefreshState");
        int socialStage = MethodCallOffset(updateAI, social, "RefreshState");
        int feedingStage = MethodCallOffset(updateAI, feeding, "RefreshState");
        int resolveStage = MethodCallOffset(updateAI, arbiter, "ResolveFrame");
        Check(environmentStage >= 0 && aiStage > environmentStage &&
              threatStage > aiStage && socialStage > threatStage &&
              feedingStage > socialStage && resolveStage > feedingStage,
            "realized R3 refresh pipeline stays Environment -> AI decision -> Threat -> Social -> Feeding -> Arbiter");
        Check(threat.GetMethod("Update", Flags) == null &&
              behavior.GetMethod("Update", Flags) == null &&
              social.GetMethod("Update", Flags) == null &&
              signals.GetMethod("Update", Flags) != null,
            "Environment regression distinguishes retired combined-update facades from the retained post-resolution Signal information tick");

        Type state = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_State", true);
        Check(state.GetFields(Flags).All(f =>
                f.Name.IndexOf("Environmental", StringComparison.OrdinalIgnoreCase) < 0 &&
                f.Name.IndexOf("HeatAgitation", StringComparison.OrdinalIgnoreCase) < 0 &&
                f.Name.IndexOf("FogMemory", StringComparison.OrdinalIgnoreCase) < 0 &&
                f.Name.IndexOf("SandstormMemory", StringComparison.OrdinalIgnoreCase) < 0),
            "Environment current environmental state is not persisted in DB_State");

        foreach (Type type in new[]
                 {
                     behavior, roomRuntime, profile, environmentalPolicy,
                     visibilityPolicy, weaponPerception,
                     mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentExposure", true)
                 })
            Check(!TypeCallsEnvironmentForbidden(type),
                "Environment type " + type.Name + " has no RoomSettings/Input/BodyChunk.vel or cross-room LeaveRoom ownership");

        Type debug = mod.GetType("DryCycle.Debugging.AI.DB_EnvironmentDebugSource", true);
        Check(debug != null,
            "Environment Observatory source exists for weather/phase/anchor/heat/failure inspection");

        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CreatureSocialRoles", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CreatureRoleScores", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.ExpressedSocialRole", false) == null,
            "Environment does not restore rejected social-role design role runtime");

        Console.WriteLine(
            "Environment environment: six phases, DryCycle-only source boundary, bounded anchors, Fog ceiling, severe DenseFog temporary Travel/Colony handoff, Heat axes, Sandstorm Home-retention policy, native Home/Burrow, R3 refresh pipeline, persistence and forbidden-ownership guards verified.");
    }

    private static bool TypeCallsEnvironmentForbidden(Type type)
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
