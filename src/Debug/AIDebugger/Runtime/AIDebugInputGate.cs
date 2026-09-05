using System;
using System.Reflection;
using BepInEx.Logging;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace DryCycle.Debugging.AI;

internal static class AIDebugInputGate
{
    private delegate Player.InputPackage PlayerInputOrig(int categoryID, int playerNumber);
    private delegate Player.InputPackage PlayerInputDetour(PlayerInputOrig orig, int categoryID, int playerNumber);

    private static Hook hook;
    private static ManualLogSource logger;

    internal static bool Installed => hook != null;

    internal static void Install(ManualLogSource log)
    {
        logger = log;
        if (hook != null) return;
        try
        {
            MethodInfo method = typeof(RWInput).GetMethod(
                nameof(RWInput.PlayerInputLogic),
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(int), typeof(int) },
                null);
            if (method == null)
                throw new MissingMethodException(typeof(RWInput).FullName, "PlayerInputLogic(int,int)");

            hook = new Hook(method, (PlayerInputDetour)PlayerInputLogicHook);
            logger?.LogInfo("DryCycle AI Observatory input gate installed on RWInput.PlayerInputLogic.");
        }
        catch (Exception error)
        {
            logger?.LogWarning("DryCycle AI Observatory input gate unavailable: " + error);
            hook?.Dispose();
            hook = null;
        }
    }

    internal static void Uninstall()
    {
        try { hook?.Dispose(); }
        catch (Exception error) { logger?.LogWarning("AI Observatory input gate dispose failed: " + error.Message); }
        hook = null;
    }

    private static Player.InputPackage PlayerInputLogicHook(PlayerInputOrig orig, int categoryID, int playerNumber)
    {
        Player.InputPackage result = orig(categoryID, playerNumber);
        if (!AIDebuggerRuntime.BlocksPlayerInput) return result;

        // Preserve controller metadata, but neutralize gameplay commands. This lets the
        // player keep using the configured device immediately after leaving INTERACT mode.
        result.x = 0;
        result.y = 0;
        result.jmp = false;
        result.thrw = false;
        result.pckp = false;
        result.mp = false;
        result.spec = false;
        result.crouchToggle = false;
        result.analogueDir = Vector2.zero;
        result.downDiagonal = 0;
        return result;
    }
}
