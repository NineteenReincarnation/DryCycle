namespace DryCycle.DevUI.DevTool.Core;

/// <summary>
/// Global DevTool presentation preference. This state is intentionally independent from
/// room/document history: changing UI presentation must never alter editor data, selection,
/// save state or Undo/Redo stacks.
/// </summary>
public static class EditorUiModeState
{
    private static volatile bool useVanilla;
    private static volatile bool overlayHidden;

    /// <summary>
    /// True when the original Rain World DevInterface is the primary UI.
    /// The rebuilt RWImGui frontend draws no windows in this mode.
    /// </summary>
    public static bool UseVanilla => useVanilla;

    /// <summary>
    /// Temporary visibility gate used by Escape. This is presentation-only state so opening
    /// Warp Menu does not alter the active editor document, selection or history.
    /// </summary>
    public static bool OverlayHidden => overlayHidden;

    public static void SetVanilla(bool value)
    {
        useVanilla = value;
        if (!value)
            overlayHidden = false;
    }

    public static void SetOverlayHidden(bool value)
    {
        overlayHidden = value;
    }

    public static void ToggleOverlayHidden()
    {
        overlayHidden = !overlayHidden;
    }
}
