using System;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Input;
using DryCycle.DevUI.DevTool.Sound;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Compatibility-only projection from the headless native sound catalogue back into vanilla
/// SoundPage state. The rebuilt editor never reads this projection. It exists solely so explicitly
/// entering Vanilla/Legacy mode remains lossless after native resource discovery stopped publishing
/// through SoundPage.fileNames.
/// </summary>
internal static class LegacySoundPageHydrator
{
    private static SoundPage observedPage;
    private static string[] observedNames;

    internal static void Step(EditorSession session)
    {
        bool legacyPresentation =
            session != null &&
            (!EditorInputRouter.FrontendAttached || EditorUiModeState.UseVanilla || session.LegacyUiVisible);

        if (!legacyPresentation ||
            session.ToolMode != EditorToolMode.Sound ||
            session.Owner?.activePage is not SoundPage page ||
            page.GetType() != typeof(SoundPage))
        {
            ResetObservation();
            return;
        }

        // If native discovery has not completed, preserve whatever state the vanilla constructor
        // already owns. Once a native generation exists, project it exactly once per generation.
        if (!SoundFileNameCatalog.IsReady)
            return;

        string[] names = SoundFileNameCatalog.CurrentNames ?? Array.Empty<string>();
        if (ReferenceEquals(observedPage, page) && ReferenceEquals(observedNames, names) &&
            ReferenceEquals(page.fileNames, names))
            return;

        page.fileNames = names;
        int maxPerPage = Math.Max(1, page.maxFilesPerPage);
        page.totalFilePages = Math.Max(1, 1 + (int)(names.Length / (float)maxPerPage + 0.5f));
        if (page.currFilesPage < 0 || page.currFilesPage >= page.totalFilePages)
            page.currFilesPage = 0;

        try
        {
            page.RefreshFilesPage();
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool legacy Sound file-list hydration failed: " + error.Message);
        }

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
