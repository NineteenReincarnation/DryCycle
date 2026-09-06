using System;
using System.Reflection;

internal static partial class Program
{
    private static void RunTask09()
    {
        Type colonyType = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyColonyState", true);
        Type migrationType = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyColonyMigration", true);
        Type routePlannerType = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyWorldRoutePlanner", true);
        Type travelPurposeType = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyTravelPurpose", true);
        Type travelNavigationType = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyTravelNavigation", true);

        object colony = Activator.CreateInstance(
            colonyType,
            Flags,
            null,
            new object[] { "SU", "SU_TASK09_TEST", 14 },
            null);

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

        currentPopulation.SetValue(colony, floor);
        int noEmptyBatch = (int)colonyType.GetMethod("RecommendedBatchSize", Flags).Invoke(colony, null);
        Check(noEmptyBatch == 0, "Task09 migration batch cannot empty a colony below hard minimum");
        currentPopulation.SetValue(colony, 14);
        int normalBatch = (int)colonyType.GetMethod("RecommendedBatchSize", Flags).Invoke(colony, null);
        Check(normalBatch >= 1 && normalBatch <= 3, "Task09 migration uses a small bounded batch");

        Type personalityType = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyPersonality", true);
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
        Check(healthyPropensity > 0f, "Task09 healthy individual has continuous migration propensity");
        Check(severePropensity == 0f, "Task09 severe wing injury blocks long migration selection");

        MethodInfo switchDestination = migrationType.GetMethod("ShouldSwitchDestination", Flags);
        Check(!(bool)switchDestination.Invoke(null, new object[] { 0.60f, 0.68f, 0.14f }), "Task09 destination margin prevents small score jitter");
        Check((bool)switchDestination.Invoke(null, new object[] { 0.60f, 0.76f, 0.14f }), "Task09 destination changes only for a materially better target");

        MethodInfo edgeCost = routePlannerType.GetMethod("EdgeCost", Flags);
        object emergency = Enum.Parse(travelPurposeType, "EmergencyRefuge");
        object migration = Enum.Parse(travelPurposeType, "ColonyMigration");
        float emergencySafe = (float)edgeCost.Invoke(null, new object[] { emergency, 0.10f });
        float emergencyDanger = (float)edgeCost.Invoke(null, new object[] { emergency, 0.80f });
        float migrationDanger = (float)edgeCost.Invoke(null, new object[] { migration, 0.80f });
        Check(emergencyDanger > emergencySafe, "Task09 route cost increases with travel danger");
        Check(emergencyDanger > migrationDanger, "Task09 emergency refuge routing penalizes danger more strongly than permanent migration");

        Check(travelNavigationType != null, "Task09 high-level TravelNavigation exists");
        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySocialRoles", false) == null,
            "Task09 does not reintroduce rejected social roles");

        string serialized = (string)colonyType.GetMethod("Serialize", Flags).Invoke(colony, null);
        object[] deserializeArgs = { serialized, null };
        bool restoredOk = (bool)colonyType.GetMethod("TryDeserialize", Flags).Invoke(null, deserializeArgs);
        Check(restoredOk && deserializeArgs[1] != null, "Task09 colony ledger round-trips through fixed-size save payload");

        Console.WriteLine("Task 09: pressure hysteresis, regional-relative stress, minimum recovery, batch limits, injury gating, route weighting and persistence verified.");
    }
}
