from pathlib import Path

ROOT = Path('.')


def read(path):
    return (ROOT / path).read_text(encoding='utf-8')


def write(path, text):
    (ROOT / path).write_text(text, encoding='utf-8', newline='\n')


def replace_once(path, old, new):
    text = read(path)
    count = text.count(old)
    if count != 1:
        raise SystemExit(f'{path}: expected one match, found {count}: {old[:100]!r}')
    write(path, text.replace(old, new, 1))


# -----------------------------------------------------------------------------
# 1. Lifecycle: the two R5-B3 bridges disappear entirely.
# -----------------------------------------------------------------------------
hooks = 'src/Creatures/DesertBatfly/DesertBatflyHooks.cs'
replace_once(hooks,
'''        DesertBatflyEnvironmentalTask09Bridge.Enable();\n        DesertBatflyEnvironmentalSurvivalBridge.Enable();\n''', '')
replace_once(hooks,
'''        DesertBatflyEnvironmentalSurvivalBridge.Disable();\n        DesertBatflyEnvironmentalTask09Bridge.Disable();\n''', '')


# -----------------------------------------------------------------------------
# 2. Explicit Task13 -> Task09 read policy. Task09 remains destination/route owner.
# -----------------------------------------------------------------------------
policy = 'src/Creatures/DesertBatfly/Environmental/DB_EnvironmentalPolicy.cs'
replace_once(policy,
'''    private const float MinimumActivityRangeScale = 0.28f;\n''',
'''    private const float MinimumActivityRangeScale = 0.28f;\n\n    internal const int SandstormHomeRecallMinimumLeadTicks = 2800;\n    internal const int DeathSandstormHomeRecallMinimumLeadTicks = 3600;\n    internal const int SandstormEmergencyMinimumLeadTicks = 4200;\n    internal const int DeathSandstormEmergencyMinimumLeadTicks = 5200;\n    internal const int SandstormEmergencyMaxHops = 2;\n    internal const int DeathSandstormEmergencyMaxHops = 1;\n''')

policy_methods = r'''
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

'''
replace_once(policy,
'''    internal static float FogNavigationFamiliarityScale(\n''',
policy_methods + '''    internal static float FogNavigationFamiliarityScale(\n''')
replace_once(policy,
'''    private static float Stable01(int seed)\n''',
'''    private static bool IsSandstorm(DesertBatflyEnvironmentalWeather weather)\n        => weather is DesertBatflyEnvironmentalWeather.Sandstorm or\n                      DesertBatflyEnvironmentalWeather.DeathSandstorm;\n\n    private static float Stable01(int seed)\n''')


# -----------------------------------------------------------------------------
# 3. Task09 consumers query policy directly. No Reflection/RuntimeDetour.
# -----------------------------------------------------------------------------
colony = 'src/Creatures/DesertBatfly/DesertBatflyColonyRuntime.cs'
replace_once(colony,
'''    private static void ScheduleMigrationBatch(World world, DesertBatflyColonyState source, int cycle)\n    {\n        if (!DesertBatflyColonyMigration.CanScheduleBatch(source)) return;\n''',
'''    private static void ScheduleMigrationBatch(World world, DesertBatflyColonyState source, int cycle)\n    {\n        // Task09 remains the migration owner; Task13 only supplies a read-only timing veto.\n        if (DB_EnvironmentalPolicy.ShouldSuppressNewMigration(world, source)) return;\n        if (!DesertBatflyColonyMigration.CanScheduleBatch(source)) return;\n''')

travel = 'src/Creatures/DesertBatfly/DesertBatflyTravelNavigation.cs'
replace_once(travel,
'''            DesertBatflyWeatherEcologySample hazard = DesertBatflyWeatherEcology.Sample(world, home);\n            string evacuationKey = colony.RoomName + "|" + hazard.HazardKind + "|" + hazard.HazardId;\n''',
'''            DesertBatflyWeatherEcologySample hazard = DesertBatflyWeatherEcology.Sample(world, home);\n            DesertBatflyEnvironmentalWeather environmentalWeather =\n                DesertBatflyEnvironmentalProfile.Classify(hazard);\n            if (DB_EnvironmentalPolicy.ShouldRecallHomeForSandstorm(environmentalWeather, hazard))\n                EndEvacuationAndReturn(world, colony.RoomName, template);\n\n            string evacuationKey = colony.RoomName + "|" + hazard.HazardKind + "|" + hazard.HazardId;\n''')

