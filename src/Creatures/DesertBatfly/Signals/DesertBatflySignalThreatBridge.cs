using System;
using System.Reflection;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Read-only bridge between Task11 and Task12. The receiver consults only its own
/// per-player Threat Signature Memory after the base social response is computed.
/// No memory is copied from the emitter, no evidence is written, and current held-item
/// cues are not inferred through a signal.
///
/// This bridge also handles Alarm packets whose source is an anonymous hazard rather than
/// a Creature. It reuses DesertBatflyAI.Threatened for the existing Escape state while
/// suppressing a fresh signal generation, then points escapeFrom at the real alarm origin.
/// </summary>
internal static class DesertBatflySignalThreatBridge
{
    private delegate float ResponseOrig(
        DesertBatfly receiver,
        DesertBatflySignalPacket packet,
        float attenuation);
    private delegate float ResponseDetour(
        ResponseOrig orig,
        DesertBatfly receiver,
        DesertBatflySignalPacket packet,
        float attenuation);

    private delegate void ApplyAlarmOrig(
        DesertBatfly receiver,
        DesertBatflySignalPacket packet,
        float response);
    private delegate void ApplyAlarmDetour(
        ApplyAlarmOrig orig,
        DesertBatfly receiver,
        DesertBatflySignalPacket packet,
        float response);

    private static Hook responseHook;
    private static Hook applyAlarmHook;
    private static FieldInfo suppressAlarmField;
    private static FieldInfo escapeFromField;

    internal static bool Installed => responseHook != null && applyAlarmHook != null;

    internal static void Enable()
    {
        if (Installed) return;
        Disable();
        try
        {
            MethodInfo response = typeof(DesertBatflySignalRuntime).GetMethod(
                "ResponseStrength",
                BindingFlags.NonPublic | BindingFlags.Static,
                null,
                new[]
                {
                    typeof(DesertBatfly),
                    typeof(DesertBatflySignalPacket),
                    typeof(float)
                },
                null);
            MethodInfo applyAlarm = typeof(DesertBatflySignalIntegration).GetMethod(
                "ApplyAlarm",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static,
                null,
                new[]
                {
                    typeof(DesertBatfly),
                    typeof(DesertBatflySignalPacket),
                    typeof(float)
                },
                null);
            suppressAlarmField = typeof(DesertBatflySignalIntegration).GetField(
                "suppressAlarmEmission",
                BindingFlags.NonPublic | BindingFlags.Static);
            escapeFromField = typeof(DesertBatflyAI).GetField(
                "escapeFrom",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (response == null || applyAlarm == null || suppressAlarmField == null ||
                escapeFromField == null)
                return;

            responseHook = new Hook(response, (ResponseDetour)ResponseHook);
            applyAlarmHook = new Hook(applyAlarm, (ApplyAlarmDetour)ApplyAlarmHook);
        }
        catch
        {
            Disable();
        }
    }

    internal static void Disable()
    {
        try { applyAlarmHook?.Dispose(); } catch { }
        try { responseHook?.Dispose(); } catch { }
        applyAlarmHook = null;
        responseHook = null;
        suppressAlarmField = null;
        escapeFromField = null;
    }

    private static float ResponseHook(
        ResponseOrig orig,
        DesertBatfly receiver,
        DesertBatflySignalPacket packet,
        float attenuation)
    {
        float response = orig(receiver, packet, attenuation);
        if (receiver?.DesertState == null || packet == null || response <= 0f)
            return response;

        Player player = packet.PlayerTarget ?? packet.Threat as Player;
        if (player == null) return response;

        int slot = DesertBatflyThreatRuntime.PlayerSlot(player);
        if (!DesertBatflyThreatRuntime.ValidSlot(slot)) return response;
        DesertBatflyPlayerThreatMemory memory =
            DesertBatflyThreatMemoryStore.For(receiver.DesertState, slot);
        if (memory == null || memory.Confidence < 0.04f) return response;

        float lethalCaution = Mathf.Clamp01(
            memory.PiercingPressure * 0.30f +
            memory.CounterKillPressure * 0.34f +
            memory.ExplosionPressure * 0.17f +
            memory.GrabCapturePressure * 0.10f +
            memory.PursuitPressure * 0.09f);
        float caution = lethalCaution * memory.Confidence;

        // The same private experience can make an alarm more credible while making a
        // voluntary Rally/Harass response less attractive. Distress is reduced only
        // mildly because Bond and rescue motivation remain independent inputs.
        switch (packet.Kind)
        {
            case DesertBatflySignalKind.AlarmFlutter:
                response *= 1f + caution * 0.24f;
                break;
            case DesertBatflySignalKind.DistressCall:
                response *= 1f - caution * 0.22f;
                break;
            case DesertBatflySignalKind.RallySignal:
                response *= 1f - caution * 0.52f;
                break;
            case DesertBatflySignalKind.HarassSignal:
                response *= 1f - caution * 0.62f;
                break;
        }

        return Mathf.Clamp01(response);
    }

    private static void ApplyAlarmHook(
        ApplyAlarmOrig orig,
        DesertBatfly receiver,
        DesertBatflySignalPacket packet,
        float response)
    {
        if (packet?.Threat != null)
        {
            orig(receiver, packet, response);
            return;
        }

        if (receiver == null || packet == null || response < 0.34f || receiver.dead ||
            !receiver.Consious || receiver.room == null || receiver.inShortcut ||
            receiver.Injury.IsSeverelyInjured ||
            DesertBatflyTravelNavigation.HasIntent(receiver.abstractCreature))
            return;

        // Anonymous hazard: reuse the existing Escape state, but prevent Threatened(null)
        // from emitting a second root Alarm. Afterwards replace its generic escape point
        // with the real signal origin so the bat moves away from the observed hazard.
        int previous = 0;
        try
        {
            object raw = suppressAlarmField?.GetValue(null);
            if (raw is int value) previous = value;
            suppressAlarmField?.SetValue(null, previous + 1);
            receiver.DesertAI.Threatened(null, false);
            escapeFromField?.SetValue(receiver.DesertAI, packet.Origin);
        }
        finally
        {
            suppressAlarmField?.SetValue(null, previous);
        }
    }
}
