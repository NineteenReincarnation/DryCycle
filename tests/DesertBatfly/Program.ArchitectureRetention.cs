using System;
using System.Reflection;

internal static partial class Program
{
    private static void RunArchitectureRetention()
    {
        Type hooks = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_RainWorldHooks", true);
        Type intimidation = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_FearRuntime", true);
        Type tactics = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_ThreatTactics", true);
        Type policy = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentalPolicy", true);
        Type ai = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_AI", true);
        Type combat = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CombatRuntime", true);
        Type social = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SocialRuntime", true);
        Type behavior = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentRuntime", true);
        Type roomEnvironment = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentRoomRuntime", true);
        Type colony = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_ColonyRuntime", true);
        Type travel = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_TravelRuntime", true);
        Type refuge = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_RefugePolicy", true);
        Type signal = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SignalRuntime", true);
        Type threat = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_ThreatRuntime", true);
        Type consumers = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EventConsumers", true);
        Type bond = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SocialBond", true);
        Type runtimePatch = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_RuntimePatch", true);
        Type sandbox = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_Sandbox", true);
        Type warp = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_WarpCompatibility", true);

        foreach (string removed in new[]
                 {
                     "DesertBatflyEnvironmentalDenseFogBridge",
                     "DesertBatflyEnvironmentalVengeanceBridge",
                     "DesertBatflyThreatVengeanceBridge",
                     "DesertBatflyEnvironmentalIntegration",
                     "DesertBatflyEnvironmentalSocialBridge",
                     "DesertBatflyEnvironmentalTravel/ColonyBridge",
                     "DesertBatflyEnvironmentalSurvivalBridge",
                     "DesertBatflySignalIntegration",
                     "DesertBatflySignalAcuteBridge",
                     "DesertBatflySignalDirectWitnessBridge",
                     "DesertBatflySignalThreatBridge",
                     "DesertBatflySignalVengeanceBridge"
                 })
            Check(mod.GetType("DryCycle.Creatures.DesertBatfly." + removed, false) == null,
                "R5 retention: retired adapter stays physically absent: " + removed);

        // B1: tactical semantics moved to the real Vengeance owner.
        Check(MethodCallOffset(intimidation.GetMethod("ForceFlight", Flags), tactics,
                  "AdjustExtremeVengeanceGoal") >= 0,
            "R5 retention B1: Vengeance still consumes Threat tactical geometry directly");

        // B2: Environmental integration was deleted only after every consumer gained a
        // direct read-only policy path.
        Check(MethodCallOffset(combat.GetMethod("CompleteCandidateScan", Flags), policy,
                  "CombatMotivation") >= 0 &&
              MethodCallOffset(combat.GetMethod("CanHarass", Flags), policy,
                  "AllowsHarassCandidate") >= 0,
            "R5 retention B2: Combat still consumes environmental motivation/permission");
        Check(MethodCallOffset(ai.GetMethod("TryPlanRoost", Flags), policy,
                  "RoostChanceScale") >= 0 &&
              MethodCallOffset(ai.GetMethod("ExecuteRoostOwned", Flags), policy,
                  "AdjustRoostDuration") >= 0,
            "R5 retention B2: Roost chance/duration still consume Environment policy");
        Check(MethodCallOffset(social.GetMethod("RefreshState", Flags), policy,
                  "SocialDriveScale") >= 0 &&
              MethodCallOffset(social.GetMethod("TryScheduleInteraction", Flags), policy,
                  "GroupCohesionScale") >= 0,
            "R5 retention B2: neutral social drive/group cohesion still consume Environment policy");

        // B3: the two removed Environment bridges are now on active R3/Travel/Colony paths.
        Check(MethodCallOffset(behavior.GetMethod("RefreshInfluence", Flags), behavior,
                  "ApplySecondaryLightRainMoisture") >= 0,
            "R5 retention B3: secondary LightRain moisture remains on live RefreshInfluence path");
        Check(MethodCallOffset(behavior.GetMethod("ApplyOwnedBehavior", Flags), behavior,
                  "ApplyNativeHomeAndBurrow") >= 0,
            "R5 retention B3: same-room Home/Hive/Burrow remains on Environment-owned execution");
        Check(MethodCallOffset(roomEnvironment.GetMethod("Update", Flags), roomEnvironment,
                  "ObserveLocalShelterFailure") >= 0,
            "R5 retention B3: LocalShelterFailure remains sampled by room runtime");
        Check(MethodCallOffset(colony.GetMethod("ScheduleMigrationBatch", Flags), policy,
                  "ShouldSuppressNewMigration") >= 0,
            "R5 retention B3: Travel/Colony migration start still consumes Environment timing veto");
        Check(MethodCallOffset(travel.GetMethod("EvaluateColonyWeather", Flags), policy,
                  "ShouldRecallHomeForSandstorm") >= 0,
            "R5 retention B3: Travel/Colony still performs early Sandstorm Home recall");
        MethodInfo findRefuge = refuge.GetMethod("TryFindEmergencyRefuge", Flags);
        Check(MethodCallOffset(findRefuge, policy, "CanConsiderSandstormOutwardRefuge") >= 0 &&
              MethodCallOffset(findRefuge, policy, "AcceptSandstormEmergencyRefuge") >= 0,
            "R5 retention B3: narrow Sandstorm outward-refuge exception remains filtered");

        // B4: Signals detours were folded into explicit owners. Preserve both behavior and
        // the subtle thresholds/order that existed before deletion.
        Check(MethodCallOffset(ai.GetMethod("RaiseLocalAlarm", Flags), signal, "EmitAlarm") >= 0,
            "R5 retention B4: AI local alarm still enters Signals explicitly");
        Check(MethodCallOffset(signal.GetMethod("ReceivePacket", Flags), signal, "ApplyAlarm") >= 0,
            "R5 retention B4: received Alarm still applies bounded Escape through SignalRuntime");
        Check(Math.Abs(Convert.ToSingle(signal.GetField("ThreatAlarmEscapeThreshold", Flags)
                          .GetRawConstantValue()) - 0.30f) < 0.0001f &&
              Math.Abs(Convert.ToSingle(signal.GetField("AnonymousAlarmEscapeThreshold", Flags)
                          .GetRawConstantValue()) - 0.34f) < 0.0001f,
            "R5 retention B4: concrete/anonymous Alarm escape thresholds remain 0.30/0.34");
        Type threatMemory = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_ThreatMemoryStore", true);
        Check(MethodCallOffset(signal.GetMethod("ResponseStrength", Flags), threatMemory, "For") >= 0,
            "R5 retention B4: Signal response still reads only receiver-owned Threat memory");
        Check(MethodCallOffset(threat.GetMethod("ReportExplosion", Flags), signal, "EmitAcuteAlarm") >= 0 &&
              MethodCallOffset(threat.GetMethod("BroadcastStartle", Flags), signal, "EmitAcuteAlarm") >= 0 &&
              MethodCallOffset(threat.GetMethod("BroadcastMassCasualty", Flags), signal, "EmitAcuteAlarm") >= 0,
            "R5 retention B4: explosion/startle/mass-casualty still publish real-position acute roots");

        MethodInfo onCapture = consumers.GetMethod("OnCapture", Flags);
        int captureFear = MethodCallOffset(onCapture, intimidation, "BroadcastPredatorCapture");
        int captureDistress = MethodCallOffset(onCapture, signal, "EmitDistress");
        int captureAlarm = MethodCallOffset(onCapture, signal, "EmitAlarm");
        Check(captureFear >= 0 && captureDistress > captureFear && captureAlarm > captureFear,
            "R5 retention B4: capture direct experience commits before social Distress/Alarm publication");
        Check(MethodCallOffset(combat.GetMethod("FindSocialHarassTarget", Flags), signal,
                  "TryGetInfluence") >= 0,
            "R5 retention B4: Combat still consumes HarassSignal interest");
        Check(MethodCallOffset(social.GetMethod("FindRoostSource", Flags), signal,
                  "TryGetInfluence") >= 0,
            "R5 retention B4: Social still consumes RoostCall interest");
        Check(MethodCallOffset(bond.GetMethod("OnBondPartnerDeath", Flags), bond,
                  "IsDirectDeathWitness") >= 0,
            "R5 retention B4: persistent grief still requires direct death witness");
        Check(MethodCallOffset(intimidation.GetMethod("ReceiveFear", Flags), signal, "EmitAlarm") >= 0 &&
              MethodCallOffset(intimidation.GetMethod("ArmVengeance", Flags), signal, "EmitRally") >= 0,
            "R5 retention B4: direct fear Alarm and synchronous Vengeance Rally remain explicit");

        // B5 classification: reflection is retained only for integration surfaces whose
        // removal would itself lose Sandbox/Warp functionality. Internal behavior does not
        // depend on this utility.
        Check(MethodCallOffset(sandbox.GetMethod("Enable", Flags), runtimePatch, "Create") >= 0 &&
              MethodCallOffset(sandbox.GetMethod("Enable", Flags), runtimePatch, "Patch") >= 0 &&
              MethodCallOffset(sandbox.GetMethod("Disable", Flags), runtimePatch, "UnpatchSelf") >= 0,
            "R5 retention B5: RuntimePatch remains required by Sandbox symbol integration");
        Check(MethodCallOffset(warp.GetMethod("Enable", Flags), runtimePatch, "Create") >= 0 &&
              MethodCallOffset(warp.GetMethod("Enable", Flags), runtimePatch, "Patch") >= 0 &&
              MethodCallOffset(warp.GetMethod("Disable", Flags), runtimePatch, "UnpatchSelf") >= 0,
            "R5 retention B5: RuntimePatch remains required by optional Warp compatibility");
        Check(MethodCallOffset(hooks.GetMethod("RainWorld_OnModsInit", Flags), sandbox, "Enable") >= 0 &&
              MethodCallOffset(hooks.GetMethod("RainWorld_OnModsInit", Flags), warp, "Enable") >= 0 &&
              MethodCallOffset(hooks.GetMethod("Disable", Flags), sandbox, "Disable") >= 0 &&
              MethodCallOffset(hooks.GetMethod("Disable", Flags), warp, "Disable") >= 0,
            "R5 retention B5: retained integrations remain wired into species lifecycle");

        Console.WriteLine(
            "Architecture bridge retention: B1-B4 deleted adapter semantics remain reachable from direct owners; B5 keeps only Sandbox/Warp reflection integration.");
    }
}
