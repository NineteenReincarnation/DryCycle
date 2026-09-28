using System;
using System.Collections.Generic;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Objects;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Compiles scene-space DevInterface Handle nodes from a headless third-party Representation into
/// DryCycle's detached gizmo model. It does not know any mod/object type. Dragging is delegated back
/// through the original Handle.Move() and Representation.Refresh() methods so custom data semantics
/// remain owned by the third-party implementation.
/// </summary>
internal static class HeadlessRepresentationGizmoBridge
{
    internal const string HandlePrefix = "headless-handle:";

    internal static EditorObjectGizmoSnapshot Capture(
        EditorSession session,
        PlacedObject target,
        int objectIndex)
    {
        if (session?.Owner == null || target == null || objectIndex < 0)
            return EditorObjectGizmoSnapshot.Empty;

        return LegacyObjectSandbox.InspectRepresentation(
            session,
            target,
            representation => CaptureRepresentation(
                session,
                target,
                objectIndex,
                representation),
            EditorObjectGizmoSnapshot.Empty);
    }

    internal static bool Move(
        EditorSession session,
        PlacedObject target,
        string handleId,
        float worldX,
        float worldY)
    {
        if (session?.Owner == null ||
            target == null ||
            string.IsNullOrEmpty(handleId) ||
            !handleId.StartsWith(HandlePrefix, StringComparison.Ordinal))
            return false;

        string path = handleId.Substring(HandlePrefix.Length);
        return LegacyObjectSandbox.MutateRepresentation(
            session,
            target,
            representation =>
            {
                DevUINode node = ResolveNode(representation, path);
                if (node is not Handle handle || HasPanelAncestor(handle, representation))
                    return false;

                Vector2 camera = CameraPosition(session);
                Vector2 parentWorld = target.pos;
                if (handle.parentNode is PositionedDevUINode positioned)
                    parentWorld = positioned.absPos + camera;

                Vector2 next = new(worldX, worldY);
                handle.Move(next - parentWorld);
                representation.Refresh();
                return true;
            });
    }

    private static EditorObjectGizmoSnapshot CaptureRepresentation(
        EditorSession session,
        PlacedObject target,
        int objectIndex,
        PlacedObjectRepresentation representation)
    {
        if (representation == null)
            return EditorObjectGizmoSnapshot.Empty;

        try
        {
            representation.Refresh();
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning(
                "DevTool headless handle refresh failed: " + error.Message);
        }

        Vector2 camera = CameraPosition(session);
        List<EditorObjectGizmoHandleSnapshot> handles = new();
        CaptureChildren(
            representation,
            representation,
            string.Empty,
            target,
            camera,
            handles);

        if (handles.Count == 0)
            return EditorObjectGizmoSnapshot.Empty;

        return new EditorObjectGizmoSnapshot
        {
            ObjectIndex = objectIndex,
            ObjectStableId = ObjectPresentationIdentity.Get(target),
            Handles = handles.ToArray()
        };
    }

    private static void CaptureChildren(
        DevUINode root,
        DevUINode parent,
        string parentPath,
        PlacedObject target,
        Vector2 camera,
        List<EditorObjectGizmoHandleSnapshot> handles)
    {
        if (parent?.subNodes == null)
            return;

        for (int i = 0; i < parent.subNodes.Count; i++)
        {
            DevUINode node = parent.subNodes[i];
            if (node == null)
                continue;

            string path = string.IsNullOrEmpty(parentPath)
                ? i.ToString()
                : parentPath + "." + i;

            if (node is Handle handle &&
                !HasPanelAncestor(handle, root))
            {
                Vector2 point = handle.absPos + camera;
                Vector2 anchor = target.pos;
                if (handle.parentNode is PositionedDevUINode positioned)
                    anchor = positioned.absPos + camera;

                handles.Add(new EditorObjectGizmoHandleSnapshot
                {
                    Id = HandlePrefix + path,
                    X = point.x,
                    Y = point.y,
                    AnchorX = anchor.x,
                    AnchorY = anchor.y,
                    DrawAnchorLine = true
                });
            }

            CaptureChildren(
                root,
                node,
                path,
                target,
                camera,
                handles);
        }
    }

    private static bool HasPanelAncestor(
        DevUINode node,
        DevUINode root)
    {
        DevUINode current = node?.parentNode;
        while (current != null && !ReferenceEquals(current, root))
        {
            if (current is Panel)
                return true;
            current = current.parentNode;
        }

        return false;
    }

    private static DevUINode ResolveNode(
        DevUINode root,
        string path)
    {
        if (root == null || string.IsNullOrWhiteSpace(path))
            return null;

        string[] parts = path.Split('.');
        DevUINode current = root;
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out int index) ||
                index < 0 ||
                current.subNodes == null ||
                index >= current.subNodes.Count)
                return null;

            current = current.subNodes[index];
            if (current == null)
                return null;
        }

        return current;
    }

    private static Vector2 CameraPosition(EditorSession session)
    {
        try
        {
            return session?.Owner?.game?.cameras != null &&
                   session.Owner.game.cameras.Length > 0
                ? session.Owner.game.cameras[0].pos
                : Vector2.zero;
        }
        catch
        {
            return Vector2.zero;
        }
    }
}
