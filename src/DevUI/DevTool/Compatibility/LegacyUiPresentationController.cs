using System;
using System.Collections.Generic;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Dialog;
using DryCycle.DevUI.DevTool.Map;
using DryCycle.DevUI.DevTool.Relationships;
using DryCycle.DevUI.DevTool.Room;
using DryCycle.DevUI.DevTool.RWImGui;
using DryCycle.DevUI.DevTool.Sound;
using DryCycle.DevUI.DevTool.Triggers;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Keeps vanilla DevInterface alive as a compatibility backend while preventing migrated
/// screen-space controls from visually/physically overlapping the rebuilt ImGui editor.
/// Useful world-space handles remain active. No third-party type or API is inspected here.
/// </summary>
internal static class LegacyUiPresentationController
{
    private const int SuppressionAuditIntervalFrames = 120;

    private sealed class NodeState
    {
        internal bool HasPosition;
        internal Vector2 Position;
        internal bool[] SpriteVisibility;
        internal bool[] LabelVisibility;
    }

    private static readonly Dictionary<DevUINode, NodeState> hidden = new();

    // MapPage owns several Futile nodes outside the DevUINode tree (CreatureVis labels/lines).
    // They are updated after the normal node tree and therefore must be suppressed separately.
    // Keep their original visibility so switching back to vanilla DevUI remains lossless.
    private static readonly Dictionary<FNode, bool> hiddenDirectMapVisuals = new();

    private static Page hiddenPage;
    private static readonly Vector2 Offscreen = new(-100000f, -100000f);
    private static bool lifetimeMonitorInstalled;
    private static bool observedLiveSession;
    private static EditorSession observedLifetimeSession;
    private static Page observedLifetimePage;
    private static EditorDocumentKey observedLifetimeDocument;
    private static bool hasObservedLifetimeDocument;

    // Stable migrated pages do not need a complete suppression tree walk every frame. Quiescence
    // prevents known screen-space controls from updating at all; only explicit legacy transactions,
    // opaque third-party backends, semantic workspace changes or sparse structure audits need to
    // touch the tree again.
    private static bool suppressionApplied;
    private static EditorSession appliedSession;
    private static EditorDocumentKey appliedDocument;
    private static bool hasAppliedDocument;
    private static long appliedWorkspaceRevision;
    private static int appliedTopLevelNodeCount = -1;
    private static int nextSuppressionAuditFrame;

