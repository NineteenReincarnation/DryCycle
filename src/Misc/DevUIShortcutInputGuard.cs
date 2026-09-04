using UnityEngine;

namespace DryCycle.Misc;

/// <summary>
/// Prevents editor shortcut letters from leaking into Player.InputPackage while the
/// vanilla O+H DevUI is open. Filtering happens after Player.checkInput so DevUI still
/// receives the physical Ctrl+Z/S/Y keystroke through UnityEngine.Input.
/// </summary>
internal static class DevUIShortcutInputGuard
{
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        On.Player.checkInput += Player_checkInput;
        _enabled = true;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.Player.checkInput -= Player_checkInput;
        _enabled = false;
    }

    private static void Player_checkInput(On.Player.orig_checkInput orig, Player self)
    {
        orig(self);

        if (!ShouldFilter(self) || self.input == null || self.input.Length == 0)
        {
            return;
        }

        bool reserveZ = !DryCycleOptions.CtrlZGameplayUnlocked && Input.GetKey(KeyCode.Z);
        bool reserveS = !DryCycleOptions.CtrlSGameplayUnlocked && Input.GetKey(KeyCode.S);
        bool reserveY = !DryCycleOptions.CtrlYGameplayUnlocked && Input.GetKey(KeyCode.Y);
        if (!reserveZ && !reserveS && !reserveY)
        {
            return;
        }

        int playerNumber = self.playerState?.playerNumber ?? 0;
        if (ModManager.ChallengeModule &&
            self.abstractCreature?.world?.game?.IsArenaSession == true &&
            self.abstractCreature.world.game.GetArenaGameSession.chMeta != null)
        {
            playerNumber = 0;
        }

        Options.ControlSetup[] allControls = self.room.game.rainWorld.options.controls;
        if (allControls == null || playerNumber < 0 || playerNumber >= allControls.Length)
        {
            return;
        }

        Options.ControlSetup controls = allControls[playerNumber];
        if (controls == null)
        {
            return;
        }

        Player.InputPackage input = self.input[0];

        if (IsReserved(controls.KeyCodeFromAction(0, 0), reserveZ, reserveS, reserveY))
        {
            input.jmp = false;
        }
        if (IsReserved(controls.KeyCodeFromAction(4, 0), reserveZ, reserveS, reserveY))
        {
            input.thrw = false;
        }
        if (IsReserved(controls.KeyCodeFromAction(3, 0), reserveZ, reserveS, reserveY))
        {
            input.pckp = false;
        }
        if (IsReserved(controls.KeyCodeFromAction(11, 0), reserveZ, reserveS, reserveY))
        {
            input.mp = false;
        }
        if (IsReserved(controls.KeyCodeFromAction(34, 0), reserveZ, reserveS, reserveY))
        {
            input.spec = false;
        }

        bool blockLeft = IsReserved(controls.KeyCodeFromAction(1, 0, axisPositive: false), reserveZ, reserveS, reserveY);
        bool blockRight = IsReserved(controls.KeyCodeFromAction(1, 0, axisPositive: true), reserveZ, reserveS, reserveY);
        bool blockDown = IsReserved(controls.KeyCodeFromAction(2, 0, axisPositive: false), reserveZ, reserveS, reserveY);
        bool blockUp = IsReserved(controls.KeyCodeFromAction(2, 0, axisPositive: true), reserveZ, reserveS, reserveY);

        if ((input.x < 0 && blockLeft) || (input.x > 0 && blockRight))
        {
            input.x = 0;
            input.analogueDir.x = 0f;
        }

        if ((input.y < 0 && blockDown) || (input.y > 0 && blockUp))
        {
            input.y = 0;
            input.analogueDir.y = 0f;
        }

        RecalculateDownDiagonal(ref input);
        self.input[0] = input;
        self.mapInput = input;
    }

    private static bool ShouldFilter(Player player)
    {
        RainWorldGame game = player?.room?.game;
        if (game == null || !game.devToolsActive || game.devUI == null)
        {
            return false;
        }

        return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
    }

    private static bool IsReserved(KeyCode mappedKey, bool reserveZ, bool reserveS, bool reserveY)
    {
        return (reserveZ && mappedKey == KeyCode.Z) ||
               (reserveS && mappedKey == KeyCode.S) ||
               (reserveY && mappedKey == KeyCode.Y);
    }

    private static void RecalculateDownDiagonal(ref Player.InputPackage input)
    {
        bool down = input.y < 0 || input.analogueDir.y < -0.05f;
        if (!down)
        {
            input.downDiagonal = 0;
            return;
        }

        if (input.x < 0 || input.analogueDir.x < -0.05f)
        {
            input.downDiagonal = -1;
        }
        else if (input.x > 0 || input.analogueDir.x > 0.05f)
        {
            input.downDiagonal = 1;
        }
        else
        {
            input.downDiagonal = 0;
        }
    }
}
