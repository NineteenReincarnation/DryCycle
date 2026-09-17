using System;
using System.Collections.Generic;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Input;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Turns the migrated vanilla DevInterface into a minimal compatibility backend while the rebuilt
/// frontend owns presentation. Screen-space legacy controls stop participating in the per-frame
/// update tree. Objects temporarily retain world-space representations; Sound/Trigger use native
/// gizmos and keep no built-in world Handle roots. Unknown third-party nodes that may still carry
/// compatibility behaviour remain live. Unknown/custom pages always fall back to the complete
/// vanilla update rather than being partially suspended by a heuristic.
/// </summary>
internal static partial class LegacyDevUiQuiescenceController
{
    private sealed class PageProfile
    {
        internal PageProfile(
            Type pageType,
            EditorToolMode toolMode,
            bool preserveWorldHandles,
            bool useNativeBackendRefresh,
            bool materializeInitialRefresh,
            bool bypassPageOverride)
        {
            PageType = pageType;
            ToolMode = toolMode;
            PreserveWorldHandles = preserveWorldHandles;
            UseNativeBackendRefresh = useNativeBackendRefresh;
            MaterializeInitialRefresh = materializeInitialRefresh;
            BypassPageOverride = bypassPageOverride;
        }

        internal Type PageType { get; }
        internal EditorToolMode ToolMode { get; }
        internal bool PreserveWorldHandles { get; }
        internal bool UseNativeBackendRefresh { get; }
        internal bool MaterializeInitialRefresh { get; }
        internal bool BypassPageOverride { get; }
    }

    private static readonly PageProfile[] Profiles =
    {
        new(typeof(RoomSettingsPage), EditorToolMode.Room, preserveWorldHandles: false, useNativeBackendRefresh: false, materializeInitialRefresh: true, bypassPageOverride: false),
        new(typeof(ObjectsPage), EditorToolMode.Objects, preserveWorldHandles: true, useNativeBackendRefresh: true, materializeInitialRefresh: true, bypassPageOverride: true),
        new(typeof(SoundPage), EditorToolMode.Sound, preserveWorldHandles: false, useNativeBackendRefresh: false, materializeInitialRefresh: true, bypassPageOverride: true),
        new(typeof(TriggersPage), EditorToolMode.Triggers, preserveWorldHandles: false, useNativeBackendRefresh: false, materializeInitialRefresh: true, bypassPageOverride: true),
        new(typeof(MapPage), EditorToolMode.Map, preserveWorldHandles: false, useNativeBackendRefresh: false, materializeInitialRefresh: false, bypassPageOverride: true),
        new(typeof(DialogPage), EditorToolMode.Dialog, preserveWorldHandles: false, useNativeBackendRefresh: false, materializeInitialRefresh: true, bypassPageOverride: true),
        new(typeof(RelationshipPage), EditorToolMode.Relationships, preserveWorldHandles: false, useNativeBackendRefresh: false, materializeInitialRefresh: true, bypassPageOverride: true)
    };

    private static readonly System.Reflection.Assembly VanillaDevUiAssembly = typeof(global::DevInterface.DevUI).Assembly;
    private static readonly System.Reflection.Assembly DryCycleAssembly = typeof(global::DryCycle.Plugin).Assembly;
    private static readonly HashSet<Page> SuppressedInitialRefreshPages = new();
    private static readonly HashSet<Page> DeferredRefreshPages = new();
    private static readonly HashSet<Page> ExternalCompatibilityPages = new();

    private static bool enabled;
    private static int selectiveTraversalDepth;
    private static int fullCompatibilityDepth;
    private static PageProfile activeProfile;
    private static bool externalWriterObservedInPump;

    internal static void Enable()
    {
        if (enabled) return;

        // DevUINode.Update is the generic fallback for pages that do not provide their own Update
        // override. Known derived vanilla pages are intercepted separately so their now-redundant
        // page-specific work (trash bins, threat sliders, layout, hidden map loading, etc.) never
        // runs while the rebuilt UI owns that workspace.
        On.DevInterface.DevUINode.Update += DevUINode_Update;
        On.DevInterface.ObjectsPage.Update += ObjectsPage_Update;
        On.DevInterface.SoundPage.Update += SoundPage_Update;
        On.DevInterface.TriggersPage.Update += TriggersPage_Update;
        On.DevInterface.MapPage.Update += MapPage_Update;
        On.DevInterface.DialogPage.Update += DialogPage_Update;
        On.DevInterface.RelationshipPage.Update += RelationshipPage_Update;

        // Objects is the only migrated workspace that still needs a reduced legacy Refresh because
        // its vanilla/custom PlacedObjectRepresentation backend remains transitional. Sound and
        // Trigger have no native-mode Refresh hook at all: their built-in editing path is fully
        // native and legacy presentation is recreated only when explicit compatibility requires it.
        On.DevInterface.ObjectsPage.Refresh += ObjectsPage_Refresh;
        enabled = true;
    }

