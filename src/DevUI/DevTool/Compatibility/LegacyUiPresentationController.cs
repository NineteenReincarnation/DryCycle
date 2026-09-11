using System;
using System.Collections.Generic;
using DevInterface;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Keeps vanilla DevInterface alive as a compatibility backend while preventing migrated
/// screen-space controls from visually/physically overlapping the rebuilt ImGui editor.
/// Useful world-space handles remain active. No third-party type or API is inspected here.
/// </summary>
internal static class LegacyUiPresentationController
{
    private sealed class NodeState
    {
        internal bool HasPosition;
        internal Vector2 Position;
        internal bool[] SpriteVisibility;
        internal bool[] LabelVisibility;
    }

    private static readonly Dictionary<DevUINode, NodeState> hidden = new();
    private static Page hiddenPage;
    private static readonly Vector2 Offscreen = new(-100000f, -100000f);

    internal static void Apply(Page page, bool suppressLegacyControls)
    {
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

        if (page is MapPage mapPage)
        {
            // MapPage persists RoomPanel.pos/devPos through SaveMapConfig(). Never move its
            // legacy controls off-screen to hide them: doing so can corrupt the saved map.
            // The rebuilt Map workspace owns input separately, so visual-only suppression is
            // sufficient here and leaves every map coordinate untouched.
            SuppressVisualSubtree(mapPage);
            return;
        }

        SuppressChildren(page);
    }

    internal static void Restore(Page page)
    {
        if (page == null || !ReferenceEquals(hiddenPage, page)) return;
        RestoreHiddenPage();
    }

    internal static void Reset()
    {
        RestoreHiddenPage();
        hidden.Clear();
        hiddenPage = null;
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
        if (hidden.Count == 0)
        {
            hiddenPage = null;
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

        foreach (KeyValuePair<DevUINode, NodeState> pair in hidden)
        {
            if (!pair.Value.HasPosition || pair.Key is not PositionedDevUINode positioned) continue;
            try { positioned.Refresh(); }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("DevTool legacy UI restore refresh failed: " + error.Message);
            }
        }

        hidden.Clear();
        hiddenPage = null;
    }

    private static void RestoreVisibility<TNode>(IList<TNode> nodes, bool[] visibility) where TNode : FNode
    {
        if (nodes == null || visibility == null) return;
        int count = Math.Min(nodes.Count, visibility.Length);
        for (int i = 0; i < count; i++)
            if (nodes[i] != null) nodes[i].isVisible = visibility[i];
    }
}
