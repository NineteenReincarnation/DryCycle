using System;
using System.Collections.Generic;

namespace DryCycle.DevUI.DevTool.Compatibility;

public sealed class DevUiCompatibilityGateSnapshot
{
    public static readonly DevUiCompatibilityGateSnapshot Empty = new();

    public bool PageCoverageComplete { get; init; }
    public int PagesVisited { get; init; }
    public int PagesTotal { get; init; }
    public int RuntimeProtocolGaps { get; init; }
    public int LoadedTypeProtocolGaps { get; init; }
    public int SemanticFailures { get; init; }
    public int SemanticPagesAudited { get; init; }
    public int VanillaGaps { get; init; }
    public int RegionKitGaps { get; init; }
    public int DryCycleGaps { get; init; }
    public int OtherGaps { get; init; }

    public bool Passed =>
        PageCoverageComplete &&
        RuntimeProtocolGaps == 0 &&
        LoadedTypeProtocolGaps == 0 &&
        SemanticFailures == 0;
}

/// <summary>
/// Strict acceptance gate for the generic migration. A green result is intentionally harder than
/// "the current page looks fine": every canonical DevUI page must have been visited, the runtime
/// tree must have zero unmapped protocols, the loaded-type inventory must have zero dormant
/// protocol gaps, and every page type seen by the semantic mirror must retain zero conformance
/// failures. No per-mod/type allowlist exists in this gate.
/// </summary>
public static class DevUiCompatibilityGate
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, int> SemanticFailuresByPage = new(StringComparer.Ordinal);
    private static volatile DevUiCompatibilityGateSnapshot current = DevUiCompatibilityGateSnapshot.Empty;

    public static DevUiCompatibilityGateSnapshot Evaluate(
        UniversalDevUiPresentationSnapshot mirror,
        DevUiPageCoverageSnapshot pageCoverage,
        DevUiSemanticConformanceSnapshot semantic,
        DevUiProtocolInventorySnapshot inventory)
    {
        if (semantic != null && !string.IsNullOrWhiteSpace(semantic.PageType))
        {
            lock (Gate)
                SemanticFailuresByPage[semantic.PageType] = semantic.FailureCount;
        }

        int semanticFailures = 0;
        int semanticPages;
        lock (Gate)
        {
            semanticPages = SemanticFailuresByPage.Count;
            foreach (int failures in SemanticFailuresByPage.Values)
                semanticFailures += Math.Max(0, failures);
        }

        DevUiMigrationCoverageSnapshot observed = DevUiMigrationCoverage.Observed ?? DevUiMigrationCoverageSnapshot.Empty;
        DevUiMigrationSourceSummary vanilla = observed.Vanilla;
        DevUiMigrationSourceSummary regionKit = observed.RegionKit;
        DevUiMigrationSourceSummary dryCycle = observed.DryCycle;
        DevUiMigrationSourceSummary other = observed.Other;

        current = new DevUiCompatibilityGateSnapshot
        {
            PageCoverageComplete = pageCoverage?.Complete == true,
            PagesVisited = pageCoverage?.VisitedPageCount ?? 0,
            PagesTotal = pageCoverage?.TotalPageCount ?? 0,
            RuntimeProtocolGaps = observed.UnmappedTypeCount,
            LoadedTypeProtocolGaps = inventory?.PotentialGapCount ?? 0,
            SemanticFailures = semanticFailures,
            SemanticPagesAudited = semanticPages,
            VanillaGaps = vanilla?.UnmappedTypeCount ?? 0,
            RegionKitGaps = regionKit?.UnmappedTypeCount ?? 0,
            DryCycleGaps = dryCycle?.UnmappedTypeCount ?? 0,
            OtherGaps = other?.UnmappedTypeCount ?? 0
        };
        return current;
    }

    public static DevUiCompatibilityGateSnapshot Current => current;

    internal static void Reset()
    {
        lock (Gate) SemanticFailuresByPage.Clear();
        current = DevUiCompatibilityGateSnapshot.Empty;
    }
}