refuge = 'src/Creatures/DesertBatfly/DesertBatflyRefuge.cs'
old_refuge = '''    internal static bool TryFindEmergencyRefuge(\n        World world,\n        AbstractRoom home,\n        CreatureTemplate template,\n        DesertBatflyWeatherEcologySample hazard,\n        float physicalCapability,\n        string knownRefuge,\n        Func<AbstractRoom, float> predatorRisk,\n        Func<AbstractRoom, float> crowding,\n        out DesertBatflyRefugeTarget target)\n    {\n        return TryFindEmergencyRefugeFrom(\n            world, home, home, template, hazard, physicalCapability, knownRefuge,\n            predatorRisk, crowding, -1, false, out target);\n    }\n'''
new_refuge = '''    internal static bool TryFindEmergencyRefuge(\n        World world,\n        AbstractRoom home,\n        CreatureTemplate template,\n        DesertBatflyWeatherEcologySample hazard,\n        float physicalCapability,\n        string knownRefuge,\n        Func<AbstractRoom, float> predatorRisk,\n        Func<AbstractRoom, float> crowding,\n        out DesertBatflyRefugeTarget target)\n    {\n        DesertBatflyEnvironmentalWeather weather = DesertBatflyEnvironmentalProfile.Classify(hazard);\n        bool sandstorm = weather is DesertBatflyEnvironmentalWeather.Sandstorm or\n                         DesertBatflyEnvironmentalWeather.DeathSandstorm;\n        if (!sandstorm)\n            return TryFindEmergencyRefugeFrom(\n                world, home, home, template, hazard, physicalCapability, knownRefuge,\n                predatorRisk, crowding, -1, false, out target);\n\n        target = default;\n        if (!DB_EnvironmentalPolicy.CanConsiderSandstormOutwardRefuge(\n                home, weather, hazard, out float homeQuality))\n            return false;\n        if (!TryFindEmergencyRefugeFrom(\n                world, home, home, template, hazard, physicalCapability, knownRefuge,\n                predatorRisk, crowding, -1, false, out DesertBatflyRefugeTarget candidate))\n            return false;\n        if (!DB_EnvironmentalPolicy.AcceptSandstormEmergencyRefuge(\n                weather, hazard, homeQuality, candidate))\n            return false;\n\n        target = candidate;\n        return true;\n    }\n'''
replace_once(refuge, old_refuge, new_refuge)


# -----------------------------------------------------------------------------
# 4. LocalShelterFailure belongs to realized-room Task13 state, not a Task09 bridge.
# -----------------------------------------------------------------------------
roomrt = 'src/Creatures/DesertBatfly/Environmental/DesertBatflyEnvironmentalRoomRuntime.cs'
replace_once(roomrt,
'''    internal const int AcuteMinimumHoldTicks = 120;\n''',
'''    internal const int AcuteMinimumHoldTicks = 120;\n    internal const int ShelterFailureMinTicks = 360;\n    internal const int ShelterFailureReportCooldownTicks = 1800;\n    internal const float MinimumUsableAnchorQuality = 0.44f;\n    internal const float SevereCrowdingPerAnchor = 5f;\n''')
replace_once(roomrt,
'''        internal bool AnchorsBuilt;\n''',
'''        internal bool AnchorsBuilt;\n        internal int ShelterFailureLastTick = int.MinValue;\n        internal int ShelterFailureAccumulatedTicks;\n        internal int ShelterFailureLastReportTick = int.MinValue;\n        internal string ShelterFailureReason = string.Empty;\n        internal float ShelterFailureSeverity;\n''')
replace_once(roomrt,
'''    internal static void Update(Room room)\n    {\n        if (room == null) return;\n        Refresh(states.GetValue(room, r => new RoomState(r)));\n    }\n''',
'''    internal static void Update(Room room)\n    {\n        if (room == null) return;\n        RoomState state = states.GetValue(room, r => new RoomState(r));\n        Refresh(state);\n        ObserveLocalShelterFailure(state);\n    }\n''')
replace_once(roomrt,
'''    internal static bool TryChooseAnchor(\n''',
'''    internal static bool TryGetShelterFailureDebug(\n        Room room,\n        out int accumulatedTicks,\n        out float severity,\n        out string reason)\n    {\n        accumulatedTicks = 0;\n        severity = 0f;\n        reason = string.Empty;\n        if (room == null || !states.TryGetValue(room, out RoomState state)) return false;\n        accumulatedTicks = state.ShelterFailureAccumulatedTicks;\n        severity = state.ShelterFailureSeverity;\n        reason = state.ShelterFailureReason;\n        return accumulatedTicks > 0 || severity > 0f;\n    }\n\n    internal static bool TryChooseAnchor(\n''')

