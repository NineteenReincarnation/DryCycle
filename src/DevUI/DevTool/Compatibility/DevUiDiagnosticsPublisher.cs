namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Backend-owned publication point for expensive compatibility diagnostics.
///
/// RWImGui is a pure consumer of detached snapshots. Reflection-backed tree/type scans and semantic
/// evaluation run here only when diagnostics are explicitly enabled; normal production editor frames
/// do not pay this cost.
/// </summary>
internal static class DevUiDiagnosticsPublisher
{
    internal static void Publish(global::DevInterface.DevUI owner)
    {
        if (!DevUiDiagnosticsPolicy.Enabled || owner == null)
            return;

        UniversalDevUiPresentationSnapshot mirror = UniversalDevUiPresentationHub.Current;

        // Universal mirror publication has already happened in the backend command phase. Build the
        // remaining diagnostic snapshots from that detached mirror so the frontend never pumps live
        // DevInterface state from a Draw call.
        DevUiPageCoverageTracker.Observe(owner, mirror);
        DevUiProtocolInventory.ObserveLoadedTypes();

        DevUiSemanticConformanceSnapshot semantic = DevUiSemanticConformanceAudit.Evaluate(mirror);
        DevUiCompatibilityGate.Evaluate(
            mirror,
            DevUiPageCoverageTracker.Current,
            semantic,
            DevUiProtocolInventory.Current);
    }
}
