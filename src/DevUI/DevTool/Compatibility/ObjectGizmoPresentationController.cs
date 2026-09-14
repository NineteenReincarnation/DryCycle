using System;
using System.Collections.Generic;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Reduces Objects-page gizmo noise while keeping vanilla representations alive for compatibility.
/// Every placed object keeps its main center handle so it can still be selected in the room.
/// Only the single selected object exposes its full vanilla gizmo and child handles.
/// </summary>
internal static class ObjectGizmoPresentationController
{
    private const int StructureAuditIntervalFrames = 120;

    private sealed class SpriteState
    {
        internal bool[] Visibility;
    }

    private static readonly Dictionary<DevUINode, SpriteState> hidden = new();
    private static readonly HashSet<Handle> activeHandles = new();
    private static readonly HashSet<Handle> suppressedHandles = new();
    private static readonly HashSet<Handle> draggingHandles = new();
    private static ObjectsPage page;
    private static PlacedObject selectedObject;
    private static bool enabled;
    private static bool presentationActive;
    private static long appliedObjectRevision;
    private static long appliedSelectionRevision;
    private static int appliedObjectCount = -1;
    private static int appliedTopLevelNodeCount = -1;
    private static int nextStructureAuditFrame;

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
        ResetAppliedState();
        presentationActive = false;
        enabled = false;
    }

    internal static void Apply(ObjectsPage objectsPage, EditorSession session, bool active)
    {
        if (!ReferenceEquals(page, objectsPage))
        {
            RestoreAll();
            ResetAppliedState();
            page = objectsPage;
        }

        bool nextActive = active && objectsPage != null && session != null;
        if (!nextActive)
        {
            if (presentationActive || hidden.Count > 0)
                RestoreAll();
            presentationActive = false;
            ResetAppliedState();
            page = objectsPage;
            return;
        }

        presentationActive = true;

        // Applying gizmo visibility used to recursively walk the complete ObjectsPage tree every
        // DevUI frame, then recursively walk every child handle again even when nothing changed.
        // The visibility policy depends only on the page structure, object model revision and
        // selection. Those are explicit semantic keys now, so stable frames return in O(1).
        // A sparse audit remains for third-party code that swaps DevUINodes without publishing a
        // DryCycle revision and happens to preserve both collection and top-level node counts.
        long objectRevision = EditorRevisionHub.Get(session, EditorRevisionKind.Objects);
        long selectionRevision = session.Selection.Revision;
        int objectCount = session.RoomSettings?.placedObjects?.Count ?? 0;
        int topLevelNodeCount = objectsPage.subNodes?.Count ?? 0;
        bool structureAuditDue = Time.frameCount >= nextStructureAuditFrame;
        if (!structureAuditDue &&
            appliedObjectRevision == objectRevision &&
            appliedSelectionRevision == selectionRevision &&
            appliedObjectCount == objectCount &&
            appliedTopLevelNodeCount == topLevelNodeCount)
            return;

        selectedObject = session.Selection.Count == 1
            ? session.Selection.PrimaryPlacedObject
            : null;
        activeHandles.Clear();
        suppressedHandles.Clear();
        draggingHandles.Clear();

        Visit(objectsPage, representation =>
        {
            bool fullGizmo = selectedObject != null && ReferenceEquals(selectedObject, representation.pObj);
            if (fullGizmo)
                RestoreFullGizmo(representation);
            else
                HideExtraGizmo(representation);
        });

        appliedObjectRevision = objectRevision;
        appliedSelectionRevision = selectionRevision;
        appliedObjectCount = objectCount;
        appliedTopLevelNodeCount = topLevelNodeCount;
        nextStructureAuditFrame = Time.frameCount + StructureAuditIntervalFrames;
    }

    internal static void Reset()
    {
        RestoreAll();
        page = null;
        presentationActive = false;
        ResetAppliedState();
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

        global::DevInterface.DevUI owner = self.owner;
        bool dragging = self.dragged || ReferenceEquals(owner?.draggedNode, self);
        if (dragging)
        {
            // A handle that was already being dragged when selection/presentation ownership changed
            // must finish that drag normally. Restore its remembered sprites only for the drag;
            // once the drag ends it falls back into the cached suppression policy below.
            if (suppressedHandles.Contains(self))
            {
                RestoreNodeSprites(self);
                draggingHandles.Add(self);
            }
            orig(self);
            return;
        }

        if (draggingHandles.Remove(self) && suppressedHandles.Contains(self))
            HideNodeSprites(self);

        // Stable known handles never walk the parent chain again. This hook runs for every vanilla
        // Handle.Update, so changing the common path from ancestor traversal to hash lookup removes
        // the remaining per-object scheduling cost after Apply itself became revision-gated.
        if (activeHandles.Contains(self))
        {
            orig(self);
            return;
        }

        if (suppressedHandles.Contains(self))
        {
            SuppressInput(orig, self, owner);
            return;
        }

        // Third-party nodes can be inserted after the page-level Apply pass. Classify an unknown
        // handle lazily the first time it updates, then cache that decision. A non-object handle is
        // considered genuinely active and is never suppressed by this controller.
        PlacedObjectRepresentation representation = FindRepresentation(self.parentNode);
        if (representation == null)
        {
            activeHandles.Add(self);
            orig(self);
            return;
        }

        bool allowChildHandle = selectedObject != null &&
                                ReferenceEquals(selectedObject, representation.pObj);
        if (allowChildHandle)
        {
            activeHandles.Add(self);
            orig(self);
            return;
        }

        Remember(self);
        HideNodeSprites(self);
        suppressedHandles.Add(self);
        SuppressInput(orig, self, owner);
    }

    private static void SuppressInput(
        On.DevInterface.Handle.orig_Update orig,
        Handle self,
        global::DevInterface.DevUI owner)
    {
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

            if (child is Handle handle)
            {
                Remember(child);
                HideNodeSprites(child);
                activeHandles.Remove(handle);
                suppressedHandles.Add(handle);
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
            if (child is Handle handle)
            {
                RestoreNodeSprites(child);
                suppressedHandles.Remove(handle);
                activeHandles.Add(handle);
            }
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
        if (hidden.Count > 0)
        {
            foreach (KeyValuePair<DevUINode, SpriteState> pair in hidden)
                RestoreNodeSprites(pair.Key);
            hidden.Clear();
        }

        activeHandles.Clear();
        suppressedHandles.Clear();
        draggingHandles.Clear();
        selectedObject = null;
    }

    private static void ResetAppliedState()
    {
        appliedObjectRevision = 0L;
        appliedSelectionRevision = 0L;
        appliedObjectCount = -1;
        appliedTopLevelNodeCount = -1;
        nextStructureAuditFrame = 0;
        activeHandles.Clear();
        suppressedHandles.Clear();
        draggingHandles.Clear();
        selectedObject = null;
    }
}