failure_methods = r'''
    private static void ObserveLocalShelterFailure(RoomState state)
    {
        Room room = state?.Room;
        if (room?.abstractRoom == null || room.world == null || room.game == null) return;

        int tick = room.game.clock;
        int elapsed = state.ShelterFailureLastTick == int.MinValue
            ? 1
            : Mathf.Clamp(tick - state.ShelterFailureLastTick, 1, 60);
        state.ShelterFailureLastTick = tick;

        DesertBatflyEnvironmentalRoomContext context = state.Context;
        if (!SeriousShelterFailureWeather(context))
        {
            state.ShelterFailureAccumulatedTicks = Mathf.Max(
                0, state.ShelterFailureAccumulatedTicks - elapsed * 3);
            state.ShelterFailureSeverity = 0f;
            state.ShelterFailureReason = "no serious active Task13 shelter demand";
            return;
        }

        float bestQuality = 0f;
        bool anyUsable = false;
        bool allCrowded = state.Anchors.Count > 0;
        for (int i = 0; i < state.Anchors.Count; i++)
        {
            DesertBatflyShelterAnchor anchor = state.Anchors[i];
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

        DesertBatflyColonyState colony = DesertBatflyColonyRuntime.TryGetColony(room.abstractRoom);
        if (colony == null) return;
        DesertBatflyColonyRuntime.ReportExternalRefuge(
            colony.RoomName,
            null,
            Mathf.Clamp(state.ShelterFailureSeverity, 0.12f, 0.80f));
        state.ShelterFailureLastReportTick = tick;
        state.ShelterFailureAccumulatedTicks = 0;
    }

    private static bool SeriousShelterFailureWeather(
        in DesertBatflyEnvironmentalRoomContext context)
    {
        if (!context.WeatherSourceValid) return false;
        if (context.Weather is DesertBatflyEnvironmentalWeather.LightRain or
            DesertBatflyEnvironmentalWeather.Fog)
            return false;
        return context.Phase is DesertBatflyEnvironmentalPhase.Sheltering or
                   DesertBatflyEnvironmentalPhase.Acute ||
               (context.Phase == DesertBatflyEnvironmentalPhase.Preparation &&
                context.ShelterUrgency >= 0.68f);
    }

'''
replace_once(roomrt,
'''    private static void Refresh(RoomState state)\n''',
failure_methods + '''    private static void Refresh(RoomState state)\n''')


# -----------------------------------------------------------------------------
# 5. Same-room Home/Hive/Burrow and secondary moisture become first-class behavior.
# -----------------------------------------------------------------------------
behavior = 'src/Creatures/DesertBatfly/Environmental/DesertBatflyEnvironmentalBehavior.cs'
replace_once(behavior,
'''        internal DesertBatflyEnvironmentalPhase LastPhase = DesertBatflyEnvironmentalPhase.Calm;\n''',
'''        internal DesertBatflyEnvironmentalPhase LastPhase = DesertBatflyEnvironmentalPhase.Calm;\n        internal int LastSecondaryMoistureTick = int.MinValue;\n''')
replace_once(behavior,
'''        if (state.LastDecisionTick == int.MinValue || tick - state.LastDecisionTick >= interval)\n        {\n            state.LastDecisionTick = tick;\n            Recompute(bat, state, tick);\n        }\n    }\n''',
'''        if (state.LastDecisionTick == int.MinValue || tick - state.LastDecisionTick >= interval)\n        {\n            state.LastDecisionTick = tick;\n            Recompute(bat, state, tick);\n        }\n        ApplySecondaryLightRainMoisture(bat, state, tick);\n    }\n''')
replace_once(behavior,
'''        if (!owns) return false;\n\n        ApplyLocalBehavior(bat, state);\n        return true;\n''',
'''        if (!owns) return false;\n\n        if (ApplyNativeHomeAndBurrow(bat, state.Influence)) return true;\n        ApplyLocalBehavior(bat, state);\n        return true;\n''')

