using System;
using System.Collections.Generic;
using RWCustom;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Objects;

/// <summary>
/// Detached scene-space gizmo primitives for the selected object. The frontend consumes only world
/// coordinates, stable handle IDs and cubic Bezier segments; it never knows PlacedObject/Data types.
/// </summary>
public sealed class EditorObjectGizmoHandleSnapshot
{
    public string Id { get; init; } = string.Empty;
    public float X { get; init; }
    public float Y { get; init; }
    public float AnchorX { get; init; }
    public float AnchorY { get; init; }
    public bool DrawAnchorLine { get; init; }
    public bool Removable { get; init; }
}

public sealed class EditorObjectLineSegmentSnapshot
{
    public float X0 { get; init; }
    public float Y0 { get; init; }
    public float X1 { get; init; }
    public float Y1 { get; init; }
}

public sealed class EditorObjectBezierSegmentSnapshot
{
    public int SegmentIndex { get; init; }
    public float X0 { get; init; }
    public float Y0 { get; init; }
    public float C0X { get; init; }
    public float C0Y { get; init; }
    public float X1 { get; init; }
    public float Y1 { get; init; }
    public float C1X { get; init; }
    public float C1Y { get; init; }
}

public sealed class EditorObjectGizmoSnapshot
{
    public static readonly EditorObjectGizmoSnapshot Empty = new();

    public int ObjectIndex { get; init; } = -1;
    public EditorObjectGizmoHandleSnapshot[] Handles { get; init; } = Array.Empty<EditorObjectGizmoHandleSnapshot>();
    public EditorObjectLineSegmentSnapshot[] Lines { get; init; } = Array.Empty<EditorObjectLineSegmentSnapshot>();
    public EditorObjectBezierSegmentSnapshot[] BezierSegments { get; init; } = Array.Empty<EditorObjectBezierSegmentSnapshot>();
}

