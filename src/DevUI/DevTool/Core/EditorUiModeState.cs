namespace DryCycle.DevUI.DevTool.Core;

/// <summary>
/// Global DevTool presentation preference. This state is intentionally independent from
/// room/document history: changing UI presentation must never alter editor data, selection,
/// save state or Undo/Redo stacks.
/// </summary>
public static class EditorUiModeState
{
    private static volatile bool useVanilla;

    /// <summary>
    /// True when the original Rain World DevInterface should be shown as the primary UI.
    /// The RWImGui frontend keeps only the tiny mode switch visible in this state.
    /// </summary>
    public static bool UseVanilla => useVanilla;

    public static void SetVanilla(bool value)
    {
        useVanilla = value;
    }
}
