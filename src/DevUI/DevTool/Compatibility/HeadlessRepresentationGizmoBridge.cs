using System;
using System.Collections.Generic;
using System.Reflection;
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
                Vector2 relative = next - parentWorld;
                handle.Move(relative);

                // Managed multi-point protocols commonly use ordinary Handle children and keep the
                // authoritative Vector2[] in the parent controller. Handle.Move alone cannot prove
                // that such an array was updated, so reflect the structural Data + Field.key
                // contract when present. This is protocol-based and contains no POM/RegionKit type
                // dependency.
                TryWriteManagedVectorArrayElement(handle, relative);

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
        List<EditorObjectLineSegmentSnapshot> lines = new();
        CaptureChildren(
            representation,
            representation,
            string.Empty,
            target,
            camera,
            handles,
            lines);

        if (handles.Count == 0 && lines.Count == 0)
            return EditorObjectGizmoSnapshot.Empty;

        return new EditorObjectGizmoSnapshot
        {
            ObjectIndex = objectIndex,
            ObjectStableId = ObjectPresentationIdentity.Get(target),
            Handles = handles.ToArray(),
            Lines = lines.ToArray()
        };
    }

    private static void CaptureChildren(
        DevUINode root,
        DevUINode parent,
        string parentPath,
        PlacedObject target,
        Vector2 camera,
        List<EditorObjectGizmoHandleSnapshot> handles,
        List<EditorObjectLineSegmentSnapshot> lines)
    {
        if (parent?.subNodes == null)
            return;

        CaptureMultiPointGeometry(parent, target, camera, lines);

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
                handles,
                lines);
        }
    }

    private static void CaptureMultiPointGeometry(
        DevUINode node,
        PlacedObject target,
        Vector2 camera,
        List<EditorObjectLineSegmentSnapshot> lines)
    {
        if (node == null ||
            target == null ||
            lines == null ||
            !TryReadManagedVectorArrayProtocol(
                node,
                out object data,
                out object field,
                out string key,
                out Vector2[] values))
            return;

        bool includeParent = ReadBool(field, "IncludeParent", false);
        string representationType =
            ReadMember(field, "RepresentationType")?.ToString() ?? string.Empty;

        List<Vector2> points = new();
        if (includeParent)
            points.Add(target.pos);

        if (node.subNodes != null)
        {
            for (int i = 0; i < node.subNodes.Count; i++)
            {
                if (node.subNodes[i] is not Handle handle)
                    continue;
                points.Add(handle.absPos + camera);
            }
        }

        // Some protocols omit a physical first Handle when the object origin is node zero. If the
        // tree shape is incomplete, fall back to the authoritative Vector2[] only for missing
        // coordinates; these values are relative to the object/parent representation.
        if (points.Count < values.Length)
        {
            int start = points.Count;
            for (int i = start; i < values.Length; i++)
                points.Add(target.pos + values[i]);
        }

        if (points.Count < 2)
            return;

        for (int i = 1; i < points.Count; i++)
            AddLine(lines, points[i - 1], points[i]);

        if (representationType.IndexOf("Polygon", StringComparison.OrdinalIgnoreCase) >= 0 &&
            points.Count > 2)
            AddLine(lines, points[points.Count - 1], points[0]);
    }

    private static bool TryWriteManagedVectorArrayElement(
        Handle handle,
        Vector2 relative)
    {
        if (handle?.parentNode == null ||
            !TryReadManagedVectorArrayProtocol(
                handle.parentNode,
                out object data,
                out object field,
                out string key,
                out Vector2[] values))
            return false;

        bool includeParent = ReadBool(field, "IncludeParent", false);
        int childHandleIndex = -1;
        int seen = 0;
        List<DevUINode> siblings = handle.parentNode.subNodes;
        if (siblings != null)
        {
            for (int i = 0; i < siblings.Count; i++)
            {
                if (siblings[i] is not Handle)
                    continue;
                if (ReferenceEquals(siblings[i], handle))
                {
                    childHandleIndex = seen;
                    break;
                }
                seen++;
            }
        }

        if (childHandleIndex < 0)
            return false;

        int valueIndex = includeParent
            ? childHandleIndex + 1
            : childHandleIndex;
        if (valueIndex < 0 || valueIndex >= values.Length)
            return false;

        Vector2[] copy = new Vector2[values.Length];
        Array.Copy(values, copy, values.Length);
        copy[valueIndex] = relative;

        MethodInfo setter = FindGenericMethod(
            data.GetType(),
            "SetValue",
            parameterCount: 2);
        if (setter == null)
            return false;

        try
        {
            setter.MakeGenericMethod(typeof(Vector2[]))
                .Invoke(data, new object[] { key, copy });
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning(
                "DevTool managed multi-point gizmo write failed: " +
                RootMessage(error));
            return false;
        }
    }

    private static bool TryReadManagedVectorArrayProtocol(
        DevUINode node,
        out object data,
        out object field,
        out string key,
        out Vector2[] values)
    {
        data = null;
        field = null;
        key = string.Empty;
        values = null;
        if (node == null)
            return false;

        data = ReadMember(node, "Data");
        field = ReadMember(node, "Field");
        if (data == null || field == null)
            return false;

        key = ReadMember(field, "key")?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key))
            return false;

        MethodInfo getter = FindGenericMethod(
            data.GetType(),
            "GetValue",
            parameterCount: 1);
        if (getter == null)
            return false;

        try
        {
            values = getter.MakeGenericMethod(typeof(Vector2[]))
                .Invoke(data, new object[] { key }) as Vector2[];
            return values != null;
        }
        catch
        {
            values = null;
            return false;
        }
    }

    private static void AddLine(
        List<EditorObjectLineSegmentSnapshot> lines,
        Vector2 a,
        Vector2 b)
    {
        lines.Add(new EditorObjectLineSegmentSnapshot
        {
            X0 = a.x,
            Y0 = a.y,
            X1 = b.x,
            Y1 = b.y
        });
    }

    private static object ReadMember(object instance, string name)
    {
        if (instance == null || string.IsNullOrEmpty(name))
            return null;

        Type current = instance.GetType();
        while (current != null)
        {
            FieldInfo field = current.GetField(
                name,
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly);
            if (field != null)
            {
                try { return field.GetValue(instance); }
                catch { return null; }
            }

            PropertyInfo property = current.GetProperty(
                name,
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly);
            if (property != null &&
                property.CanRead &&
                property.GetIndexParameters().Length == 0)
            {
                try { return property.GetValue(instance, null); }
                catch { return null; }
            }

            current = current.BaseType;
        }

        return null;
    }

    private static bool ReadBool(
        object instance,
        string name,
        bool fallback)
    {
        object value = ReadMember(instance, name);
        return value is bool boolean ? boolean : fallback;
    }

    private static MethodInfo FindGenericMethod(
        Type type,
        string name,
        int parameterCount)
    {
        Type current = type;
        while (current != null)
        {
            MethodInfo[] methods = current.GetMethods(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                ParameterInfo[] parameters = method.GetParameters();
                if (method.Name == name &&
                    method.IsGenericMethodDefinition &&
                    method.GetGenericArguments().Length == 1 &&
                    parameters.Length == parameterCount &&
                    parameters.Length > 0 &&
                    parameters[0].ParameterType == typeof(string))
                    return method;
            }

            current = current.BaseType;
        }

        return null;
    }

    private static string RootMessage(Exception error)
    {
        Exception current = error;
        while (current is TargetInvocationException invocation &&
               invocation.InnerException != null)
            current = invocation.InnerException;
        return current?.Message ?? "unknown error";
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
