using System;
using System.Reflection;
using MonoMod.RuntimeDetour;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Hard environmental survival temporarily pauses the realized Intimidation/Vengeance
/// controller without clearing its runtime state. Task13 therefore wins the local survival
/// frame, and valid vengeance can resume after Recovery.
/// </summary>
internal static class DesertBatflyEnvironmentalVengeanceBridge
{
    private delegate void IntimidationUpdateOrig(DesertBatfly bat);
    private delegate void IntimidationUpdateDetour(IntimidationUpdateOrig orig, DesertBatfly bat);

    private static Hook updateHook;

    internal static bool Installed => updateHook != null;

    internal static void Enable()
    {
        if (Installed) return;
        Disable();
        try
        {
            MethodInfo update = typeof(DesertBatflyIntimidation).GetMethod(
                "Update",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                null,
                new[] { typeof(DesertBatfly) },
                null);
            if (update == null) return;
            updateHook = new Hook(update, (IntimidationUpdateDetour)UpdateHook);
        }
        catch
        {
            Disable();
        }
    }

    internal static void Disable()
    {
        try { updateHook?.Dispose(); } catch { }
        updateHook = null;
    }

    private static void UpdateHook(IntimidationUpdateOrig orig, DesertBatfly bat)
    {
        if (bat != null && DesertBatflyEnvironmentalBehavior.HardSurvival(bat))
        {
            // Do not call ClearVengeance. Runtime vengeance/fear state is deliberately
            // frozen for this hard-survival frame and can continue when weather recovers.
            return;
        }
        orig(bat);
    }
}
