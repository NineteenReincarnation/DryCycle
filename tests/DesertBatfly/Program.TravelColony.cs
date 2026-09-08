using System;
using System.Reflection;

internal static partial class Program
{
    private static void RunTravelColony()
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
        Check(floor >= 2 && floor < 14, "Travel/Colony hard minimum is a real low population floor");
        Check(ceiling >= floor && ceiling < 14, "Travel/Colony natural recovery ceiling stays below preferred population");

        MethodInfo nextMigration = colonyType.GetMethod("NextMigrationState", Flags);
        Check(!(bool)nextMigration.Invoke(null, new object[] { false, 0.69f }), "Travel/Colony migration does not begin below entry threshold");
        Check((bool)nextMigration.Invoke(null, new object[] { false, 0.71f }), "Travel/Colony migration begins above entry threshold");
        Check((bool)nextMigration.Invoke(null, new object[] { true, 0.50f }), "Travel/Colony migration hysteresis retains state in deadband");
        Check(!(bool)nextMigration.Invoke(null, new object[] { true, 0.39f }), "Travel/Colony migration stops below stop threshold");

        MethodInfo relativeStress = colonyType.GetMethod("ComputeRelativeHabitatStress", Flags);
        float equalRegional = (float)relativeStress.Invoke(null, new object[] { 0.78f, 0.76f, 0.15f });
        float localFailure = (float)relativeStress.Invoke(null, new object[] { 0.90f, 0.42f, 0.55f });
        Check(equalRegional < 0.10f, "Travel/Colony equally bad regional weather does not create migration carousel pressure");
        Check(localFailure > equalRegional + 0.35f, "Travel/Colony locally worse habitat creates strong relative pressure");

        environmental.SetValue(colony, 1f);
        predator.SetValue(colony, 1f);
        mortality.SetValue(colony, 1f);
        shelterFailure.SetValue(colony, 1f);
        currentPopulation.SetValue(colony, Math.Max(0, floor - 1));
        colonyType.GetMethod("RecalculateRecoveryCeiling", Flags).Invoke(colony, null);
        int harshCeiling = (int)recoveryCeiling.GetValue(colony);
        float harshChance = (float)colonyType.GetMethod("RecoveryChance", Flags).Invoke(colony, null);
        Check(harshCeiling >= floor, "Travel/Colony severe environment never lowers background recovery below hard persistence floor");
        Check(harshChance > 0f, "Travel/Colony severe environment retains non-zero minimum recovery chance");

        currentPopulation.SetValue(colony, harshCeiling);
        float atCeilingChance = (float)colonyType.GetMethod("RecoveryChance", Flags).Invoke(colony, null);
        Check(atCeilingChance == 0f, "Travel/Colony background recovery stops exactly at the current recovery ceiling");
        currentPopulation.SetValue(colony, floor);
        int noEmptyBatch = (int)colonyType.GetMethod("RecommendedBatchSize", Flags).Invoke(colony, null);
        Check(noEmptyBatch == 0, "Travel/Colony migration batch cannot empty a colony below hard minimum");
        currentPopulation.SetValue(colony, 14);
        int normalBatch = (int)colonyType.GetMethod("RecommendedBatchSize", Flags).Invoke(colony, null);
        Check(normalBatch >= 1 && normalBatch <= 3, "Travel/Colony migration uses a small bounded batch");

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
        Check(healthyPropensity > 0f, "Travel/Colony healthy individual has continuous migration propensity");
        Check(severePropensity == 0f, "Travel/Colony severe wing injury blocks long migration selection");
        Check(cooldownPropensity == 0f, "Travel/Colony individual migration cooldown prevents immediate ping-pong");

