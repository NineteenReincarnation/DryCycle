using System;
using System.Reflection;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Task11 tactical modifier for the R3-owned Vengeance executor.
///
/// Intimidation state/fear ticking is no longer intercepted here. R3 owns that lifecycle
/// explicitly; this bridge only adjusts the already-authorized Vengeance flight request.
/// </summary>
internal static class DesertBatflyThreatVengeanceBridge
{
    private delegate void ForceFlightOrig(DesertBatfly bat, Vector2 goal, float speed);
    private delegate void ForceFlightDetour(ForceFlightOrig orig, DesertBatfly bat, Vector2 goal, float speed);

    private static Hook forceFlightHook;

    internal static bool Installed => forceFlightHook != null;

    internal static void Enable()
    {
        if (Installed) return;
        Disable();
        try
        {
            MethodInfo forceFlight = typeof(DesertBatflyIntimidation).GetMethod(
                "ForceFlight",
                BindingFlags.NonPublic | BindingFlags.Static,
                null,
                new[] { typeof(DesertBatfly), typeof(Vector2), typeof(float) },
                null);
            if (forceFlight == null) return;
            forceFlightHook = new Hook(forceFlight, (ForceFlightDetour)ForceFlightHook);
        }
        catch
        {
            Disable();
        }
    }

    internal static void Disable()
    {
        try { forceFlightHook?.Dispose(); } catch { }
        forceFlightHook = null;
    }

    private static void ForceFlightHook(
        ForceFlightOrig orig,
        DesertBatfly bat,
        Vector2 goal,
        float speed)
    {
        if (DesertBatflyIntimidation.TryGetVengeanceTarget(bat, out Creature target) &&
            target is Player player)
        {
            goal = DesertBatflyThreatTactics.AdjustExtremeVengeanceGoal(
                bat,
                player,
                goal,
                ref speed);
        }
        orig(bat, goal, speed);
    }
}