    internal static void Apply(Page page, bool suppressLegacyControls)
    {
        EnsureLifetimeMonitor();
        ObserveDocumentLifetime(page);

        // Compatibility verification is diagnostic work, not presentation work. A full audit walks
        // instantiated DevInterface trees, mirrors controls and exercises reflection-backed action
        // routes. Running it from Apply meant the same frame that H constructed vanilla DevUI also
        // paid for the audit. Keep the audit available for explicit development sessions only.
        if (DevUiDiagnosticsPolicy.Enabled)
            DevUiFullAudit.ObserveAll(page);

        if (!ReferenceEquals(hiddenPage, page))
        {
            RestoreHiddenPage();
            hiddenPage = page;
        }

        if (page == null || !suppressLegacyControls)
        {
            RestoreHiddenPage();
            return;
        }

        EditorSession session = DevToolSessionHub.Current;
        long workspaceRevision = session == null
            ? 0L
            : EditorRevisionHub.Get(session, EditorRevisionTracker.WorkspaceKind(session.ToolMode));
        int topLevelNodeCount = page.subNodes?.Count ?? 0;
        bool safetyAuditDue = Time.frameCount >= nextSuppressionAuditFrame;
        bool documentChanged =
            !ReferenceEquals(appliedSession, session) ||
            !hasAppliedDocument ||
            session == null ||
            !appliedDocument.Equals(session.DocumentKey);
        bool revisionChanged = appliedWorkspaceRevision != workspaceRevision;
        bool topLevelChanged = appliedTopLevelNodeCount != topLevelNodeCount;

        // Opaque compatibility nodes are allowed to run their own Update every frame and may change
        // visibility without publishing a DryCycle revision. Active legacy transactions likewise run
        // the complete original lifecycle. Keep per-frame suppression only for those conservative
        // cases; exact migrated/quiescent pages otherwise use the retained hidden state.
        bool continuousSuppression =
            DevUiDiagnosticsPolicy.Enabled ||
            session?.LegacyTransactions.HasPendingTransaction == true ||
            LegacyDevUiQuiescenceController.HasExternalCompatibilityNodes(page);

        bool semanticOrStructuralChange =
            !suppressionApplied ||
            documentChanged ||
            topLevelChanged ||
            safetyAuditDue ||
            (!continuousSuppression && revisionChanged);

        if (!continuousSuppression && !semanticOrStructuralChange)
            return;

        // Rebase remembered visibility only at structural/semantic boundaries. This both drops stale
        // node references after Refresh/replacement and captures the current live tree as the new
        // restoration baseline. Continuous opaque writers still get suppression every frame without
        // paying Restore+recapture every frame; the sparse audit rebases them periodically.
        bool rebase = suppressionApplied &&
                      (documentChanged || topLevelChanged || safetyAuditDue ||
                       (!continuousSuppression && revisionChanged));
        if (rebase)
        {
            RestoreHiddenPage();
            hiddenPage = page;
        }

        if (page is MapPage mapPage)
        {
            // MapPage persists RoomPanel.pos/devPos through SaveMapConfig(). Never move its
            // legacy controls off-screen to hide them: doing so can corrupt the saved map.
            // The rebuilt Map workspace owns input separately, so visual-only suppression is
            // sufficient here and leaves every map coordinate untouched.
            SuppressVisualSubtree(mapPage);

            // Vanilla MapPage.CreatureVis does not live under subNodes/fSprites. Those Futile
            // labels/lines are added directly to Futile.stage and CreatureVis.Update() can make
            // them visible again every frame. Suppress them after the vanilla update as well.
            SuppressMapDirectVisuals(mapPage);
        }
        else
        {
            SuppressChildren(page);
        }

        suppressionApplied = true;
        appliedSession = session;
        if (session != null)
        {
            appliedDocument = session.DocumentKey;
            hasAppliedDocument = true;
        }
        else
        {
            appliedDocument = default;
            hasAppliedDocument = false;
        }
        appliedWorkspaceRevision = workspaceRevision;
        appliedTopLevelNodeCount = topLevelNodeCount;

        if (semanticOrStructuralChange)
            nextSuppressionAuditFrame = Time.frameCount + SuppressionAuditIntervalFrames;
    }

    internal static void Restore(Page page)
    {
        if (page == null || !ReferenceEquals(hiddenPage, page)) return;
        RestoreHiddenPage();
    }

    internal static void Reset()
    {
        Page retiredPage = hiddenPage ?? observedLifetimePage;

        if (lifetimeMonitorInstalled)
        {
            On.RainWorldGame.Update -= RainWorldGame_Update;
            lifetimeMonitorInstalled = false;
        }
        observedLiveSession = false;
        observedLifetimeSession = null;
        observedLifetimePage = null;
        observedLifetimeDocument = default;
        hasObservedLifetimeDocument = false;

        RestoreHiddenPage();
        hidden.Clear();
        hiddenDirectMapVisuals.Clear();
        hiddenPage = null;
        ResetSuppressionState();
        DevUiFullAudit.Reset();
        DevUiMigrationCoverage.Reset();
        UniversalDevUiCommandQueue.Clear();
        UniversalDevUiPresentationHub.Clear();
        DevUiPageCoverageTracker.Reset();
        LegacyDevUiQuiescenceController.ReleasePage(retiredPage);
    }

    /// <summary>
    /// DevUI.Update stops running as soon as DevTools closes, so page/session references retained by
    /// presentation caches cannot rely on a later editor frame to clear themselves. Install one tiny
    /// RainWorldGame lifetime observer while DevUI is active and release those roots exactly once on
    /// the live -> dormant edge. The observer unhooks itself during cleanup and is lazily installed
    /// again by Apply when DevTools is opened next time.
    /// </summary>
    private static void EnsureLifetimeMonitor()
    {
        if (lifetimeMonitorInstalled) return;
        On.RainWorldGame.Update += RainWorldGame_Update;
        lifetimeMonitorInstalled = true;
        observedLiveSession = DevToolSessionHub.IsCurrentSessionLive;
    }

