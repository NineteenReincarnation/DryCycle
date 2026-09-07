using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Narrow bridges from the Task12 information layer into existing behavior owners.
/// Task12 never owns locomotion. Alarm reuses DesertBatflyAI.Threatened while suppressing
/// its legacy re-broadcast. Intimidation keeps tier-0/direct experience, while historical
/// tier-1+ Secondary/Chain fear is replaced by one bounded Task12 Alarm generation.
/// Formal Harass/Roost social contagion consumes Task12 signals instead of spying on
/// another bat's private AI state.
/// </summary>
internal static class DesertBatflySignalIntegration
{
    private delegate void RaiseLocalAlarmOrig(DesertBatflyAI self);
    private delegate void RaiseLocalAlarmDetour(RaiseLocalAlarmOrig orig, DesertBatflyAI self);
    private delegate Player FindSocialHarassTargetOrig(DesertBatflyAI self);
    private delegate Player FindSocialHarassTargetDetour(FindSocialHarassTargetOrig orig, DesertBatflyAI self);
    private delegate DesertBatfly FindRoostSourceOrig(
        DesertBatfly bat,
        IReadOnlyList<DesertBatfly> roosting,
        out int chainSize,
        out float bond);
    private delegate DesertBatfly FindRoostSourceDetour(
        FindRoostSourceOrig orig,
        DesertBatfly bat,
        IReadOnlyList<DesertBatfly> roosting,
        out int chainSize,
        out float bond);

    private sealed class FearEventStamp
    {
        internal int Clock = int.MinValue;
        internal int ThreatIdentity = int.MinValue;
        internal int PositionHash = int.MinValue;
        internal int Kind = int.MinValue;
    }

    private static Hook alarmHook;
    private static Hook harassHook;
    private static Hook roostHook;
    private static Hook receiveFearHook;
    private static Delegate receiveFearDetour;

    private static FieldInfo aiFlyField;
    private static FieldInfo aiAttackerField;
    private static FieldInfo aiDangerField;
    private static FieldInfo aiEscapeFromField;

    private static FieldInfo intimidationStatesField;
    private static MethodInfo intimidationTryGetValue;
    private static FieldInfo vengeanceRoleField;
    private static FieldInfo vengeanceTargetField;

    private static ConditionalWeakTable<Room, FearEventStamp> fearEventStamps = new();

    [ThreadStatic]
    private static int suppressAlarmEmission;

    internal static bool Installed =>
        alarmHook != null && harassHook != null && roostHook != null && receiveFearHook != null;

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

            Type social = typeof(DesertBatflySocialLife);
            MethodInfo findRoost = social.GetMethod(
                "FindRoostSource",
                BindingFlags.NonPublic | BindingFlags.Static);

            if (raiseAlarm == null || findHarass == null || findRoost == null || aiFlyField == null ||
                aiAttackerField == null || aiDangerField == null || aiEscapeFromField == null)
                return;

            alarmHook = new Hook(raiseAlarm, (RaiseLocalAlarmDetour)RaiseLocalAlarmHook);
            harassHook = new Hook(findHarass, (FindSocialHarassTargetDetour)FindSocialHarassTargetHook);
            roostHook = new Hook(findRoost, (FindRoostSourceDetour)FindRoostSourceHook);

            if (!InstallReceiveFearHook())
            {
                Disable();
                return;
            }

