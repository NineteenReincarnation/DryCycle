using DevInterface;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Lifetime cleanup for page-keyed quiescence state.
///
/// The main quiescence controller intentionally keeps several HashSet&lt;Page&gt; caches because their
/// hot-path membership checks are simple and allocation-free. Those sets must not outlive the Page
/// instances they describe, however: DevUI replaces Page objects when tools/rooms change. Explicitly
/// releasing the retired page preserves the cheap hot path without turning those caches into roots
/// for every editor page visited during a long mapping session.
/// </summary>
internal static partial class LegacyDevUiQuiescenceController
{
    internal static void ReleasePage(Page page)
    {
        if (page == null) return;

        SuppressedInitialRefreshPages.Remove(page);
        DeferredRefreshPages.Remove(page);
        ExternalCompatibilityPages.Remove(page);
        InvalidateBackendPlan(page);
    }
}
