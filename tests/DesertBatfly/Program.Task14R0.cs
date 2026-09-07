using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;

internal static partial class Program
{
    private static void RunTask14R0()
    {
        // R0 protects external/game-visible identities before DB_ source renaming begins.
        Type definitionType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DesertBatflyDefinition", true);
        var creatureType = (CreatureTemplate.Type)definitionType.GetField("CreatureType", Flags).GetValue(null);
        Check(creatureType != null && creatureType.value == "DesertBatfly",
            "Task14 R0 freezes CreatureTemplate.Type value DesertBatfly");

        Type sandboxType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DesertBatflySandbox", true);
        Check((string)sandboxType.GetField("UnlockValue", Flags).GetRawConstantValue() == "DesertBatfly",
            "Task14 R0 freezes sandbox unlock value DesertBatfly");

        Type stateType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DesertBatflyState", true);
        Check((string)stateType.GetField("SaveKey", Flags).GetRawConstantValue() == "DCDesertBatflyV1",
            "Task14 R0 freezes primary Desert Batfly save key");

        Type threatStoreType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DesertBatflyThreatMemoryStore", true);
        Check((string)threatStoreType.GetField("SaveKey", Flags).GetRawConstantValue() == "DCDesertBatflyThreatV1",
            "Task14 R0 freezes Threat Signature save key");

        Type colonyRuntimeType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DesertBatflyColonyRuntime", true);
        Check((string)colonyRuntimeType.GetField("SavePrefix", Flags).GetRawConstantValue() == "DCBATCOLONY09<svB>" &&
              (string)colonyRuntimeType.GetField("PayloadVersion", Flags).GetRawConstantValue() == "V1",
            "Task14 R0 freezes colony ledger prefix/version even though its historical 09 remains external data");

        Type swarmRoomType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DesertSwarmRoom", true);
        MethodInfo isDesertSwarmRoom = swarmRoomType.GetMethod("IsDesertSwarmRoom", Flags);
        Check(MethodLoadsStringR0(isDesertSwarmRoom, "DESERTSWARMROOM"),
            "Task14 R0 freezes authored room tag DESERTSWARMROOM");

        // Migration-mode naming guard: old Task09-13 production names are tolerated until
        // R5/R6 removes them, but no new Task14+ runtime names may be introduced.
        foreach (Type type in mod.GetTypes())
        {
            string ns = type.Namespace ?? string.Empty;
            if (!ns.StartsWith("DryCycle.Creatures.DesertBatfly", StringComparison.Ordinal)) continue;
            Check(type.Name.IndexOf("Task14", StringComparison.OrdinalIgnoreCase) < 0 &&
                  type.Name.IndexOf("Task15", StringComparison.OrdinalIgnoreCase) < 0 &&
                  type.Name.IndexOf("Task16", StringComparison.OrdinalIgnoreCase) < 0,
                "Task14 R0 prevents new task-number production types: " + type.FullName);
            Check(!type.Name.StartsWith("DB_Task", StringComparison.Ordinal),
                "Task14 R0 forbids combining DB_ source prefix with TaskXX architecture names: " + type.FullName);
        }

        // Hidden-bug baseline guard 1: the already-existing room-progress frame stamp is
        // truly idempotent. R3 will extend this exactly-once rule to every realized travel
        // countdown/cooldown, including DepartureDelay and ReplanCooldown.
        Type travelType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DesertBatflyTravelNavigation", true);
        Type intentType = travelType.GetNestedType("TravelIntent", Flags);
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
            "Task14 R0 preserves existing same-frame idempotence for realized room-progress ticks");

        // Hidden-bug baseline guard 2: injury recovery must continue using Rain World's
        // native FlyAI Dijkstra helper. R4 will fix the wrong seed/localGoal semantics and
        // remove its second ordinary-flight velocity loop without replacing native pathing.
        Type aiType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DesertBatflyAI", true);
        MethodInfo recoveryHive = aiType.GetMethod("TryDriveRecoveryHive", Flags);
        Check(recoveryHive != null &&
              MethodCallOffset(recoveryHive, typeof(FlyAI), "ProgressLocalGoalAlongDijkstraMap") >= 0,
            "Task14 R0 freezes native FlyAI Dijkstra ownership for severe-injury hive recovery");

        Console.WriteLine(
            "Task14 R0: external IDs/save keys, migration naming guard, partial travel frame idempotence and native injury Dijkstra dependency frozen.");
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
