using System;
using System.Reflection;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Narrow Task11 tactical bridge into the existing Vengeance runtime.
///
/// ForceFlight remains owned by Intimidation. Task11 may only alter its requested goal/speed.
/// R3 no longer keeps a parallel Travel frame stamp or reflects into Intimidation private
/// state: the actual DB_BehaviorArbiter PrimaryOwner enforces Travel priority, and the
/// Vengeance target is read through an explicit Intimidation query API.
/// </summary>
internal static class DesertBatflyThreatVengeanceBridge
{
    private delegate void ForceFlightOrig(DesertBatfly bat, Vector2 goal, float speed);
    private delegate void ForceFlightDetour(ForceFlightOrig orig, DesertBatfly bat, Vector2 goal, float speed);
    private delegate void IntimidationUpdateOrig(DesertBatfly bat);
    private delegate void IntimidationUpdateDetour(IntimidationUpdateOrig orig, DesertBatfly bat);

    private static Hook forceFlightHook;
    private static Hook intimidationUpdateHook;

    internal static bool Installed => forceFlightHook != null && intimidationUpdateHook != null;

    internal static void Enable()
    {
        if (Installed) return;
        Disable();
        try
        {
            Type intimidation = typeof(DesertBatflyIntimidation);
            MethodInfo forceFlight = intimidation.GetMethod(
                "ForceFlight",
                BindingFlags.NonPublic | BindingFlags.Static,
                null,
                new[] { typeof(DesertBatfly), typeof(Vector2), typeof(float) },
                null);
            MethodInfo update = intimidation.GetMethod(
                "Update",
                BindingFlags.NonPublic | BindingFlags.Static,
                null,
                new[] { typeof(DesertBatfly) },
                null);
            if (forceFlight == null || update == null) return;

            forceFlightHook = new Hook(forceFlight, (ForceFlightDetour)ForceFlightHook);
            intimidationUpdateHook = new Hook(update, (IntimidationUpdateDetour)IntimidationUpdateHook);
        }
        catch
        {
            Disable();
        }
    }

    internal static void Disable()
    {
        try { intimidationUpdateHook?.Dispose(); } catch { }
        try { forceFlightHook?.Dispose(); } catch { }
        intimidationUpdateHook = null;
        forceFlightHook = null;
    }

    private static void IntimidationUpdateHook(
        IntimidationUpdateOrig orig,
        DesertBatfly bat)
    {
        // Travel wins only when the R3 arbiter selected it for this exact game tick.
        // Merely having a TravelIntent, including a currently suspended one, never freezes
        // Vengeance. The state is paused, not cleared, while Travel owns the frame.
        if (DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.Travel))
            return;
        orig(bat);
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