internal static class NativeObjectGizmoPresentation
{
    internal static EditorObjectGizmoSnapshot Capture(
        PlacedObject target,
        int objectIndex,
        EditorPropertySnapshot[] properties)
    {
        if (target == null || objectIndex < 0)
            return EditorObjectGizmoSnapshot.Empty;

        List<EditorObjectGizmoHandleSnapshot> handles = new();
        List<EditorObjectLineSegmentSnapshot> lines = new();
        List<EditorObjectBezierSegmentSnapshot> beziers = new();

        bool specialized = false;
        if (target.data is WaterCurrent.WaterCurrentData water)
        {
            CaptureWaterCurrent(target, water, handles);
            specialized = true;
        }
        else if (target.data is WaterCutoffData waterCutoff)
        {
            handles.Add(Handle(
                "waterCutoff:end",
                target.pos + waterCutoff.handlePos,
                target.pos,
                drawLine: true));
            specialized = true;
        }
        else if (target.data is AirPocketData airPocket)
        {
            Vector2 corner = target.pos + airPocket.handlePos;
            handles.Add(Handle(
                "airPocket:corner",
                corner,
                target.pos,
                drawLine: false));
            handles.Add(Handle(
                "airPocket:waterLevel",
                target.pos + new Vector2(0f, airPocket.waterLevel),
                target.pos,
                drawLine: false));
            AddRectangle(lines, target.pos, corner);
            AddLine(
                lines,
                target.pos + new Vector2(0f, airPocket.waterLevel),
                target.pos + new Vector2(airPocket.handlePos.x, airPocket.waterLevel));
            specialized = true;
        }
        else if (ModManager.Watcher &&
                 target.data is Watcher.FlameJet.FlameJetData flameJet)
        {
            handles.Add(Handle(
                "flameJet:target",
                target.pos + flameJet.target,
                target.pos,
                drawLine: true));
            specialized = true;
        }
        else if (target.data is PlacedObject.LightningMachineData lightning)
        {
            handles.Add(Handle(
                "lightning:start",
                target.pos + lightning.startPoint,
                target.pos,
                drawLine: true));
            handles.Add(Handle(
                "lightning:end",
                target.pos + lightning.endPoint,
                target.pos,
                drawLine: true));
            specialized = true;
        }
        else if (ModManager.Watcher &&
                 target.type == WatcherEnums.PlacedObjectType.WeaverSpot &&
                 target.data is PlacedObject.ResizableObjectData weaver)
        {
            Vector2 direction = weaver.handlePos.sqrMagnitude > 0.0001f
                ? weaver.handlePos.normalized * 460f
                : Vector2.up * 460f;
            handles.Add(Handle(
                "weaver:direction",
                target.pos + direction,
                target.pos,
                drawLine: true));
            specialized = true;
        }
        else if (ModManager.Watcher &&
                 target.data is Watcher.UrbanCandlePlacer.UrbanCandlePlacerData candlePlacer)
        {
            Vector2 brush = candlePlacer.brushHandlePos;
            Vector2 radiusHandle = brush + candlePlacer.handlePos;
            handles.Add(Handle("urbanCandle:brush", brush, target.pos, drawLine: true));
            handles.Add(Handle("urbanCandle:radius", radiusHandle, brush, drawLine: true));
            specialized = true;
        }
        else if (ModManager.Watcher &&
                 target.data is Watcher.UrbanLife.UrbanLifeData urbanLife)
        {
            Vector2 upLeft = target.pos + urbanLife.upLeft;
            Vector2 downRight = target.pos + urbanLife.downRight;
            handles.Add(Handle("urbanLife:upLeft", upLeft, target.pos, drawLine: false));
            handles.Add(Handle("urbanLife:downRight", downRight, target.pos, drawLine: false));
            handles.Add(Handle(
                "urbanLife:direction",
                target.pos + urbanLife.direction,
                target.pos,
                drawLine: true));
            AddRectangle(lines, upLeft, downRight);
            specialized = true;
        }
        else if (ModManager.Watcher &&
                 target.data is Watcher.UrbanLifePath.UrbanLifePathData urbanPath)
        {
            Vector2 pointA = target.pos + urbanPath.pointA;
            Vector2 pointB = target.pos + urbanPath.pointB;
            handles.Add(Handle("urbanPath:pointA", pointA, target.pos, drawLine: true));
            handles.Add(Handle("urbanPath:pointB", pointB, target.pos, drawLine: true));
            AddLine(lines, pointA, pointB);
            specialized = true;
        }
        else if (target.data is PlacedObject.SplineObjectData splineData && splineData.spline != null)
        {
            CaptureSpline(target, splineData.spline, handles, beziers);
            if (splineData is PlacedObject.LocalTerrainData localTerrain)
            {
                Vector2 bottom = target.pos + new Vector2(0f, 0f - localTerrain.bottom);
                handles.Add(Handle("localTerrain:bottom", bottom, target.pos, drawLine: true));
            }
            specialized = true;
        }

        // Simple verified Data members still use the reflected inspector schema as their semantic
        // source. Complex WaterCurrent/Spline models publish their own complete primitive set above.
        if (!specialized)
            CapturePropertyHandles(target, properties, handles);

        if (ModManager.Watcher &&
            target.data is Watcher.KarmaFlowerPatch.KarmaFlowerPatchData karmaPatch)
        {
            handles.Add(Handle(
                "karmaPatch:tilt",
                target.pos + karmaPatch.handlePos * karmaPatch.tilt,
                target.pos,
                drawLine: true));
        }

        if (ModManager.Watcher && target.data is LobeTree.LobeTreeData lobeTree)
        {
            handles.Add(Handle(
                "lobeTree:root",
                target.pos + lobeTree.rootOffset,
                target.pos,
                drawLine: true));
        }

        if (target.data is PlacedObject.SuperSlopeData superSlope)
        {
            Vector2 bottom = target.pos + new Vector2(0f, 0f - superSlope.bottom);
            handles.Add(Handle("superSlope:bottom", bottom, target.pos, drawLine: true));
        }

        if (target.data is PlacedObject.WaterFlowData waterFlow)
        {
            Vector2 width = target.pos + new Vector2(waterFlow.width * 20f, 0f);
            handles.Add(Handle("waterFlow:width", width, target.pos, drawLine: true));
        }

        if (target.data is MudPit.MudPitData mudPit)
        {
            Vector2 decal = target.pos + new Vector2(mudPit.decalSize, 0f);
            handles.Add(Handle("mudPit:decalSize", decal, target.pos, drawLine: true));
            AddRectangle(lines, target.pos, target.pos + mudPit.handlePos);
        }

        if (handles.Count == 0 && lines.Count == 0 && beziers.Count == 0)
            return EditorObjectGizmoSnapshot.Empty;

        return new EditorObjectGizmoSnapshot
        {
            ObjectIndex = objectIndex,
            Handles = handles.ToArray(),
            Lines = lines.ToArray(),
            BezierSegments = beziers.ToArray()
        };
    }

