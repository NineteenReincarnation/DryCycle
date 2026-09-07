using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Narrow Task 11 bridge into the existing Vengeance runtime.
///
/// ForceFlight remains owned by Intimidation: Task 11 may only alter the requested goal
/// and speed before calling the original method. A separate frame stamp enforces the
/// already-defined Task 09 priority without deleting Vengeance state: when Task 09 really
/// owns a realized frame, Intimidation.Update is skipped for that exact game tick, freezing
/// Vengeance until travel yields again.
/// </summary>
internal static class DesertBatflyThreatVengeanceBridge
{
    private delegate void ForceFlightOrig(DesertBatfly bat, Vector2 goal, float speed);
    private delegate void ForceFlightDetour(ForceFlightOrig orig, DesertBatfly bat, Vector2 goal, float speed);
    private delegate void IntimidationUpdateOrig(DesertBatfly bat);
    private delegate void IntimidationUpdateDetour(IntimidationUpdateOrig orig, DesertBatfly bat);

    private sealed class TravelFrameStamp
    {
        internal int Clock = int.MinValue;
    }

    private static Hook forceFlightHook;
    private static Hook intimidationUpdateHook;
    private static ConditionalWeakTable<DesertBatfly, TravelFrameStamp> travelFrames = new();
    private static FieldInfo statesField;
    private static MethodInfo tryGetValue;
    private static FieldInfo vengeanceTargetField;

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

            if (forceFlight == null || update == null || statesField == null ||
                tryGetValue == null || vengeanceTargetField == null)
                return;

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
        travelFrames = new ConditionalWeakTable<DesertBatfly, TravelFrameStamp>();
        statesField = null;
        tryGetValue = null;
        vengeanceTargetField = null;
    }

    /// <summary>
    /// Called only after Task09.TryDriveRealized returned true. An intent that is merely
    /// present but suspended by restraint, immediate danger or severe injury never gets
    /// this stamp, so those higher priorities still allow Intimidation to update normally.
    /// </summary>
    internal static void MarkTravelOwnedFrame(DesertBatfly bat)
    {
        int clock = bat?.room?.game?.clock ?? int.MinValue;
        if (bat == null || clock == int.MinValue) return;
        travelFrames.GetOrCreateValue(bat).Clock = clock;
    }

    private static void IntimidationUpdateHook(
        IntimidationUpdateOrig orig,
        DesertBatfly bat)
    {
        int clock = bat?.room?.game?.clock ?? int.MinValue;
        if (clock != int.MinValue &&
            travelFrames.TryGetValue(bat, out TravelFrameStamp stamp) &&
            stamp.Clock == clock)
            return;
        orig(bat);
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
