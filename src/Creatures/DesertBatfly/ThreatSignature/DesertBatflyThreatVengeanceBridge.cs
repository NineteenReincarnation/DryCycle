using System;
using System.Reflection;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Narrow Task 11 bridge into the existing private Vengeance movement entry point.
/// The bridge modifies only the requested local goal/speed, then calls the original
/// Intimidation ForceFlight implementation. Vengeance continues to own commitment,
/// timers, damage and BodyChunk velocity.
/// </summary>
internal static class DesertBatflyThreatVengeanceBridge
{
    private delegate void ForceFlightOrig(DesertBatfly bat, Vector2 goal, float speed);
    private delegate void ForceFlightDetour(ForceFlightOrig orig, DesertBatfly bat, Vector2 goal, float speed);

    private static Hook hook;
    private static FieldInfo statesField;
    private static MethodInfo tryGetValue;
    private static FieldInfo vengeanceTargetField;

    internal static bool Installed => hook != null;

    internal static void Enable()
    {
        if (hook != null) return;
        try
        {
            Type intimidation = typeof(DesertBatflyIntimidation);
            MethodInfo forceFlight = intimidation.GetMethod(
                "ForceFlight",
                BindingFlags.NonPublic | BindingFlags.Static,
                null,
                new[] { typeof(DesertBatfly), typeof(Vector2), typeof(float) },
                null);
            statesField = intimidation.GetField(
                "states",
                BindingFlags.NonPublic | BindingFlags.Static);
            Type stateType = intimidation.GetNestedType(
                "State",
                BindingFlags.NonPublic);
            vengeanceTargetField = stateType?.GetField(
                "VengeanceTarget",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

            object table = statesField?.GetValue(null);
            tryGetValue = table?.GetType().GetMethod(
                "TryGetValue",
                BindingFlags.Public | BindingFlags.Instance);

            if (forceFlight == null || statesField == null || tryGetValue == null ||
                vengeanceTargetField == null)
                return;

            hook = new Hook(forceFlight, (ForceFlightDetour)ForceFlightHook);
        }
        catch
        {
            try { hook?.Dispose(); } catch { }
            hook = null;
        }
    }

    internal static void Disable()
    {
        try { hook?.Dispose(); } catch { }
        hook = null;
        statesField = null;
        tryGetValue = null;
        vengeanceTargetField = null;
    }

    private static void ForceFlightHook(
        ForceFlightOrig orig,
        DesertBatfly bat,
        Vector2 goal,
        float speed)
    {
        Player target = ResolveVengeancePlayer(bat);
        if (target != null)
            goal = DesertBatflyThreatTactics.AdjustExtremeVengeanceGoal(
                bat,
                target,
                goal,
                ref speed);
        orig(bat, goal, speed);
    }

    private static Player ResolveVengeancePlayer(DesertBatfly bat)
    {
        if (bat == null || !DesertBatflyIntimidation.IsExtremeVengeanceActive(bat) ||
            statesField == null || tryGetValue == null || vengeanceTargetField == null)
            return null;

        try
        {
            object table = statesField.GetValue(null);
            if (table == null) return null;
            object[] args = { bat, null };
            if (tryGetValue.Invoke(table, args) is not bool found || !found || args[1] == null)
                return null;
            return vengeanceTargetField.GetValue(args[1]) as Player;
        }
        catch
        {
            return null;
        }
    }
}
