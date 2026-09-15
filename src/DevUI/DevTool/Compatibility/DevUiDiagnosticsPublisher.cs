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

        // Generic protocol registration is backend infrastructure, not presentation work. Keep the
        // one-time bootstrap here so an ImGui Draw call never mutates compatibility registries.
        DevUiGenericProtocolBootstrap.Ensure();

        // Migration coverage is the source for the universal mirror's unmapped count. Run the
        // throttled full audit first so the mirror and every downstream diagnostic snapshot describe
        // the same backend observation instead of mixing current-frame UI with prior-frame coverage.
        DevUiFullAudit.ObserveAll(owner);
        UniversalDevUiPresentationHub.Publish(owner);
        UniversalDevUiPresentationSnapshot mirror = UniversalDevUiPresentationHub.Current;

        DevUiPageCoverageTracker.Observe(owner, mirror);
        DevUiProtocolInventory.ObserveLoadedTypes();

        DevUiSemanticConformanceSnapshot semantic = DevUiSemanticConformanceAudit.Evaluate(mirror);
        DevUiCompatibilityGate.Evaluate(
            DevUiPageCoverageTracker.Current,
            semantic,
            DevUiProtocolInventory.Current);
    }
}
