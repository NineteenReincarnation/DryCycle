using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

internal static partial class Program
{
    private static void RunSocial()
    {
        Type socialType = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CreatureSocialLife", true);
        Type socialModeType = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CreatureSocialMode", true);
        Type socialDebugType = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CreatureSocialDebugState", true);
        Type roomRuntimeType = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CreatureSocialRoomRuntime", true);
        Type personalityType = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_Personality", true);
        Type stateType = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_State", true);

        string[] expectedModes =
        {
            "None",
            "CompanionDrift",
            "PassBy",
            "SocialChase",
            "GroupDrift",
            "RoostInvitation",
            "PositionNegotiation",
            "ChainSocialization"
        };
        string[] actualModes = Enum.GetNames(socialModeType);
        Check(expectedModes.All(name => actualModes.Contains(name)),
            "Social exposes all seven temporary social interactions plus None");
        Check(!actualModes.Contains("Sentinel") && !actualModes.Contains("Bully") &&
              !actualModes.Contains("Opportunist") && !actualModes.Contains("Leader") &&
              !actualModes.Contains("Follower"),
            "Social social modes are temporary interactions, never rejected social roles");

        object lowConformity = Activator.CreateInstance(personalityType, Flags, null, new object[] { 101 }, null);
        object highConformity = null;
        FieldInfo conformity = personalityType.GetField("Conformity", Flags);
        for (int seed = 102; seed < 10000; seed++)
        {
            object candidate = Activator.CreateInstance(personalityType, Flags, null, new object[] { seed }, null);
            if ((float)conformity.GetValue(candidate) > 0.88f)
            {
                highConformity = candidate;
                break;
            }
        }
        Check(highConformity != null, "Social test found a stable high-Conformity personality seed");

        MethodInfo drivePerTick = socialType.GetMethod("SocialDrivePerTick", Flags);
        MethodInfo threshold = socialType.GetMethod("StartThreshold", Flags);
        float lowDrive = (float)drivePerTick.Invoke(null, new[] { lowConformity });
        float highDrive = (float)drivePerTick.Invoke(null, new[] { highConformity });
        float highThreshold = (float)threshold.Invoke(null, new[] { highConformity });
        Check(lowDrive > 0f && highDrive > 0f,
            "Social eligible neutral flight accumulates positive SocialDrive");
        Check(highDrive > lowDrive * 0.70f,
            "Social SocialDrive remains gradual instead of a zero/one trigger");
        Check(highThreshold >= 0.46f && highThreshold <= 0.78f,
            "Social interaction threshold stays in bounded neutral-life range");

        MethodInfo priority = socialType.GetMethod("PriorityAllowsSocialFlags", Flags);
        Check((bool)priority.Invoke(null, new object[] { false, false, false, false, false, false, false }),
            "Social neutral eligibility accepts an unclaimed neutral frame");
        Check(!(bool)priority.Invoke(null, new object[] { true, false, false, false, false, false, false }),
            "Social immediate danger blocks social life");
        Check(!(bool)priority.Invoke(null, new object[] { false, true, false, false, false, false, false }),
            "Social Travel/Colony travel blocks social life");
        Check(!(bool)priority.Invoke(null, new object[] { false, false, true, false, false, false, false }),
            "Social severe injury blocks ordinary social life");
        Check(!(bool)priority.Invoke(null, new object[] { false, false, false, true, false, false, false }),
            "Social stable Chain/Roost is not interrupted by social life");
        Check(!(bool)priority.Invoke(null, new object[] { false, false, false, false, true, false, false }),
            "Social Drop/Passive is not forced back into social flight");
        Check(!(bool)priority.Invoke(null, new object[] { false, false, false, false, false, true, false }),
            "Social formal attack blocks neutral social life");

        MethodInfo preference = socialType.GetMethod("PartnerPreference", Flags);
        float stranger = (float)preference.Invoke(null, new object[] { 0.7f, 0.6f, 0f, 0.4f });
        float bonded = (float)preference.Invoke(null, new object[] { 0.7f, 0.6f, 1f, 0.4f });
        Check(bonded > stranger && bonded - stranger <= 0.30f,
            "Social SocialBond is a real but weak partner preference bonus");
        Check(stranger > 0f,
            "Social strangers can still interact without a SocialBond hard requirement");

        MethodInfo pairSide = socialType.GetMethod("StablePairSide", Flags);
        var idA = new EntityID(5, 101);
        var idB = new EntityID(7, 202);
        int sideAB = (int)pairSide.Invoke(null, new object[] { idA, idB });
        int sideBA = (int)pairSide.Invoke(null, new object[] { idB, idA });
        Check((sideAB == -1 || sideAB == 1) && sideAB == sideBA,
            "Social pair hash is stable independent of caller order; runtime assigns the partner the opposite side");

        MethodInfo companionOffset = socialType.GetMethod("CompanionOffset", Flags);
        Vector2 leftOffset = (Vector2)companionOffset.Invoke(null, new object[] { -1, 0.5f });
        Vector2 rightOffset = (Vector2)companionOffset.Invoke(null, new object[] { 1, 0.5f });
        Check(leftOffset.x < 0f && rightOffset.x > 0f &&
              Mathf.Abs(leftOffset.x) > Mathf.Abs(leftOffset.y) * 4f,
            "Social CompanionDrift has a dominant horizontal offset instead of vertical stacking");

        FieldInfo separationX = socialType.GetField("GroupSeparationXWeight", Flags);
        FieldInfo separationY = socialType.GetField("GroupSeparationYWeight", Flags);
        Check((float)separationX.GetValue(null) > (float)separationY.GetValue(null) * 3f,
            "Social GroupDrift separation is explicitly horizontal-biased");

        Type roomStateType = roomRuntimeType.GetNestedType("RoomState", Flags);
        Check(roomRuntimeType.GetNestedType("Reservation", Flags) != null && roomStateType != null,
            "Social has explicit room-scoped reservation/candidate-cache structures");
        Check(roomStateType.GetProperty("Roosting", Flags) != null &&
              roomStateType.GetMethod("CountRoostingNear", Flags) != null,
            "Social caches roosting bats at room scope instead of rescanning the flock for every roost tile");
        MethodInfo removeGroupMember = roomStateType.GetMethod("RemoveGroupMember", Flags);
        Check(removeGroupMember != null && removeGroupMember.ReturnType == typeof(bool),
            "Social group removal reports whether the microflock remains valid for synchronous cleanup");
        Check(socialType.GetMethod("CancelForPriority", Flags) != null &&
              socialType.GetMethod("Reset", Flags) != null,
            "Social exposes cleanup hooks for death/travel/disable lifecycle");

        MethodInfo socialSteer = socialType.GetMethod("SocialSteer", Flags);
        Check(socialSteer != null, "Social has one centralized neutral social steering bridge");
        Check(!MethodWritesField(socialSteer, typeof(BodyChunk), "vel"),
            "Social SocialSteer never writes BodyChunk.vel; vanilla BatFlight owns flight physics");

        foreach (string field in new[]
        {
            "Eligible", "SocialDrive", "SocialCooldown", "Mode", "InteractionTicks", "Duration",
            "Partner", "Anchor", "MicroFlockId", "MicroFlockSize", "LastInteractionType",
            "DecisionReason", "CandidateCount", "RoostTarget", "NegotiationSide"
        })
            Check(socialDebugType.GetField(field, Flags) != null,
                "Social debug state exposes " + field);

        foreach (string forbidden in new[]
        {
            "SocialDrive", "SocialCooldown", "TemporaryPartner", "TemporaryAnchor", "MicroFlock", "SocialMode"
        })
        {
            Check(stateType.GetField(forbidden, Flags) == null && stateType.GetProperty(forbidden, Flags) == null,
                "Social realized-only state is not persisted in DB_State: " + forbidden);
        }

        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CreatureRoleScores", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.ExpressedSocialRole", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CreatureSocialRoles", false) == null,
            "Social does not revive any rejected rejected social-role design social-role runtime type");