survival_methods = r'''
    private static void ApplySecondaryLightRainMoisture(DesertBatfly bat, State state, int tick)
    {
        if (bat?.room == null || bat.mainBodyChunk == null || bat.DesertState.Thirst <= 0f || state == null)
            return;

        DesertBatflyEnvironmentalRoomRuntime.RoomState roomState =
            DesertBatflyEnvironmentalRoomRuntime.For(bat.room);
        if (roomState == null ||
            roomState.Context.Weather is DesertBatflyEnvironmentalWeather.LightRain or
                DesertBatflyEnvironmentalWeather.HeavyRain ||
            roomState.WeatherAxes.LightRainIntensity <= 0f)
            return;

        const int secondaryMoistureIntervalTicks = 14;
        if (state.LastSecondaryMoistureTick != int.MinValue &&
            tick - state.LastSecondaryMoistureTick < secondaryMoistureIntervalTicks)
            return;
        state.LastSecondaryMoistureTick = tick;

        IntVector2 tile = bat.room.GetTilePosition(bat.mainBodyChunk.pos);
        DesertBatflyEnvironmentalExposureSample exposure =
            DesertBatflyEnvironmentalExposure.Sample(bat.room, tile, 1f);
        if (exposure.RainExposure < 0.55f) return;

        float rain = roomState.WeatherAxes.LightRainIntensity;
        float relief = DesertBatflyTuning.ThirstPerTick * 2.15f * rain * exposure.RainExposure;
        bat.DesertState.Thirst = Mathf.Max(0f, bat.DesertState.Thirst - relief);
    }

    private static bool ApplyNativeHomeAndBurrow(
        DesertBatfly bat,
        in DesertBatflyEnvironmentalInfluence influence)
    {
        if (bat?.room?.aimap == null || bat.AI == null || bat.dead || !bat.Consious ||
            bat.inShortcut || bat.Emergence?.Active == true)
            return false;
        bool seekHome = DB_EnvironmentalPolicy.ShouldSeekHome(influence);
        bool burrow = DB_EnvironmentalPolicy.ShouldBurrow(influence);
        if (!seekHome && !burrow) return false;
        if (bat.room.hives == null || bat.room.hives.Length == 0) return false;

        int bestMap = -1;
        int bestHive = -1;
        int bestDistance = int.MaxValue;
        IntVector2 current = bat.room.GetTilePosition(bat.mainBodyChunk.pos);
        for (int i = 0; i < bat.room.hives.Length; i++)
        {
            IntVector2[] hive = bat.room.hives[i];
            if (hive == null || hive.Length == 0) continue;
            int map = bat.room.exitAndDenIndex.Length + i;
            int distance = bat.room.aimap.ExitDistanceForCreature(current, map, bat.Template);
            if (distance < 0 || distance >= bestDistance) continue;
            bestDistance = distance;
            bestHive = i;
            bestMap = map;
        }
        if (bestHive < 0 || bestMap < 0) return false;

        bool onHiveTile = bat.room.GetTile(bat.mainBodyChunk.pos).hive;
        if (onHiveTile && burrow)
        {
            DesertBatflySocialLife.CancelForPriority(bat, "Task13 environmental Burrow priority");
            bat.DesertAI.CancelAttack();
            bat.AI.ChangeBehavior(FlyAI.Behavior.Burrow);
            bat.burrowOrHangSpot = bat.mainBodyChunk.pos;
            bat.movMode = Fly.MovementMode.Burrow;
            bat.AI.afraid = Mathf.Max(bat.AI.afraid, influence.HardSurvival ? 1.25f : 0.62f);
            return true;
        }

        if (!seekHome && influence.BurrowDrive < 0.45f) return false;
        if (bat.DesertAI.FormalAttack && !influence.HardSurvival) return false;

        DesertBatflySocialLife.CancelForPriority(bat, "Task13 same-room Home/Hive retreat");
        if (influence.HardSurvival) bat.DesertAI.CancelAttack();
        bat.AI.leaveRoomDijkstra = -1;
        bat.AI.followingDijkstraMap = bestMap;
        Vector2 nextGoal = bat.AI.ProgressLocalGoalAlongDijkstraMap(bat.AI.localGoal, bestMap);
        DB_BehaviorOwner owner = influence.HardSurvival
            ? DB_BehaviorOwner.EnvironmentHardSurvival
            : DB_BehaviorOwner.EnvironmentLocalSurvival;
        DB_FlightMotor.TryGuideNative(bat, owner, nextGoal);
        bat.AI.afraid = Mathf.Max(bat.AI.afraid, influence.HardSurvival ? 1.10f : 0.35f);
        return true;
    }

'''
replace_once(behavior,
'''    private static void ApplyLightRainMoisture(\n''',
survival_methods + '''    private static void ApplyLightRainMoisture(\n''')


