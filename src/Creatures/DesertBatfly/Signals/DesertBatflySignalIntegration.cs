using System;
using System.Reflection;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Narrow bridges from the Task12 information layer into existing behavior owners.
/// Task12 never owns locomotion. Alarm reuses DesertBatflyAI.Threatened while suppressing
/// its legacy re-broadcast, and formal social harass target selection consumes HarassSignal
/// instead of inspecting another bat's private AI mode directly.
/// </summary>
internal static class DesertBatflySignalIntegration
{
    private delegate void RaiseLocalAlarmOrig(DesertBatflyAI self);
    private delegate void RaiseLocalAlarmDetour(RaiseLocalAlarmOrig orig, DesertBatflyAI self);
    private delegate Player FindSocialHarassTargetOrig(DesertBatflyAI self);
    private delegate Player FindSocialHarassTargetDetour(FindSocialHarassTargetOrig orig, DesertBatflyAI self);

    private static Hook alarmHook;
    private static Hook harassHook;
    private static FieldInfo aiFlyField;
    private static FieldInfo aiAttackerField;
    private static FieldInfo aiDangerField;
    private static FieldInfo aiEscapeFromField;

    private static FieldInfo intimidationStatesField;
    private static MethodInfo intimidationTryGetValue;
    private static FieldInfo vengeanceRoleField;
    private static FieldInfo vengeanceTargetField;

    [ThreadStatic]
    private static int suppressAlarmEmission;

    internal static bool Installed => alarmHook != null && harassHook != null;

    internal static void Enable()
    {
        if (Installed) return;
        Disable();
        try
        {
            Type ai = typeof(DesertBatflyAI);
            const BindingFlags privateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
            MethodInfo raiseAlarm = ai.GetMethod("RaiseLocalAlarm", privateInstance, null, Type.EmptyTypes, null);
            MethodInfo findHarass = ai.GetMethod("FindSocialHarassTarget", privateInstance, null, Type.EmptyTypes, null);
            aiFlyField = ai.GetField("fly", privateInstance);
            aiAttackerField = ai.GetField("attacker", privateInstance);
            aiDangerField = ai.GetField("danger", privateInstance);
            aiEscapeFromField = ai.GetField("escapeFrom", privateInstance);

            if (raiseAlarm == null || findHarass == null || aiFlyField == null ||
                aiAttackerField == null || aiDangerField == null || aiEscapeFromField == null)
                return;

            alarmHook = new Hook(raiseAlarm, (RaiseLocalAlarmDetour)RaiseLocalAlarmHook);
            harassHook = new Hook(findHarass, (FindSocialHarassTargetDetour)FindSocialHarassTargetHook);

            CacheVengeanceReflection();
            On.Fly.Grabbed += FlyGrabbed;
            On.LizardTongue.Update += TongueUpdate;
        }
        catch
        {
            Disable();
        }
    }

    internal static void Disable()
    {
        try { On.Fly.Grabbed -= FlyGrabbed; } catch { }
        try { On.LizardTongue.Update -= TongueUpdate; } catch { }
        try { harassHook?.Dispose(); } catch { }
        try { alarmHook?.Dispose(); } catch { }
        harassHook = null;
        alarmHook = null;
        aiFlyField = null;
        aiAttackerField = null;
        aiDangerField = null;
        aiEscapeFromField = null;
        intimidationStatesField = null;
        intimidationTryGetValue = null;
        vengeanceRoleField = null;
        vengeanceTargetField = null;
        suppressAlarmEmission = 0;
    }

    internal static void Reset()
    {
        suppressAlarmEmission = 0;
    }

    internal static void ApplyAlarm(
        DesertBatfly receiver,
        DesertBatflySignalPacket packet,
        float response)
    {
        if (receiver == null || packet == null || response < 0.30f || receiver.dead ||
            !receiver.Consious || receiver.room == null || receiver.inShortcut)
            return;
        if (receiver.Injury.IsSeverelyInjured || DesertBatflyTravelNavigation.HasIntent(receiver.abstractCreature))
            return;

        Creature threat = packet.Threat;
        if (threat == null || threat.dead || threat.room != receiver.room)
            return;

        suppressAlarmEmission++;
        try
        {
            // Existing AI retains all consequences: chain break, attack cancellation,
            // retreat duration and Escape state. Task12 supplies only indirect context.
            receiver.DesertAI.Threatened(threat, false);
        }
        finally
        {
            suppressAlarmEmission = Mathf.Max(0, suppressAlarmEmission - 1);
        }
    }

    internal static bool IsVengeanceAvenger(DesertBatfly bat)
    {
        if (!TryGetVengeanceState(bat, out object state) || vengeanceRoleField == null)
            return false;
        string role = vengeanceRoleField.GetValue(state)?.ToString() ?? string.Empty;
        return role == "TrueAvenger" || role == "Avenger";
    }

    internal static Creature VengeanceTarget(DesertBatfly bat)
    {
        return TryGetVengeanceState(bat, out object state) && vengeanceTargetField != null
            ? vengeanceTargetField.GetValue(state) as Creature
            : null;
    }

