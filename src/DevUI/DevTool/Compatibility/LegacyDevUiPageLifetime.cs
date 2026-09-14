using DevInterface;
using DryCycle.DevUI.DevTool.Map;

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

        // The unified Map geometry cache stores live AbstractRoom and RoomRepresentation references.
        // A tool switch constructs a fresh MapPage when the developer returns, even if region and
        // room counts are unchanged. Keeping the old cache would therefore both root the retired
        // page/world graph and allow Prime() to reuse stale RoomRepresentation instances until the
        // periodic structure audit runs. Retiring a MapPage is an exact lifetime boundary: flush the
        // persistent snapshot and rebuild cheaply from the new page on next entry.
        if (page is MapPage)
            MapRoomGeometryPresentationHub.Clear();
    }
}
