using System;
using System.Reflection;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Fog/DenseFog only reduces Task12's visual channel. Alarm/Distress close acoustic
/// perception remains unchanged, preserving Task12's hard signal semantics.
/// </summary>
internal static class DesertBatflyEnvironmentalSignalBridge
{
    private delegate bool TryPerceiveOrig(
        DesertBatfly receiver,
        DesertBatflySignalPacket packet,
        out DesertBatflySignalPerception perception,
        out float attenuation);

    private delegate bool TryPerceiveDetour(
        TryPerceiveOrig orig,
        DesertBatfly receiver,
        DesertBatflySignalPacket packet,
        out DesertBatflySignalPerception perception,
        out float attenuation);

    private static Hook perceiveHook;

    internal static bool Installed => perceiveHook != null;

    internal static void Enable()
    {
        if (Installed) return;
        Disable();
        try
        {
            MethodInfo method = typeof(DesertBatflySignalRuntime).GetMethod(
                "TryPerceive",
                BindingFlags.Static | BindingFlags.NonPublic,
                null,
                new[]
                {
                    typeof(DesertBatfly),
                    typeof(DesertBatflySignalPacket),
                    typeof(DesertBatflySignalPerception).MakeByRefType(),
                    typeof(float).MakeByRefType()
                },
                null);
            if (method == null) return;
            perceiveHook = new Hook(method, (TryPerceiveDetour)TryPerceiveHook);
        }
        catch
        {
            Disable();
        }
    }

    internal static void Disable()
    {
        try { perceiveHook?.Dispose(); } catch { }
        perceiveHook = null;
    }

    private static bool TryPerceiveHook(
        TryPerceiveOrig orig,
        DesertBatfly receiver,
        DesertBatflySignalPacket packet,
        out DesertBatflySignalPerception perception,
        out float attenuation)
    {
        bool result = orig(receiver, packet, out perception, out attenuation);
        if (!result || perception != DesertBatflySignalPerception.Visual ||
            receiver == null || packet?.Emitter == null)
            return result;

        float visibility = DesertBatflyEnvironmentalBehavior.VisibilityScale(receiver);
        if (visibility >= 0.98f) return true;

        float distance = Vector2.Distance(receiver.mainBodyChunk.pos, packet.Emitter.mainBodyChunk.pos);
        float baseRadius = VisualRadius(packet.Kind);
        float visualRadius = baseRadius * Mathf.Lerp(0.34f, 1f, visibility);
        if (distance <= visualRadius)
        {
            attenuation = Mathf.Lerp(1f, 0.34f, Mathf.Clamp01(distance / Mathf.Max(1f, visualRadius)));
            return true;
        }

        float acousticRadius = packet.Kind switch
        {
            DesertBatflySignalKind.AlarmFlutter => 95f,
            DesertBatflySignalKind.DistressCall => 108f,
            _ => 0f
        };
        if (acousticRadius > 0f && distance <= acousticRadius)
        {
            perception = DesertBatflySignalPerception.CloseAcoustic;
            attenuation = Mathf.Lerp(0.62f, 0.30f, Mathf.Clamp01(distance / acousticRadius));
            return true;
        }

        perception = DesertBatflySignalPerception.None;
        attenuation = 0f;
        return false;
    }

    private static float VisualRadius(DesertBatflySignalKind kind) => kind switch
    {
        DesertBatflySignalKind.AlarmFlutter => 300f,
        DesertBatflySignalKind.DistressCall => 250f,
        DesertBatflySignalKind.RallySignal => 235f,
        DesertBatflySignalKind.RoostCall => 215f,
        DesertBatflySignalKind.HarassSignal => 235f,
        DesertBatflySignalKind.SafeSignal => 195f,
        _ => 200f
    };
}