            CacheVengeanceReflection();
            DB_EventHub.Capture += CaptureEvent;
        }
        catch
        {
            Disable();
        }
    }

    internal static void Disable()
    {
        try { DB_EventHub.Capture -= CaptureEvent; } catch { }
        try { receiveFearHook?.Dispose(); } catch { }
        try { roostHook?.Dispose(); } catch { }
        try { harassHook?.Dispose(); } catch { }
        try { alarmHook?.Dispose(); } catch { }
        receiveFearHook = null;
        receiveFearDetour = null;
        roostHook = null;
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
        fearEventStamps = new ConditionalWeakTable<Room, FearEventStamp>();
        suppressAlarmEmission = 0;
    }

    internal static void Reset()
    {
        fearEventStamps = new ConditionalWeakTable<Room, FearEventStamp>();
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

    private static DesertBatfly FindRoostSourceHook(
        FindRoostSourceOrig orig,
        DesertBatfly bat,
        IReadOnlyList<DesertBatfly> roosting,
        out int chainSize,
        out float bond)
    {
        chainSize = 0;
        bond = 0f;
        if (bat == null || bat.room == null ||
            !DesertBatflySignalRuntime.TryGetInfluence(bat, out DesertBatflySignalInfluence influence) ||
            influence.RoostInterest < 0.16f)
            return null;

        DesertBatfly source = influence.RoostSource;
        if (source == null || source == bat || source.room != bat.room || source.dead ||
            !source.Consious || source.inShortcut || source.AI?.behavior != FlyAI.Behavior.Chain)
            return null;

        if (Vector2.Distance(bat.mainBodyChunk.pos, source.mainBodyChunk.pos) > 230f)
            return null;

        chainSize = ChainLength(source);
        bond = Mathf.Max(
            DesertBatflySocialBond.GetBondStrength(bat, source),
            DesertBatflySocialBond.GetBondStrength(source, bat));
        return source;
    }

    private static int ChainLength(Fly source)
    {
        Fly member = source?.FirstInChain();
        int count = 0;
        while (member != null && count < 16)
        {
            count++;
            member = member.NextInChain();
        }
        return count;
    }

    private static void CaptureEvent(DB_CaptureEvent capture)
    {
        DesertBatfly bat = capture.Victim;
        Creature threat = capture.Captor;
        if (bat == null || threat == null || bat.dead || bat.room == null || threat.room != bat.room)
            return;

        bool tongue = capture.CaptureKind == DB_CaptureKind.Tongue;
        DesertBatflySignalRuntime.EmitDistress(
            bat,
            threat,
            tongue ? 0.94f : 0.88f,
            tongue
                ? "semantic tongue capture emits one DistressCall"
                : "semantic non-Fly grasp emits one DistressCall");
        DesertBatflySignalRuntime.EmitAlarm(
            bat,
            threat,
            threat.mainBodyChunk.pos,
            RWCustom.Custom.DirVec(bat.mainBodyChunk.pos, threat.mainBodyChunk.pos),
            tongue ? 0.90f : 0.82f,
            tongue
                ? "semantic tongue capture emits one AlarmFlutter"
                : "semantic grasp emits one AlarmFlutter alongside DistressCall");
    }

    private static bool InstallReceiveFearHook()
    {
        Type intimidation = typeof(DesertBatflyIntimidation);
        MethodInfo receiveFear = intimidation.GetMethod(
            "ReceiveFear",
            BindingFlags.NonPublic | BindingFlags.Static);
        if (receiveFear == null) return false;

        ParameterInfo[] p = receiveFear.GetParameters();
        if (p.Length != 6 || p[0].ParameterType != typeof(DesertBatfly) ||
            p[1].ParameterType != typeof(Creature) || p[2].ParameterType != typeof(Vector2) ||
            p[3].ParameterType != typeof(int) || p[4].ParameterType != typeof(float))
            return false;

        Type origDelegateType = Expression.GetDelegateType(new[]
        {
            p[0].ParameterType,
            p[1].ParameterType,
            p[2].ParameterType,
            p[3].ParameterType,
            p[4].ParameterType,
            p[5].ParameterType,
            typeof(void)
        });
        Type detourDelegateType = Expression.GetDelegateType(new[]
        {
            origDelegateType,
            p[0].ParameterType,
            p[1].ParameterType,
            p[2].ParameterType,
            p[3].ParameterType,
            p[4].ParameterType,
            p[5].ParameterType,
            typeof(void)
        });

        var dm = new DynamicMethod(
            "DryCycle_Task12_ReceiveFear",
            typeof(void),
            new[]
            {
                origDelegateType,
                p[0].ParameterType,
                p[1].ParameterType,
                p[2].ParameterType,
                p[3].ParameterType,
                p[4].ParameterType,
                p[5].ParameterType
            },
            typeof(DesertBatflySignalIntegration).Module,
            true);
        ILGenerator il = dm.GetILGenerator();
        Label indirect = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_S, (byte)4);
        il.Emit(OpCodes.Brtrue_S, indirect);

        il.Emit(OpCodes.Call, typeof(DesertBatflySignalIntegration).GetMethod(
            nameof(BeginDirectFear), BindingFlags.NonPublic | BindingFlags.Static));
        il.Emit(OpCodes.Ldarg_0);
        for (byte i = 1; i <= 6; i++) il.Emit(OpCodes.Ldarg_S, i);
        il.Emit(OpCodes.Callvirt, origDelegateType.GetMethod("Invoke"));
        EmitFearHandlerCall(il);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(indirect);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Ldarg_S, (byte)4);
        il.Emit(OpCodes.Ldarg_S, (byte)5);
        il.Emit(OpCodes.Ldarg_S, (byte)6);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Call, typeof(DesertBatflySignalIntegration).GetMethod(
            nameof(HandleIndirectFear), BindingFlags.NonPublic | BindingFlags.Static));
        il.Emit(OpCodes.Ret);

        receiveFearDetour = dm.CreateDelegate(detourDelegateType);
        receiveFearHook = new Hook(receiveFear, receiveFearDetour);
        return receiveFearHook != null;
    }

    private static void EmitFearHandlerCall(ILGenerator il)
    {
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Ldarg_S, (byte)4);
        il.Emit(OpCodes.Ldarg_S, (byte)5);
        il.Emit(OpCodes.Ldarg_S, (byte)6);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Call, typeof(DesertBatflySignalIntegration).GetMethod(
            nameof(EndDirectFearAndEmit), BindingFlags.NonPublic | BindingFlags.Static));
    }

    private static void BeginDirectFear()
    {
        suppressAlarmEmission++;
    }

    private static void EndDirectFearAndEmit(
        DesertBatfly witness,
        Creature threat,
        Vector2 eventPosition,
        int tier,
        float threatScale,
        int eventKind)
    {
        suppressAlarmEmission = Mathf.Max(0, suppressAlarmEmission - 1);
        if (tier != 0 || witness?.room == null || witness.dead || threat == null)
            return;

        int clock = witness.room.game?.clock ?? 0;
        int identity = ThreatIdentity(threat);
        int positionHash = Mathf.RoundToInt(eventPosition.x / 20f) * 73856093 ^
                           Mathf.RoundToInt(eventPosition.y / 20f) * 19349663;
        FearEventStamp stamp = fearEventStamps.GetOrCreateValue(witness.room);
        if (stamp.Clock == clock && stamp.ThreatIdentity == identity &&
            stamp.PositionHash == positionHash && stamp.Kind == eventKind)
            return;

        stamp.Clock = clock;
        stamp.ThreatIdentity = identity;
        stamp.PositionHash = positionHash;
        stamp.Kind = eventKind;

        DesertBatflySignalRuntime.EmitAlarm(
            witness,
            threat,
            eventPosition,
            RWCustom.Custom.DirVec(witness.mainBodyChunk.pos, eventPosition),
            Mathf.Clamp01(0.62f + Mathf.Clamp(threatScale, 0f, 1.5f) * 0.20f),
            "direct Intimidation witness emits sole indirect Task12 Alarm generation");
    }

    private static void HandleIndirectFear(
        DesertBatfly bat,
        Creature threat,
        Vector2 eventPosition,
        int tier,
        float threatScale,
        int eventKind)
    {
        if (bat?.abstractCreature == null ||
            !DryCycle.Debugging.AI.AIDebugTrace.IsWatched(bat.abstractCreature))
            return;
        DryCycle.Debugging.AI.AIDebugTrace.Record(
            bat.abstractCreature,
            DryCycle.Debugging.AI.AIDebugEventCategory.Social,
            "Task12LegacyIndirectFearSuppressed",
            $"tier={tier}",
            "Secondary/Chain fear no longer applies Trauma/Fear directly; Task12 Alarm perception owns indirect propagation");
    }

    private static int ThreatIdentity(Creature threat)
    {
        if (threat?.abstractCreature == null) return threat?.GetHashCode() ?? int.MinValue;
        unchecked
        {
            EntityID id = threat.abstractCreature.ID;
            return id.spawner * 397 ^ id.number;
        }
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
