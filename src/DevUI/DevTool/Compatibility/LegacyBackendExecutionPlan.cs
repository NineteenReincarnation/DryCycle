using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DevInterface;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Compiled execution plan for the quiescent legacy backend.
///
/// The legacy DevUI tree can contain hundreds of hidden screen-space nodes while only a small
/// subset must stay alive: world-space handles and opaque third-party compatibility roots. Walking
/// the complete hidden tree every frame defeats part of the quiescence win, so one structural pass
/// compiles those roots into a retained plan. Stable frames execute only that plan.
///
/// Correctness is protected by four invalidation layers:
/// 1. top-level child count is checked every frame; pages that actually retain backend roots also
///    validate top-level identity/order without allocation;
/// 2. every compiled relevant entry verifies that it is still attached to the same parent slot;
/// 3. known page Refresh operations explicitly invalidate the plan;
/// 4. a low-frequency full semantic audit detects nested relevant nodes added by code that bypasses
///    our known mutation paths without disturbing an already-compiled entry.
///
/// The audit only compares relevant execution roots. Changes confined to dormant vanilla/DryCycle
/// screen controls intentionally do not invalidate the plan because they cannot affect the backend.
/// </summary>
internal static partial class LegacyDevUiQuiescenceController
{
    private const int BackendPlanAuditInterval = 30;

    private enum BackendPlanEntryKind
    {
        WorldBackend,
        ExternalCompatibility
    }

    private readonly struct BackendPlanEntry
    {
        internal BackendPlanEntry(
            DevUINode node,
            BackendPlanEntryKind kind,
            DevUINode parent,
            int childIndex)
        {
            Node = node;
            Kind = kind;
            Parent = parent;
            ChildIndex = childIndex;
        }

        internal DevUINode Node { get; }
        internal BackendPlanEntryKind Kind { get; }
        internal DevUINode Parent { get; }
        internal int ChildIndex { get; }
    }

    private sealed class BackendPlan
    {
        internal BackendPlan(
            PageProfile profile,
            DevUINode[] pageRoots,
            BackendPlanEntry[] entries)
        {
            Profile = profile;
            PageRoots = pageRoots ?? Array.Empty<DevUINode>();
            Entries = entries ?? Array.Empty<BackendPlanEntry>();
            FramesUntilAudit = BackendPlanAuditInterval;
        }

        internal PageProfile Profile { get; }
        internal DevUINode[] PageRoots { get; }
        internal BackendPlanEntry[] Entries { get; }
        internal int FramesUntilAudit { get; set; }
    }

    private static ConditionalWeakTable<Page, BackendPlan> backendPlans = new();

    private static void PumpCompiledBackendPlan(Page page, PageProfile profile)
    {
        if (page == null || profile == null)
            return;

        BackendPlan plan = GetBackendPlan(page, profile);
        BackendPlanEntry[] entries = plan.Entries;
        for (int i = 0; i < entries.Length; i++)
        {
            DevUINode node = entries[i].Node;
            if (node == null) continue;

            // PumpBranch preserves the exact compatibility semantics already used by the old full
            // traversal: external roots receive their complete recursive Update, while world roots
            // run their derived Update with selective base-node recursion.
            PumpBranch(node, profile);
        }
    }

    private static BackendPlan GetBackendPlan(Page page, PageProfile profile)
    {
        if (!backendPlans.TryGetValue(page, out BackendPlan plan) ||
            !ReferenceEquals(plan.Profile, profile) ||
            !TopLevelRootsMatch(page, profile, plan.PageRoots, plan.Entries) ||
            !CompiledEntriesRemainAttached(plan.Entries))
        {
            return RebuildBackendPlan(page, profile);
        }

        plan.FramesUntilAudit--;
        if (plan.FramesUntilAudit <= 0)
        {
            plan.FramesUntilAudit = BackendPlanAuditInterval;
            if (!RelevantStructureMatches(page, profile, plan.Entries))
                return RebuildBackendPlan(page, profile);
        }

        return plan;
    }

    private static BackendPlan RebuildBackendPlan(Page page, PageProfile profile)
    {
        List<BackendPlanEntry> entries = new();
        CollectRelevantChildren(page, profile, page, entries);

        DevUINode[] roots = CaptureTopLevelRoots(page);
        BackendPlan next = new(profile, roots, entries.ToArray());

        backendPlans.Remove(page);
        backendPlans.Add(page, next);
        return next;
    }

    private static DevUINode[] CaptureTopLevelRoots(Page page)
    {
        if (page?.subNodes == null || page.subNodes.Count == 0)
            return Array.Empty<DevUINode>();

        DevUINode[] roots = new DevUINode[page.subNodes.Count];
        for (int i = 0; i < roots.Length; i++)
            roots[i] = page.subNodes[i];
        return roots;
    }

