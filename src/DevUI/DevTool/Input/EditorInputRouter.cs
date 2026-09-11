using DryCycle.DevUI.Controls;
using DryCycle.DevUI.DevTool.Commands;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.Misc;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Input;

/// <summary>
/// Single input-arbitration point for the rebuilt DevTool. It coordinates optional ImGui
/// capture, DryCycle's legacy text fields, editor shortcuts, vanilla DevTool hotkeys and
/// gameplay input. ImGui itself is never referenced from DryCycle.dll.
/// </summary>
public static class EditorInputRouter
{
    private static volatile bool frontendAttached;
    private static volatile bool wantsMouse;
    private static volatile bool wantsKeyboard;
    private static volatile bool wantsTextInput;
    private static bool enabled;
    private static RainWorldGame capturedGame;
    private static int capturedUnityFrame = -1;

    public static bool FrontendAttached => frontendAttached;
    public static bool WantsMouse => wantsMouse;
    public static bool WantsKeyboard => wantsKeyboard;
    public static bool WantsTextInput => wantsTextInput;

    public static void SetFrontendAttached(bool attached)
    {
        frontendAttached = attached;
        if (!attached)
            SetFrontendCapture(false, false, false);
    }

    public static void SetFrontendCapture(bool mouse, bool keyboard, bool textInput)
    {
        wantsMouse = mouse;
        wantsKeyboard = keyboard;
        wantsTextInput = textInput;
    }

    internal static void Enable()
    {
        if (enabled) return;
        On.Player.checkInput += Player_checkInput;
        On.RainWorldGame.RawUpdate += RainWorldGame_RawUpdate;
        On.RainWorldGame.Update += RainWorldGame_Update;
        enabled = true;
    }

    internal static void Disable()
    {
        if (!enabled) return;
        On.Player.checkInput -= Player_checkInput;
        On.RainWorldGame.RawUpdate -= RainWorldGame_RawUpdate;
        On.RainWorldGame.Update -= RainWorldGame_Update;
        SetFrontendAttached(false);
        capturedGame = null;
        capturedUnityFrame = -1;
        enabled = false;
    }

