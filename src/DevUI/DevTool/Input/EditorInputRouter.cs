using DryCycle.DevUI.Controls;
using DryCycle.DevUI.DevTool.Commands;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.Misc;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Input;

/// <summary>
/// Single input-arbitration point for the rebuilt DevTool. It coordinates optional ImGui
/// capture, DryCycle's legacy text fields, editor shortcuts, vanilla DevTool hotkeys,
/// world-space handles and gameplay input. ImGui itself is never referenced from DryCycle.dll.
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
        On.DevInterface.Handle.Update += Handle_Update;
        On.DevInterface.MapPage.Update += MapPage_Update;
        enabled = true;
    }

    internal static void Disable()
    {
        if (!enabled) return;
        On.Player.checkInput -= Player_checkInput;
        On.RainWorldGame.RawUpdate -= RainWorldGame_RawUpdate;
        On.RainWorldGame.Update -= RainWorldGame_Update;
        On.DevInterface.Handle.Update -= Handle_Update;
        On.DevInterface.MapPage.Update -= MapPage_Update;
        SetFrontendAttached(false);
        capturedGame = null;
        capturedUnityFrame = -1;
        enabled = false;
    }

    internal static void UpdateShortcuts(EditorSession session)
    {
        if (session == null) return;

        bool ctrl = global::UnityEngine.Input.GetKey(KeyCode.LeftControl) || global::UnityEngine.Input.GetKey(KeyCode.RightControl) ||
                    global::UnityEngine.Input.GetKey(KeyCode.LeftCommand) || global::UnityEngine.Input.GetKey(KeyCode.RightCommand);
        bool shift = global::UnityEngine.Input.GetKey(KeyCode.LeftShift) || global::UnityEngine.Input.GetKey(KeyCode.RightShift);

        // Vanilla presentation hides every rebuilt window, so a keyboard path is required to
        // return without leaving DevTools. This shortcut deliberately works in either mode.
        if (ctrl && shift && global::UnityEngine.Input.GetKeyDown(KeyCode.U))
        {
            EditorUiModeState.SetVanilla(!EditorUiModeState.UseVanilla);
            EditorUiModeState.SetOverlayHidden(false);
            SetFrontendCapture(false, false, false);
            return;
        }

        // Escape belongs to the game/Warp Menu. The editor only changes its own visibility and
        // immediately drops capture; it never consumes or synthesizes the Escape key itself.
        if (!EditorUiModeState.UseVanilla && global::UnityEngine.Input.GetKeyDown(KeyCode.Escape))
        {
            if (session.PlacementActive)
                session.CancelPlacement();
            else
                EditorUiModeState.ToggleOverlayHidden();

            SetFrontendCapture(false, false, false);
            capturedGame = null;
            capturedUnityFrame = -1;
            return;
        }

        if (HasLocalTextFocus(session.Owner?.game) || wantsTextInput ||
            session.LegacyTransactions.HasPendingTransaction)
            return;

        // Save/Undo/Redo belong to the rebuilt core, not to one visual frontend. They remain
        // available in Vanilla presentation mode because the old shortcut runtime was removed.
        if (ctrl && global::UnityEngine.Input.GetKeyDown(KeyCode.S))
        {
            EditorActions.Save(session);
            return;
        }
        if (ctrl && global::UnityEngine.Input.GetKeyDown(KeyCode.Z))
        {
            if (shift) EditorActions.Redo(session);
            else EditorActions.Undo(session);
            return;
        }
        if (ctrl && global::UnityEngine.Input.GetKeyDown(KeyCode.Y))
        {
            EditorActions.Redo(session);
            return;
        }

        // Vanilla mode restores the original editor interaction model. Do not let hidden
        // New-UI commands such as duplicate/delete/focus/browser shortcuts fire behind it.
        if (EditorUiModeState.UseVanilla || EditorUiModeState.OverlayHidden)
            return;

        if (ctrl && global::UnityEngine.Input.GetKeyDown(KeyCode.D) && session.ToolMode == EditorToolMode.Objects)
            EditorActions.DuplicateSelection(session);
        else if (global::UnityEngine.Input.GetKeyDown(KeyCode.Delete) && session.ToolMode == EditorToolMode.Objects)
            EditorActions.DeleteSelection(session);
        else if (ctrl && global::UnityEngine.Input.GetKeyDown(KeyCode.B))
            session.ToggleBrowser();
        else if (ctrl && global::UnityEngine.Input.GetKeyDown(KeyCode.I))
            session.ToggleInspector();
        else if (!ctrl && global::UnityEngine.Input.GetKeyDown(KeyCode.Tab))
            session.ToggleFocusMode();
    }

    private static void RainWorldGame_RawUpdate(
        On.RainWorldGame.orig_RawUpdate orig,
        RainWorldGame self,
        float dt)
    {
        bool textKeyboardOwner = HasTextKeyboardOwner(self);
        bool suppressVanillaFastForward = ShouldSuppressVanillaFastForward(self);

        // Vanilla RainWorldGame.RawUpdate interprets any held S as the DevTools 400 FPS shortcut.
        // Ctrl/Command+S belongs to the rebuilt UI's save command, so bypass only that vanilla
        // DevTools shortcut path while keeping plain S fast-forward unchanged.
        if (!textKeyboardOwner && !suppressVanillaFastForward)
        {
            orig(self, dt);
            return;
        }

        if (textKeyboardOwner)
            MarkKeyboardCaptured(self);

        bool devToolsWasActive = self.devToolsActive;
        DevInterface.DevUI focusedDevUi = self.devUI;

        self.mDown = global::UnityEngine.Input.GetKey(KeyCode.M);
        self.hDown = global::UnityEngine.Input.GetKey(KeyCode.H);
        self.pDown = global::UnityEngine.Input.GetKey(KeyCode.P);
        self.kDown = global::UnityEngine.Input.GetKey(KeyCode.K);
        self.oDown = global::UnityEngine.Input.GetKey(KeyCode.O);

        if (suppressVanillaFastForward)
            self.framesPerSecond = 40;

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
            self.lastRestartButton = global::UnityEngine.Input.GetKey(KeyCode.R);
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

    /// <summary>
    /// Prevents a click consumed by the overlay from also beginning an unrelated drag on a
    /// world-space DevInterface handle underneath it. Existing drags are always allowed to finish.
    /// Sound spatial handles are the deliberate exception: when the cursor is exactly over a
    /// Spot/Directional sound handle, that precise world control wins over the broad ImGui window
    /// capture so an overlapping editor window never requires a sacrificial first click.
    /// </summary>
    private static void Handle_Update(On.DevInterface.Handle.orig_Update orig, DevInterface.Handle self)
    {
        if (self?.owner == null || self.dragged)
        {
            orig(self);
            return;
        }

        EditorSession session = DevToolSessionHub.Current;
        bool ownsThisUi = session != null && ReferenceEquals(session.Owner, self.owner);
        bool newUiVisible = !EditorUiModeState.UseVanilla && !EditorUiModeState.OverlayHidden;
        bool soundHandleOwnsClick =
            ownsThisUi &&
            newUiVisible &&
            session.ToolMode == EditorToolMode.Sound &&
            self.owner.game?.devToolsActive == true &&
            self.owner.mouseClick &&
            self.MouseOver &&
            IsSoundWorldHandle(self);

        // Exact sound gizmo hit-testing is narrower than an ImGui window rectangle, so it gets
        // first refusal. Once the handle becomes dragged, the early branch above keeps ownership
        // even while the cursor subsequently crosses Browser/Inspector windows.
        if (soundHandleOwnsClick)
        {
            orig(self);
            return;
        }

        bool newUiPlacementOwnsMouse = newUiVisible && session?.PlacementActive == true;
        bool frontendOwnsMouse = newUiVisible && frontendAttached && wantsMouse;
        bool blockNewDrag = ownsThisUi && self.owner.game?.devToolsActive == true &&
                            (newUiPlacementOwnsMouse || frontendOwnsMouse);
        if (!blockNewDrag)
        {
            orig(self);
            return;
        }

        bool mouseClick = self.owner.mouseClick;
        self.owner.mouseClick = false;
        try
        {
            orig(self);
        }
        finally
        {
            self.owner.mouseClick = mouseClick;
        }
    }

    private static bool IsSoundWorldHandle(DevInterface.Handle handle)
    {
        if (handle is DevInterface.SpotSoundHandle || handle is DevInterface.DirectionalSoundHandle)
            return true;

        // SpotSoundHandle owns a plain Handle for radius editing. Walk upward so that nested
        // radius nub receives the same priority without granting click-through to every generic
        // DevInterface Handle used by unrelated editors.
        DevInterface.DevUINode current = handle?.parentNode;
        while (current != null)
        {
            if (current is DevInterface.SpotSoundHandle || current is DevInterface.DirectionalSoundHandle)
                return true;
            if (current is DevInterface.AmbientSoundPanel)
                return false;
            current = current.parentNode;
        }

        return false;
    }

    /// <summary>
    /// The rebuilt Map workspace must keep the vanilla MapPage alive because its world data,
    /// save path and mod hooks remain authoritative. However, its hidden controls must not
    /// react behind the ImGui canvas. Suppress only the DevUI mouse state for the duration of
    /// the vanilla MapPage update; no RoomPanel position or map data is modified.
    /// </summary>
    private static void MapPage_Update(On.DevInterface.MapPage.orig_Update orig, DevInterface.MapPage self)
    {
        if (!ShouldBlockLegacyMapMouse(self))
        {
            orig(self);
            return;
        }

        DevInterface.DevUI owner = self.owner;
        bool mouseClick = owner.mouseClick;
        bool mouseDown = owner.mouseDown;
        owner.mouseClick = false;
        owner.mouseDown = false;
        try
        {
            orig(self);
        }
        finally
        {
            owner.mouseClick = mouseClick;
            owner.mouseDown = mouseDown;
        }
    }

    private static bool ShouldBlockLegacyMapMouse(DevInterface.MapPage page)
    {
        if (!frontendAttached || EditorUiModeState.UseVanilla || page?.owner == null)
            return false;

        EditorSession session = DevToolSessionHub.Current;
        return session != null &&
               session.ToolMode == EditorToolMode.Map &&
               !session.LegacyUiVisible &&
               ReferenceEquals(session.Owner, page.owner) &&
               page.owner.game?.devToolsActive == true;
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

        bool reserveZ = !DryCycleOptions.CtrlZGameplayUnlocked && global::UnityEngine.Input.GetKey(KeyCode.Z);
        bool reserveS = !DryCycleOptions.CtrlSGameplayUnlocked && global::UnityEngine.Input.GetKey(KeyCode.S);
        bool reserveY = !DryCycleOptions.CtrlYGameplayUnlocked && global::UnityEngine.Input.GetKey(KeyCode.Y);
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

    private static bool HasTextKeyboardOwner(RainWorldGame game)
    {
        if (game == null || game.devUI == null || !game.devToolsActive) return false;
        bool frontendVisible = frontendAttached && !EditorUiModeState.UseVanilla && !EditorUiModeState.OverlayHidden;
        return HasLocalTextFocus(game) || (frontendVisible && wantsTextInput);
    }

    private static bool ShouldSuppressVanillaFastForward(RainWorldGame game)
    {
        if (game == null || game.devUI == null || !game.devToolsActive) return false;
        if (!frontendAttached || EditorUiModeState.UseVanilla || EditorUiModeState.OverlayHidden) return false;

        bool ctrlOrCommand =
            global::UnityEngine.Input.GetKey(KeyCode.LeftControl) ||
            global::UnityEngine.Input.GetKey(KeyCode.RightControl) ||
            global::UnityEngine.Input.GetKey(KeyCode.LeftCommand) ||
            global::UnityEngine.Input.GetKey(KeyCode.RightCommand);

        return ctrlOrCommand && global::UnityEngine.Input.GetKey(KeyCode.S);
    }

    private static bool HasKeyboardOwner(RainWorldGame game)
    {
        if (game == null || game.devUI == null || !game.devToolsActive) return false;
        bool frontendVisible = frontendAttached && !EditorUiModeState.UseVanilla && !EditorUiModeState.OverlayHidden;
        return HasLocalTextFocus(game) || (frontendVisible && (wantsKeyboard || wantsTextInput));
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
        return global::UnityEngine.Input.GetKey(KeyCode.LeftControl) || global::UnityEngine.Input.GetKey(KeyCode.RightControl) ||
               global::UnityEngine.Input.GetKey(KeyCode.LeftCommand) || global::UnityEngine.Input.GetKey(KeyCode.RightCommand);
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