    private static void CapturePropertyHandles(
        PlacedObject target,
        EditorPropertySnapshot[] properties,
        List<EditorObjectGizmoHandleSnapshot> handles)
    {
        if (properties == null) return;

        for (int i = 0; i < properties.Length; i++)
        {
            EditorPropertySnapshot property = properties[i];
            if (property == null || property.GizmoHint == EditorPropertyGizmoHint.None)
                continue;

            float x;
            float y;
            switch (property.GizmoHint)
            {
                case EditorPropertyGizmoHint.RelativePoint:
                    x = target.pos.x + property.X;
                    y = target.pos.y + property.Y;
                    break;
                case EditorPropertyGizmoHint.VerticalDistance:
                    x = target.pos.x;
                    y = target.pos.y + property.X;
                    break;
                default:
                    continue;
            }

            handles.Add(new EditorObjectGizmoHandleSnapshot
            {
                Id = "property:" + property.Key,
                X = x,
                Y = y,
                AnchorX = target.pos.x,
                AnchorY = target.pos.y,
                DrawAnchorLine = true
            });
        }
    }

    private static void CaptureWaterCurrent(
        PlacedObject target,
        WaterCurrent.WaterCurrentData data,
        List<EditorObjectGizmoHandleSnapshot> handles)
    {
        Vector2 origin = target.pos;
        Vector2 end = origin + data.handlePos;
        Vector2 normal = data.handlePos.sqrMagnitude > 0.0001f
            ? data.handlePos.normalized
            : Vector2.up;
        Vector2 perpendicular = Custom.PerpendicularVector(data.handlePos);
        if (perpendicular.sqrMagnitude < 0.0001f)
            perpendicular = Vector2.right;

        Vector2 width = origin + perpendicular.normalized * (data.width * 0.5f);
        Vector2 velocityAnchor = origin + data.handlePos * 0.5f;
        Vector2 velocity = velocityAnchor + normal * data.velocity;

        handles.Add(Handle("water:end", end, origin, drawLine: true));
        handles.Add(Handle("water:width", width, origin, drawLine: true));
        handles.Add(Handle("water:velocity", velocity, velocityAnchor, drawLine: true));
    }

    private static void CaptureSpline(
        PlacedObject target,
        BezierSpline spline,
        List<EditorObjectGizmoHandleSnapshot> handles,
        List<EditorObjectBezierSegmentSnapshot> beziers)
    {
        Vector2 origin = target.pos;

        handles.Add(Handle("spline:posB", origin + spline.posB, origin, drawLine: false));
        handles.Add(Handle("spline:handleA", origin + spline.handleA, origin + spline.posA, drawLine: true));
        handles.Add(Handle("spline:handleB", origin + spline.handleB, origin + spline.posB, drawLine: true));

        for (int i = 0; i < spline.midpoints.Count; i++)
        {
            BezierSpline.Midpoint midpoint = spline.midpoints[i];
            Vector2 point = origin + midpoint.pos;
            handles.Add(new EditorObjectGizmoHandleSnapshot
            {
                Id = "spline:mid:" + i + ":pos",
                X = point.x,
                Y = point.y,
                Removable = true
            });
            handles.Add(Handle("spline:mid:" + i + ":dir1", point + midpoint.dir1, point, drawLine: true));
            handles.Add(Handle("spline:mid:" + i + ":dir2", point + midpoint.dir2, point, drawLine: true));
        }

        for (int segment = 0; segment < spline.Segments; segment++)
        {
            BezierCurve curve = spline.GetBezier(segment);
            beziers.Add(new EditorObjectBezierSegmentSnapshot
            {
                SegmentIndex = segment,
                X0 = origin.x + curve.posA.x,
                Y0 = origin.y + curve.posA.y,
                C0X = origin.x + curve.handleA.x,
                C0Y = origin.y + curve.handleA.y,
                X1 = origin.x + curve.posB.x,
                Y1 = origin.y + curve.posB.y,
                C1X = origin.x + curve.handleB.x,
                C1Y = origin.y + curve.handleB.y
            });
        }
    }

    private static void AddRectangle(
        List<EditorObjectLineSegmentSnapshot> lines,
        Vector2 a,
        Vector2 b)
    {
        Vector2 c = new Vector2(a.x, b.y);
        Vector2 d = new Vector2(b.x, a.y);
        AddLine(lines, a, c);
        AddLine(lines, c, b);
        AddLine(lines, b, d);
        AddLine(lines, d, a);
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

    private static EditorObjectGizmoHandleSnapshot Handle(
        string id,
        Vector2 point,
        Vector2 anchor,
        bool drawLine) =>
        new()
        {
            Id = id,
            X = point.x,
            Y = point.y,
            AnchorX = anchor.x,
            AnchorY = anchor.y,
            DrawAnchorLine = drawLine
        };
}
