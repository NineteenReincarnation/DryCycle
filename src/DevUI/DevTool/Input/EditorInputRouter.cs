using DryCycle.DevUI.DevTool.Commands;
using DryCycle.DevUI.DevTool.Core;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Input;

/// <summary>
/// Shared ownership state between the RWImGui frontend and Rain World. Presentation sets
/// ImGui capture intent; the game-side runtime owns shortcut dispatch and gameplay input
/// suppression. This keeps ImGui calls out of DryCycle.dll.
/// </summary>
public static class EditorInputRouter
{
    private static volatile bool wantsMouse;
    private static volatile bool wantsKeyboard;
    private static volatile bool wantsTextInput;
    private static bool enabled;

    public static bool WantsMouse => wantsMouse;
    public static bool WantsKeyboard => wantsKeyboard;
    public static bool WantsTextInput => wantsTextInput;

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
        enabled = true;
    }

    internal static void Disable()
    {
        if (!enabled) return;
        On.Player.checkInput -= Player_checkInput;
        SetFrontendCapture(false, false, false);
        enabled = false;
    }

    internal static void UpdateShortcuts(EditorSession session)
    {
        if (session == null || wantsTextInput) return;

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

    private static void Player_checkInput(On.Player.orig_checkInput orig, Player self)
    {
        orig(self);
        if (!wantsKeyboard && !wantsTextInput) return;

        RainWorldGame game = self?.room?.game;
        if (game?.devToolsActive != true || DevToolSessionHub.Current == null || self.input == null || self.input.Length == 0)
            return;

        Player.InputPackage input = self.input[0];
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
        self.input[0] = input;
        self.mapInput = input;
    }
}
