using System;
using System.Collections.Concurrent;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.History;
using DryCycle.DevUI.DevTool.Objects;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Gizmos;

/// <summary>
/// Continuous command stream for native PlacedObject.Data geometry handles. The frontend sends an
/// object index, detached inspector property key and model-space Vector2 value; it never owns a
/// PlacedObject reference. One Begin/Update*/Commit gesture produces one History entry.
/// </summary>
public readonly struct NativeObjectGeometryGizmoCommand
{
    public NativeObjectGeometryGizmoCommand(
        NativeGizmoCommandKind kind,
        int objectIndex,
        string propertyKey,
        float x = 0f,
        float y = 0f)
    {
        Kind = kind;
        ObjectIndex = objectIndex;
        PropertyKey = propertyKey ?? string.Empty;
        X = x;
        Y = y;
    }

    public NativeGizmoCommandKind Kind { get; }
    public int ObjectIndex { get; }
    public string PropertyKey { get; }
    public float X { get; }
    public float Y { get; }
}

public static class NativeObjectGeometryGizmoCommandQueue
{
    private static readonly ConcurrentQueue<NativeObjectGeometryGizmoCommand> queue = new();

    public static void Enqueue(NativeObjectGeometryGizmoCommand command) => queue.Enqueue(command);

    internal static void Process(EditorSession session)
    {
        while (queue.TryDequeue(out NativeObjectGeometryGizmoCommand command))
        {
            try
            {
                ProcessOne(session, command);
            }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("DevTool native object geometry gizmo failed: " + error.Message);
            }
        }
    }

    internal static void Clear()
    {
        while (queue.TryDequeue(out _)) { }
    }

    private static void ProcessOne(EditorSession session, NativeObjectGeometryGizmoCommand command)
    {
        if (session?.ToolMode != EditorToolMode.Objects || string.IsNullOrEmpty(command.PropertyKey))
            return;

        string key = TransactionKey(command);
        switch (command.Kind)
        {
            case NativeGizmoCommandKind.Begin:
            {
                PlacedObject target = ObjectAt(session, command.ObjectIndex);
                IEditorStateSnapshot before =
                    SinglePlacedObjectStateSnapshot.Capture(session.RoomSettings, target);
                if (before != null)
                    EditorContinuousTransactionHub.Begin(session, key, "Move object handle", before);
                break;
            }
            case NativeGizmoCommandKind.Update:
                if (EditorContinuousTransactionHub.IsActive(session, key) && Apply(session, command))
                    MarkChanged(session, command.ObjectIndex);
                break;
            case NativeGizmoCommandKind.Commit:
                EditorContinuousTransactionHub.Commit(session, key);
                break;
            case NativeGizmoCommandKind.Cancel:
                if (EditorContinuousTransactionHub.Cancel(session, key))
                    MarkChanged(session, command.ObjectIndex);
                break;
        }
    }

    private static bool Apply(EditorSession session, NativeObjectGeometryGizmoCommand command)
    {
        PlacedObject target = ObjectAt(session, command.ObjectIndex);
        if (target == null) return false;

        if (!NativeDataReflectionInspector.TryBuildNativeGizmoValue(
                target,
                command.PropertyKey,
                command.X,
                command.Y,
                out EditorPropertyValue value))
            return false;
        if (!ObjectInspectorRegistry.TrySetValue(target, command.PropertyKey, value))
            return false;

        try { target.data?.RefreshLiveVisuals(); }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool native object geometry live refresh failed: " + error.Message);
        }
        return true;
    }

    private static void MarkChanged(EditorSession session, int index)
    {
        PlacedObject target = ObjectAt(session, index);
        EditorRevisionHub.Mark(session, EditorRevisionKind.Objects);
        if (target != null)
            ObjectPresentationChangeHintHub.MarkMember(session, target);
        else
            ObjectPresentationChangeHintHub.MarkFull(session);
    }

    private static PlacedObject ObjectAt(EditorSession session, int index) =>
        session?.RoomSettings?.placedObjects != null && index >= 0 && index < session.RoomSettings.placedObjects.Count
            ? session.RoomSettings.placedObjects[index]
            : null;

    private static string TransactionKey(NativeObjectGeometryGizmoCommand command) =>
        "NativeObjectGeometry:" + command.ObjectIndex + ":" + command.PropertyKey;
}