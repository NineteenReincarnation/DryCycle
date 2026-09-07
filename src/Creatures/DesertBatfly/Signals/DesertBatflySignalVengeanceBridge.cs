using System;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Emits RallySignal at the moment an existing Intimidation Avenger is armed.
/// It does not create Vengeance and does not touch Vengeance state; it only exposes the
/// already-made decision to nearby receivers before ArmVengeanceGroup scores supporters.
/// The Task11->Task12 read-only threat-response bridge is enabled and disabled alongside
/// this bridge so the existing DesertBatflyHooks lifecycle owns all Task12 detours.
/// </summary>
internal static class DesertBatflySignalVengeanceBridge
{
    private static Hook armHook;
    private static Delegate detour;

    internal static bool Installed => armHook != null;

    internal static void Enable()
    {
        if (Installed)
        {
            DesertBatflySignalThreatBridge.Enable();
            return;
        }
        Disable();
        try
        {
            MethodInfo arm = typeof(DesertBatflyIntimidation).GetMethod(
                "ArmVengeance",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (arm == null) return;
            ParameterInfo[] p = arm.GetParameters();
            if (p.Length != 10 || p[0].ParameterType != typeof(DesertBatfly) ||
                p[2].ParameterType != typeof(Creature) || p[6].ParameterType != typeof(float) ||
                p[8].ParameterType != typeof(bool) || p[9].ParameterType != typeof(DesertBatfly))
                return;

            Type origType = Expression.GetDelegateType(new[]
            {
                p[0].ParameterType, p[1].ParameterType, p[2].ParameterType, p[3].ParameterType,
                p[4].ParameterType, p[5].ParameterType, p[6].ParameterType, p[7].ParameterType,
                p[8].ParameterType, p[9].ParameterType, typeof(void)
            });
            Type detourType = Expression.GetDelegateType(new[]
            {
                origType,
                p[0].ParameterType, p[1].ParameterType, p[2].ParameterType, p[3].ParameterType,
                p[4].ParameterType, p[5].ParameterType, p[6].ParameterType, p[7].ParameterType,
                p[8].ParameterType, p[9].ParameterType, typeof(void)
            });

            var dm = new DynamicMethod(
                "DryCycle_Task12_ArmVengeance",
                typeof(void),
                new[]
                {
                    origType,
                    p[0].ParameterType, p[1].ParameterType, p[2].ParameterType, p[3].ParameterType,
                    p[4].ParameterType, p[5].ParameterType, p[6].ParameterType, p[7].ParameterType,
                    p[8].ParameterType, p[9].ParameterType
                },
                typeof(DesertBatflySignalVengeanceBridge).Module,
                true);
            ILGenerator il = dm.GetILGenerator();

            il.Emit(OpCodes.Ldarg_0);
            for (byte i = 1; i <= 10; i++) il.Emit(OpCodes.Ldarg_S, i);
            il.Emit(OpCodes.Callvirt, origType.GetMethod("Invoke"));

            il.Emit(OpCodes.Ldarg_1); // bat
            il.Emit(OpCodes.Ldarg_3); // threat
            il.Emit(OpCodes.Ldarg_S, (byte)7); // drive
            il.Emit(OpCodes.Ldarg_S, (byte)9); // supportOnly
            il.Emit(OpCodes.Ldarg_S, (byte)10); // leader
            il.Emit(OpCodes.Call, typeof(DesertBatflySignalVengeanceBridge).GetMethod(
                nameof(AfterArm), BindingFlags.NonPublic | BindingFlags.Static));
            il.Emit(OpCodes.Ret);

            detour = dm.CreateDelegate(detourType);
            armHook = new Hook(arm, detour);
            DesertBatflySignalThreatBridge.Enable();
        }
        catch
        {
            Disable();
        }
    }

    internal static void Disable()
    {
        DesertBatflySignalThreatBridge.Disable();
        try { armHook?.Dispose(); } catch { }
        armHook = null;
        detour = null;
    }

    private static void AfterArm(
        DesertBatfly bat,
        Creature threat,
        float drive,
        bool supportOnly,
        DesertBatfly leader)
    {
        // Only the actual Avenger emits Rally. Supporters never recursively recruit.
        if (bat?.room == null || bat.dead || threat == null || supportOnly || leader != null ||
            !DesertBatflySignalIntegration.IsVengeanceAvenger(bat))
            return;

        DesertBatflySignalRoomRuntime.RoomState room = DesertBatflySignalRoomRuntime.For(bat.room);
        Vector2 origin = bat.mainBodyChunk.pos;
        Vector2 direction = threat.mainBodyChunk != null
            ? RWCustom.Custom.DirVec(origin, threat.mainBodyChunk.pos)
            : Vector2.zero;
        DesertBatflySignalPacket packet = room?.AddOrRefresh(
            bat.room,
            DesertBatflySignalKind.RallySignal,
            bat,
            bat,
            threat,
            threat as Player,
            origin,
            direction,
            Mathf.Clamp01(0.55f + Mathf.Clamp01(drive) * 0.30f),
            84);
        if (packet == null) return;

        // Delivery is immediate so the remainder of ArmVengeanceGroup can consume
        // RallyInterest in DesertBatflySocialBond.Motivation during this same event.
        room.DeliverUrgent(bat.room, packet);
    }
}
