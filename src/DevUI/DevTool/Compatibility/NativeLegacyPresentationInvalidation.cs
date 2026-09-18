using System;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Input;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Executes the compatibility-only half of native model invalidation.
///
/// The authoritative history mutation boundary first asks LegacyDevUiQuiescenceController to defer
/// a stale rebuilt page. Only when defer is refused (explicit Vanilla/Legacy presentation or opaque
/// third-party DevUI) does this bridge perform an immediate original page Refresh. Native actions do
/// not call Page.Refresh themselves.
/// </summary>
internal static class NativeLegacyPresentationInvalidation
{
    internal static void RefreshCurrentObjectFallback(EditorSession session)
    {
        if (session?.Owner?.activePage is not ObjectsPage page)
            return;

        // A native Objects workspace owns NativeToolAnchorPage, so reaching an ObjectsPage here means
        // compatibility presentation is materialized (or the optional frontend is unavailable).
        // Never make normal rebuilt object actions depend on this path.
        if (EditorInputRouter.FrontendAttached &&
            !EditorUiModeState.UseVanilla &&
            !session.LegacyUiVisible &&
            !DevUiDiagnosticsPolicy.Enabled)
            return;

        try
        {
            page.Refresh();
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning(
                "DevTool legacy Objects compatibility refresh failed: " + error.Message);
        }
    }

    internal static void RefreshCurrentSoundOrTriggerFallback(EditorSession session)
    {
        Page page = session?.Owner?.activePage;
        if (page is not SoundPage && page is not TriggersPage)
            return;

        try
        {
            page.Refresh();
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning(
                "DevTool legacy Sound/Trigger compatibility refresh failed: " + error.Message);
        }
    }
}