        MethodInfo switchDestination = migrationType.GetMethod("ShouldSwitchDestination", Flags);
        Check(!(bool)switchDestination.Invoke(null, new object[] { 0.60f, 0.68f, 0.14f }), "Travel/Colony destination margin prevents small score jitter");
        Check((bool)switchDestination.Invoke(null, new object[] { 0.60f, 0.76f, 0.14f }), "Travel/Colony destination changes only for a materially better target");

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
            "Travel/Colony former-colony familiarity is a real but deliberately weak return bonus");
        Check(bondedDestination > baseDestination && bondedDestination - baseDestination <= 0.06f,
            "Travel/Colony bond-partner presence is a weak destination bonus, not a hard command");

        MethodInfo edgeCost = routePlannerType.GetMethod("EdgeCost", Flags);
        MethodInfo needsReplan = routePlannerType.GetMethod("NeedsSafetyReplan", Flags);
        object emergency = Enum.Parse(travelPurposeType, "EmergencyRefuge");
        object returnHome = Enum.Parse(travelPurposeType, "ReturnHome");
        object migration = Enum.Parse(travelPurposeType, "ColonyMigration");
        float emergencySafe = (float)edgeCost.Invoke(null, new object[] { emergency, 0.10f });
        float emergencyDanger = (float)edgeCost.Invoke(null, new object[] { emergency, 0.80f });
        float migrationDanger = (float)edgeCost.Invoke(null, new object[] { migration, 0.80f });
        Check(emergencyDanger > emergencySafe, "Travel/Colony route cost increases with travel danger");
        Check(emergencyDanger > migrationDanger, "Travel/Colony emergency refuge routing penalizes danger more strongly than permanent migration");
        Check(!(bool)needsReplan.Invoke(null, new object[] { emergency, 0.40f }),
            "Travel/Colony committed emergency route ignores harmless risk jitter");
        Check((bool)needsReplan.Invoke(null, new object[] { emergency, 0.90f }),
            "Travel/Colony emergency route replans after a materially unsafe next room");
        Check(!(bool)needsReplan.Invoke(null, new object[] { returnHome, 0.70f }),
            "Travel/Colony return route commitment prevents unnecessary replanning");
        Check(!(bool)needsReplan.Invoke(null, new object[] { migration, 0.80f }),
            "Travel/Colony permanent migration tolerates moderate travel risk without route oscillation");

        MethodInfo canArrive = refugeType.GetMethod("CanArriveBeforeDanger", Flags);
        Check((bool)canArrive.Invoke(null, new object[] { 800, 1600 }),
            "Travel/Colony refuge timing accepts travel that fits before danger plus safety margin");
        Check(!(bool)canArrive.Invoke(null, new object[] { 1100, 1600 }),
            "Travel/Colony refuge timing rejects routes that cannot beat danger plus safety margin");
        Check(!(bool)canArrive.Invoke(null, new object[] { int.MaxValue, 5000 }),
            "Travel/Colony refuge timing rejects unreachable travel estimates");

        Check(travelNavigationType != null, "Travel/Colony high-level TravelNavigation exists");
        Check(travelDebugType.GetField("Suspended", Flags) != null &&
              travelDebugType.GetField("StatusReason", Flags) != null &&
              travelDebugType.GetField("RefugeNode", Flags) != null,
            "Travel/Colony travel debug state exposes suspension reason and refuge node");
        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CreatureSocialRoles", false) == null,
            "Travel/Colony does not reintroduce rejected social roles");
        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CreatureRoleScores", false) == null,
            "Travel/Colony remains structurally independent of rejected role scores");
        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.ExpressedSocialRole", false) == null,
            "Travel/Colony remains structurally independent of the rejected role enum");

        string serialized = (string)colonyType.GetMethod("Serialize", Flags).Invoke(colony, null);
        object[] deserializeArgs = { serialized, null };
        bool restoredOk = (bool)colonyType.GetMethod("TryDeserialize", Flags).Invoke(null, deserializeArgs);
        Check(restoredOk && deserializeArgs[1] != null, "Travel/Colony colony ledger round-trips through fixed-size save payload");

        object[] oldPayloadArgs =
        {
            "U1U=;U1VfT0xEX1RBU0swOV9MRUdBQ1k=;2;14;NaN;Infinity;-1;999;0;-2147483648;0;0", null
        };
        bool oldPayloadLoaded = (bool)colonyType.GetMethod("TryDeserialize", Flags).Invoke(null, oldPayloadArgs);
        Check(oldPayloadLoaded && oldPayloadArgs[1] != null,
            "Travel/Colony legacy/short colony payload loads with safe defaults instead of crashing");

        Console.WriteLine("Travel/Colony: pressure/hysteresis, regional-relative stress, recovery floor/ceiling, batch/cooldown, injury gating, weak return affinity, route commitment/replan, refuge timing, debug state and persistence verified.");
    }
}