    private static bool TopLevelRootsMatch(
        Page page,
        PageProfile profile,
        DevUINode[] roots,
        BackendPlanEntry[] entries)
    {
        int count = page?.subNodes?.Count ?? 0;
        if (roots == null || roots.Length != count)
            return false;

        // Room/Map/Dialog/Relationships normally compile an empty backend plan: their exact vanilla
        // screen-space tree is completely dormant. Scanning every top-level node on every stable
        // frame buys nothing in that case. Count changes remain immediate, while the existing sparse
        // semantic audit catches the unusual same-count insertion of a new third-party backend.
        if (profile?.PreserveWorldHandles != true && (entries == null || entries.Length == 0))
            return true;

        // Pages with live world/external roots keep the stronger identity/order check. Objects,
        // Sound and Triggers already pay O(relevant roots) to pump those handles, so preserving exact
        // vanilla ordering here does not reintroduce a hidden-screen traversal cost.
        for (int i = 0; i < count; i++)
            if (!ReferenceEquals(roots[i], page.subNodes[i]))
                return false;
        return true;
    }

    /// <summary>
    /// Stable-frame attachment validation for nodes already present in the compiled plan. This is
    /// intentionally O(relevant entries), not O(entire hidden tree): deleting, reparenting or moving
    /// a retained world/external node invalidates immediately, while unrelated dormant controls do
    /// not get revisited every frame.
    /// </summary>
    private static bool CompiledEntriesRemainAttached(BackendPlanEntry[] entries)
    {
        if (entries == null)
            return false;

        for (int i = 0; i < entries.Length; i++)
        {
            BackendPlanEntry entry = entries[i];
            DevUINode parent = entry.Parent;
            int childIndex = entry.ChildIndex;
            if (parent?.subNodes == null ||
                childIndex < 0 ||
                childIndex >= parent.subNodes.Count ||
                !ReferenceEquals(parent.subNodes[childIndex], entry.Node))
            {
                return false;
            }
        }

        return true;
    }

    private static bool RelevantStructureMatches(
        Page page,
        PageProfile profile,
        BackendPlanEntry[] expected)
    {
        int index = 0;
        if (page?.subNodes != null)
        {
            for (int i = page.subNodes.Count - 1; i >= 0; i--)
            {
                if (!ValidateRelevantBranch(page.subNodes[i], profile, expected, ref index))
                    return false;
            }
        }

        return expected != null && index == expected.Length;
    }

    private static bool ValidateRelevantBranch(
        DevUINode node,
        PageProfile profile,
        BackendPlanEntry[] expected,
        ref int index)
    {
        if (node == null) return true;

        if (IsExternalCompatibilityNode(node))
            return MatchExpected(node, BackendPlanEntryKind.ExternalCompatibility, expected, ref index);

        if (profile.PreserveWorldHandles && IsWorldBackendNode(node))
            return MatchExpected(node, BackendPlanEntryKind.WorldBackend, expected, ref index);

        if (node.subNodes == null) return true;
        for (int i = node.subNodes.Count - 1; i >= 0; i--)
            if (!ValidateRelevantBranch(node.subNodes[i], profile, expected, ref index))
                return false;
        return true;
    }

    private static bool MatchExpected(
        DevUINode node,
        BackendPlanEntryKind kind,
        BackendPlanEntry[] expected,
        ref int index)
    {
        if (expected == null || index < 0 || index >= expected.Length)
            return false;

        BackendPlanEntry entry = expected[index++];
        return entry.Kind == kind && ReferenceEquals(entry.Node, node);
    }

    private static void CollectRelevantChildren(
        DevUINode parent,
        PageProfile profile,
        Page owningPage,
        List<BackendPlanEntry> entries)
    {
        if (parent?.subNodes == null) return;
        for (int i = parent.subNodes.Count - 1; i >= 0; i--)
            CollectRelevantBranch(parent.subNodes[i], profile, owningPage, entries, parent, i);
    }

    private static void CollectRelevantBranch(
        DevUINode node,
        PageProfile profile,
        Page owningPage,
        List<BackendPlanEntry> entries,
        DevUINode parent,
        int childIndex)
    {
        if (node == null) return;

        if (IsExternalCompatibilityNode(node))
        {
            entries.Add(new BackendPlanEntry(
                node,
                BackendPlanEntryKind.ExternalCompatibility,
                parent,
                childIndex));
            if (owningPage != null)
                ExternalCompatibilityPages.Add(owningPage);
            return;
        }

        if (profile.PreserveWorldHandles && IsWorldBackendNode(node))
        {
            entries.Add(new BackendPlanEntry(
                node,
                BackendPlanEntryKind.WorldBackend,
                parent,
                childIndex));
            return;
        }

        if (node.subNodes == null) return;
        for (int i = node.subNodes.Count - 1; i >= 0; i--)
            CollectRelevantBranch(node.subNodes[i], profile, owningPage, entries, node, i);
    }

    /// <summary>
    /// Explicit structural invalidation for operations that rebuild a legacy page. This keeps the
    /// next quiescent pump exact without waiting for the periodic integrity audit.
    /// </summary>
    private static void InvalidateBackendPlan(Page page)
    {
        if (page != null)
            backendPlans.Remove(page);
    }

    private static void ResetBackendPlans() =>
        backendPlans = new ConditionalWeakTable<Page, BackendPlan>();
}
