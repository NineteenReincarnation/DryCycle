using DryCycle.DevUI.Controls;
using UnityEngine;

namespace DryCycle.Misc;

/// <summary>
/// Keeps keyboard input owned by DryCycle DevUI text fields from leaking into Rain
/// World's gameplay/dev-tool shortcuts. A focused text field is treated as a modal
/// keyboard target: gameplay input is neutralized, vanilla DevTools hotkeys are skipped,
/// and the DevUI itself is updated once manually so text entry still receives the keys.
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
        On.RainWorldGame.Update += RainWorldGame_Update;
        _enabled = true;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.Player.checkInput -= Player_checkInput;
        On.RainWorldGame.Update -= RainWorldGame_Update;
        _enabled = false;
    }

    private static void RainWorldGame_Update(On.RainWorldGame.orig_Update orig, RainWorldGame self)
    {
        if (!HasFocusedTextField(self))
        {
            orig(self);
            return;
        }

        // Vanilla RainWorldGame.Update owns a number of raw single-letter DevTools
        // shortcuts (A/S/Q/E/M/H/P/K/L/O/R). They use UnityEngine.Input directly, so
        // consuming Input.inputString in the text field is not enough to stop them.
        // Temporarily hide DevTools from vanilla's update, then update the already-open
        // DevUI exactly once ourselves after the game update has finished.
        bool devToolsWasActive = self.devToolsActive;
        DevInterface.DevUI focusedDevUi = self.devUI;

        // Preserve edge-trigger latches while typing. This also prevents a held key from
        // firing on the first frame after Enter/Escape/mouse focus release.
        self.mDown = Input.GetKey(KeyCode.M);
        self.hDown = Input.GetKey(KeyCode.H);
        self.pDown = Input.GetKey(KeyCode.P);
        self.kDown = Input.GetKey(KeyCode.K);
        self.oDown = true; // O is checked outside the devToolsActive block.
        self.lastRestartButton = Input.GetKey(KeyCode.R);

        // Escape belongs to the text field while editing. RainWorldGame checks the pause
        // action later in Update, before our manual DevUI update gets a chance to cancel
        // the field, so latch it as already handled for this frame.
        self.lastPauseButton = true;

        self.devToolsActive = false;
        try
        {
            orig(self);
        }
        finally
        {
            self.devToolsActive = devToolsWasActive;
        }

        // Vanilla skipped this because devToolsActive was temporarily false. Keep the
        // same DevUI instance alive and give it one update so the focused field receives
        // Input.inputString, cursor keys, clipboard shortcuts, Enter, Escape, etc.
        if (devToolsWasActive && focusedDevUi != null && ReferenceEquals(self.devUI, focusedDevUi))
        {
            focusedDevUi.Update();
        }
    }

    private static void Player_checkInput(On.Player.orig_checkInput orig, Player self)
    {
        orig(self);

        if (self.input == null || self.input.Length == 0)
        {
            return;
        }

        RainWorldGame game = self?.room?.game;
        if (HasFocusedTextField(game))
        {
            Player.InputPackage focusedInput = self.input[0];
            NeutralizeGameplayInput(ref focusedInput);
            self.input[0] = focusedInput;
            self.mapInput = focusedInput;
            return;
        }

        if (!ShouldFilterEditorShortcuts(self))
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

    private static bool HasFocusedTextField(RainWorldGame game)
    {
        return game != null && game.devUI != null && DryCycleInputFocus.Focused != null;
    }

    private static bool ShouldFilterEditorShortcuts(Player player)
    {
        RainWorldGame game = player?.room?.game;
        if (game == null || !game.devToolsActive || game.devUI == null)
        {
            return false;
        }

        return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
    }

    private static void NeutralizeGameplayInput(ref Player.InputPackage input)
    {
        input.x = 0;
        input.y = 0;
        input.jmp = false;
        input.thrw = false;
        input.pckp = false;
        input.mp = false;
        input.spec = false;
        input.crouchToggle = false;
        input.analogueDir = Vector2.zero;
        input.downDiagonal = 0;
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
