namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Owns only the visibility handoff for Rain World's built-in yellow developer-tools label.
/// New UI replaces that label with DevToolTopStatusWindow; Vanilla restores the original behavior.
/// </summary>
internal static class VanillaDevToolsLabelVisibility
{
    private static RainWorldGame observedGame;

    internal static void Apply(
        RainWorldGame game,
        bool newUiOwnsStatus)
    {
        if (!ReferenceEquals(
                observedGame,
                game))
        {
            Restore();
            observedGame =
                game;
        }

        if (game?.devToolsLabel == null)
            return;

        game.devToolsLabel.isVisible =
            newUiOwnsStatus
                ? false
                : game.devToolsActive;
    }

    internal static void Restore()
    {
        RainWorldGame game =
            observedGame;
        observedGame =
            null;

        if (game?.devToolsLabel != null)
            game.devToolsLabel.isVisible =
                game.devToolsActive;
    }
}
