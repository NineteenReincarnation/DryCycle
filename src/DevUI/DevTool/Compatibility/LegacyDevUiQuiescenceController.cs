using System;
using System.Collections.Generic;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Input;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Keeps the still-materialized migrated vanilla pages dormant while rebuilt presentation owns them.
/// Objects/Sound/Trigger are page-less native tools and therefore do not participate here during
/// normal rebuilt editing. The retained backend plan now exists only to keep opaque third-party
/// DevInterface roots alive on legacy pages that still back Room/Map/Dialog/Relationships.
/// Unknown/custom pages always fall back to the complete vanilla lifecycle.
/// </summary>
internal static partial class LegacyDevUiQuiescenceController
{
    private sealed class PageProfile
    {
        internal PageProfile(
            Type pageType,
            EditorToolMode toolMode,
            bool materializeInitialRefresh,
            bool bypassPageOverride)
        {
            PageType = pageType;
            ToolMode = toolMode;
            MaterializeInitialRefresh = materializeInitialRefresh;
            BypassPageOverride = bypassPageOverride;
        }

        internal Type PageType { get; }
        internal EditorToolMode ToolMode { get; }
        internal bool MaterializeInitialRefresh { get; }
        internal bool BypassPageOverride { get; }
    }

    private static readonly PageProfile[] Profiles =
    {
        new(typeof(RoomSettingsPage), EditorToolMode.Room, materializeInitialRefresh: true, bypassPageOverride: false),
        new(typeof(SoundPage), EditorToolMode.Sound, materializeInitialRefresh: true, bypassPageOverride: true),
        new(typeof(TriggersPage), EditorToolMode.Triggers, materializeInitialRefresh: true, bypassPageOverride: true),
        new(typeof(MapPage), EditorToolMode.Map, materializeInitialRefresh: false, bypassPageOverride: true),
        new(typeof(DialogPage), EditorToolMode.Dialog, materializeInitialRefresh: true, bypassPageOverride: true),
        new(typeof(RelationshipPage), EditorToolMode.Relationships, materializeInitialRefresh: true, bypassPageOverride: true)
    };

    private static readonly System.Reflection.Assembly VanillaDevUiAssembly = typeof(global::DevInterface.DevUI).Assembly;
    private static readonly System.Reflection.Assembly DryCycleAssembly = typeof(global::DryCycle.Plugin).Assembly;
    private static readonly HashSet<Page> SuppressedInitialRefreshPages = new();
    private static readonly HashSet<Page> DeferredRefreshPages = new();
    private static readonly HashSet<Page> ExternalCompatibilityPages = new();

    private static bool enabled;
    private static int fullCompatibilityDepth;
    private static bool externalWriterObservedInPump;

    internal static void Enable()
    {
        if (enabled) return;

        // DevUINode.Update is the generic fallback for exact migrated pages without a derived hook.
        // Page-less native Objects/Sound/Trigger own no normal rebuilt Page.Update interception.
        On.DevInterface.DevUINode.Update += DevUINode_Update;
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
        On.DevInterface.DevUINode.Update -= DevUINode_Update;

        SuppressedInitialRefreshPages.Clear();
        DeferredRefreshPages.Clear();
        ExternalCompatibilityPages.Clear();
        ResetBackendPlans();
        fullCompatibilityDepth = 0;
        externalWriterObservedInPump = false;
        enabled = false;
    }

    internal static bool IsQuiescent(global::DevInterface.DevUI owner)
    {
        if (owner?.activePage == null) return false;
        return TryGetQuiescentProfile(owner.activePage, out _);
    }

    /// <summary>
    /// Rebuilt editors can mutate the model without rebuilding an invisible vanilla screen-space
    /// page immediately. Exact migrated pages with no opaque third-party controls are marked stale
    /// and refreshed once, immediately before vanilla/legacy presentation becomes active again.
    /// </summary>
    internal static bool TryDeferRefresh(EditorSession session)
    {
        Page page = session?.Owner?.activePage;
        if (page == null || HasExternalCompatibilityNodesNow(page))
            return false;
        if (!TryGetQuiescentProfile(page, out _))
            return false;

        DeferredRefreshPages.Add(page);
        return true;
    }

    internal static bool HasExternalCompatibilityNodes(Page page) =>
        page != null && ExternalCompatibilityPages.Contains(page);

    private static bool HasExternalCompatibilityNodesNow(Page page)
    {
        if (page == null)
            return false;
        if (ExternalCompatibilityPages.Contains(page))
            return true;
        if (!ContainsExternalCompatibilityNode(page))
            return false;

        ExternalCompatibilityPages.Add(page);
        InvalidateBackendPlan(page);
        return true;
    }

    private static bool ContainsExternalCompatibilityNode(DevUINode parent)
    {
        if (parent?.subNodes == null)
            return false;

        for (int i = parent.subNodes.Count - 1; i >= 0; i--)
        {
            DevUINode child = parent.subNodes[i];
            if (child == null)
                continue;
            if (IsExternalCompatibilityNode(child))
                return true;
            if (ContainsExternalCompatibilityNode(child))
                return true;
        }

        return false;
    }

