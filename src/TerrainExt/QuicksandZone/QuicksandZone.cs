using RWCustom;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

internal sealed class QuicksandZone : TerrainCurve, TerrainManager.ITerrain
{
    private readonly PlacedObject _placedObject;
    private float[] _materialUAtSample = new float[0];

    internal PlacedObject PlacedObject => _placedObject;
    internal QuicksandZoneData Data => _placedObject?.data as QuicksandZoneData;

    internal QuicksandZone(Room room, PlacedObject placedObject)
        : base(room)
    {
        _placedObject = placedObject;
        RefreshCurve();
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
        float newStartX = curves[0].posA.x;
        float newEndX = curves[curves.Length - 1].posB.x;

        if (newEndX <= newStartX + 1f)
        {
            Plugin.Logger?.LogWarning("QuicksandZone SurfaceSpline must run from left to right.");
            return;
        }

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
            while (curveIndex < curves.Length - 1 && curves[curveIndex].posB.x < x)
            {
                curveIndex++;
            }

            BezierCurve frontCurve = curves[curveIndex];
            float frontY = Custom.BezierYatX(frontCurve, x);
            frontPoints[i] = new Vector2(
                x,
                TerrainCurve.Normalize(frontY, -10000f));

            BezierCurve backCurve = frontCurve;
            backCurve.posA.y += 50f;
            backCurve.posB.y += 50f;
            backPoints[i] = new Vector2(
                x,
                TerrainCurve.Normalize(Custom.BezierYatX(backCurve, x), -10000f));

            Vector2 localFront = frontPoints[i] - origin;
            _materialUAtSample[i] = QuicksandSurface.FindNearestU(
                Data.SurfaceSpline,
                localFront,
                out _);
        }

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
        if (Data == null || IsQuicksandAtWorldX(center.x))
        {
            normal = Vector2.zero;
            return center;
        }

        return base.SnapToTerrain(center, radius, out normal, lastCenter);
    }

    bool TerrainManager.ITerrain.ObstructsTile(int x, int y)
    {
        float tileCenterX = x * 20f + 10f;
        if (Data == null || IsQuicksandAtWorldX(tileCenterX))
        {
            return false;
        }

        return base.ObstructsTile(x, y);
    }

    float TerrainManager.ITerrain.GetCoverage(int x, int y)
    {
        float tileCenterX = x * 20f + 10f;
        if (Data == null || IsQuicksandAtWorldX(tileCenterX))
        {
            return 0f;
        }

        return base.GetCoverage(x, y);
    }

    private static Vector2 SafeNormal(Vector2 value, Vector2 fallback)
    {
        return value.sqrMagnitude > 0.0001f ? value.normalized : fallback;
    }
}
