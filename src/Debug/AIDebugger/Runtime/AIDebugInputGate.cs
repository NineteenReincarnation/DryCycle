using System;
using BepInEx.Logging;
using UnityEngine;

namespace DryCycle.Debugging.AI;

/// <summary>
/// Prevents Observatory interaction from leaking into slugcat gameplay input.
///
/// This hooks Player.checkInput after Rain World has populated the player's input packages rather
/// than detouring RWInput.PlayerInputLogic through reflection. That keeps the gate on a normal
/// HookGen boundary and avoids depending on runtime overload discovery.
/// </summary>
internal static class AIDebugInputGate
{
    private static bool installed;
    private static ManualLogSource logger;

    internal static bool Installed => installed;

    internal static void Install(ManualLogSource log)
    {
        logger = log;
        if (installed) return;

        On.Player.checkInput += Player_checkInput;
        installed = true;
        logger?.LogInfo("DryCycle AI Observatory input gate installed on Player.checkInput through HookGen.");
    }

    internal static void Uninstall()
    {
        if (!installed) return;
        try
        {
            On.Player.checkInput -= Player_checkInput;
        }
        catch (Exception error)
        {
            StartupDiagnostics.Failure("AIDebugInputGate.Uninstall", error);
            logger?.LogWarning("AI Observatory input gate cleanup failed: " + error.Message);
        }
        installed = false;
    }

    private static void Player_checkInput(On.Player.orig_checkInput orig, Player self)
    {
        orig(self);

        if (self == null || self.AI != null || !AIDebuggerRuntime.BlocksPlayerInput ||
            self.input == null || self.input.Length == 0)
            return;

        self.input[0] = Neutralize(self.input[0]);
        self.mapInput = Neutralize(self.mapInput);
        self.pointInput = Neutralize(self.pointInput);
    }

    private static Player.InputPackage Neutralize(Player.InputPackage result)
    {
        // Preserve controller metadata, but neutralize every gameplay command. This lets the
        // configured device resume immediately after leaving the Observatory interaction surface.
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
