using System;
using System.Collections.Concurrent;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.History;
using DryCycle.DevUI.DevTool.Objects;
using RWCustom;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Gizmos;

public enum NativeObjectGizmoEditKind
{
    Begin,
    Update,
    Commit,
    Cancel,
    InsertCurvePoint,
    RemoveHandle
}

public readonly struct NativeObjectGizmoEditCommand
{
    public NativeObjectGizmoEditCommand(
        NativeObjectGizmoEditKind kind,
        int objectIndex,
        string handleId = null,
        float x = 0f,
        float y = 0f,
        int segmentIndex = -1,
        float curveT = 0f,
        bool snap = false)
    {
        Kind = kind;
        ObjectIndex = objectIndex;
        HandleId = handleId ?? string.Empty;
        X = x;
        Y = y;
        SegmentIndex = segmentIndex;
        CurveT = curveT;
        Snap = snap;
    }

    public NativeObjectGizmoEditKind Kind { get; }
    public int ObjectIndex { get; }
    public string HandleId { get; }
    public float X { get; }
    public float Y { get; }
    public int SegmentIndex { get; }
    public float CurveT { get; }
    public bool Snap { get; }
}

/// <summary>
/// Unified command boundary for selected-object scene gizmos. Frontend commands contain only detached
/// IDs/world coordinates. All Rain World model semantics, snapping and history live in the backend.
/// </summary>
public static class NativeObjectGizmoEditCommandQueue
{
    private static readonly ConcurrentQueue<NativeObjectGizmoEditCommand> queue = new();

    public static void Enqueue(NativeObjectGizmoEditCommand command) => queue.Enqueue(command);

