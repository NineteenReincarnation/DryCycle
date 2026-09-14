using System;
using System.Collections.Generic;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Input;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Turns the migrated vanilla DevInterface into a minimal compatibility backend while the rebuilt
/// frontend owns presentation. Screen-space legacy controls stop participating in the per-frame
/// update tree; only world-space gizmos and unknown third-party nodes that may still carry
/// compatibility behaviour remain live. Unknown/custom pages always fall back to the complete
/// vanilla update rather than being partially suspended by a heuristic.
/// </summary>
internal static class LegacyDevUiQuiescenceController
{
    private sealed class PageProfile
    {
        internal PageProfile(
            Type pageType,
            EditorToolMode toolMode,
            bool preserveWorldHandles,
            bool materializeInitialRefresh,
            bool bypassPageOverride)
        {
            PageType = pageType;
            ToolMode = toolMode;
            PreserveWorldHandles = preserveWorldHandles;
            MaterializeInitialRefresh = materializeInitialRefresh;
            BypassPageOverride = bypassPageOverride;
        }

        internal Type PageType { get; }
        internal EditorToolMode ToolMode { get; }
        internal bool PreserveWorldHandles { get; }
        internal bool MaterializeInitialRefresh { get; }
        internal bool BypassPageOverride { get; }
    }

    private static readonly PageProfile[] Profiles =
    {
        new(typeof(RoomSettingsPage), EditorToolMode.Room, preserveWorldHandles: false, materializeInitialRefresh: true, bypassPageOverride: false),
        new(typeof(ObjectsPage), EditorToolMode.Objects, preserveWorldHandles: true, materializeInitialRefresh: true, bypassPageOverride: true),
        new(typeof(SoundPage), EditorToolMode.Sound, preserveWorldHandles: true, materializeInitialRefresh: true, bypassPageOverride: true),
        new(typeof(TriggersPage), EditorToolMode.Triggers, preserveWorldHandles: true, materializeInitialRefresh: true, bypassPageOverride: true),
        new(typeof(MapPage), EditorToolMode.Map, preserveWorldHandles: false, materializeInitialRefresh: false, bypassPageOverride: true),
        new(typeof(DialogPage), EditorToolMode.Dialog, preserveWorldHandles: false, materializeInitialRefresh: true, bypassPageOverride: true),
        new(typeof(RelationshipPage), EditorToolMode.Relationships, preserveWorldHandles: false, materializeInitialRefresh: true, bypassPageOverride: true)
    };

    private static readonly System.Reflection.Assembly VanillaDevUiAssembly = typeof(DevUI).Assembly;
    private static readonly System.Reflection.Assembly DryCycleAssembly = typeof(global::DryCycle.Plugin).Assembly;
    private static readonly HashSet<Page> SuppressedInitialRefreshPages = new();

    private static bool enabled;
    private static int selectiveTraversalDepth;
    private static int fullCompatibilityDepth;
    private static PageProfile activeProfile;

    internal static void Enable()
    {
        if (enabled) return;

        On.DevInterface.DevUINode.Update += DevUINode_Update;
        On.DevInterface.ObjectsPage.Update += ObjectsPage_Update;
        On.DevInterface.SoundPage.Update += SoundPage_Update;
        On.DevInterface.TriggersPage.Update += TriggersPage_Update;
        On.DevInterface.MapPage.Update += MapPage_Update;
        On.DevInterface.DialogPage.Update += DialogPage_Update;
        On.DevInterface.RelationshipPage.Update += RelationshipPage_Update;
        enabled = true;
    }

    internal static void Disable()
    {
        if (!enabled) return;

        On.DevInterface.RelationshipPage.Update -= RelationshipPage_Update;
        On.DevInterface.DialogPage.Update -= DialogPage_Update;
        On.DevInterface.MapPage.Update -= MapPage_Update;
        On.DevInterface.TriggersPage.Update -= TriggersPage_Update;
        On.DevInterface.SoundPage.Update -= SoundPage_Update;
        On.DevInterface.ObjectsPage.Update -= ObjectsPage_Update;
        On.DevInterface.DevUINode.Update -= DevUINode_Update;

        SuppressedInitialRefreshPages.Clear();
        selectiveTraversalDepth = 0;
        fullCompatibilityDepth = 0;
        activeProfile = null;
        enabled = false;
    }

    internal static bool IsQuiescent(DevUI owner)
    {
        if (owner?.activePage == null) return false;
        return TryGetQuiescentProfile(owner.activePage, out _);
    }

    private static void ObjectsPage_Update(On.DevInterface.ObjectsPage.orig_Update orig, ObjectsPage self)
    {
        if (TryPumpDerivedPage(self)) return;
        orig(self);
    }

    private static void SoundPage_Update(On.DevInterface.SoundPage.orig_Update orig, SoundPage self)
    {
        if (TryPumpDerivedPage(self)) return;
        orig(self);
    }

    private static void TriggersPage_Update(On.DevInterface.TriggersPage.orig_Update orig, TriggersPage self)
    {
        if (TryPumpDerivedPage(self)) return;
        orig(self);
    }

    private static void MapPage_Update(On.DevInterface.MapPage.orig_Update orig, MapPage self)
    {
        if (TryPumpDerivedPage(self)) return;
        orig(self);
    }