    private static void RaiseLocalAlarmHook(RaiseLocalAlarmOrig orig, DesertBatflyAI self)
    {
        if (self == null || suppressAlarmEmission > 0) return;
        DesertBatfly emitter = aiFlyField?.GetValue(self) as DesertBatfly;
        if (emitter?.room == null || emitter.dead) return;

        Creature threat = aiDangerField?.GetValue(self) as Creature ??
                          aiAttackerField?.GetValue(self) as Creature;
        Vector2 origin = threat?.mainBodyChunk != null
            ? threat.mainBodyChunk.pos
            : aiEscapeFromField?.GetValue(self) is Vector2 stored ? stored : emitter.mainBodyChunk.pos;
        Vector2 direction = threat?.mainBodyChunk != null
            ? RWCustom.Custom.DirVec(emitter.mainBodyChunk.pos, threat.mainBodyChunk.pos)
            : RWCustom.Custom.DirVec(emitter.mainBodyChunk.pos, origin);

        DesertBatflySignalRuntime.EmitAlarm(
            emitter,
            threat,
            origin,
            direction,
            Mathf.Lerp(0.58f, 0.92f, 1f - emitter.Personality.Nerve),
            "legacy RaiseLocalAlarm migrated to Task12 AlarmFlutter");
        // Deliberately do not call orig: the old method directly wrote every neighbour's
        // retreat/escapeFrom and would double-count the new signal network.
    }

    private static Player FindSocialHarassTargetHook(
        FindSocialHarassTargetOrig orig,
        DesertBatflyAI self)
    {
        DesertBatfly bat = aiFlyField?.GetValue(self) as DesertBatfly;
        if (bat == null || bat.room == null || bat.Injury.BlocksCombat ||
            DesertBatflyIntimidation.HasActiveFearSuppression(bat) ||
            !DesertBatflySignalRuntime.TryGetInfluence(bat, out DesertBatflySignalInfluence influence) ||
            influence.HarassInterest < 0.20f)
            return null;

        Player target = influence.HarassTarget;
        if (target == null || target.dead || target.room != bat.room ||
            !bat.room.VisualContact(bat.mainBodyChunk.pos, target.mainBodyChunk.pos))
            return null;

        if (DesertBatflyThreatRuntime.TryGetDebugState(bat, out DesertBatflyThreatDebugState threat))
        {
            float caution = threat.CounterKillPressure * 0.55f +
                            threat.PiercingPressure * 0.30f +
                            threat.GrabCapturePressure * 0.15f;
            float courage = bat.Personality.Nerve * 0.55f + bat.Personality.Temperament * 0.45f;
            if (caution * threat.Confidence > courage + 0.18f)
                return null;
        }

        return target;
    }

    private static void FlyGrabbed(On.Fly.orig_Grabbed orig, Fly self, Creature.Grasp grasp)
    {
        orig(self, grasp);
        if (self is not DesertBatfly bat || grasp?.grabber == null || grasp.grabber is Fly ||
            bat.dead || bat.room == null)
            return;

        Creature threat = grasp.grabber;
        DesertBatflySignalRuntime.EmitDistress(
            bat,
            threat,
            0.88f,
            "non-Fly grasp emits DistressCall");
        DesertBatflySignalRuntime.EmitAlarm(
            bat,
            threat,
            threat.mainBodyChunk.pos,
            RWCustom.Custom.DirVec(bat.mainBodyChunk.pos, threat.mainBodyChunk.pos),
            0.82f,
            "capture emits AlarmFlutter alongside DistressCall");
    }

    private static void TongueUpdate(On.LizardTongue.orig_Update orig, LizardTongue self)
    {
        orig(self);
        if (self?.lizard == null || self.attached?.owner is not DesertBatfly bat ||
            bat.dead || bat.room == null)
            return;

        DesertBatflySignalRuntime.EmitDistress(
            bat,
            self.lizard,
            0.94f,
            "tongue capture emits DistressCall");
        DesertBatflySignalRuntime.EmitAlarm(
            bat,
            self.lizard,
            self.lizard.mainBodyChunk.pos,
            RWCustom.Custom.DirVec(bat.mainBodyChunk.pos, self.lizard.mainBodyChunk.pos),
            0.90f,
            "tongue capture emits AlarmFlutter");
    }

    private static void CacheVengeanceReflection()
    {
        Type intimidation = typeof(DesertBatflyIntimidation);
        intimidationStatesField = intimidation.GetField(
            "states",
            BindingFlags.NonPublic | BindingFlags.Static);
        Type stateType = intimidation.GetNestedType("State", BindingFlags.NonPublic);
        vengeanceRoleField = stateType?.GetField(
            "Role",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        vengeanceTargetField = stateType?.GetField(
            "VengeanceTarget",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        object table = intimidationStatesField?.GetValue(null);
        intimidationTryGetValue = table?.GetType().GetMethod(
            "TryGetValue",
            BindingFlags.Public | BindingFlags.Instance);
    }

    private static bool TryGetVengeanceState(DesertBatfly bat, out object state)
    {
        state = null;
        if (bat == null || intimidationStatesField == null || intimidationTryGetValue == null)
            return false;
        try
        {
            object table = intimidationStatesField.GetValue(null);
            if (table == null) return false;
            object[] args = { bat, null };
            if (intimidationTryGetValue.Invoke(table, args) is not bool found || !found || args[1] == null)
                return false;
            state = args[1];
            return true;
        }
        catch
        {
            return false;
        }
    }
}