    internal static void Process(EditorSession session)
    {
        while (queue.TryDequeue(out NativeObjectGizmoEditCommand command))
        {
            try
            {
                ProcessOne(session, command);
            }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("DevTool native object gizmo edit failed: " + error.Message);
            }
        }
    }

    internal static void Clear()
    {
        while (queue.TryDequeue(out _)) { }
    }

    private static void ProcessOne(EditorSession session, NativeObjectGizmoEditCommand command)
    {
        if (session?.ToolMode != EditorToolMode.Objects)
            return;

        PlacedObject target = ObjectAt(session, command.ObjectIndex);
        if (target == null)
            return;

        if (command.Kind == NativeObjectGizmoEditKind.InsertCurvePoint)
        {
            ExecuteDiscrete(
                session,
                target,
                "Split spline segment",
                () => SplitSpline(target, command.SegmentIndex, command.CurveT));
            return;
        }

        if (command.Kind == NativeObjectGizmoEditKind.RemoveHandle)
        {
            ExecuteDiscrete(
                session,
                target,
                "Join spline segments",
                () => RemoveSplineMidpoint(target, command.HandleId));
            return;
        }

        if (string.IsNullOrEmpty(command.HandleId))
            return;

        string transactionKey = TransactionKey(command.ObjectIndex, command.HandleId);
        switch (command.Kind)
        {
            case NativeObjectGizmoEditKind.Begin:
            {
                NativeObjectRuntimeReconciler.PrepareForMutation(session, target);
                IEditorStateSnapshot before =
                    SinglePlacedObjectStateSnapshot.Capture(session.RoomSettings, target);
                if (before != null)
                    EditorContinuousTransactionHub.Begin(
                        session,
                        transactionKey,
                        "Move object gizmo",
                        before);
                break;
            }

            case NativeObjectGizmoEditKind.Update:
                if (EditorContinuousTransactionHub.IsActive(session, transactionKey) &&
                    ApplyDrag(target, command))
                {
                    MarkChanged(session, target);
                }
                break;

            case NativeObjectGizmoEditKind.Commit:
                EditorContinuousTransactionHub.Commit(session, transactionKey);
                break;

            case NativeObjectGizmoEditKind.Cancel:
                if (EditorContinuousTransactionHub.Cancel(session, transactionKey))
                {
                    NativeObjectRuntimeReconciler.RefreshAfterMutation(session, target);
                    MarkChanged(session, target);
                }
                break;
        }
    }

    private static bool ApplyDrag(PlacedObject target, NativeObjectGizmoEditCommand command)
    {
        if (command.HandleId.StartsWith("property:", StringComparison.Ordinal))
        {
            string propertyKey = command.HandleId.Substring("property:".Length);
            Vector2 relative = new Vector2(command.X, command.Y) - target.pos;
            if (!NativeDataReflectionInspector.TryBuildNativeGizmoValue(
                    target,
                    propertyKey,
                    relative.x,
                    relative.y,
                    out EditorPropertyValue value))
                return false;

            return ObjectInspectorRegistry.TrySetValue(target, propertyKey, value);
        }

        if (target.data is WaterCurrent.WaterCurrentData water &&
            ApplyWaterCurrent(target, water, command))
            return true;

        if (ModManager.Watcher &&
            target.data is Watcher.FlameJet.FlameJetData flameJet &&
            command.HandleId == "flameJet:target")
        {
            flameJet.target = new Vector2(command.X, command.Y) - target.pos;
            return true;
        }

        if (ModManager.Watcher &&
            target.data is Watcher.KarmaFlowerPatch.KarmaFlowerPatchData karmaPatch &&
            command.HandleId == "karmaPatch:tilt")
        {
            Vector2 relative = new Vector2(command.X, command.Y) - target.pos;
            float sqrMagnitude = karmaPatch.handlePos.sqrMagnitude;
            karmaPatch.tilt = sqrMagnitude <= 0.0001f
                ? 0f
                : Mathf.Clamp01(Vector2.Dot(relative, karmaPatch.handlePos / sqrMagnitude));
            return true;
        }

        if (target.data is PlacedObject.LightningMachineData lightning)
        {
            if (command.HandleId == "lightning:start")
            {
                lightning.startPoint = new Vector2(command.X, command.Y) - target.pos;
                return true;
            }

            if (command.HandleId == "lightning:end")
            {
                lightning.endPoint = new Vector2(command.X, command.Y) - target.pos;
                return true;
            }
        }

        if (ModManager.Watcher &&
            target.type == WatcherEnums.PlacedObjectType.WeaverSpot &&
            target.data is PlacedObject.ResizableObjectData weaver &&
            command.HandleId == "weaver:direction")
        {
            Vector2 direction = new Vector2(command.X, command.Y) - target.pos;
            if (direction.sqrMagnitude <= 0.0001f)
                direction = Vector2.up;
            weaver.handlePos = direction.normalized * 460f;
            return true;
        }

        if (ModManager.Watcher &&
            target.data is LobeTree.LobeTreeData lobeTree &&
            command.HandleId == "lobeTree:root")
        {
            lobeTree.rootOffset = new Vector2(command.X, command.Y) - target.pos;
            return true;
        }

        if (ModManager.Watcher &&
            target.data is Watcher.UrbanCandlePlacer.UrbanCandlePlacerData candlePlacer)
        {
            Vector2 world = new Vector2(command.X, command.Y);
            switch (command.HandleId)
            {
                case "urbanCandle:brush":
                    candlePlacer.brushHandlePos = world;
                    return true;
                case "urbanCandle:radius":
                    candlePlacer.handlePos = world - candlePlacer.brushHandlePos;
                    candlePlacer.radius = candlePlacer.handlePos.magnitude;
                    return true;
            }
        }

        if (ModManager.Watcher &&
            target.data is Watcher.UrbanLife.UrbanLifeData urbanLife)
        {
            Vector2 relative = new Vector2(command.X, command.Y) - target.pos;
            switch (command.HandleId)
            {
                case "urbanLife:upLeft":
                    urbanLife.upLeft = relative;
                    return true;
                case "urbanLife:downRight":
                    urbanLife.downRight = relative;
                    return true;
                case "urbanLife:direction":
                    urbanLife.direction = relative;
                    return true;
            }
        }

        if (ModManager.Watcher &&
            target.data is Watcher.UrbanLifePath.UrbanLifePathData urbanPath)
        {
            Vector2 relative = new Vector2(command.X, command.Y) - target.pos;
            switch (command.HandleId)
            {
                case "urbanPath:pointA":
                    urbanPath.pointA = relative;
                    return true;
                case "urbanPath:pointB":
                    urbanPath.pointB = relative;
                    return true;
            }
        }

        if (target.data is WaterCutoffData waterCutoff &&
            command.HandleId == "waterCutoff:end")
        {
            Vector2 relative = new Vector2(command.X, command.Y) - target.pos;
            if (!command.Snap)
                relative.y = 0f;
            waterCutoff.handlePos = relative;
            return true;
        }

        if (target.data is AirPocketData airPocket)
        {
            if (command.HandleId == "airPocket:corner")
            {
                airPocket.handlePos = new Vector2(command.X, command.Y) - target.pos;
                return true;
            }

            if (command.HandleId == "airPocket:waterLevel")
            {
                airPocket.waterLevel = command.Y - target.pos.y;
                return true;
            }
        }

        if (target.data is MudPit.MudPitData mudPit &&
            command.HandleId == "mudPit:decalSize")
        {
            mudPit.decalSize = command.X - target.pos.x;
            return true;
        }

        if (target.data is PlacedObject.LocalTerrainData localTerrain &&
            command.HandleId == "localTerrain:bottom")
        {
            localTerrain.bottom = Mathf.Max(0f, target.pos.y - command.Y);
            return true;
        }

        if (target.data is PlacedObject.SuperSlopeData superSlope &&
            command.HandleId == "superSlope:bottom")
        {
            superSlope.bottom = Mathf.Max(10f, target.pos.y - command.Y);
            return true;
        }

        if (target.data is PlacedObject.WaterFlowData waterFlow &&
            command.HandleId == "waterFlow:width")
        {
            waterFlow.width = Mathf.Max(
                Mathf.RoundToInt((command.X - target.pos.x) / 20f),
                1);
            return true;
        }

        if (target.data is PlacedObject.SplineObjectData splineData &&
            splineData.spline != null &&
            ApplySpline(target, splineData.spline, command))
            return true;

        return false;
    }

    private static bool ApplyWaterCurrent(
        PlacedObject target,
        WaterCurrent.WaterCurrentData data,
        NativeObjectGizmoEditCommand command)
    {
        Vector2 relative = new Vector2(command.X, command.Y) - target.pos;

        switch (command.HandleId)
        {
            case "water:end":
                if (command.Snap)
                    relative = SnapEightDirections(relative);
                data.handlePos = relative;
                return true;

            case "water:width":
                data.width = relative.magnitude * 2f;
                return true;

            case "water:velocity":
            {
                Vector2 direction = data.handlePos.sqrMagnitude > 0.0001f
                    ? data.handlePos.normalized
                    : Vector2.up;
                data.velocity = Vector2.Dot(relative - data.handlePos * 0.5f, direction);
                return true;
            }

            default:
                return false;
        }
    }

    private static bool ApplySpline(
        PlacedObject target,
        BezierSpline spline,
        NativeObjectGizmoEditCommand command)
    {
        Vector2 relative = new Vector2(command.X, command.Y) - target.pos;
        int lastSegment = spline.Segments - 1;

        if (command.HandleId == "spline:posB")
        {
            BezierCurve curve = spline.GetBezier(lastSegment);
            Vector2 tangent = curve.handleB - curve.posB;
            curve.posB = relative;
            curve.handleB = relative + tangent;
            spline.SetBezier(lastSegment, curve);
            return true;
        }

        if (command.HandleId == "spline:handleA")
        {
            BezierCurve curve = spline.GetBezier(0);
            curve.handleA = relative;
            spline.SetBezier(0, curve);
            return true;
        }

        if (command.HandleId == "spline:handleB")
        {
            BezierCurve curve = spline.GetBezier(lastSegment);
            curve.handleB = relative;
            spline.SetBezier(lastSegment, curve);
            return true;
        }

        if (!TryParseMidpointHandle(command.HandleId, out int midpointIndex, out string part) ||
            midpointIndex < 0 ||
            midpointIndex >= spline.midpoints.Count)
            return false;

        BezierSpline.Midpoint midpoint = spline.midpoints[midpointIndex];
        switch (part)
        {
            case "pos":
                midpoint.pos = relative;
                break;
            case "dir1":
                midpoint.dir1 = relative - midpoint.pos;
                break;
            case "dir2":
                midpoint.dir2 = relative - midpoint.pos;
                break;
            default:
                return false;
        }

        spline.midpoints[midpointIndex] = midpoint;
        InvalidateMidpointSegments(spline, midpointIndex);
        return true;
    }

    private static bool SplitSpline(PlacedObject target, int segmentIndex, float curveT)
    {
        if (target?.data is not PlacedObject.SplineObjectData data ||
            data.spline == null ||
            segmentIndex < 0 ||
            segmentIndex >= data.spline.Segments)
            return false;

        BezierSpline spline = data.spline;
        float t = Mathf.Clamp01(curveT);
        spline.GetBezier(segmentIndex).Split(t, out BezierCurve first, out BezierCurve second);
        spline.AddMidpoint(default, segmentIndex);
        spline.SetBezier(segmentIndex, first);
        spline.SetBezier(segmentIndex + 1, second);
        return true;
    }

    private static bool RemoveSplineMidpoint(PlacedObject target, string handleId)
    {
        if (target?.data is not PlacedObject.SplineObjectData data ||
            data.spline == null ||
            !TryParseMidpointHandle(handleId, out int midpointIndex, out string part) ||
            part != "pos" ||
            midpointIndex < 0 ||
            midpointIndex >= data.spline.midpoints.Count)
            return false;

        BezierSpline spline = data.spline;
        float leftLength = Mathf.Max(0.001f, spline.GetSegmentLength(midpointIndex));
        float rightLength = Mathf.Max(0.001f, spline.GetSegmentLength(midpointIndex + 1));
        float totalLength = leftLength + rightLength;

        if (midpointIndex == 0)
        {
            spline.handleA = spline.posA +
                             (spline.handleA - spline.posA) * (totalLength / leftLength);
        }
        else
        {
            BezierSpline.Midpoint previous = spline.midpoints[midpointIndex - 1];
            previous.dir2 *= Mathf.Min(totalLength / leftLength, 2f);
            spline.midpoints[midpointIndex - 1] = previous;
        }

        if (midpointIndex == spline.midpoints.Count - 1)
        {
            spline.handleB = spline.posB +
                             (spline.handleB - spline.posB) * (totalLength / rightLength);
        }
        else
        {
            BezierSpline.Midpoint next = spline.midpoints[midpointIndex + 1];
            next.dir1 *= Mathf.Min(totalLength / rightLength, 2f);
            spline.midpoints[midpointIndex + 1] = next;
        }

        spline.RemoveMidpoint(midpointIndex);
        if (midpointIndex < spline.Segments)
            spline.SetBezier(midpointIndex, spline.GetBezier(midpointIndex));
        return true;
    }

    private static void ExecuteDiscrete(
        EditorSession session,
        PlacedObject target,
        string label,
        Func<bool> mutation)
    {
        IEditorStateSnapshot before =
            SinglePlacedObjectStateSnapshot.Capture(session.RoomSettings, target);
        if (before == null || mutation == null || !mutation())
            return;

        MarkChanged(session, target);

        IEditorStateSnapshot after = before.CaptureCurrent(session);
        if (SnapshotHistoryEntry.TryCreate(label, before, after, out SnapshotHistoryEntry entry))
            session.History.Push(entry);
    }

    private static void InvalidateMidpointSegments(BezierSpline spline, int midpointIndex)
    {
        if (midpointIndex >= 0 && midpointIndex < spline.Segments)
            spline.SetBezier(midpointIndex, spline.GetBezier(midpointIndex));
        if (midpointIndex + 1 >= 0 && midpointIndex + 1 < spline.Segments)
            spline.SetBezier(midpointIndex + 1, spline.GetBezier(midpointIndex + 1));
    }

    private static Vector2 SnapEightDirections(Vector2 value)
    {
        float absX = Mathf.Abs(value.x);
        float absY = Mathf.Abs(value.y);

        if (absX * 0.4142f < absY && absY * 0.4142f < absX)
        {
            Vector2 diagonal = new Vector2(Mathf.Sign(value.x), Mathf.Sign(value.y)).normalized;
            return diagonal * Vector2.Dot(diagonal, value);
        }

        if (absX < absY)
            value.x = 0f;
        else
            value.y = 0f;

        return value;
    }

    private static bool TryParseMidpointHandle(
        string handleId,
        out int midpointIndex,
        out string part)
    {
        midpointIndex = -1;
        part = string.Empty;
        if (string.IsNullOrEmpty(handleId) ||
            !handleId.StartsWith("spline:mid:", StringComparison.Ordinal))
            return false;

        string[] pieces = handleId.Split(':');
        return pieces.Length == 4 &&
               int.TryParse(pieces[2], out midpointIndex) &&
               !string.IsNullOrEmpty(part = pieces[3]);
    }

    private static void MarkChanged(EditorSession session, PlacedObject target)
    {
        NativeObjectRuntimeReconciler.RefreshAfterMutation(session, target);
        EditorRevisionHub.Mark(session, EditorRevisionKind.Objects);
        ObjectPresentationChangeHintHub.MarkMember(session, target);
    }

    private static PlacedObject ObjectAt(EditorSession session, int index) =>
        session?.RoomSettings?.placedObjects != null &&
        index >= 0 &&
        index < session.RoomSettings.placedObjects.Count
            ? session.RoomSettings.placedObjects[index]
            : null;

    private static string TransactionKey(int objectIndex, string handleId) =>
        "NativeObjectGizmo:" + objectIndex + ":" + handleId;
}