    private static void DialogPage_Update(On.DevInterface.DialogPage.orig_Update orig, DialogPage self)
    {
        if (TryPumpDerivedPage(self)) return;
        orig(self);
    }

    private static void RelationshipPage_Update(On.DevInterface.RelationshipPage.orig_Update orig, RelationshipPage self)
    {
        if (TryPumpDerivedPage(self)) return;
        orig(self);
    }

    private static bool TryPumpDerivedPage(Page page)
    {
        if (!TryGetQuiescentProfile(page, out PageProfile profile) || !profile.BypassPageOverride)
            return false;

        PumpPageBackend(page, profile);
        return true;
    }

    private static void DevUINode_Update(On.DevInterface.DevUINode.orig_Update orig, DevUINode self)
    {
        if (self == null)
        {
            orig(self);
            return;
        }

        if (fullCompatibilityDepth > 0)
        {
            orig(self);
            return;
        }

        if (selectiveTraversalDepth > 0 && activeProfile != null && IsWorldBackendNode(self))
        {
            PumpChildren(self, activeProfile);
            CompleteInitialNodeRefresh(self);
            return;
        }

        if (self is Page page)
        {
            if (TryGetQuiescentProfile(page, out PageProfile profile) && !profile.BypassPageOverride)
            {
                PumpPageBackend(page, profile);
                return;
            }

            if (SuppressedInitialRefreshPages.Remove(page))
                page.initRefresh = true;
        }

        orig(self);
    }

    private static void PumpPageBackend(Page page, PageProfile profile)
    {
        using DevToolPerformanceMonitor.Scope performanceScope =
            DevToolPerformanceMonitor.Measure(DevToolPerformanceMetric.LegacyQuiescenceBackend);

        PageProfile previousProfile = activeProfile;
        activeProfile = profile;
        selectiveTraversalDepth++;
        try
        {
            PumpChildren(page, profile);

            if (!page.initRefresh) return;

            if (profile.MaterializeInitialRefresh)
            {
                fullCompatibilityDepth++;
                try
                {
                    page.Refresh();
                }
                finally
                {
                    fullCompatibilityDepth--;
                }
            }
            else
            {
                SuppressedInitialRefreshPages.Add(page);
            }

            page.initRefresh = false;
        }
        finally
        {
            selectiveTraversalDepth--;
            activeProfile = previousProfile;
        }
    }

    private static void PumpChildren(DevUINode parent, PageProfile profile)
    {
        if (parent?.subNodes == null) return;

        for (int i = parent.subNodes.Count - 1; i >= 0; i--)
            PumpBranch(parent.subNodes[i], profile);
    }

    private static void PumpBranch(DevUINode node, PageProfile profile)
    {
        if (node == null) return;

        if (IsExternalCompatibilityNode(node))
        {
            // Unknown third-party DevInterface code is an opaque writer. Keep its complete subtree
            // alive and invalidate only this workspace so the rebuilt UI observes any state changes
            // without globally disabling revision-driven presentation for unrelated editors.
            EditorSession session = DevToolSessionHub.Current;
            if (session != null && ReferenceEquals(session.Owner, node.owner))
                EditorRevisionHub.MarkWorkspace(session, profile.ToolMode);

            fullCompatibilityDepth++;
            try
            {
                node.Update();
            }
            finally
            {
                fullCompatibilityDepth--;
            }
            return;
        }

        if (profile.PreserveWorldHandles && IsWorldBackendNode(node))
        {
            node.Update();
            return;
        }

        if (node.subNodes == null) return;
        for (int i = node.subNodes.Count - 1; i >= 0; i--)
            PumpBranch(node.subNodes[i], profile);
    }

    private static void CompleteInitialNodeRefresh(DevUINode node)
    {
        if (!node.initRefresh) return;

        fullCompatibilityDepth++;
        try
        {
            node.Refresh();
        }
        finally
        {
            fullCompatibilityDepth--;
        }
        node.initRefresh = false;
    }

    private static bool IsWorldBackendNode(DevUINode node)
    {
        return node is Handle || node is BezierControl;
    }

    private static bool IsExternalCompatibilityNode(DevUINode node)
    {
        Type type = node?.GetType();
        if (type == null) return false;

        System.Reflection.Assembly assembly = type.Assembly;
        if (assembly == VanillaDevUiAssembly) return false;
        return assembly != DryCycleAssembly;
    }

    private static bool TryGetQuiescentProfile(Page page, out PageProfile profile)
    {
        profile = null;
        if (!enabled || page?.owner == null) return false;

        EditorSession session = DevToolSessionHub.Current;
        if (session == null || !ReferenceEquals(session.Owner, page.owner)) return false;
        if (page.owner.game?.devToolsActive != true) return false;

        if (!EditorInputRouter.FrontendAttached || EditorUiModeState.UseVanilla || session.LegacyUiVisible)
            return false;
        if (session.LegacyTransactions.HasPendingTransaction)
            return false;
        if (DevUiDiagnosticsPolicy.Enabled)
            return false;

        Type runtimePageType = page.GetType();
        for (int i = 0; i < Profiles.Length; i++)
        {
            PageProfile candidate = Profiles[i];
            if (runtimePageType != candidate.PageType || session.ToolMode != candidate.ToolMode)
                continue;

            profile = candidate;
            return true;
        }

        return false;
    }
}
