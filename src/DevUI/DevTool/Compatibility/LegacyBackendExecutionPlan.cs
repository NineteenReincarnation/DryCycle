using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DevInterface;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Compiled execution plan for the quiescent legacy backend.
///
/// Page-less native Objects/Sound/Trigger no longer retain vanilla world-space representations.
/// For the legacy pages that still back rebuilt tools, the only code that must remain live is an
/// opaque third-party DevInterface root. One structural pass compiles those roots; stable frames
/// execute only that retained plan instead of walking the complete hidden UI tree.
/// </summary>
internal static partial class LegacyDevUiQuiescenceController
{
    private const int BackendPlanAuditInterval = 30;

    private readonly struct BackendPlanEntry
    {
        internal BackendPlanEntry(DevUINode node, DevUINode parent, int childIndex)
        {
            Node = node;
            Parent = parent;
            ChildIndex = childIndex;
        }

        internal DevUINode Node { get; }
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
            if (node != null)
                PumpBranch(node);
        }
    }

    private static BackendPlan GetBackendPlan(Page page, PageProfile profile)
    {
        if (!backendPlans.TryGetValue(page, out BackendPlan plan) ||
            !ReferenceEquals(plan.Profile, profile) ||
            !TopLevelRootsMatch(page, plan.PageRoots, plan.Entries) ||
            !CompiledEntriesRemainAttached(plan.Entries))
        {
            return RebuildBackendPlan(page, profile);
        }

        plan.FramesUntilAudit--;
        if (plan.FramesUntilAudit <= 0)
        {
            plan.FramesUntilAudit = BackendPlanAuditInterval;
            if (!RelevantStructureMatches(page, plan.Entries))
                return RebuildBackendPlan(page, profile);
        }

        return plan;
    }

    private static BackendPlan RebuildBackendPlan(Page page, PageProfile profile)
    {
        List<BackendPlanEntry> entries = new();
        CollectRelevantChildren(page, page, entries);

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
        DevUINode[] roots,
        BackendPlanEntry[] entries)
    {
        int count = page?.subNodes?.Count ?? 0;
        if (roots == null || roots.Length != count)
            return false;

        // An empty compatibility plan is the common stable-frame case. Child-count changes are
        // immediate; the sparse semantic audit catches unusual same-count insertion of a foreign root.
        if (entries == null || entries.Length == 0)
            return true;

        for (int i = 0; i < count; i++)
            if (!ReferenceEquals(roots[i], page.subNodes[i]))
                return false;
        return true;
    }

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

    private static bool RelevantStructureMatches(Page page, BackendPlanEntry[] expected)
    {
        int index = 0;
        if (page?.subNodes != null)
        {
            for (int i = page.subNodes.Count - 1; i >= 0; i--)
            {
                if (!ValidateRelevantBranch(page.subNodes[i], expected, ref index))
                    return false;
            }
        }

        return expected != null && index == expected.Length;
    }

    private static bool ValidateRelevantBranch(
        DevUINode node,
        BackendPlanEntry[] expected,
        ref int index)
    {
        if (node == null) return true;

        if (IsExternalCompatibilityNode(node))
            return MatchExpected(node, expected, ref index);

        if (node.subNodes == null) return true;
        for (int i = node.subNodes.Count - 1; i >= 0; i--)
            if (!ValidateRelevantBranch(node.subNodes[i], expected, ref index))
                return false;
        return true;
    }

    private static bool MatchExpected(
        DevUINode node,
        BackendPlanEntry[] expected,
        ref int index)
    {
        if (expected == null || index < 0 || index >= expected.Length)
            return false;

        BackendPlanEntry entry = expected[index++];
        return ReferenceEquals(entry.Node, node);
    }

    private static void CollectRelevantChildren(
        DevUINode parent,
        Page owningPage,
        List<BackendPlanEntry> entries)
    {
        if (parent?.subNodes == null) return;
        for (int i = parent.subNodes.Count - 1; i >= 0; i--)
            CollectRelevantBranch(parent.subNodes[i], owningPage, entries, parent, i);
    }

    private static void CollectRelevantBranch(
        DevUINode node,
        Page owningPage,
        List<BackendPlanEntry> entries,
        DevUINode parent,
        int childIndex)
    {
        if (node == null) return;

        if (IsExternalCompatibilityNode(node))
        {
            entries.Add(new BackendPlanEntry(node, parent, childIndex));
            if (owningPage != null)
                ExternalCompatibilityPages.Add(owningPage);
            return;
        }

        if (node.subNodes == null) return;
        for (int i = node.subNodes.Count - 1; i >= 0; i--)
            CollectRelevantBranch(node.subNodes[i], owningPage, entries, node, i);
    }

    private static void InvalidateBackendPlan(Page page)
    {
        if (page != null)
            backendPlans.Remove(page);
    }

    private static void ResetBackendPlans() =>
        backendPlans = new ConditionalWeakTable<Page, BackendPlan>();
}