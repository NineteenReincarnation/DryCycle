using System;
using System.Reflection;
using System.Reflection.Emit;

internal static partial class Program
{
    private static void RunTask10Guards()
    {
        Type social = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CreatureSocialLife", true);

        Check(!TypeCallsForbiddenTask10Method(social),
            "Task10 neutral social layer does not call Random/combat/trauma/damage APIs");

        Type roomRuntime = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_CreatureSocialRoomRuntime", true);
        Type roomState = roomRuntime.GetNestedType("RoomState", Flags);
        FieldInfo refreshInterval = roomState?.GetField("RefreshInterval", Flags);
        Check(refreshInterval != null && (int)refreshInterval.GetRawConstantValue() == 20,
            "Task10 room-wide candidate/roost cache refresh remains 20 ticks, not per-frame per-bat");

        Type socialState = social.GetNestedType("State", Flags);
        Check(socialState != null && socialState.GetField("Partner", Flags) != null &&
              socialState.GetField("Anchor", Flags) != null && socialState.GetField("Token", Flags) != null,
            "Task10 partner/anchor/reservation state stays realized-only inside SocialLife");

        Console.WriteLine(
            "Task 10 guards: no random/combat/trauma calls in neutral social runtime; room cache cadence and realized-only state verified.");
    }

    private static bool TypeCallsForbiddenTask10Method(Type type)
    {
        foreach (MethodInfo method in type.GetMethods(
                     BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
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
                else
                {
                    opcode = SingleByteOpCode(first);
                }

                int operandOffset = offset;
                int operandSize = OperandSize(opcode.OperandType, il, operandOffset);
                if ((opcode == OpCodes.Call || opcode == OpCodes.Callvirt) && operandSize >= 4)
                {
                    int token = BitConverter.ToInt32(il, operandOffset);
                    try
                    {
                        MethodBase called = method.Module.ResolveMethod(token);
                        if (ForbiddenTask10Call(called)) return true;
                    }
                    catch (ArgumentException)
                    {
                        // A malformed token will fail normal build/runtime validation;
                        // this guard only classifies successfully resolved calls.
                    }
                }
                offset += operandSize;
            }
        }
        return false;
    }

    private static bool ForbiddenTask10Call(MethodBase called)
    {
        if (called == null) return false;
        Type owner = called.DeclaringType;
        if (owner?.FullName == "UnityEngine.Random") return true;

        string name = called.Name;
        if (name == "Threatened" || name == "Violence" || name == "AcquireSlot" ||
            name == "AddTrauma" || name == "DrainWater" || name == "ApplyInitialRetaliationImpact")
            return true;

        string ownerName = owner?.FullName ?? string.Empty;
        return ownerName.IndexOf("DryCycle.Thirst.ThirstStore", StringComparison.Ordinal) >= 0 ||
               ownerName.IndexOf("DesertBatflyAttack", StringComparison.Ordinal) >= 0;
    }
}