    /// <summary>
    /// A mod can theoretically reuse the same Page instance while changing the editor document.
    /// Page identity alone would then preserve a deferred-refresh/external-writer/backend-plan cache
    /// compiled for the previous room. Detect that uncommon edge from the authoritative DocumentKey
    /// after EditorSession.Synchronize has run and discard only the page-keyed compatibility state.
    /// </summary>
    private static void ObserveDocumentLifetime(Page page)
    {
        EditorSession session = DevToolSessionHub.Current;
        if (session == null)
        {
            observedLifetimeSession = null;
            observedLifetimePage = null;
            observedLifetimeDocument = default;
            hasObservedLifetimeDocument = false;
            return;
        }

        bool sameSession = ReferenceEquals(observedLifetimeSession, session);
        bool samePage = ReferenceEquals(observedLifetimePage, page);
        if (sameSession && samePage && hasObservedLifetimeDocument &&
            !observedLifetimeDocument.Equals(session.DocumentKey))
        {
            LegacyDevUiQuiescenceController.ReleasePage(page);
        }

        observedLifetimeSession = session;
        observedLifetimePage = page;
        observedLifetimeDocument = session.DocumentKey;
        hasObservedLifetimeDocument = true;
    }

    private static void RainWorldGame_Update(On.RainWorldGame.orig_Update orig, global::RainWorldGame self)
    {
        orig(self);

        bool live = DevToolSessionHub.IsCurrentSessionLive;
        if (live)
        {
            observedLiveSession = true;
            return;
        }

        if (!observedLiveSession)
            return;

        observedLiveSession = false;
        ReleaseDormantEditorState();
    }

    private static void ReleaseDormantEditorState()
    {
        EditorSession session = DevToolSessionHub.Current;
        Page retiredPage = session?.Owner?.activePage;

        // Finish/cancel transient editor ownership first so nothing stale can execute against a new
        // DevUI owner if the editor is reopened later.
        session?.LegacyTransactions.Reset();
        session?.CancelPlacement();
        EditorUiCommandQueue.Clear();
        RoomEditorCommandQueue.Clear();
        SoundEditorCommandQueue.Clear();
        TriggerEditorCommandQueue.Clear();
        MapEditorCommandQueue.Clear();
        DialogEditorCommandQueue.Clear();
        RelationshipEditorCommandQueue.Clear();
        UniversalDevUiCommandQueue.Clear();

        // Restore any temporarily hidden legacy visuals before dropping the strong node/page roots.
        // Reset also removes this lifetime hook; Apply installs it again on the next DevUI lifetime.
        Reset();
        ObjectGizmoPresentationController.Reset();

        EditorPresentationHub.Clear();
        RoomEditorPresentationHub.Clear();
        SoundEditorPresentationHub.Clear();
        TriggerEditorPresentationHub.Clear();
        MapEditorPresentationHub.Clear();
        DialogEditorPresentationHub.Clear();
        RelationshipEditorPresentationHub.Clear();
        UniversalDevUiPresentationHub.Clear();
        DevUiPageCoverageTracker.Reset();

        // RWImGui views retain pre-grouped rows and source indexes by snapshot identity. The
        // presentation hubs above no longer own those arrays after Clear(), so release the view-side
        // mirrors on the same dormant edge instead of keeping the last room alive until next open.
        DevToolOverlay.ResetRetainedState();
        SceneWorkspaceWindow.ResetRetainedState();
        SoundEditorView.ResetRetainedState();
        TriggerEditorView.ResetRetainedState();

        // Reset normally releases the observed/hidden page already. Keep the explicit release for
        // the case where presentation never hid the active page during this DevUI lifetime.
        LegacyDevUiQuiescenceController.ReleasePage(retiredPage);
    }