# -----------------------------------------------------------------------------
# 6. Debug + tests now protect direct ownership, not bridge existence.
# -----------------------------------------------------------------------------
debug = 'src/Debug/AIDebugger/Sources/DesertBatflyTask13DebugSource.cs'
replace_once(debug,
'''        bool hasFailure = DesertBatflyEnvironmentalTask09Bridge.TryGetShelterFailureDebug(\n''',
'''        bool hasFailure = DesertBatflyEnvironmentalRoomRuntime.TryGetShelterFailureDebug(\n''')

task13 = 'tests/DesertBatfly/Program.Task13.cs'
old_task13 = '''        Type task09Bridge = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalTask09Bridge", true);\n        Check(task09Bridge.GetMethod("ShouldSuppressNewMigration", Flags) != null &&\n              task09Bridge.GetMethod("TryGetShelterFailureDebug", Flags) != null,\n            "Task13 has a narrow Task09 bridge for migration timing and realized shelter failure");\n        Check((int)task09Bridge.GetField("ShelterFailureMinTicks", Flags).GetRawConstantValue() >= 300,\n            "Task13 LocalShelterFailure requires sustained realized failure");\n        Check((int)task09Bridge.GetField("ShelterFailureReportCooldownTicks", Flags).GetRawConstantValue() >= 1200,\n            "Task13 LocalShelterFailure reporting is bounded by cooldown");\n        Check(task09Bridge.GetMethod("ShouldRecallHomeForSandstorm", Flags) != null &&\n              task09Bridge.GetMethod("CanConsiderSandstormOutwardRefuge", Flags) != null &&\n              task09Bridge.GetMethod("AcceptSandstormEmergencyRefuge", Flags) != null,\n            "Task13 Sandstorm Task09 policy distinguishes early Home recall from narrow outward emergency refuge");\n        Check((int)task09Bridge.GetField("SandstormEmergencyMaxHops", Flags).GetRawConstantValue() <= 2 &&\n              (int)task09Bridge.GetField("DeathSandstormEmergencyMaxHops", Flags).GetRawConstantValue() <= 1,\n            "Task13 Sandstorm outward emergency exception is deliberately short-range");\n        Check((int)task09Bridge.GetField("SandstormHomeRecallMinimumLeadTicks", Flags).GetRawConstantValue() < advisory &&\n              (int)task09Bridge.GetField("DeathSandstormHomeRecallMinimumLeadTicks", Flags).GetRawConstantValue() < advisory,\n            "Task13 Sandstorm Home recall operates inside the bounded species forecast horizon");\n\n        Type survivalBridge = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalSurvivalBridge", true);\n        Check(survivalBridge.GetMethod("ShouldSeekHome", Flags) != null &&\n              survivalBridge.GetMethod("ShouldBurrow", Flags) != null &&\n              survivalBridge.GetMethod("HigherPriorityOwnsLocalGoal", Flags) != null,\n            "Task13 HomeReturn/Burrow uses native FlyAI while preserving higher-priority local goals");\n\n        Type environmentalPolicy = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentalPolicy", true);\n'''
new_task13 = '''        Type environmentalPolicy = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentalPolicy", true);\n        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalTask09Bridge", false) == null &&\n              mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalSurvivalBridge", false) == null,\n            "Task13 R5 retires Task09/native-survival RuntimeDetour bridges");\n        Check(roomRuntime.GetMethod("TryGetShelterFailureDebug", Flags) != null,\n            "Task13 realized-room runtime owns LocalShelterFailure evidence directly");\n        Check((int)roomRuntime.GetField("ShelterFailureMinTicks", Flags).GetRawConstantValue() >= 300 &&\n              (int)roomRuntime.GetField("ShelterFailureReportCooldownTicks", Flags).GetRawConstantValue() >= 1200,\n            "Task13 LocalShelterFailure remains sustained and cooldown-bounded");\n        Check(environmentalPolicy.GetMethod("ShouldSuppressNewMigration", Flags) != null &&\n              environmentalPolicy.GetMethod("ShouldRecallHomeForSandstorm", Flags) != null &&\n              environmentalPolicy.GetMethod("CanConsiderSandstormOutwardRefuge", Flags) != null &&\n              environmentalPolicy.GetMethod("AcceptSandstormEmergencyRefuge", Flags) != null,\n            "Task09 directly consumes explicit Sandstorm environment policy");\n        Check((int)environmentalPolicy.GetField("SandstormEmergencyMaxHops", Flags).GetRawConstantValue() <= 2 &&\n              (int)environmentalPolicy.GetField("DeathSandstormEmergencyMaxHops", Flags).GetRawConstantValue() <= 1,\n            "Task13 Sandstorm outward emergency exception remains deliberately short-range");\n        Check((int)environmentalPolicy.GetField("SandstormHomeRecallMinimumLeadTicks", Flags).GetRawConstantValue() < advisory &&\n              (int)environmentalPolicy.GetField("DeathSandstormHomeRecallMinimumLeadTicks", Flags).GetRawConstantValue() < advisory,\n            "Task13 Sandstorm Home recall remains inside the bounded species forecast horizon");\n        Check(behavior.GetMethod("ApplyOwnedBehavior", Flags) != null,\n            "Task13 same-room Home/Hive/Burrow executes through the Environment-owned behavior path");\n\n'''
replace_once(task13, old_task13, new_task13)
replace_once(task13,
'''        Check(MethodCallOffset(hooksEnable, task09Bridge, "Enable") >= 0 &&\n              MethodCallOffset(hooksEnable, survivalBridge, "Enable") >= 0,\n            "Task13 remaining Task09/native-survival adapters are wired into DesertBatfly lifecycle");\n        Check(MethodCallOffset(hooksDisable, task09Bridge, "Disable") >= 0 &&\n              MethodCallOffset(hooksDisable, survivalBridge, "Disable") >= 0,\n            "Task13 remaining auxiliary adapters are removed with DesertBatfly lifecycle");\n''',
'''        Check(MethodCallOffset(hooksEnable, behavior, "Reset") >= 0 &&\n              MethodCallOffset(hooksEnable, roomRuntime, "Reset") >= 0 &&\n              MethodCallOffset(hooksDisable, behavior, "Reset") >= 0 &&\n              MethodCallOffset(hooksDisable, roomRuntime, "Reset") >= 0,\n            "Task13 direct behavior/room runtimes remain wired into DesertBatfly lifecycle");\n''')
replace_once(task13,
'''                     visibilityPolicy, weaponPerception, task09Bridge, survivalBridge,\n''',
'''                     visibilityPolicy, weaponPerception,\n''')