    internal static void UpdateShortcuts(EditorSession session)
    {
        if (session == null || HasLocalTextFocus(session.Owner?.game) || wantsTextInput ||
            session.LegacyTransactions.HasPendingTransaction)
            return;

        bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) ||
                    Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand);
        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

        if (ctrl && Input.GetKeyDown(KeyCode.S))
            EditorActions.Save(session);
        else if (ctrl && Input.GetKeyDown(KeyCode.Z))
        {
            if (shift) EditorActions.Redo(session);
            else EditorActions.Undo(session);
        }
        else if (ctrl && Input.GetKeyDown(KeyCode.Y))
            EditorActions.Redo(session);
        else if (ctrl && Input.GetKeyDown(KeyCode.B))
            session.ToggleBrowser();
        else if (ctrl && Input.GetKeyDown(KeyCode.I))
            session.ToggleInspector();
        else if (!ctrl && Input.GetKeyDown(KeyCode.Tab))
            session.ToggleFocusMode();
    }

    private static void RainWorldGame_RawUpdate(
        On.RainWorldGame.orig_RawUpdate orig,
        RainWorldGame self,
        float dt)
    {
        if (!HasKeyboardOwner(self))
        {
            orig(self, dt);
            return;
        }

        MarkKeyboardCaptured(self);

        bool devToolsWasActive = self.devToolsActive;
        DevInterface.DevUI focusedDevUi = self.devUI;

        // RainWorldGame.RawUpdate owns vanilla single-letter DevTools shortcuts. Hide the
        // DevTools flag only for that raw update while an editor text/control owns keyboard
        // input, then manually update the already-open DevUI once so its backend stays live.
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

        if (devToolsWasActive && focusedDevUi != null && ReferenceEquals(self.devUI, focusedDevUi))
            focusedDevUi.Update();
    }

    private static void RainWorldGame_Update(On.RainWorldGame.orig_Update orig, RainWorldGame self)
    {
        bool captured = IsKeyboardCapturedThisFrame(self) || HasKeyboardOwner(self);
        if (captured)
        {
            // RainWorldGame.Update owns restart and pause edges. A text field may release
            // focus during RawUpdate, so the raw-frame marker survives through this call.
            self.lastRestartButton = Input.GetKey(KeyCode.R);
            self.lastPauseButton = true;
        }

        try
        {
            orig(self);
        }
        finally
        {
            if (ReferenceEquals(capturedGame, self) && capturedUnityFrame == Time.frameCount)
            {
                capturedGame = null;
                capturedUnityFrame = -1;
            }
        }
    }

    private static void Player_checkInput(On.Player.orig_checkInput orig, Player self)
    {
        orig(self);
        if (self.input == null || self.input.Length == 0) return;

        RainWorldGame game = self?.room?.game;
        if (HasKeyboardOwner(game) || IsKeyboardCapturedThisFrame(game))
        {
            Player.InputPackage focusedInput = self.input[0];
            NeutralizeGameplayInput(ref focusedInput);
            self.input[0] = focusedInput;
            self.mapInput = focusedInput;
            return;
        }

        if (!ShouldFilterEditorShortcuts(self)) return;

        bool reserveZ = !DryCycleOptions.CtrlZGameplayUnlocked && Input.GetKey(KeyCode.Z);
        bool reserveS = !DryCycleOptions.CtrlSGameplayUnlocked && Input.GetKey(KeyCode.S);
        bool reserveY = !DryCycleOptions.CtrlYGameplayUnlocked && Input.GetKey(KeyCode.Y);
        if (!reserveZ && !reserveS && !reserveY) return;

        int playerNumber = self.playerState?.playerNumber ?? 0;
        if (ModManager.ChallengeModule &&
            self.abstractCreature?.world?.game?.IsArenaSession == true &&
            self.abstractCreature.world.game.GetArenaGameSession.chMeta != null)
        {
            playerNumber = 0;
        }

        Options.ControlSetup[] allControls = self.room.game.rainWorld.options.controls;
        if (allControls == null || playerNumber < 0 || playerNumber >= allControls.Length) return;

        Options.ControlSetup controls = allControls[playerNumber];
        if (controls == null) return;

        Player.InputPackage input = self.input[0];

        if (IsReserved(controls.KeyCodeFromAction(0, 0), reserveZ, reserveS, reserveY)) input.jmp = false;
        if (IsReserved(controls.KeyCodeFromAction(4, 0), reserveZ, reserveS, reserveY)) input.thrw = false;
        if (IsReserved(controls.KeyCodeFromAction(3, 0), reserveZ, reserveS, reserveY)) input.pckp = false;
        if (IsReserved(controls.KeyCodeFromAction(11, 0), reserveZ, reserveS, reserveY)) input.mp = false;
        if (IsReserved(controls.KeyCodeFromAction(34, 0), reserveZ, reserveS, reserveY)) input.spec = false;

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

    private static bool HasKeyboardOwner(RainWorldGame game)
    {
        if (game == null || game.devUI == null || !game.devToolsActive) return false;
        return HasLocalTextFocus(game) || (frontendAttached && (wantsKeyboard || wantsTextInput));
    }

    private static bool HasLocalTextFocus(RainWorldGame game)
    {
        return game != null && game.devUI != null &&
               (DryCycleInputFocus.Focused != null || PaletteDirectInputRuntime.HasActiveInput);
    }

    private static void MarkKeyboardCaptured(RainWorldGame game)
    {
        capturedGame = game;
        capturedUnityFrame = Time.frameCount;
    }

    private static bool IsKeyboardCapturedThisFrame(RainWorldGame game)
    {
        return game != null && ReferenceEquals(capturedGame, game) && capturedUnityFrame == Time.frameCount;
    }

    private static bool ShouldFilterEditorShortcuts(Player player)
    {
        RainWorldGame game = player?.room?.game;
        if (game == null || !game.devToolsActive || game.devUI == null) return false;
        return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) ||
               Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand);
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
            input.downDiagonal = -1;
        else if (input.x > 0 || input.analogueDir.x > 0.05f)
            input.downDiagonal = 1;
        else
            input.downDiagonal = 0;
    }
}
