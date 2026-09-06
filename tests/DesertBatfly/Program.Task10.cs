using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

internal static partial class Program
{
    private static void RunTask10()
    {
        Type socialType = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySocialLife", true);
        Type socialModeType = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySocialMode", true);
        Type socialDebugType = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySocialDebugState", true);
        Type roomRuntimeType = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySocialRoomRuntime", true);
        Type personalityType = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyPersonality", true);
        Type stateType = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyState", true);

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
            "Task10 exposes all seven temporary social interactions plus None");
        Check(!actualModes.Contains("Sentinel") && !actualModes.Contains("Bully") &&
              !actualModes.Contains("Opportunist") && !actualModes.Contains("Leader") &&
              !actualModes.Contains("Follower"),
            "Task10 social modes are temporary interactions, never rejected social roles");

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
        Check(highConformity != null, "Task10 test found a stable high-Conformity personality seed");

        MethodInfo drivePerTick = socialType.GetMethod("SocialDrivePerTick", Flags);
        MethodInfo threshold = socialType.GetMethod("StartThreshold", Flags);
        float lowDrive = (float)drivePerTick.Invoke(null, new[] { lowConformity });
        float highDrive = (float)drivePerTick.Invoke(null, new[] { highConformity });
        float highThreshold = (float)threshold.Invoke(null, new[] { highConformity });
        Check(lowDrive > 0f && highDrive > 0f,
            "Task10 eligible neutral flight accumulates positive SocialDrive");
        Check(highDrive > lowDrive * 0.70f,
            "Task10 SocialDrive remains gradual instead of a zero/one trigger");
        Check(highThreshold >= 0.46f && highThreshold <= 0.78f,
            "Task10 interaction threshold stays in bounded neutral-life range");

        MethodInfo priority = socialType.GetMethod("PriorityAllowsSocialFlags", Flags);
        Check((bool)priority.Invoke(null, new object[] { false, false, false, false, false, false, false }),
            "Task10 neutral eligibility accepts an unclaimed neutral frame");
        Check(!(bool)priority.Invoke(null, new object[] { true, false, false, false, false, false, false }),
            "Task10 immediate danger blocks social life");
        Check(!(bool)priority.Invoke(null, new object[] { false, true, false, false, false, false, false }),
            "Task10 Task09 travel blocks social life");
        Check(!(bool)priority.Invoke(null, new object[] { false, false, true, false, false, false, false }),
            "Task10 severe injury blocks ordinary social life");
        Check(!(bool)priority.Invoke(null, new object[] { false, false, false, true, false, false, false }),
            "Task10 stable Chain/Roost is not interrupted by social life");
        Check(!(bool)priority.Invoke(null, new object[] { false, false, false, false, true, false, false }),
            "Task10 Drop/Passive is not forced back into social flight");
        Check(!(bool)priority.Invoke(null, new object[] { false, false, false, false, false, true, false }),
            "Task10 formal attack blocks neutral social life");

        MethodInfo preference = socialType.GetMethod("PartnerPreference", Flags);
        float stranger = (float)preference.Invoke(null, new object[] { 0.7f, 0.6f, 0f, 0.4f });
        float bonded = (float)preference.Invoke(null, new object[] { 0.7f, 0.6f, 1f, 0.4f });
        Check(bonded > stranger && bonded - stranger <= 0.30f,
            "Task10 SocialBond is a real but weak partner preference bonus");
        Check(stranger > 0f,
            "Task10 strangers can still interact without a SocialBond hard requirement");

        MethodInfo pairSide = socialType.GetMethod("StablePairSide", Flags);
        var idA = new EntityID(5, 101);
        var idB = new EntityID(7, 202);
        int sideAB = (int)pairSide.Invoke(null, new object[] { idA, idB });
        int sideBA = (int)pairSide.Invoke(null, new object[] { idB, idA });
        Check((sideAB == -1 || sideAB == 1) && sideAB == sideBA,
            "Task10 pair hash is stable independent of caller order; runtime assigns the partner the opposite side");

        MethodInfo companionOffset = socialType.GetMethod("CompanionOffset", Flags);
        Vector2 leftOffset = (Vector2)companionOffset.Invoke(null, new object[] { -1, 0.5f });
        Vector2 rightOffset = (Vector2)companionOffset.Invoke(null, new object[] { 1, 0.5f });
        Check(leftOffset.x < 0f && rightOffset.x > 0f &&
              Mathf.Abs(leftOffset.x) > Mathf.Abs(leftOffset.y) * 4f,
            "Task10 CompanionDrift has a dominant horizontal offset instead of vertical stacking");

        FieldInfo separationX = socialType.GetField("GroupSeparationXWeight", Flags);
        FieldInfo separationY = socialType.GetField("GroupSeparationYWeight", Flags);
        Check((float)separationX.GetValue(null) > (float)separationY.GetValue(null) * 3f,
            "Task10 GroupDrift separation is explicitly horizontal-biased");

        Check(roomRuntimeType.GetNestedType("Reservation", Flags) != null &&
              roomRuntimeType.GetNestedType("RoomState", Flags) != null,
            "Task10 has explicit room-scoped reservation/candidate-cache structures");
        Check(socialType.GetMethod("CancelForPriority", Flags) != null &&
              socialType.GetMethod("Reset", Flags) != null,
            "Task10 exposes cleanup hooks for death/travel/disable lifecycle");

        foreach (string field in new[]
        {
            "Eligible", "SocialDrive", "SocialCooldown", "Mode", "InteractionTicks", "Duration",
            "Partner", "Anchor", "MicroFlockId", "MicroFlockSize", "LastInteractionType",
            "DecisionReason", "CandidateCount", "RoostTarget", "NegotiationSide"
        })
            Check(socialDebugType.GetField(field, Flags) != null,
                "Task10 debug state exposes " + field);

        foreach (string forbidden in new[]
        {
            "SocialDrive", "SocialCooldown", "TemporaryPartner", "TemporaryAnchor", "MicroFlock", "SocialMode"
        })
        {
            Check(stateType.GetField(forbidden, Flags) == null && stateType.GetProperty(forbidden, Flags) == null,
                "Task10 realized-only state is not persisted in DesertBatflyState: " + forbidden);
        }

        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyRoleScores", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.ExpressedSocialRole", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySocialRoles", false) == null,
            "Task10 does not revive any rejected Task02 social-role runtime type");

        Console.WriteLine("Task 10: temporary mode set, SocialDrive/threshold, priority gates, weak Bond preference, stable pair side, horizontal layout, room reservations, non-persistence and debug shape verified.");
    }
}