r5test = 'tests/DesertBatfly/Program.Task14R5.cs'
replace_once(r5test,
'''        Check(policy.GetMethod("AggressionAuthorized", Flags) != null &&\n              policy.GetMethod("CombatMotivation", Flags) != null &&\n              policy.GetMethod("AllowsHarassCandidate", Flags) != null &&\n              policy.GetMethod("AdjustRoostDuration", Flags) != null &&\n              policy.GetMethod("BlocksNeutralSocial", Flags) != null,\n            "R5 explicit environmental policy replaces mutation/detour based cross-domain behavior");\n\n        Console.WriteLine("Task14 R5 B2: Environment Combat/Roost/Social integration is explicit; old detours and temporary Thirst spoofing are removed.");\n''',
'''        Check(policy.GetMethod("AggressionAuthorized", Flags) != null &&\n              policy.GetMethod("CombatMotivation", Flags) != null &&\n              policy.GetMethod("AllowsHarassCandidate", Flags) != null &&\n              policy.GetMethod("AdjustRoostDuration", Flags) != null &&\n              policy.GetMethod("BlocksNeutralSocial", Flags) != null &&\n              policy.GetMethod("ShouldSuppressNewMigration", Flags) != null &&\n              policy.GetMethod("ShouldRecallHomeForSandstorm", Flags) != null &&\n              policy.GetMethod("CanConsiderSandstormOutwardRefuge", Flags) != null &&\n              policy.GetMethod("AcceptSandstormEmergencyRefuge", Flags) != null,\n            "R5 explicit environmental policy replaces mutation/detour based cross-domain behavior");\n        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalTask09Bridge", false) == null &&\n              mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalSurvivalBridge", false) == null,\n            "R5 B3 physically removes Task09 and Survival RuntimeDetour bridges");\n\n        Type roomRuntime = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalRoomRuntime", true);\n        Type behavior = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalBehavior", true);\n        Check(roomRuntime.GetMethod("TryGetShelterFailureDebug", Flags) != null &&\n              behavior.GetMethod("ApplyOwnedBehavior", Flags) != null,\n            "R5 B3 keeps LocalShelterFailure and same-room survival in their direct Task13 owners");\n\n        Console.WriteLine("Task14 R5 B3: Task09 and same-room survival consume explicit Environment policy/behavior; both RuntimeDetour bridges are removed.");\n''')


