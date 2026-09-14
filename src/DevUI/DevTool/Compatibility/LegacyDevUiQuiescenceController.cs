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

        // A third-party compatibility subtree deliberately receives the ordinary recursive update.
        // Do not let the selective base-hook intercept its own base.Update() calls.
        if (fullCompatibilityDepth > 0)
        {
            orig(self);
            return;
        }

        // World-space gizmos are invoked explicitly by the selective page traversal. Their derived
        // Update implementations still run, but their base DevUINode recursion is filtered here so
        // hidden panels/buttons beneath a representation do not wake back up every frame.
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

            // Map can intentionally skip its first legacy Refresh while the new UI owns it. If the
            // developer switches back to vanilla/legacy mode, restore that initialization contract
            // before normal DevUINode.Update resumes.
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
                // Materialize once so vanilla and third-party world representations still exist as
                // a compatibility backend. Their screen-space controls become dormant immediately
                // after construction unless explicitly needed by a bridge transaction.
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
        }
    }

    private static void PumpChildren(DevUINode parent, PageProfile profile)
    {
        if (parent?.subNodes == null) return;

        // Preserve vanilla's reverse child order. Some gizmo hierarchies rely on the last-created
        // handle getting first refusal on dragging.
        for (int i = parent.subNodes.Count - 1; i >= 0; i--)
            PumpBranch(parent.subNodes[i], profile);
    }

    private static void PumpBranch(DevUINode node, PageProfile profile)
    {
        if (node == null) return;

        if (IsExternalCompatibilityNode(node))
        {
            // Unknown external DevInterface code gets the conservative path. RegionKit and other
            // mods may put meaningful behaviour in an otherwise screen-looking node, so keep that
            // entire subtree alive rather than applying DryCycle's pruning rules to foreign code.
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

        // Refresh is allowed once for a live world-space gizmo so derived representations can place
        // their sprites and synchronize handle geometry. The expensive recurring Update tree is
        // still pruned on every subsequent frame.
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
        // Handle covers PlacedObjectRepresentation, Spot/DirectionalSoundHandle,
        // SpotTriggerHandle and their nested radius/vector handles. BezierControl is the other
        // vanilla world-space editor primitive used by spline/terrain representations.
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
