using System;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Keeps legacy presentation coherence at the compatibility boundary without making native
/// authoring depend on a hidden Page.Refresh cycle.
///
/// Pure rebuilt pages are only marked stale and receive one full vanilla Refresh if/when the user
/// returns to Vanilla/Legacy presentation. If an opaque third-party DevUI subtree is present, defer
/// is intentionally refused and the current legacy page is refreshed immediately so the foreign
/// contract continues to observe model mutations.
/// </summary>
internal static class NativeLegacyPresentationInvalidation
{
    internal static void InvalidateCurrentSoundOrTriggerPage(EditorSession session)
    {
        Page page = session?.Owner?.activePage;
        if (page is not SoundPage && page is not TriggersPage)
            return;

        if (LegacyDevUiQuiescenceController.TryDeferRefresh(session))
            return;

        try
        {
            // This path is intentionally compatibility-only: vanilla presentation is already active,
            // or the page contains opaque third-party DevUI that cannot be represented by the native
            // editor contract. Refresh it immediately rather than pretending the foreign subtree can
            // tolerate deferred state.
            page.Refresh();
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning(
                "DevTool legacy Sound/Trigger compatibility refresh failed: " + error.Message);
        }
    }
}