    private static void MapPage_Update(On.DevInterface.MapPage.orig_Update orig, MapPage self)
    {
        if (TryPumpDerivedPage(self)) return;
        PrepareFullLegacyPageUpdate(self);
        orig(self);
    }

    private static void DialogPage_Update(On.DevInterface.DialogPage.orig_Update orig, DialogPage self)
    {
        if (TryPumpDerivedPage(self)) return;
        PrepareFullLegacyPageUpdate(self);
        orig(self);
    }

    private static void RelationshipPage_Update(On.DevInterface.RelationshipPage.orig_Update orig, RelationshipPage self)
    {
        if (TryPumpDerivedPage(self)) return;
        PrepareFullLegacyPageUpdate(self);
        orig(self);
    }

    private static void PrepareFullLegacyPageUpdate(Page page)
    {
        LegacyUiPresentationController.Restore(page);
        FlushDeferredRefresh(page);
    }

    private static bool TryPumpDerivedPage(Page page)
    {
        if (!TryGetQuiescentProfile(page, out PageProfile profile) || !profile.BypassPageOverride)
            return false;

        PrepareQuiescentFrame(page);
        PumpPageBackend(page, profile);
        return true;
    }

    /// <summary>
    /// Sound/Trigger can enter this helper only through the conservative top-level compatibility
    /// pump when a materialized built-in page temporarily exists without explicit legacy ownership.
    /// </summary>
    private static void PrepareQuiescentFrame(Page page)
    {
        switch (page)
        {
            case SoundPage sound:
                sound.draggedObject = null;
                sound.removeIfReleaseObject = null;
                break;
            case TriggersPage triggers:
                triggers.draggedObject = null;
                triggers.removeIfReleaseObject = null;
                break;
        }
    }

    private static void DevUINode_Update(On.DevInterface.DevUINode.orig_Update orig, DevUINode self)
    {
        if (self == null)
        {
            orig(self);
            return;
        }

        // An opaque compatibility subtree deliberately receives the ordinary recursive lifecycle.
        if (fullCompatibilityDepth > 0)
        {
            orig(self);
            return;
        }

        if (self is Page page)
        {
            if (TryGetQuiescentProfile(page, out PageProfile profile) && !profile.BypassPageOverride)
            {
                PumpPageBackend(page, profile);
                return;
            }

            PrepareFullLegacyPageUpdate(page);

            // Map intentionally skips its first hidden legacy Refresh while the rebuilt map owns
            // presentation. Returning to vanilla restores that initialization contract losslessly.
            if (SuppressedInitialRefreshPages.Remove(page))
                page.initRefresh = true;
        }

        orig(self);
    }

    private static void FlushDeferredRefresh(Page page)
    {
        if (page == null || !DeferredRefreshPages.Remove(page)) return;

        fullCompatibilityDepth++;
        try
        {
            page.Refresh();
            InvalidateBackendPlan(page);
            page.initRefresh = false;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool deferred legacy page refresh failed: " + error.Message);
        }
        finally
        {
            fullCompatibilityDepth--;
        }
    }

    private static void PumpPageBackend(Page page, PageProfile profile)
    {
        using DevToolPerformanceMonitor.Scope performanceScope =
            DevToolPerformanceMonitor.Measure(DevToolPerformanceMetric.LegacyQuiescenceBackend);

        bool previousExternalWriterObserved = externalWriterObservedInPump;
        externalWriterObservedInPump = false;
        try
        {
            PumpCompiledBackendPlan(page, profile);

            if (externalWriterObservedInPump)
            {
                EditorSession session = DevToolSessionHub.Current;
                if (session != null && ReferenceEquals(session.Owner, page.owner))
                    EditorRevisionHub.MarkWorkspace(session, profile.ToolMode);
            }

            if (!page.initRefresh) return;

            if (profile.MaterializeInitialRefresh)
            {
                // Compatibility probing remains available for exact legacy pages that are still
                // materialized by Room/Dialog/Relationships or the conservative Sound/Trigger path.
                fullCompatibilityDepth++;
                try
                {
                    page.Refresh();
                    InvalidateBackendPlan(page);
                    DeferredRefreshPages.Remove(page);
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
            externalWriterObservedInPump = previousExternalWriterObserved;
        }
    }

    private static void PumpBranch(DevUINode node)
    {
        if (node == null) return;

        if (IsExternalCompatibilityNode(node))
        {
            externalWriterObservedInPump = true;
            Page owningPage = node.owner?.activePage;
            if (owningPage != null)
                ExternalCompatibilityPages.Add(owningPage);

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

        // Vanilla and DryCycle-owned hidden controls are dormant. Walk only far enough to discover
        // a nested opaque third-party root; no vanilla Handle/Bezier backend is retained anymore.
        if (node.subNodes == null) return;
        for (int i = node.subNodes.Count - 1; i >= 0; i--)
            PumpBranch(node.subNodes[i]);
    }

    private static bool IsExternalCompatibilityNode(DevUINode node)
    {
        Type type = node?.GetType();
        if (type == null) return false;

        System.Reflection.Assembly assembly = type.Assembly;
        if (assembly == VanillaDevUiAssembly) return false;

        // DryCycle-owned old controls are represented by rebuilt presentation and sleep like vanilla
        // nodes. Unknown foreign assemblies remain conservative opaque compatibility backends.
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