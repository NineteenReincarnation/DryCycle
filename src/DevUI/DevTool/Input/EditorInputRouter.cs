using System;
using DryCycle.DevUI.Controls;
using DryCycle.DevUI.DevTool.Commands;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.Misc;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Input;

/// <summary>
/// Single input-arbitration point for the rebuilt DevTool. It coordinates optional ImGui
/// capture, DryCycle's legacy text fields, editor shortcuts, vanilla DevTool hotkeys,
/// remaining legacy world-space handles and gameplay input. ImGui itself is never referenced from
/// DryCycle.dll.
/// </summary>
public static class EditorInputRouter
{
    private static volatile bool frontendAttached;
    private static volatile bool wantsMouse;
    private static volatile bool wantsKeyboard;
    private static volatile bool wantsTextInput;
    private static bool enabled;
    private static RainWorldGame vanillaHotkeysSuppressedGame;
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
        IL.RainWorldGame.RawUpdate += RainWorldGame_RawUpdateIL;
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
        IL.RainWorldGame.RawUpdate -= RainWorldGame_RawUpdateIL;
        On.Player.checkInput -= Player_checkInput;
        On.RainWorldGame.RawUpdate -= RainWorldGame_RawUpdate;
        On.RainWorldGame.Update -= RainWorldGame_Update;
        On.DevInterface.Handle.Update -= Handle_Update;
        On.DevInterface.MapPage.Update -= MapPage_Update;
        SetFrontendAttached(false);
        vanillaHotkeysSuppressedGame = null;
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

        bool textEditing = HasLocalTextFocus(session.Owner?.game) ||
                           wantsTextInput ||
                           session.LegacyTransactions.HasPendingTransaction;

        // World Map contains persistent search/inspector InputText controls. Dear ImGui can keep
        // those controls focused after the developer stops typing, so gating Ctrl+S on text focus
        // makes Map saving appear randomly broken. Save is an application-level Map command and
        // Ctrl/Command+S is not meaningful text input, therefore Map may save while a text field is
        // focused. Other tools retain the old transaction guard to avoid changing their semantics.
        if (ctrl && global::UnityEngine.Input.GetKeyDown(KeyCode.S) &&
            (session.ToolMode == EditorToolMode.Map || !textEditing))
        {
            EditorActions.Save(session);
            return;
        }

        if (textEditing)
            return;

        // Undo/Redo stay below the text-focus guard because Ctrl+Z/Ctrl+Y should continue to edit
        // the focused text field instead of unexpectedly rewinding the whole editor document.
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

    private static void RainWorldGame_RawUpdateIL(ILContext il)
    {
        // Gate only vanilla's DevTools hotkey block. Changing game.devToolsActive while RawUpdate
        // calls the simulation Update makes the editor lifetime monitor retire the live session.
        // The branch exits at the O-key toggle, whose edge latch is handled by the wrapper below.
        ILCursor cursor = new(il);
        if (!cursor.TryGotoNext(MoveType.After,
                instruction => instruction.MatchLdfld<RainWorldGame>(nameof(RainWorldGame.devToolsActive)),
                instruction => instruction.MatchBrfalse(out ILLabel target) && target.Target.MatchLdstr("o")))
            throw new InvalidOperationException("DevTool input routing could not locate RainWorldGame.RawUpdate's DevTools hotkey block.");

        cursor.Index--;
        cursor.Emit(OpCodes.Ldarg_0);
        cursor.EmitDelegate<Func<bool, RainWorldGame, bool>>(AllowVanillaDevToolsHotkeys);
    }

    private static bool AllowVanillaDevToolsHotkeys(bool active, RainWorldGame game) =>
        active && !ReferenceEquals(vanillaHotkeysSuppressedGame, game);

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

        DevInterface.DevUI focusedDevUi = self.devUI;

        self.mDown = global::UnityEngine.Input.GetKey(KeyCode.M);
        self.hDown = global::UnityEngine.Input.GetKey(KeyCode.H);
        self.pDown = global::UnityEngine.Input.GetKey(KeyCode.P);
        self.kDown = global::UnityEngine.Input.GetKey(KeyCode.K);
        self.oDown = global::UnityEngine.Input.GetKey(KeyCode.O);

        if (suppressVanillaFastForward)
            self.framesPerSecond = 40;

        // Keep the wrapper and IL gate on the same capture decision even if the render thread
        // publishes new focus flags during the simulation update. This scope owns input only.
        RainWorldGame previousSuppressedGame = vanillaHotkeysSuppressedGame;
        vanillaHotkeysSuppressedGame = self;
        try
        {
            orig(self, dt);
        }
        finally
        {
            vanillaHotkeysSuppressedGame = previousSuppressedGame;
        }

        if (self.devToolsActive && focusedDevUi != null && ReferenceEquals(self.devUI, focusedDevUi))
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
    /// Prevents a click consumed by the overlay from also beginning a drag on a remaining legacy
    /// DevInterface handle underneath it. Existing drags are always allowed to finish. Built-in
    /// Sound/Trigger handles no longer receive special treatment because native gizmos own those
    /// workspaces and their vanilla Panel/Handle trees are retired.
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
