using RWCustom;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

internal sealed class QuicksandZone : TerrainCurve, TerrainManager.ITerrain
{
    private const float RoomEdgeSealThreshold = 8f;
    private const float RoomEdgeSealPadding = 400f;

    private readonly PlacedObject _placedObject;
    private float[] _materialUAtSample = new float[0];
    private float _authoredStartX;
    private float _authoredEndX;

    internal PlacedObject PlacedObject => _placedObject;
    internal QuicksandZoneData Data => _placedObject?.data as QuicksandZoneData;

    internal QuicksandZone(Room room, PlacedObject placedObject)
        : base(room)
    {
        _placedObject = placedObject;
        QuicksandDrillCrabCompatibility.EnsureEnabled();
        RefreshCurve();
    }

    public override void Update(bool eu)
    {
        base.Update(eu);

        // LocalTerrainCurve is explicitly excluded from TerrainCurve.UpdateHandles(),
        // because its spline owns its geometry. QuicksandZone is also spline-owned,
        // but inherits TerrainCurve directly so vanilla TerrainHandle edits can
        // temporarily replace its points. Detect that takeover and restore our spline.
        // All actual TerrainCurve rendering settings (Depth, Stain, Grain, Waves,
        // Edge Radius, Sky Fade, terrain palette, lighting, etc.) remain vanilla.
        if (NeedsSplineGeometryRestore())
        {
            RefreshCurve();
        }
    }

    internal void RefreshCurve()
    {
        if (_placedObject == null || Data?.SurfaceSpline == null)
        {
            return;
        }

        Vector2 origin = _placedObject.pos;
        BezierCurve[] curves = Data.SurfaceSpline.GetAllBeziers();
        if (curves == null || curves.Length == 0)
        {
            return;
        }

        for (int i = 0; i < curves.Length; i++)
        {
            curves[i] += origin;
        }

        float newBottom = origin.y - Data.BottomDepth;
        _authoredStartX = curves[0].posA.x;
        _authoredEndX = curves[curves.Length - 1].posB.x;

        if (_authoredEndX <= _authoredStartX + 1f)
        {
            Plugin.Logger?.LogWarning("QuicksandZone SurfaceSpline must run from left to right.");
            return;
        }

        // TerrainCurve's global room mesh extends 400 px beyond either room edge.
        // A local curve ending exactly on a room edge otherwise exposes a diagonal
        // parallax wedge when minDepth/maxDepth separate its front and back surfaces.
        // Only seal endpoints that are actually authored against the room boundary;
        // ordinary local endpoints keep their exact authored span.
        float newStartX = _authoredStartX <= RoomEdgeSealThreshold
            ? -RoomEdgeSealPadding
            : _authoredStartX;
        float newEndX = _authoredEndX >= room.PixelWidth - RoomEdgeSealThreshold
            ? room.PixelWidth + RoomEdgeSealPadding
            : _authoredEndX;

        if (Mathf.Abs(newStartX - startX) > 0.001f ||
            Mathf.Abs(newEndX - endX) > 0.001f ||
            Mathf.Abs(newBottom - bottom) > 0.001f)
        {
            UpdateSize(newStartX, newEndX, newBottom);
        }
        else
        {
            bottom = newBottom;
        }

        if (_materialUAtSample == null || _materialUAtSample.Length != segments)
        {
            _materialUAtSample = new float[segments];
        }

        int curveIndex = 0;
        for (int i = 0; i < segments; i++)
        {
            float x = Mathf.Lerp(startX, endX, (float)i / Mathf.Max(1, segments - 1));
            float authoredX = Mathf.Clamp(x, _authoredStartX, _authoredEndX);

            while (curveIndex < curves.Length - 1 && curves[curveIndex].posB.x < authoredX)
            {
                curveIndex++;
            }

            BezierCurve frontCurve = curves[curveIndex];
            float frontY = Custom.BezierYatX(frontCurve, authoredX);
            frontPoints[i] = new Vector2(
                x,
                TerrainCurve.Normalize(frontY, -10000f));

            BezierCurve backCurve = frontCurve;
            backCurve.posA.y += 50f;
            backCurve.posB.y += 50f;
            backPoints[i] = new Vector2(
                x,
                TerrainCurve.Normalize(Custom.BezierYatX(backCurve, authoredX), -10000f));

            if (x <= _authoredStartX)
            {
                _materialUAtSample[i] = 0f;
            }
            else if (x >= _authoredEndX)
            {
                _materialUAtSample[i] = 1f;
            }
            else
            {
                Vector2 localFront = new Vector2(authoredX, frontPoints[i].y) - origin;
                _materialUAtSample[i] = QuicksandSurface.FindNearestU(
                    Data.SurfaceSpline,
                    localFront,
                    out _);
            }
        }

        // TerrainCurve.DrawSprites hides non-LocalTerrainCurve instances when the
        // room-handle list has fewer than two entries. Our spline supplies geometry
        // directly, so keep two internal handles solely for that native visibility
        // guard; frontPoints/backPoints above remain authoritative.
        handles.Clear();
        handles.Add(new TerrainCurve.Handle(
            frontPoints[0],
            frontPoints[0],
            frontPoints[0],
            50f));
        handles.Add(new TerrainCurve.Handle(
            frontPoints[segments - 1],
            frontPoints[segments - 1],
            frontPoints[segments - 1],
            50f));

        UpdateCollision();
        maskSource?.SetVertices(frontPoints, backPoints, newBottom);
    }