    internal static void Disable()
    {
        if (!enabled) return;

        On.DevInterface.ObjectsPage.Refresh -= ObjectsPage_Refresh;
        On.DevInterface.RelationshipPage.Update -= RelationshipPage_Update;
        On.DevInterface.DialogPage.Update -= DialogPage_Update;
        On.DevInterface.MapPage.Update -= MapPage_Update;
        On.DevInterface.TriggersPage.Update -= TriggersPage_Update;
        On.DevInterface.SoundPage.Update -= SoundPage_Update;
        On.DevInterface.ObjectsPage.Update -= ObjectsPage_Update;
        On.DevInterface.DevUINode.Update -= DevUINode_Update;

        SuppressedInitialRefreshPages.Clear();
        DeferredRefreshPages.Clear();
        ExternalCompatibilityPages.Clear();
        ResetBackendPlans();
        selectiveTraversalDepth = 0;
        fullCompatibilityDepth = 0;
        activeProfile = null;
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
    ///
    /// This decision is a low-frequency mutation boundary, so inspect the current tree rather than
    /// trusting only the sticky compatibility flag populated by the compiled backend audit. A newly
    /// inserted RegionKit/POM/other foreign node therefore forces the safe full path immediately.
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

    /// <summary>
    /// Once an opaque third-party subtree has been observed on a page, remember that fact for the
    /// page lifetime. This lets the revision layer stay conservative when the developer temporarily
    /// exposes the full legacy UI, where selective traversal is intentionally disabled.
    /// </summary>
    internal static bool HasExternalCompatibilityNodes(Page page) =>
        page != null && ExternalCompatibilityPages.Contains(page);

    /// <summary>
    /// Refresh/defer decisions are infrequent enough to inspect the live tree. This closes the
    /// window between a third-party runtime insertion and the periodic compiled-plan audit without
    /// putting an O(N) walk back onto stable frames.
    /// </summary>
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

    private static void ObjectsPage_Update(On.DevInterface.ObjectsPage.orig_Update orig, ObjectsPage self)
    {
        if (TryPumpDerivedPage(self)) return;
        PrepareFullLegacyPageUpdate(self);
        orig(self);
    }

    private static void SoundPage_Update(On.DevInterface.SoundPage.orig_Update orig, SoundPage self)
    {
        if (TryPumpDerivedPage(self)) return;
        PrepareFullLegacyPageUpdate(self);
        orig(self);
    }

    private static void TriggersPage_Update(On.DevInterface.TriggersPage.orig_Update orig, TriggersPage self)
    {
        if (TryPumpDerivedPage(self)) return;
        PrepareFullLegacyPageUpdate(self);
        orig(self);
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

    /// <summary>
    /// If quiescence is no longer active, restore parked screen controls before vanilla's own page
    /// Update executes. In particular, switching to Vanilla via Ctrl+Shift+U happens earlier in the
    /// same DevUI frame; waiting for DevToolRuntime's post-orig presentation pass would otherwise
    /// give vanilla one update against hidden/offscreen controls.
    /// </summary>
    private static void PrepareFullLegacyPageUpdate(Page page)
    {
        LegacyUiPresentationController.Restore(page);
        FlushDeferredRefresh(page);
    }

    private static void ObjectsPage_Refresh(On.DevInterface.ObjectsPage.orig_Refresh orig, ObjectsPage self)
    {
        if (CanUseNativeBackendRefresh(self) && LegacySpatialBackendRefresh.TryRefreshObjects(self))
        {
            InvalidateBackendPlan(self);
            DeferredRefreshPages.Add(self);
            return;
        }

        orig(self);
        InvalidateBackendPlan(self);
        DeferredRefreshPages.Remove(self);
    }

    private static bool CanUseNativeBackendRefresh(Page page)
    {
        if (fullCompatibilityDepth > 0 || page == null || HasExternalCompatibilityNodesNow(page))
            return false;

        return TryGetQuiescentProfile(page, out PageProfile profile) && profile.UseNativeBackendRefresh;
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
    /// Preserve the tiny transient-state contract from the bypassed vanilla page Update methods.
    /// Objects still let legacy representations repopulate draggedObject. Sound/Trigger now have no
    /// built-in legacy spatial nodes, but clearing the same fields prevents stale values from a
    /// previous Vanilla/compatibility frame from leaking back into native revision logic.
    /// </summary>
    private static void PrepareQuiescentFrame(Page page)
    {
        switch (page)
        {
            case ObjectsPage objects:
                objects.draggedObject = null;
                objects.removeIfReleaseObject = null;
                break;
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

        // A third-party compatibility subtree deliberately receives the ordinary recursive update.
        // Do not let the selective base-hook intercept its own base.Update() calls.
        if (fullCompatibilityDepth > 0)
        {
            orig(self);
            return;
        }

        // World-space gizmos are invoked explicitly by the selective page traversal. At this stage
        // that path is primarily Objects; built-in Sound/Trigger are native and no longer compile
        // vanilla Handle roots into their backend plan.
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

            PrepareFullLegacyPageUpdate(page);

            // Map can intentionally skip its first legacy Refresh while the new UI owns it. If the
            // developer switches back to vanilla/legacy mode, restore that initialization contract
            // before normal DevUINode.Update resumes.
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

        PageProfile previousProfile = activeProfile;
        bool previousExternalWriterObserved = externalWriterObservedInPump;
        activeProfile = profile;
        externalWriterObservedInPump = false;
        selectiveTraversalDepth++;
        try
        {
            PumpCompiledBackendPlan(page, profile);

            // A page can contain several nodes from the same or different third-party assemblies.
            // They are all opaque writers, but presentation only needs one workspace revision edge
            // after the complete legacy backend pass. Do not bump once per foreign node.
            if (externalWriterObservedInPump)
            {
                EditorSession session = DevToolSessionHub.Current;
                if (session != null && ReferenceEquals(session.Owner, page.owner))
                    EditorRevisionHub.MarkWorkspace(session, profile.ToolMode);
            }

            if (!page.initRefresh) return;

            if (profile.MaterializeInitialRefresh)
            {
                // Materialize once as a compatibility probe so third-party Refresh hooks can attach
                // their nodes. Objects keeps required representations. Pure built-in Sound/Trigger
                // presentation is retired immediately after this frame by the native spatial owner;
                // a deferred Refresh recreates it only if Vanilla/Legacy is later requested.
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
                // Map is fully represented by World Workspace. Its hidden legacy Refresh creates a
                // MapObject and begins its own room-texture preparation pipeline, so skip it while
                // ImGui owns presentation. Returning to vanilla restores initRefresh losslessly.
                SuppressedInitialRefreshPages.Add(page);
            }

            page.initRefresh = false;
        }
        finally
        {
            selectiveTraversalDepth--;
            activeProfile = previousProfile;
            externalWriterObservedInPump = previousExternalWriterObserved;
        }
    }

    private static void PumpChildren(DevUINode parent, PageProfile profile)
    {
        if (parent?.subNodes == null) return;

        // Preserve vanilla's reverse child order. Some gizmo hierarchies rely on the last-created
        // handle getting first refusal on dragging. This helper remains for the small subtree beneath
        // one compiled world-backend root; the page-wide traversal itself is retained in a plan.
        for (int i = parent.subNodes.Count - 1; i >= 0; i--)
            PumpBranch(parent.subNodes[i], profile);
    }

    private static void PumpBranch(DevUINode node, PageProfile profile)
    {
        if (node == null) return;

        if (IsExternalCompatibilityNode(node))
        {
            // Unknown third-party DevInterface code is an opaque writer. Keep its complete subtree
            // alive, remember that the page contains foreign compatibility code, and let the page
            // pump publish one batched workspace invalidation after all such nodes have updated.
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

        if (profile.PreserveWorldHandles && IsWorldBackendNode(node))
        {
            node.Update();
            return;
        }

        // Vanilla and DryCycle-owned screen-space nodes are dormant. Keep walking structure only to
        // discover a nested world-space handle or truly external compatibility node.
        if (node.subNodes == null) return;
        for (int i = node.subNodes.Count - 1; i >= 0; i--)
            PumpBranch(node.subNodes[i], profile);
    }

    private static void CompleteInitialNodeRefresh(DevUINode node)
    {
        if (!node.initRefresh) return;

        // Refresh is allowed once for a retained world-space compatibility gizmo so derived
        // representations can place their sprites and synchronize geometry. Built-in Sound/Trigger
        // never enter this path now because their profiles do not preserve world handles.
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
        // Objects still uses Handle/BezierControl as a temporary compatibility backend. The same
        // primitive types may also appear under an opaque third-party subtree, which is handled by
        // the foreign-node branch before this predicate is consulted.
        return node is Handle || node is BezierControl;
    }

    private static bool IsExternalCompatibilityNode(DevUINode node)
    {
        Type type = node?.GetType();
        if (type == null) return false;

        System.Reflection.Assembly assembly = type.Assembly;
        if (assembly == VanillaDevUiAssembly) return false;

        // DryCycle's own old DevUI controls (DryCycleTextField/NumericSlider/etc.) are already
        // represented by the rebuilt frontend and should sleep just like vanilla screen controls.
        // Unknown foreign assemblies remain conservative compatibility backends.
        return assembly != DryCycleAssembly;
    }

    private static bool TryGetQuiescentProfile(Page page, out PageProfile profile)
    {
        profile = null;
        if (!enabled || page?.owner == null) return false;

        EditorSession session = DevToolSessionHub.Current;
        if (session == null || !ReferenceEquals(session.Owner, page.owner)) return false;
        if (page.owner.game?.devToolsActive != true) return false;

        // Quiescence is a New-UI optimization only. Explicit legacy/vanilla presentation and
        // in-flight legacy transactions retain the complete original lifecycle.
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
            // Exact type is intentional. A third-party custom Page subclass is an unknown contract
            // and therefore falls back to the complete vanilla update instead of being partially
            // suspended by a heuristic.
            if (runtimePageType != candidate.PageType || session.ToolMode != candidate.ToolMode)
                continue;

            profile = candidate;
            return true;
        }

        return false;
    }
}
