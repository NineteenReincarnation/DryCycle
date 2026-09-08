using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using DryCycle.Creatures.DesertBatfly;

internal static partial class Program
{
    private static void RunTask11()
    {
        Type dimensionType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_ThreatDimension", true);
        Type memoryType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_PlayerThreatMemory", true);
        Type setType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_ThreatMemorySet", true);
        Type storeType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_ThreatMemoryStore", true);
        Type evidenceType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_ThreatEvidence", true);
        Type tagType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_ThreatTag", true);
        Type adapterType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_ThreatClassifier", true);
        Type runtimeType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_CreatureThreatRuntime", true);
        Type cueType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_CreatureThreatCue", true);
        Type debugType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_CreatureThreatDebugState", true);
        Type stateType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_State", true);

        string[] expectedDimensions =
        {
            "Projectile", "Piercing", "BluntStun", "Explosion", "Startle", "Shock",
            "AreaDenial", "GrabCapture", "Pursuit", "CounterKill",
            "RetreatTendency", "NonAggressionConfidence"
        };
        string[] actualDimensions = Enum.GetNames(dimensionType);
        Check(expectedDimensions.SequenceEqual(actualDimensions),
            "Task11 exposes exactly the twelve agreed continuous threat dimensions");

        Check((int)setType.GetField("PlayerSlots", Flags).GetRawConstantValue() == 4,
            "Task11 persistent memory is fixed to four co-op player slots");
        object set = Activator.CreateInstance(setType, Flags, null, Array.Empty<object>(), null);
        Array players = (Array)setType.GetField("Players", Flags).GetValue(set);
        Check(players.Length == 4 && !ReferenceEquals(players.GetValue(0), players.GetValue(1)),
            "Task11 allocates four independent per-player memory records");

        MethodInfo addDimension = memoryType.GetMethod("Add", Flags);
        MethodInfo getDimension = memoryType.GetMethod("Get", Flags);
        MethodInfo decay = memoryType.GetMethod("ApplyDecay", Flags);
        object memory = Activator.CreateInstance(memoryType, Flags, null, Array.Empty<object>(), null);
        object projectileDimension = Enum.Parse(dimensionType, "Projectile");
        addDimension.Invoke(memory, new object[] { projectileDimension, 0.5f });
        float first = (float)getDimension.Invoke(memory, new[] { projectileDimension });
        addDimension.Invoke(memory, new object[] { projectileDimension, 0.5f });
        float second = (float)getDimension.Invoke(memory, new[] { projectileDimension });
        Check(Math.Abs(first - 0.5f) < 0.0001f && second > first && second < 1f &&
              Math.Abs(second - 0.75f) < 0.0001f,
            "Task11 evidence uses saturating score += evidence * (1-score)");
        decay.Invoke(memory, new object[] { 4 });
        float afterDecay = (float)getDimension.Invoke(memory, new[] { projectileDimension });
        Check(afterDecay < second * 0.45f && afterDecay > 0f,
            "Task11 threat signatures decay meaningfully over several cycles without instant forgetting");

        float signatureDecay = (float)storeType.GetField("SignatureDecayPerCycle", Flags).GetRawConstantValue();
        float confidenceDecay = (float)storeType.GetField("ConfidenceDecayPerCycle", Flags).GetRawConstantValue();
        Check(signatureDecay >= 0.75f && signatureDecay <= 0.82f &&
              confidenceDecay >= 0.82f && confidenceDecay <= 0.88f,
            "Task11 decay constants stay in the agreed 3-6 cycle memory range");
        Check((string)storeType.GetField("SaveKey", Flags).GetRawConstantValue() == "DCDesertBatflyThreatV1",
            "Task11 persists through its own versioned save key instead of changing DCDesertBatflyV1 indices");

        MethodInfo classify = adapterType.GetMethod("Classify", Flags);
        object rockEvidence = classify.Invoke(null, new object[]
        {
            Bare<Rock>(), Creature.DamageType.Blunt, 0.01f, 45f, true
        });
        Check(Evidence(rockEvidence, evidenceType, "Projectile") > 0f &&
              Evidence(rockEvidence, evidenceType, "BluntStun") > 0f &&
              Evidence(rockEvidence, evidenceType, "Piercing") == 0f,
            "Task11 Rock maps to projectile + blunt/stun, never piercing");

        object spearEvidence = classify.Invoke(null, new object[]
        {
            Bare<Spear>(), Creature.DamageType.Stab, 1f, 0f, true
        });
        Check(Evidence(spearEvidence, evidenceType, "Projectile") > 0f &&
              Evidence(spearEvidence, evidenceType, "Piercing") > 0f &&
              Evidence(spearEvidence, evidenceType, "BluntStun") == 0f,
            "Task11 Spear maps to projectile + piercing rather than rock-like stun");

        object firecrackerEvidence = classify.Invoke(null, new object[]
        {
            Bare<FirecrackerPlant>(), null, 0f, 0f, false
        });
        Check(Evidence(firecrackerEvidence, evidenceType, "Startle") >= 0.50f &&
              Evidence(firecrackerEvidence, evidenceType, "AreaDenial") > 0f &&
              Evidence(firecrackerEvidence, evidenceType, "Piercing") == 0f &&
              Evidence(firecrackerEvidence, evidenceType, "Explosion") == 0f,
            "Task11 FirecrackerPlant is primarily Startle with weak area cue, not a lethal explosion/piercing weapon");

        Type explosiveSpearType = typeof(Spear).Assembly.GetType("ExplosiveSpear", false);
        Check(explosiveSpearType != null, "Rain World exposes ExplosiveSpear for Task11 built-in classification");
        if (explosiveSpearType != null)
        {
            object explosiveSpear = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(explosiveSpearType);
            object explosiveEvidence = classify.Invoke(null, new[]
            {
                explosiveSpear, Creature.DamageType.Explosion, (object)1f, 40f, true
            });
            Check(Evidence(explosiveEvidence, evidenceType, "Projectile") > 0f &&
                  Evidence(explosiveEvidence, evidenceType, "Piercing") > 0f &&
                  Evidence(explosiveEvidence, evidenceType, "Explosion") > 0f &&
                  Evidence(explosiveEvidence, evidenceType, "AreaDenial") > 0f,
                "Task11 ExplosiveSpear contributes multiple threat tags from one real event");
        }

        Check(adapterType.GetMethod("Register", Flags) != null &&
              adapterType.GetMethod("Unregister", Flags) != null &&
              adapterType.GetMethod("ResetCustomAdapters", Flags) != null,
            "Task11 exposes a generic adapter registry for future custom threat items");

        var creature = Bare<AbstractCreature>();
        creature.creatureTemplate = Bare<CreatureTemplate>();
        creature.ID = new EntityID(-1, 11011);
        var persistent = new DB_State(creature);
        object evidence = Activator.CreateInstance(evidenceType);
        evidenceType.GetField("Projectile", Flags).SetValue(evidence, 0.40f);
        evidenceType.GetField("Piercing", Flags).SetValue(evidence, 0.55f);
        MethodInfo addEvidence = storeType.GetMethod("AddEvidence", Flags);
        addEvidence.Invoke(null, new object[] { persistent, 0, evidence, 1f, 12 });
        string saveKey = (string)storeType.GetField("SaveKey", Flags).GetRawConstantValue();
        Check(persistent.unrecognizedSaveStrings.ContainsKey(saveKey),
            "Task11 writes persistent memory into its independent CreatureState save key");

        MethodInfo forPlayer = storeType.GetMethod("For", Flags);
        object p0 = forPlayer.Invoke(null, new object[] { persistent, 0 });
        object p1 = forPlayer.Invoke(null, new object[] { persistent, 1 });
        Check((float)memoryType.GetField("PiercingPressure", Flags).GetValue(p0) > 0f &&
              (float)memoryType.GetField("PiercingPressure", Flags).GetValue(p1) == 0f,
            "Task11 evidence for player slot 0 cannot leak into co-op player slot 1");

        string serializedState = persistent.ToString();
        var restored = new DB_State(creature);
        restored.LoadFromString(System.Text.RegularExpressions.Regex.Split(serializedState, "<cB>"));
        storeType.GetMethod("ResetRuntime", Flags).Invoke(null, null);
        object restoredP0 = forPlayer.Invoke(null, new object[] { restored, 0 });
        Check(Math.Abs((float)memoryType.GetField("PiercingPressure", Flags).GetValue(restoredP0) -
                       (float)memoryType.GetField("PiercingPressure", Flags).GetValue(p0)) < 0.0001f,
            "Task11 threat signature round-trips without modifying the legacy DB_State payload");

        Check(stateType.GetField("CurrentThreatCue", Flags) == null &&
              stateType.GetField("AcuteExplosionTimer", Flags) == null &&
              stateType.GetField("AcuteMassCasualtyTimer", Flags) == null,
            "Task11 Current Cue and Acute Event state are realized-only and never persisted");

        foreach (string cue in new[]
        {
            "PlayerSlot", "VisibleSpear", "VisibleRock", "VisibleExplosive", "VisibleStartle",
            "VisibleShock", "RecentSpearThrow", "RecentRockThrow", "RecentExplosion",
            "RecentGrabAttempt", "ProjectileThreat", "ProjectileThreatDirection",
            "CurrentHazardCenter", "PlayerRetreating"
        })
            Check(cueType.GetField(cue, Flags) != null,
                "Task11 Current Cue exposes " + cue);

        Type runtimeState = runtimeType.GetNestedType("RuntimeState", Flags);
        Check(runtimeState != null &&
              runtimeState.GetField("FormalAggressionPlayerSlot", Flags) != null &&
              runtimeState.GetField("FormalAggressionTick", Flags) != null &&
              runtimeState.GetField("PursuitTicks", Flags) != null &&
              runtimeState.GetField("EncounterTicks", Flags) != null,
            "Task11 keeps CounterKill/Pursuit/encounter evidence windows as realized event state");
        Check((int)runtimeType.GetField("FormalAggressionMemoryTicks", Flags).GetRawConstantValue() > 0 &&
              (int)runtimeType.GetField("PursuitMinimumTicks", Flags).GetRawConstantValue() >= 40 &&
              (int)runtimeType.GetField("RetreatMinimumTicks", Flags).GetRawConstantValue() >= 40,
            "Task11 CounterKill, Pursuit and Retreat require non-zero sustained event windows");

        Type roomState = runtimeType.GetNestedType("RoomState", Flags);
        Check(roomState != null && roomState.GetField("LastRefreshClock", Flags) != null &&
              roomState.GetField("Players", Flags) != null && roomState.GetField("ThrownWeapons", Flags) != null,
            "Task11 Current Cue uses one room-scoped player/projectile cache rather than a per-bat room scan");
        Check((int)runtimeType.GetField("CueRefreshTicks", Flags).GetRawConstantValue() >= 8,
            "Task11 room cue cache remains low-frequency instead of scanning all objects every frame");

        MethodInfo tactical = runtimeType.GetMethod("ApplyTacticalAdjustment", Flags);
        Check(tactical != null && !MethodWritesField(tactical, typeof(BodyChunk), "vel"),
            "Task11 tactical adaptation changes local goals/state but never owns BodyChunk.vel");
        Check(!TypeCallsForbiddenTask11Input(runtimeType),
            "Task11 never reads player Input/GetKey/controller state or UnityEngine.Random for hidden prediction");

        foreach (string field in new[]
        {
            "PlayerSlot", "Confidence", "ProjectilePressure", "PiercingPressure", "BluntStunPressure",
            "ExplosionPressure", "StartlePressure", "ShockPressure", "AreaDenialPressure",
            "GrabCapturePressure", "PursuitPressure", "CounterKillPressure", "RetreatTendency",
            "NonAggressionConfidence", "DominantSignature", "Cue", "AcuteExplosionTimer",
            "AcuteStartleTimer", "AcuteMassCasualtyTimer", "AcuteCaptureTimer", "AcuteShockTimer",
            "ModifierReason", "AttackGeometryAdjustment", "AttachSuppression", "EvadeTarget",
            "LastEvidenceType", "LastEvidenceStrength", "LastEvidencePlayerSlot", "LastWitnessReason"
        })
            Check(debugType.GetField(field, Flags) != null,
                "Task11 debug state exposes " + field);

        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CreatureRoleScores", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.ExpressedSocialRole", false) == null &&
              mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CreatureSocialRoles", false) == null,
            "Task11 does not revive rejected Task02 social roles");

        Console.WriteLine(
            "Task 11: twelve-dimensional memory, four-player isolation, saturating learning/decay, built-in threat tags, independent save key, room cues, non-persistent acute state, vanilla flight ownership and debug shape verified.");
    }

    private static float Evidence(object boxedEvidence, Type evidenceType, string field)
        => (float)evidenceType.GetField(field, Flags).GetValue(boxedEvidence);

    private static bool TypeCallsForbiddenTask11Input(Type type)
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
                else opcode = SingleByteOpCode(first);

                int operandOffset = offset;
                int operandSize = OperandSize(opcode.OperandType, il, operandOffset);
                if ((opcode == OpCodes.Call || opcode == OpCodes.Callvirt) && operandSize >= 4)
                {
                    try
                    {
                        MethodBase called = method.Module.ResolveMethod(BitConverter.ToInt32(il, operandOffset));
                        Type owner = called?.DeclaringType;
                        string ownerName = owner?.FullName ?? string.Empty;
                        if (ownerName == "UnityEngine.Input" || ownerName == "UnityEngine.Random" ||
                            ownerName.IndexOf("Rewired", StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
                    }
                    catch (ArgumentException) { }
                }
                offset += operandSize;
            }
        }
        return false;
    }
}
