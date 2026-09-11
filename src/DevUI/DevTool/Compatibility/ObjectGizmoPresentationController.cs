using System;
using System.Collections.Generic;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Reduces Objects-page gizmo noise while keeping vanilla representations alive for compatibility.
/// Every placed object keeps its main center handle so it can still be selected in the room.
/// Only the single selected object exposes its full vanilla gizmo and child handles.
/// </summary>
internal static class ObjectGizmoPresentationController
{
    private sealed class SpriteState
    {
        internal bool[] Visibility;
    }

    private static readonly Dictionary<DevUINode, SpriteState> hidden = new();
    private static ObjectsPage page;
    private static bool enabled;
    private static bool presentationActive;

    internal static void Enable()
    {
        if (enabled) return;
        On.DevInterface.Handle.Update += Handle_Update;
        enabled = true;
    }

    internal static void Disable()
    {
        if (!enabled) return;
        On.DevInterface.Handle.Update -= Handle_Update;
        RestoreAll();
        presentationActive = false;
        enabled = false;
    }

    internal static void Apply(ObjectsPage objectsPage, EditorSession session, bool active)
    {
        if (!ReferenceEquals(page, objectsPage))
        {
            RestoreAll();
            page = objectsPage;
        }

        presentationActive = active && objectsPage != null && session != null;
        if (!presentationActive)
        {
            RestoreAll();
            page = objectsPage;
            return;
        }

        PlacedObject selected = session.Selection.Count == 1
            ? session.Selection.PrimaryPlacedObject
            : null;

        Visit(objectsPage, representation =>
        {
            bool fullGizmo = selected != null && ReferenceEquals(selected, representation.pObj);
            if (fullGizmo)
                RestoreFullGizmo(representation);
            else
                HideExtraGizmo(representation);
        });
    }

    internal static void Reset()
    {
        RestoreAll();
        page = null;
        presentationActive = false;
    }

    private static void Handle_Update(On.DevInterface.Handle.orig_Update orig, Handle self)
    {
        if (self == null || !presentationActive)
        {
            orig(self);
            return;
        }

        // The representation itself is the lightweight center handle and remains usable even
        // while its extended gizmo is hidden. This preserves direct room-space selection.
        if (self is PlacedObjectRepresentation)
        {
            orig(self);
            return;
        }

        PlacedObjectRepresentation representation = FindRepresentation(self.parentNode);
        if (representation == null)
        {
            orig(self);
            return;
        }

        EditorSession session = DevToolSessionHub.Current;
        bool allowChildHandle =
            session != null &&
            session.ToolMode == EditorToolMode.Objects &&
            session.LegacyUiVisible == false &&
            session.Selection.Count == 1 &&
            ReferenceEquals(session.Selection.PrimaryPlacedObject, representation.pObj);

        if (allowChildHandle)
        {
            orig(self);
            return;
        }

        global::DevInterface.DevUI owner = self.owner;
        if (owner == null)
        {
            self.dragged = false;
            orig(self);
            return;
        }

        bool oldClick = owner.mouseClick;
        bool oldDown = owner.mouseDown;
        if (ReferenceEquals(owner.draggedNode, self)) owner.draggedNode = null;
        self.dragged = false;
        owner.mouseClick = false;
        owner.mouseDown = false;
        try
        {
            orig(self);
        }
        finally
        {
            owner.mouseClick = oldClick;
            owner.mouseDown = oldDown;
        }
    }

    private static PlacedObjectRepresentation FindRepresentation(DevUINode node)
    {
        DevUINode current = node;
        while (current != null)
        {
            if (current is PlacedObjectRepresentation representation)
                return representation;
            current = current.parentNode;
        }
        return null;
    }

    private static void Visit(DevUINode node, Action<PlacedObjectRepresentation> visitor)
    {
        if (node?.subNodes == null) return;
        for (int i = 0; i < node.subNodes.Count; i++)
        {
            DevUINode child = node.subNodes[i];
            if (child == null) continue;
            if (child is PlacedObjectRepresentation representation)
                visitor(representation);
            else
                Visit(child, visitor);
        }
    }

    private static void HideExtraGizmo(PlacedObjectRepresentation representation)
    {
        if (representation == null) return;

        // Keep sprite 0: this is the standard Handle center marker. Additional representation
        // sprites are rings, vectors, rectangles and similar large scene-space gizmos.
        Remember(representation);
        if (representation.fSprites != null)
        {
            RestoreNodeSprites(representation);
            for (int i = 1; i < representation.fSprites.Count; i++)
                if (representation.fSprites[i] != null)
                    representation.fSprites[i].isVisible = false;
        }

        HideChildHandles(representation);
    }

    private static void HideChildHandles(DevUINode node)
    {
        if (node?.subNodes == null) return;
        for (int i = 0; i < node.subNodes.Count; i++)
        {
            DevUINode child = node.subNodes[i];
            if (child == null) continue;

            if (child is Handle)
            {
                Remember(child);
                HideNodeSprites(child);
            }

            HideChildHandles(child);
        }
    }

    private static void RestoreFullGizmo(PlacedObjectRepresentation representation)
    {
        if (representation == null) return;
        RestoreNodeSprites(representation);
        RestoreChildHandles(representation);
    }

    private static void RestoreChildHandles(DevUINode node)
    {
        if (node?.subNodes == null) return;
        for (int i = 0; i < node.subNodes.Count; i++)
        {
            DevUINode child = node.subNodes[i];
            if (child == null) continue;
            if (child is Handle) RestoreNodeSprites(child);
            RestoreChildHandles(child);
        }
    }

    private static void Remember(DevUINode node)
    {
        if (node == null || hidden.ContainsKey(node)) return;
        SpriteState state = new();
        if (node.fSprites != null)
        {
            state.Visibility = new bool[node.fSprites.Count];
            for (int i = 0; i < node.fSprites.Count; i++)
                state.Visibility[i] = node.fSprites[i]?.isVisible ?? false;
        }
        hidden[node] = state;
    }

    private static void HideNodeSprites(DevUINode node)
    {
        if (node?.fSprites == null) return;
        for (int i = 0; i < node.fSprites.Count; i++)
            if (node.fSprites[i] != null)
                node.fSprites[i].isVisible = false;
    }

    private static void RestoreNodeSprites(DevUINode node)
    {
        if (node == null || !hidden.TryGetValue(node, out SpriteState state) || state?.Visibility == null)
            return;

        int count = Math.Min(node.fSprites?.Count ?? 0, state.Visibility.Length);
        for (int i = 0; i < count; i++)
            if (node.fSprites[i] != null)
                node.fSprites[i].isVisible = state.Visibility[i];
    }

    private static void RestoreAll()
    {
        if (hidden.Count == 0) return;
        foreach (KeyValuePair<DevUINode, SpriteState> pair in hidden)
            RestoreNodeSprites(pair.Key);
        hidden.Clear();
    }
}
