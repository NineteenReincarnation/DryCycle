using System;
using System.Reflection;

internal static partial class Program
{
    private static void RunTask09()
    {
        Type colonyType = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_ColonyState", true);
        Type migrationType = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_MigrationPolicy", true);
        Type routePlannerType = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_WorldRoutePlanner", true);
        Type travelPurposeType = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_TravelPurpose", true);
        Type travelNavigationType = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_TravelRuntime", true);
        Type travelDebugType = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_TravelDebugState", true);
        Type refugeType = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_RefugePolicy", true);

        object colony = Activator.CreateInstance(
            colonyType, Flags, null, new object[] { "SU", "SU_TASK09_TEST", 14 }, null);

        FieldInfo currentPopulation = colonyType.GetField("CurrentPopulation", Flags);
        FieldInfo hardMinimum = colonyType.GetField("HardMinimumPersistence", Flags);
        FieldInfo recoveryCeiling = colonyType.GetField("NaturalRecoveryCeiling", Flags);
        FieldInfo environmental = colonyType.GetField("EnvironmentalPressure", Flags);
        FieldInfo predator = colonyType.GetField("PredatorPressure", Flags);
        FieldInfo mortality = colonyType.GetField("MortalityPressure", Flags);
        FieldInfo shelterFailure = colonyType.GetField("ShelterFailureMemory", Flags);

        int floor = (int)hardMinimum.GetValue(colony);
        int ceiling = (int)recoveryCeiling.GetValue(colony);
        Check(floor >= 2 && floor < 14, "Task09 hard minimum is a real low population floor");
        Check(ceiling >= floor && ceiling < 14, "Task09 natural recovery ceiling stays below preferred population");

        MethodInfo nextMigration = colonyType.GetMethod("NextMigrationState", Flags);
        Check(!(bool)nextMigration.Invoke(null, new object[] { false, 0.69f }), "Task09 migration does not begin below entry threshold");
        Check((bool)nextMigration.Invoke(null, new object[] { false, 0.71f }), "Task09 migration begins above entry threshold");
        Check((bool)nextMigration.Invoke(null, new object[] { true, 0.50f }), "Task09 migration hysteresis retains state in deadband");
        Check(!(bool)nextMigration.Invoke(null, new object[] { true, 0.39f }), "Task09 migration stops below stop threshold");

        MethodInfo relativeStress = colonyType.GetMethod("ComputeRelativeHabitatStress", Flags);
        float equalRegional = (float)relativeStress.Invoke(null, new object[] { 0.78f, 0.76f, 0.15f });
        float localFailure = (float)relativeStress.Invoke(null, new object[] { 0.90f, 0.42f, 0.55f });
        Check(equalRegional < 0.10f, "Task09 equally bad regional weather does not create migration carousel pressure");
        Check(localFailure > equalRegional + 0.35f, "Task09 locally worse habitat creates strong relative pressure");

        environmental.SetValue(colony, 1f);
        predator.SetValue(colony, 1f);
        mortality.SetValue(colony, 1f);
        shelterFailure.SetValue(colony, 1f);
        currentPopulation.SetValue(colony, Math.Max(0, floor - 1));
        colonyType.GetMethod("RecalculateRecoveryCeiling", Flags).Invoke(colony, null);
        int harshCeiling = (int)recoveryCeiling.GetValue(colony);
        float harshChance = (float)colonyType.GetMethod("RecoveryChance", Flags).Invoke(colony, null);
        Check(harshCeiling >= floor, "Task09 severe environment never lowers background recovery below hard persistence floor");
        Check(harshChance > 0f, "Task09 severe environment retains non-zero minimum recovery chance");

        currentPopulation.SetValue(colony, harshCeiling);
        float atCeilingChance = (float)colonyType.GetMethod("RecoveryChance", Flags).Invoke(colony, null);
        Check(atCeilingChance == 0f, "Task09 background recovery stops exactly at the current recovery ceiling");
        currentPopulation.SetValue(colony, floor);
        int noEmptyBatch = (int)colonyType.GetMethod("RecommendedBatchSize", Flags).Invoke(colony, null);
        Check(noEmptyBatch == 0, "Task09 migration batch cannot empty a colony below hard minimum");
        currentPopulation.SetValue(colony, 14);
        int normalBatch = (int)colonyType.GetMethod("RecommendedBatchSize", Flags).Invoke(colony, null);
        Check(normalBatch >= 1 && normalBatch <= 3, "Task09 migration uses a small bounded batch");

        Type personalityType = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_Personality", true);
        object personality = Activator.CreateInstance(personalityType, Flags, null, new object[] { 909 }, null);
        MethodInfo propensity = migrationType.GetMethod("IndividualPropensity", Flags);
        float healthyPropensity = (float)propensity.Invoke(null, new object[]
        {
            personality, 0.90f, false, false, 0.35f, 0.20f, 0.55f, 20, int.MinValue
        });
        float severePropensity = (float)propensity.Invoke(null, new object[]
        {
            personality, 0.90f, true, false, 0.35f, 0.20f, 0.55f, 20, int.MinValue
        });
        float cooldownPropensity = (float)propensity.Invoke(null, new object[]
        {
            personality, 0.90f, false, false, 0.35f, 0.20f, 0.55f, 20, 19
        });
        Check(healthyPropensity > 0f, "Task09 healthy individual has continuous migration propensity");
        Check(severePropensity == 0f, "Task09 severe wing injury blocks long migration selection");
        Check(cooldownPropensity == 0f, "Task09 individual migration cooldown prevents immediate ping-pong");

        MethodInfo switchDestination = migrationType.GetMethod("ShouldSwitchDestination", Flags);
        Check(!(bool)switchDestination.Invoke(null, new object[] { 0.60f, 0.68f, 0.14f }), "Task09 destination margin prevents small score jitter");
        Check((bool)switchDestination.Invoke(null, new object[] { 0.60f, 0.76f, 0.14f }), "Task09 destination changes only for a materially better target");

        object destination = Activator.CreateInstance(
            colonyType, Flags, null, new object[] { "SU", "SU_TASK09_DEST", 14 }, null);
        colonyType.GetField("CurrentPopulation", Flags).SetValue(destination, 7);
        MethodInfo destinationSuitability = migrationType.GetMethod("DestinationSuitability", Flags);
        float baseDestination = (float)destinationSuitability.Invoke(null,
            new object[] { destination, 0.80f, 0.25f, 0f, 0f });
        float familiarDestination = (float)destinationSuitability.Invoke(null,
            new object[] { destination, 0.80f, 0.25f, 1f, 0f });
        float bondedDestination = (float)destinationSuitability.Invoke(null,
            new object[] { destination, 0.80f, 0.25f, 0f, 1f });
        Check(familiarDestination > baseDestination && familiarDestination - baseDestination <= 0.06f,
            "Task09 former-colony familiarity is a real but deliberately weak return bonus");
        Check(bondedDestination > baseDestination && bondedDestination - baseDestination <= 0.06f,
            "Task09 bond-partner presence is a weak destination bonus, not a hard command");

        MethodInfo edgeCost = routePlannerType.GetMethod("EdgeCost", Flags);
        MethodInfo needsReplan = routePlannerType.GetMethod("NeedsSafetyReplan", Flags);
        object emergency = Enum.Parse(travelPurposeType, "EmergencyRefuge");
        object returnHome = Enum.Parse(travelPurposeType, "ReturnHome");
        object migration = Enum.Parse(travelPurposeType, "ColonyMigration");
        float emergencySafe = (float)edgeCost.Invoke(null, new object[] { emergency, 0.10f });
        float emergencyDanger = (float)edgeCost.Invoke(null, new object[] { emergency, 0.80f });
        float migrationDanger = (float)edgeCost.Invoke(null, new object[] { migration, 0.80f });
        Check(emergencyDanger > emergencySafe, "Task09 route cost increases with travel danger");
        Check(emergencyDanger > migrationDanger, "Task09 emergency refuge routing penalizes danger more strongly than permanent migration");
        Check(!(bool)needsReplan.Invoke(null, new object[] { emergency, 0.40f }),
            "Task09 committed emergency route ignores harmless risk jitter");
        Check((bool)needsReplan.Invoke(null, new object[] { emergency, 0.90f }),
            "Task09 emergency route replans after a materially unsafe next room");
        Check(!(bool)needsReplan.Invoke(null, new object[] { returnHome, 0.70f }),
            "Task09 return route commitment prevents unnecessary replanning");
        Check(!(bool)needsReplan.Invoke(null, new object[] { migration, 0.80f }),
            "Task09 permanent migration tolerates moderate travel risk without route oscillation");

        MethodInfo canArrive = refugeType.GetMethod("CanArriveBeforeDanger", Flags);
        Check((bool)canArrive.Invoke(null, new object[] { 800, 1600 }),
            "Task09 refuge timing accepts travel that fits before danger plus safety margin");
        Check(!(bool)canArrive.Invoke(null, new object[] { 1100, 1600 }),
            "Task09 refuge timing rejects routes that cannot beat danger plus safety margin");
        Check(!(bool)canArrive.Invoke(null, new object[] { int.MaxValue, 5000 }),
            "Task09 refuge timing rejects unreachable travel estimates");

        Check(travelNavigationType != null, "Task09 high-level TravelNavigation exists");
        Check(travelDebugType.GetField("Suspended", Flags) != null &&
              travelDebugType.GetField("StatusReason", Flags) != null &&
              travelDebugType.GetField("RefugeNode", Flags) != null,
            "Task09 travel debug state exposes suspension reason and refuge node");
        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySocialRoles", false) == null,
            "Task09 does not reintroduce rejected social roles");
        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyRoleScores", false) == null,
            "Task09 remains structurally independent of rejected role scores");
        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.ExpressedSocialRole", false) == null,
            "Task09 remains structurally independent of the rejected role enum");

        string serialized = (string)colonyType.GetMethod("Serialize", Flags).Invoke(colony, null);
        object[] deserializeArgs = { serialized, null };
        bool restoredOk = (bool)colonyType.GetMethod("TryDeserialize", Flags).Invoke(null, deserializeArgs);
        Check(restoredOk && deserializeArgs[1] != null, "Task09 colony ledger round-trips through fixed-size save payload");

        object[] oldPayloadArgs =
        {
            "U1U=;U1VfT0xEX1RBU0swOV9MRUdBQ1k=;2;14;NaN;Infinity;-1;999;0;-2147483648;0;0", null
        };
        bool oldPayloadLoaded = (bool)colonyType.GetMethod("TryDeserialize", Flags).Invoke(null, oldPayloadArgs);
        Check(oldPayloadLoaded && oldPayloadArgs[1] != null,
            "Task09 legacy/short colony payload loads with safe defaults instead of crashing");

        Console.WriteLine("Task 09: pressure/hysteresis, regional-relative stress, recovery floor/ceiling, batch/cooldown, injury gating, weak return affinity, route commitment/replan, refuge timing, debug state and persistence verified.");
    }
}