# -----------------------------------------------------------------------------
# 7. Status document.
# -----------------------------------------------------------------------------
status = 'docs/Discussion/Task_14_R5_BridgeDebtStatus.txt'
text = read(status)
text = text.replace('Revision: R5-B2 / 2026-09-07', 'Revision: R5-B3 / 2026-09-07', 1)
text = text.replace('Status: 【R5 进行中 / B2 Environmental detour debt removed】',
                    'Status: 【R5 进行中 / B3 Task09 + same-room survival bridges removed】', 1)
text += '''\n\n======================================================================\n6. R5-B3 closed\n======================================================================\n\n- DesertBatflyEnvironmentalTask09Bridge.cs DELETED.\n- DesertBatflyEnvironmentalSurvivalBridge.cs DELETED.\n- Task09 remains the sole cross-room destination/route/ReturnHome/Migration owner. It now\n  directly queries DB_EnvironmentalPolicy for Sandstorm migration timing, early Home recall\n  and the narrow outward-refuge exception. No Reflection is used to invoke ReturnHome.\n- LocalShelterFailure observation moved into DesertBatflyEnvironmentalRoomRuntime; only the\n  bounded evidence report crosses into DesertBatflyColonyRuntime.\n- Same-room Home/Hive/Burrow execution moved into DesertBatflyEnvironmentalBehavior's\n  Environment-owned execution path, using native FlyAI Dijkstra/Burrow plus DB_FlightMotor.\n- Secondary LightRain moisture moved into RefreshInfluence with its original bounded interval;\n  it no longer depends on a detour around an obsolete EnvironmentalBehavior.Update entry.\n- Task13 debug and regression guards now reference direct owners/policy rather than adapters.\n\nRemaining R5 bridge debt is the Signal bridge family plus DesertBatflyRuntimePatch\nclassification.\n'''
write(status, text)


# -----------------------------------------------------------------------------
# 8. Physical deletion and self-cleanup.
# -----------------------------------------------------------------------------
for doomed in [
    'src/Creatures/DesertBatfly/Environmental/DesertBatflyEnvironmentalTask09Bridge.cs',
    'src/Creatures/DesertBatfly/Environmental/DesertBatflyEnvironmentalSurvivalBridge.cs',
    'scripts/task14_r5_b3_apply.py',
    '.github/workflows/task14-r5-b3.yml',
]:
    p = ROOT / doomed
    if p.exists():
        p.unlink()