        Console.WriteLine("Social: temporary modes, SocialDrive/priority, weak Bond preference, stable horizontal pairing, room caches/reservations, vanilla-locomotion ownership, non-persistence and debug shape verified.");
    }

    private static bool MethodWritesField(MethodInfo method, Type declaringType, string fieldName)
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
                if (offset >= il.Length) break;
                opcode = MultiByteOpCode(il[offset++]);
            }
            else
            {
                opcode = SingleByteOpCode(first);
            }

            int operandOffset = offset;
            int operandSize = OperandSize(opcode.OperandType, il, operandOffset);
            if ((opcode == OpCodes.Stfld || opcode == OpCodes.Stsfld) && operandSize >= 4)
            {
                int token = BitConverter.ToInt32(il, operandOffset);
                try
                {
                    FieldInfo field = method.Module.ResolveField(token);
                    if (field != null && field.DeclaringType == declaringType && field.Name == fieldName)
                        return true;
                }
                catch (ArgumentException)
                {
                    // Malformed metadata would fail the real build/test elsewhere; it is
                    // irrelevant to this focused ownership assertion.
                }
            }
            offset += operandSize;
        }
        return false;
    }

    private static OpCode SingleByteOpCode(byte value)
    {
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is OpCode opcode && opcode.Size == 1 && (byte)opcode.Value == value)
                return opcode;
        }
        return default;
    }

    private static OpCode MultiByteOpCode(byte second)
    {
        short value = unchecked((short)(0xFE00 | second));
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is OpCode opcode && opcode.Size == 2 && opcode.Value == value)
                return opcode;
        }
        return default;
    }

    private static int OperandSize(OperandType type, byte[] il, int offset)
    {
        switch (type)
        {
            case OperandType.InlineNone:
                return 0;
            case OperandType.ShortInlineBrTarget:
            case OperandType.ShortInlineI:
            case OperandType.ShortInlineVar:
                return 1;
            case OperandType.InlineVar:
                return 2;
            case OperandType.InlineI:
            case OperandType.InlineBrTarget:
            case OperandType.InlineField:
            case OperandType.InlineMethod:
            case OperandType.InlineSig:
            case OperandType.InlineString:
            case OperandType.InlineTok:
            case OperandType.InlineType:
            case OperandType.ShortInlineR:
                return 4;
            case OperandType.InlineI8:
            case OperandType.InlineR:
                return 8;
            case OperandType.InlineSwitch:
                if (offset + 4 > il.Length) return Math.Max(0, il.Length - offset);
                int count = BitConverter.ToInt32(il, offset);
                return 4 + Math.Max(0, count) * 4;
            default:
                return 0;
        }
    }
}
