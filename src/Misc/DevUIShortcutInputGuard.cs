using DryCycle.DevUI.Controls;
using UnityEngine;

namespace DryCycle.Misc;

/// <summary>
/// Keeps keyboard input owned by DryCycle DevUI text fields from leaking into Rain
/// World's gameplay/dev-tool shortcuts. Text entry is processed exactly once per raw
/// frame while the vanilla single-letter DevTools hotkeys are temporarily bypassed.
/// </summary>
internal static class DevUIShortcutInputGuard
{
    private static bool _enabled;
    private static RainWorldGame _capturedGame;
    private static int _capturedUnityFrame = -1;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        On.Player.checkInput += Player_checkInput;
        On.RainWorldGame.RawUpdate += RainWorldGame_RawUpdate;
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
        On.RainWorldGame.RawUpdate -= RainWorldGame_RawUpdate;
        On.RainWorldGame.Update -= RainWorldGame_Update;
        _capturedGame = null;
        _capturedUnityFrame = -1;
        _enabled = false;
    }

    private static void RainWorldGame_RawUpdate(
        On.RainWorldGame.orig_RawUpdate orig,
        RainWorldGame self,
        float dt)
    {
        if (!HasFocusedTextField(self))
        {
            orig(self, dt);
            return;
        }

        MarkKeyboardCaptured(self);

        bool devToolsWasActive = self.devToolsActive;
        DevInterface.DevUI focusedDevUi = self.devUI;

        // RainWorldGame.RawUpdate owns the raw A/S/Q/E/M/H/P/K/L/O DevTools keys.
        // Hide DevTools only for the vanilla RawUpdate call. This prevents those keys
        // from firing while a text field is focused. We then run the already-open DevUI
        // once ourselves, because vanilla skipped its normal update while hidden.
        //
        // Keep edge latches synchronized to the physical key state so releasing focus
        // cannot immediately replay a held editor letter as a DevTools command.
        self.mDown = Input.GetKey(KeyCode.M);
        self.hDown = Input.GetKey(KeyCode.H);
        self.pDown = Input.GetKey(KeyCode.P);
        self.kDown = Input.GetKey(KeyCode.K);
        self.oDown = Input.GetKey(KeyCode.O);

        self.devToolsActive = false;
        try
        {
            orig(self, dt);
        }
        finally
        {
            self.devToolsActive = devToolsWasActive;
        }

        // Exactly one text-input update for this raw frame. The previous implementation
        // did this from RainWorldGame.Update while vanilla had already updated DevUI in
        // RawUpdate, causing Input.inputString to be consumed two or more times (for
        // example one O keypress becoming several 'o' characters).
        if (devToolsWasActive && focusedDevUi != null && ReferenceEquals(self.devUI, focusedDevUi))
        {
            focusedDevUi.Update();
        }
    }

    private static void RainWorldGame_Update(On.RainWorldGame.orig_Update orig, RainWorldGame self)
    {
        bool captured = IsKeyboardCapturedThisFrame(self) || HasFocusedTextField(self);
        if (captured)
        {
            // RainWorldGame.Update owns the R restart shortcut and the pause edge. A text
            // field may release focus during RawUpdate (Enter/Escape), so the raw-frame
            // capture marker intentionally survives until this Update has completed.
            self.lastRestartButton = Input.GetKey(KeyCode.R);
            self.lastPauseButton = true;
        }

        try
        {
            orig(self);
        }
        finally
        {
            if (ReferenceEquals(_capturedGame, self) && _capturedUnityFrame == Time.frameCount)
            {
                _capturedGame = null;
                _capturedUnityFrame = -1;
            }
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
        if (HasFocusedTextField(game) || IsKeyboardCapturedThisFrame(game))
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

    private static void MarkKeyboardCaptured(RainWorldGame game)
    {
        _capturedGame = game;
        _capturedUnityFrame = Time.frameCount;
    }

    private static bool IsKeyboardCapturedThisFrame(RainWorldGame game)
    {
        return game != null && ReferenceEquals(_capturedGame, game) && _capturedUnityFrame == Time.frameCount;
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
