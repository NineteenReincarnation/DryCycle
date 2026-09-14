using System;
using System.Collections.Generic;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Input;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Turns the migrated vanilla DevInterface into a minimal compatibility backend while the rebuilt
/// frontend owns presentation. Screen-space legacy controls stop participating in the per-frame
/// update tree; only world-space gizmos and third-party nodes that may still carry compatibility
/// behaviour remain live. Unknown/custom pages always fall back to the complete vanilla update.
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
        new(typeof(ObjectsPage), EditorToolMode.Objects, preserveWorldHandles: true, materializeInitialRefresh: true, bypassPageOverride: false),
        new(typeof(SoundPage), EditorToolMode.Sound, preserveWorldHandles: true, materializeInitialRefresh: true, bypassPageOverride: false),
        new(typeof(TriggersPage), EditorToolMode.Triggers, preserveWorldHandles: true, materializeInitialRefresh: true, bypassPageOverride: false),
        new(typeof(MapPage), EditorToolMode.Map, preserveWorldHandles: false, materializeInitialRefresh: false, bypassPageOverride: true),
        new(typeof(DialogPage), EditorToolMode.Dialog, preserveWorldHandles: false, materializeInitialRefresh: true, bypassPageOverride: false),
        new(typeof(RelationshipPage), EditorToolMode.Relationships, preserveWorldHandles: false, materializeInitialRefresh: true, bypassPageOverride: false)
    };

    private static readonly System.Reflection.Assembly VanillaDevUiAssembly = typeof(DevUI).Assembly;
    private static readonly HashSet<Page> SuppressedInitialRefreshPages = new();

    private static bool enabled;
    private static int selectiveTraversalDepth;
    private static int fullCompatibilityDepth;
    private static PageProfile activeProfile;

    internal static void Enable()
    {
        if (enabled) return;
        On.DevInterface.DevUINode.Update += DevUINode_Update;
        enabled = true;
    }

    internal static void Disable()
    {
        if (!enabled) return;
        On.DevInterface.DevUINode.Update -= DevUINode_Update;
        SuppressedInitialRefreshPages.Clear();
        selectiveTraversalDepth = 0;
        fullCompatibilityDepth = 0;
        activeProfile = null;
        enabled = false;
    }

    /// <summary>
    /// MapPage is the one migrated page whose own override performs substantial hidden work after
    /// base.Update(), including MapObject's synchronous room preparation loop. The input router
    /// calls this before vanilla MapPage.Update. Returning true means the minimal backend was
    /// pumped and the original override must not run this frame.
    /// </summary>
    internal static bool TryUpdateMapBackend(MapPage page)
    {
        if (!TryGetQuiescentProfile(page, out PageProfile profile) || !profile.BypassPageOverride)
            return false;

        PumpPageBackend(page, profile);
        return true;
    }

    internal static bool IsQuiescent(DevUI owner)
    {
        if (owner?.activePage == null) return false;
        return TryGetQuiescentProfile(owner.activePage, out _);
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

            // Some pages (notably Map) deliberately skip their first legacy Refresh while the new
            // UI owns presentation. If the developer switches back to vanilla/legacy mode, restore
            // that initialization contract before the normal DevUINode.Update runs.
            if (SuppressedInitialRefreshPages.Remove(page))
                page.initRefresh = true;
        }

        orig(self);
    }

    private static void PumpPageBackend(Page page, PageProfile profile)
    {
        PageProfile previousProfile = activeProfile;
        activeProfile = profile;
        selectiveTraversalDepth++;
        try
        {
            PumpChildren(page, profile);

            if (!page.initRefresh) return;

            if (profile.MaterializeInitialRefresh)
            {
                // Materialize once so vanilla/third-party representations still exist as a
                // compatibility backend. They are subsequently dormant unless explicitly needed.
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
                // Large fully-replaced pages such as Map do not need their first hidden UI refresh.
                // Remember this so returning to vanilla presentation can restore it losslessly.
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
            // External DevInterface types are intentionally conservative: if another assembly put
            // behaviour in a node, keep that subtree alive instead of guessing that it is cosmetic.
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

        // Vanilla screen-space containers are dormant, but keep walking their structure because a
        // useful world-space handle or third-party compatibility node can be nested underneath.
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
        return type != null && type.Assembly != VanillaDevUiAssembly;
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