    internal bool IsQuicksandAtWorldX(float worldX)
    {
        return Data != null && Data.IsQuicksand(MaterialUAtWorldX(worldX));
    }

    internal float MaterialUAtWorldX(float worldX)
    {
        if (_materialUAtSample == null || _materialUAtSample.Length < 2 || segments < 2)
        {
            return Mathf.InverseLerp(startX, endX, worldX);
        }

        float raw = (worldX - startX) / Mathf.Max(0.001f, segmentWidth);
        int segment = Mathf.Clamp(Mathf.FloorToInt(raw), 0, _materialUAtSample.Length - 2);
        float t = Mathf.Clamp01(raw - segment);
        return Mathf.Lerp(_materialUAtSample[segment], _materialUAtSample[segment + 1], t);
    }

    internal bool TrySampleSurfaceFrame(
        float u,
        out Vector2 surfacePoint,
        out Vector2 tangent,
        out Vector2 inward,
        out float depthLength)
    {
        surfacePoint = Vector2.zero;
        tangent = Vector2.right;
        inward = Vector2.down;
        depthLength = 0f;

        if (_placedObject == null || Data?.SurfaceSpline == null)
        {
            return false;
        }

        u = Mathf.Clamp01(u);
        const float deltaU = 0.0035f;
        Vector2 local = QuicksandSurface.EvaluateByApproximateLength(Data.SurfaceSpline, u);
        Vector2 localBefore = QuicksandSurface.EvaluateByApproximateLength(
            Data.SurfaceSpline,
            Mathf.Max(0f, u - deltaU));
        Vector2 localAfter = QuicksandSurface.EvaluateByApproximateLength(
            Data.SurfaceSpline,
            Mathf.Min(1f, u + deltaU));

        surfacePoint = _placedObject.pos + local;
        tangent = SafeNormal(localAfter - localBefore, Vector2.right);
        inward = SafeNormal(new Vector2(tangent.y, -tangent.x), Vector2.down);
        if (inward.y > 0f)
        {
            inward = -inward;
        }

        float bottomY = _placedObject.pos.y - Data.BottomDepth;
        Vector2 bottomPoint = new(surfacePoint.x, bottomY);
        if (Vector2.Dot(inward, bottomPoint - surfacePoint) < 0f)
        {
            inward = -inward;
        }

        depthLength = Mathf.Max(4f, Vector2.Dot(bottomPoint - surfacePoint, inward));
        return true;
    }

    internal float EstimateSurfaceLength()
    {
        return Mathf.Max(1f, Data?.SurfaceSpline?.GetFullLength ?? 1f);
    }

    public override TerrainCurveMaskSource CreateMaskSource()
    {
        return new TerrainCurveMaskSource(frontPoints, backPoints, minDepth, maxDepth, bottom);
    }

    public override void Destroy()
    {
        if (room?.terrain?.terrainList != null)
        {
            room.terrain.terrainList.Remove(this);
        }

        base.Destroy();
    }

    bool TerrainManager.ITerrain.BurrowAllowed => true;

    Vector2 TerrainManager.ITerrain.SnapToTerrain(
        Vector2 center,
        float radius,
        out Vector2 normal,
        Vector2? lastCenter)
    {
        if (Data == null ||
            (!QuicksandDrillCrabCompatibility.TreatQuicksandAsSolidTerrain &&
             IsQuicksandAtWorldX(center.x)))
        {
            normal = Vector2.zero;
            return center;
        }

        return base.SnapToTerrain(center, radius, out normal, lastCenter);
    }

    bool TerrainManager.ITerrain.ObstructsTile(int x, int y)
    {
        float tileCenterX = x * 20f + 10f;
        if (Data == null ||
            (!QuicksandDrillCrabCompatibility.TreatQuicksandAsSolidTerrain &&
             IsQuicksandAtWorldX(tileCenterX)))
        {
            return false;
        }

        return base.ObstructsTile(x, y);
    }

    float TerrainManager.ITerrain.GetCoverage(int x, int y)
    {
        float tileCenterX = x * 20f + 10f;
        if (Data == null ||
            (!QuicksandDrillCrabCompatibility.TreatQuicksandAsSolidTerrain &&
             IsQuicksandAtWorldX(tileCenterX)))
        {
            return 0f;
        }

        return base.GetCoverage(x, y);
    }

    private bool NeedsSplineGeometryRestore()
    {
        if (_placedObject == null || Data?.SurfaceSpline == null)
        {
            return false;
        }

        if (handles == null || handles.Count != 2)
        {
            return true;
        }

        Vector2 expectedStart = new(
            _authoredStartX <= RoomEdgeSealThreshold ? -RoomEdgeSealPadding : _authoredStartX,
            frontPoints != null && frontPoints.Length > 0 ? frontPoints[0].y : 0f);
        Vector2 expectedEnd = new(
            _authoredEndX >= room.PixelWidth - RoomEdgeSealThreshold
                ? room.PixelWidth + RoomEdgeSealPadding
                : _authoredEndX,
            frontPoints != null && frontPoints.Length > 0 ? frontPoints[frontPoints.Length - 1].y : 0f);

        return Vector2.SqrMagnitude(handles[0].Middle - expectedStart) > 0.0001f ||
               Vector2.SqrMagnitude(handles[1].Middle - expectedEnd) > 0.0001f;
    }

    private static Vector2 SafeNormal(Vector2 value, Vector2 fallback)
    {
        return value.sqrMagnitude > 0.0001f ? value.normalized : fallback;
    }
}
