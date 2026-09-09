using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;

internal static partial class Program
{
    private static void RunArchitectureBaseline()
    {
        // R0 protects external/game-visible identities before DB_ source renaming begins.
        Type definitionType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_Definition", true);
        var creatureType = (CreatureTemplate.Type)definitionType.GetField("CreatureType", Flags).GetValue(null);
        Check(creatureType != null && creatureType.value == "DesertBatfly",
            "Architecture baseline freezes CreatureTemplate.Type value DesertBatfly");

        Type sandboxType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_Sandbox", true);
        Check((string)sandboxType.GetField("UnlockValue", Flags).GetRawConstantValue() == "DesertBatfly",
            "Architecture baseline freezes sandbox unlock value DesertBatfly");

        Type stateType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_State", true);
        Check((string)stateType.GetField("SaveKey", Flags).GetRawConstantValue() == "DCDesertBatflyV1",
            "Architecture baseline freezes primary Desert Batfly save key");

        Type threatStoreType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_ThreatMemoryStore", true);
        Check((string)threatStoreType.GetField("SaveKey", Flags).GetRawConstantValue() == "DCDesertBatflyThreatV1",
            "Architecture baseline freezes Threat Signature save key");

        Type colonyRuntimeType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_ColonyRuntime", true);
        Check((string)colonyRuntimeType.GetField("SavePrefix", Flags).GetRawConstantValue() == "DCBATCOLONY09<svB>" &&
              (string)colonyRuntimeType.GetField("PayloadVersion", Flags).GetRawConstantValue() == "V1",
            "Architecture baseline freezes colony ledger prefix/version even though its historical 09 remains external data");

        Type swarmRoomType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_SwarmRoom", true);
        Check(TypeLoadsStringR0(swarmRoomType, "DESERTSWARMROOM"),
            "Architecture baseline freezes authored room tag DESERTSWARMROOM");

        // Domain naming guard: production types may not encode development-task numbers.
        Type[] productionTypes;
        try
        {
            productionTypes = mod.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Optional third-party dependencies can make reflection return a partial type
            // list on developer machines. Naming guards still apply to every loaded type.
            productionTypes = ex.Types;
        }

        foreach (Type type in productionTypes)
        {
            if (type == null) continue;
            string ns = type.Namespace ?? string.Empty;
            if (!ns.StartsWith("DryCycle.Creatures.DesertBatfly", StringComparison.Ordinal)) continue;
            Check(!System.Text.RegularExpressions.Regex.IsMatch(
                    type.Name,
                    @"(?i)task[\s_-]*[0-9]+"),
                "Architecture baseline prevents development-task numbers in production type names: " + type.FullName);
        }

        // Hidden-bug baseline guard 1: the already-existing room-progress frame stamp is
        // truly idempotent. R3 will extend this exactly-once rule to every realized travel
        // countdown/cooldown, including DepartureDelay and ReplanCooldown.
        Type travelType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_TravelRuntime", true);
        Type intentType = travelType.GetNestedType("DB_TravelIntent", Flags);
        object intent = FormatterServices.GetUninitializedObject(intentType);
        intentType.GetField("LastObservedFrame", Flags).SetValue(intent, int.MinValue);
        intentType.GetField("LastObservedRoom", Flags).SetValue(intent, 77);
        intentType.GetField("SameRoomTravelTicks", Flags).SetValue(intent, 10);
        MethodInfo tickRoomProgress = travelType.GetMethod("TickRoomProgressRealized", Flags);
        tickRoomProgress.Invoke(null, new[] { intent, (object)77 });
        int once = (int)intentType.GetField("SameRoomTravelTicks", Flags).GetValue(intent);
        tickRoomProgress.Invoke(null, new[] { intent, (object)77 });
        int twice = (int)intentType.GetField("SameRoomTravelTicks", Flags).GetValue(intent);
        Check(once == 11 && twice == once,
            "Architecture baseline preserves existing same-frame idempotence for realized room-progress ticks");

        // Hidden-bug baseline guard 2: severe-injury hive recovery must continue using Rain
        // World's native FlyAI Dijkstra helper. R6 moved this responsibility out of DB_AI
        // into the dedicated Injury domain; the baseline follows the current owner and also
        // prevents the retired DB_AI compatibility entry point from returning.
        Type aiType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_AI", true);
        Type injuryRecoveryType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_InjuryRecovery", true);
        MethodInfo recoveryHive = injuryRecoveryType.GetMethod("TryDriveRecoveryHive", Flags);
        Check(recoveryHive != null &&
              MethodCallOffset(recoveryHive, typeof(FlyAI), "ProgressLocalGoalAlongDijkstraMap") >= 0,
            "Architecture baseline preserves native FlyAI Dijkstra ownership inside severe-injury recovery");
        Check(aiType.GetMethod("TryDriveRecoveryHive", Flags) == null,
            "Architecture baseline keeps severe-injury hive routing out of the retired DB_AI compatibility surface");

        Console.WriteLine(
            "Architecture baseline: external IDs/save keys, migration naming guard, partial travel frame idempotence and native injury Dijkstra dependency frozen.");
    }

    private static bool TypeLoadsStringR0(Type owner, string expected)
    {
        if (owner == null) return false;
        foreach (MethodInfo method in owner.GetMethods(Flags))
            if (MethodLoadsStringR0(method, expected)) return true;

        foreach (Type nested in owner.GetNestedTypes(Flags))
            if (TypeLoadsStringR0(nested, expected)) return true;

        return false;
    }

    private static bool MethodLoadsStringR0(MethodInfo method, string expected)
    {
        byte[] il = method?.GetMethodBody()?.GetILAsByteArray();
        if (il == null || il.Length == 0) return false;

        int offset = 0;
        while (offset < il.Length)
        {
            OpCode opcode;
            byte first = il[offset++];
            if (first == 0xFE)
            {
                if (offset >= il.Length) return false;
                opcode = MultiByteOpCode(il[offset++]);
            }
            else opcode = SingleByteOpCode(first);

            int operandOffset = offset;
            int operandSize = OperandSize(opcode.OperandType, il, operandOffset);
            if (opcode == OpCodes.Ldstr && operandSize >= 4)
            {
                try
                {
                    string value = method.Module.ResolveString(BitConverter.ToInt32(il, operandOffset));
                    if (string.Equals(value, expected, StringComparison.Ordinal)) return true;
                }
                catch (ArgumentException) { }
            }
            offset += operandSize;
        }
        return false;
    }
}