    private static void SuppressChildren(DevUINode parent)
    {
        if (parent?.subNodes == null) return;

        for (int i = 0; i < parent.subNodes.Count; i++)
        {
            DevUINode node = parent.subNodes[i];
            if (node == null) continue;

            if (node is PlacedObjectRepresentation representation)
            {
                RememberAndHideLabels(representation);
                SuppressRepresentationChildren(representation);
                continue;
            }

            if (node is AmbientSoundPanel soundPanel)
            {
                SuppressDraggablePanelKeepingHandles(soundPanel);
                continue;
            }

            if (node is TriggerPanel triggerPanel)
            {
                SuppressDraggablePanelKeepingHandles(triggerPanel);
                continue;
            }

            if (node is Handle)
            {
                // Standalone handles are scene-space controls and remain usable.
                continue;
            }

            SuppressSubtree(node);
        }
    }

    private static void SuppressRepresentationChildren(DevUINode representation)
    {
        if (representation?.subNodes == null) return;

        for (int i = 0; i < representation.subNodes.Count; i++)
        {
            DevUINode child = representation.subNodes[i];
            if (child == null) continue;

            if (child is Handle)
            {
                // Radius/vector/line handles are the useful scene gizmos retained from the
                // original representation.
                continue;
            }

            SuppressSubtree(child);
        }
    }

    private static void SuppressDraggablePanelKeepingHandles(Panel panel)
    {
        if (panel == null) return;

        // These legacy panels are draggable. Hiding only their sprites would leave an
        // invisible click target over the room, so move the panel itself off-screen. Their
        // world-space Handle children maintain their own absolute positions during Update.
        Remember(panel);
        panel.pos = Offscreen;
        HideVisuals(panel);

        if (panel.subNodes == null) return;
        for (int i = 0; i < panel.subNodes.Count; i++)
        {
            DevUINode child = panel.subNodes[i];
            if (child == null) continue;

            if (child is Handle handle)
            {
                // Spot/Directional sound handles, Spot trigger handles and nested radius
                // handles remain scene gizmos. Their original visual line back to the legacy
                // panel must be hidden, otherwise moving the panel off-screen creates a huge
                // diagonal line across the room.
                SuppressPanelConnector(handle);
                continue;
            }

            SuppressSubtree(child);
        }
    }

    private static void SuppressPanelConnector(Handle handle)
    {
        if (handle == null) return;

        int connectorIndex = handle switch
        {
            SpotSoundHandle => 4,
            DirectionalSoundHandle => 2,
            SpotTriggerHandle => 3,
            _ => -1
        };

        if (connectorIndex < 0 || handle.fSprites == null || connectorIndex >= handle.fSprites.Count)
            return;

        Remember(handle);
        if (handle.fSprites[connectorIndex] != null)
            handle.fSprites[connectorIndex].isVisible = false;
    }

    private static void SuppressSubtree(DevUINode node)
    {
        if (node == null) return;
        Remember(node);

        if (node is PositionedDevUINode positioned)
            positioned.pos = Offscreen;

        HideVisuals(node);

        if (node.subNodes == null) return;
        for (int i = 0; i < node.subNodes.Count; i++)
            SuppressSubtree(node.subNodes[i]);
    }

    private static void SuppressVisualSubtree(DevUINode node)
    {
        if (node == null) return;
        RememberVisualState(node);
        HideVisuals(node);

        if (node.subNodes == null) return;
        for (int i = 0; i < node.subNodes.Count; i++)
            SuppressVisualSubtree(node.subNodes[i]);
    }

    private static void SuppressMapDirectVisuals(MapPage page)
    {
        if (page?.creatureVisualizations == null) return;

        for (int i = 0; i < page.creatureVisualizations.Count; i++)
        {
            MapPage.CreatureVis visual = page.creatureVisualizations[i];
            if (visual == null) continue;
            RememberAndHideDirectMapVisual(visual.label);
            RememberAndHideDirectMapVisual(visual.label2);
            RememberAndHideDirectMapVisual(visual.sprite);
            RememberAndHideDirectMapVisual(visual.sprite2);
        }
    }

