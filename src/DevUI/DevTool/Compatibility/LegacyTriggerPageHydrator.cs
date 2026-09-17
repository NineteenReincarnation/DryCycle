using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Input;
using DryCycle.DevUI.DevTool.Triggers;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Projects the native Trigger song catalogue back into an exact vanilla TriggersPage only while
/// explicit Vanilla/Legacy presentation is active. Native Trigger presentation never reads this
/// field; the projection only preserves compatibility with the original MusicEvent UI.
/// </summary>
internal static class LegacyTriggerPageHydrator
{
    private static TriggersPage observedPage;
    private static string[] observedNames;

    internal static void Step(EditorSession session)
    {
        bool legacyPresentation =
            session != null &&
            (!EditorInputRouter.FrontendAttached || EditorUiModeState.UseVanilla || session.LegacyUiVisible);

        if (!legacyPresentation ||
            session.ToolMode != EditorToolMode.Triggers ||
            session.Owner?.activePage is not TriggersPage page ||
            page.GetType() != typeof(TriggersPage))
        {
            ResetObservation();
            return;
        }

        if (!TriggerSongCatalog.EnsureLoaded())
            return;

        string[] names = TriggerSongCatalog.CurrentNames;
        if (ReferenceEquals(observedPage, page) && ReferenceEquals(observedNames, names) &&
            ReferenceEquals(page.songNames, names))
            return;

        page.songNames = names;
        observedPage = page;
        observedNames = names;
    }

    internal static void Reset() => ResetObservation();

    private static void ResetObservation()
    {
        observedPage = null;
        observedNames = null;
    }
}
