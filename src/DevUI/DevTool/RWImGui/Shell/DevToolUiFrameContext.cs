using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Immutable per-render snapshot of frontend input and display state.
///
/// Dear ImGui is immediate-mode, but the frontend does not need to cross the managed/native
/// boundary repeatedly for the same IO, mouse-button and display values inside one render pass.
/// Build this once in BridgePlugin and pass it to hot-path presentation helpers.
/// </summary>
internal readonly struct DevToolUiFrameContext
{
    internal DevToolUiFrameContext(ImGuiIOPtr io)
    {
        Io = io;

        Num.Vector2 display = io.DisplaySize;
        if (display.X < 1f) display.X = 1366f;
        if (display.Y < 1f) display.Y = 768f;
        DisplaySize = display;

        MousePosition = io.MousePos;
        MouseDelta = io.MouseDelta;
        KeyCtrl = io.KeyCtrl;
        KeyShift = io.KeyShift;
        WantTextInput = io.WantTextInput;

        LeftMouseDown = ImGui.IsMouseDown(ImGuiMouseButton.Left);
        LeftMouseClicked = ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        LeftMouseReleased = ImGui.IsMouseReleased(ImGuiMouseButton.Left);
        AnyKeyDown = global::UnityEngine.Input.anyKeyDown;

        GroupShortcutPressed =
            AnyKeyDown &&
            !WantTextInput &&
            KeyCtrl &&
            !KeyShift &&
            ImGui.IsKeyPressed(ImGuiKey.G);
    }

    internal ImGuiIOPtr Io { get; }
    internal Num.Vector2 DisplaySize { get; }
    internal Num.Vector2 MousePosition { get; }
    internal Num.Vector2 MouseDelta { get; }
    internal bool KeyCtrl { get; }
    internal bool KeyShift { get; }
    internal bool WantTextInput { get; }
    internal bool LeftMouseDown { get; }
    internal bool LeftMouseClicked { get; }
    internal bool LeftMouseReleased { get; }
    internal bool AnyKeyDown { get; }
    internal bool GroupShortcutPressed { get; }
}