    private static void RememberAndHideDirectMapVisual(FNode node)
    {
        if (node == null) return;
        if (!hiddenDirectMapVisuals.ContainsKey(node))
            hiddenDirectMapVisuals[node] = node.isVisible;
        node.isVisible = false;
    }

    private static void RememberAndHideLabels(DevUINode node)
    {
        if (node == null) return;
        Remember(node);
        if (node.fLabels == null) return;
        for (int i = 0; i < node.fLabels.Count; i++)
        {
            if (node.fLabels[i] != null)
                node.fLabels[i].isVisible = false;
        }
    }

    private static void Remember(DevUINode node)
    {
        Remember(node, capturePosition: true);
    }

    private static void RememberVisualState(DevUINode node)
    {
        Remember(node, capturePosition: false);
    }

    private static void Remember(DevUINode node, bool capturePosition)
    {
        if (node == null || hidden.ContainsKey(node)) return;

        NodeState state = new();
        if (capturePosition && node is PositionedDevUINode positioned)
        {
            state.HasPosition = true;
            state.Position = positioned.pos;
        }

        if (node.fSprites != null)
        {
            state.SpriteVisibility = new bool[node.fSprites.Count];
            for (int i = 0; i < node.fSprites.Count; i++)
                state.SpriteVisibility[i] = node.fSprites[i]?.isVisible ?? false;
        }

        if (node.fLabels != null)
        {
            state.LabelVisibility = new bool[node.fLabels.Count];
            for (int i = 0; i < node.fLabels.Count; i++)
                state.LabelVisibility[i] = node.fLabels[i]?.isVisible ?? false;
        }

        hidden[node] = state;
    }

    private static void HideVisuals(DevUINode node)
    {
        if (node.fSprites != null)
        {
            for (int i = 0; i < node.fSprites.Count; i++)
                if (node.fSprites[i] != null) node.fSprites[i].isVisible = false;
        }

        if (node.fLabels != null)
        {
            for (int i = 0; i < node.fLabels.Count; i++)
                if (node.fLabels[i] != null) node.fLabels[i].isVisible = false;
        }
    }

    private static void RestoreHiddenPage()
    {
        if (hidden.Count == 0 && hiddenDirectMapVisuals.Count == 0)
        {
            hiddenPage = null;
            ResetSuppressionState();
            return;
        }

        foreach (KeyValuePair<DevUINode, NodeState> pair in hidden)
        {
            DevUINode node = pair.Key;
            NodeState state = pair.Value;
            if (node == null || state == null) continue;

            if (state.HasPosition && node is PositionedDevUINode positioned)
                positioned.pos = state.Position;

            RestoreVisibility(node.fSprites, state.SpriteVisibility);
            RestoreVisibility(node.fLabels, state.LabelVisibility);
        }

        foreach (KeyValuePair<FNode, bool> pair in hiddenDirectMapVisuals)
        {
            if (pair.Key != null)
                pair.Key.isVisible = pair.Value;
        }

        // Do not call Refresh() here. A page switch / room transition can rebuild the legacy
        // DevUI tree between suppression and restoration. Refreshing an old PositionedDevUINode
        // then lets its implementation index newly-shaped sub-node/sprite lists and can throw
        // IndexOutOfRangeException repeatedly. Position/visibility are already restored above;
        // the currently active vanilla page refreshes itself during its next normal DevUI update.
        hidden.Clear();
        hiddenDirectMapVisuals.Clear();
        hiddenPage = null;
        ResetSuppressionState();
    }

    private static void ResetSuppressionState()
    {
        suppressionApplied = false;
        appliedSession = null;
        appliedDocument = default;
        hasAppliedDocument = false;
        appliedWorkspaceRevision = 0L;
        appliedTopLevelNodeCount = -1;
        nextSuppressionAuditFrame = 0;
    }

    private static void RestoreVisibility<TNode>(IList<TNode> nodes, bool[] visibility) where TNode : FNode
    {
        if (nodes == null || visibility == null) return;
        int count = Math.Min(nodes.Count, visibility.Length);
        for (int i = 0; i < count; i++)
            if (nodes[i] != null) nodes[i].isVisible = visibility[i];
    }
}
