using System;
using System.Collections.Generic;
using DevInterface;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Keeps vanilla DevInterface alive as a compatibility backend while preventing its old
/// screen-space controls from visually/physically overlapping the ImGui Objects workspace.
/// World-space handles remain active. No third-party type or API is inspected here.
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
                // Keep the representation and its world-space handle active, but remove the
                // large legacy object-name label. Child Handles remain scene gizmos; panels
                // and rectangular controls beneath the representation are suppressed below.
                RememberAndHideLabels(representation);
                SuppressRepresentationChildren(representation);
                continue;
            }

            if (node is Handle)
            {
                // Standalone handles are world-space controls and must remain usable.
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
                // Radius/vector/line handles are the useful scene gizmos we intentionally
                // keep from the original representation.
                continue;
            }

            SuppressSubtree(child);
        }
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
        if (node == null || hidden.ContainsKey(node)) return;

        NodeState state = new();
        if (node is PositionedDevUINode positioned)
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

        // Rebuild screen positions after restoring logical coordinates. Do not call
        // Page.Refresh(): some pages rebuild temporary nodes there. Refresh only the nodes
        // whose coordinates were actually displaced.
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
