using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.World;
using UnityEngine;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Persistent GPU presentation for world-space connection routes.
///
/// Route meshes rebuild only when a route revision changes. Pan/zoom changes only the off-screen
/// camera and route visibility; no route geometry is regenerated.
/// </summary>
internal sealed class WorldMapRetainedConnectionRenderer
{
    private sealed class RouteObject
    {
        internal GameObject Root;
        internal MeshFilter Filter;
        internal MeshRenderer Renderer;
        internal long Revision = long.MinValue;
    }

    private const float CoreHalfWidth = 1.20f;
    private const float ShadowHalfWidth = 2.85f;
    private const float DashLength = 10f;
    private const float DashGap = 6f;

    // Direction is metadata, not the dominant shape of a route. Small chevrons preserve flow
    // readability without covering lane spacing, port numbers or neighbouring connections.
    private const float DirectionMarkerLength = 6.5f;
    private const float DirectionMarkerSpread = 3.4f;
    private const float DirectionMarkerCoreHalfWidth = 0.78f;
    private const float DirectionMarkerShadowHalfWidth = 1.55f;
    private const float MinimumDirectionMarkerRun = 20f;

    private const float CrossingRadius = 6.4f;
    private const float CrossingRise = 4.8f;
    private const float DenseCrossingRadius = 5.2f;
    private const float DenseCrossingRise = 3.7f;

    private readonly Dictionary<string, RouteObject> routeObjects =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> visibleNow =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> visiblePrevious =
        new(StringComparer.Ordinal);
    private readonly List<string> stale = new();

    private GameObject root;
    private Material material;
    private Shader shader;
    private GameObject crossingObject;
    private MeshFilter crossingFilter;
    private MeshRenderer crossingRenderer;
    private long crossingRevision = long.MinValue;
    private readonly HashSet<string> crossingVisibleRouteIds =
        new(StringComparer.Ordinal);
    private bool crossingHasGeometry;

    internal int RetainedRouteCount => routeObjects.Count;

    internal void ApplyDirty(WorldMapDirtySet dirty)
    {
        if (dirty == null) return;

        foreach (string id in dirty.RemovedConnections)
        {
            visibleNow.Remove(id);
            visiblePrevious.Remove(id);
            RemoveRoute(id);
        }
    }

    internal bool SynchronizeVisible(
        WorldMapConnectionResourceStore resources,
        IReadOnlyList<string> visibleRouteIds,
        bool showConnections,
        Transform renderScene)
    {
        if (resources == null || visibleRouteIds == null)
            return false;
        if (!EnsureResources(renderScene)) return false;

        if (!showConnections)
        {
            HideVisible();
            if (crossingObject != null) crossingObject.SetActive(false);
            return true;
        }

        visibleNow.Clear();

        for (int i = 0; i < visibleRouteIds.Count; i++)
        {
            string id = visibleRouteIds[i];
            if (string.IsNullOrEmpty(id) ||
                !resources.TryGet(id, out ConnectionRouteResource route) ||
                route?.Points == null ||
                route.Points.Length < 2)
                continue;

            visibleNow.Add(id);
            RouteObject obj = GetOrCreate(id);
            if (obj.Revision != route.Revision)
            {
                ReplaceMesh(obj.Filter, BuildRouteMesh(route));
                obj.Revision = route.Revision;
            }

            if (!obj.Root.activeSelf)
                obj.Root.SetActive(true);
        }

        foreach (string id in visiblePrevious)
        {
            if (visibleNow.Contains(id)) continue;
            if (routeObjects.TryGetValue(id, out RouteObject obj) && obj.Root.activeSelf)
                obj.Root.SetActive(false);
        }

        visiblePrevious.Clear();
        visiblePrevious.UnionWith(visibleNow);

        stale.Clear();
        foreach (string id in routeObjects.Keys)
            if (!resources.Routes.ContainsKey(id))
                stale.Add(id);
        for (int i = 0; i < stale.Count; i++)
            RemoveRoute(stale[i]);

        if (!resources.CrossingsCurrent)
        {
            if (crossingObject != null)
                crossingObject.SetActive(false);
        }
        else
        {
            bool visibilityChanged =
                !crossingVisibleRouteIds.SetEquals(
                    visibleNow);

            if (crossingRevision != resources.CrossingRevision ||
                visibilityChanged)
            {
                Mesh crossingMesh =
                    BuildCrossingMesh(
                        resources,
                        visibleNow);

                ReplaceMesh(
                    crossingFilter,
                    crossingMesh);

                crossingRevision =
                    resources.CrossingRevision;
                crossingVisibleRouteIds.Clear();
                crossingVisibleRouteIds.UnionWith(
                    visibleNow);
                crossingHasGeometry =
                    crossingMesh != null;
            }

            if (crossingObject != null)
                crossingObject.SetActive(
                    crossingHasGeometry);
        }

        return true;
    }

    internal void Reset()
    {
        foreach (RouteObject route in routeObjects.Values)
            DestroyRouteObject(route);
        routeObjects.Clear();
        visibleNow.Clear();
        visiblePrevious.Clear();
        stale.Clear();

        if (crossingFilter?.sharedMesh != null)
            UnityEngine.Object.Destroy(crossingFilter.sharedMesh);
        if (crossingObject != null)
            UnityEngine.Object.Destroy(crossingObject);
        crossingObject = null;
        crossingFilter = null;
        crossingRenderer = null;
        crossingRevision = long.MinValue;
        crossingVisibleRouteIds.Clear();
        crossingHasGeometry = false;

        if (material != null)
            UnityEngine.Object.Destroy(material);
        material = null;
        shader = null;

        if (root != null)
            UnityEngine.Object.Destroy(root);
        root = null;
    }

    private bool EnsureResources(Transform renderScene)
    {
        if (renderScene == null) throw new ArgumentNullException(nameof(renderScene));
        if (root == null)
        {
            root = new GameObject("DryCycle.WorldMapV2.Connections")
            {
                hideFlags = HideFlags.HideAndDontSave,
                layer = WorldMapRenderTextureSurface.RenderLayer
            };
        }
        if (root.transform.parent != renderScene)
            root.transform.SetParent(renderScene, false);

        if (shader == null)
        {
            shader =
                Shader.Find("Sprites/Default") ??
                Shader.Find("Unlit/Transparent") ??
                Shader.Find("UI/Default");
            if (shader == null) return false;
        }

        if (material == null)
        {
            material = new Material(shader)
            {
                name = "DryCycle.WorldMapV2.Connections",
                hideFlags = HideFlags.HideAndDontSave,
                mainTexture = Texture2D.whiteTexture
            };
        }

        if (crossingObject == null)
        {
            crossingObject = new GameObject("CrossingBridges")
            {
                hideFlags = HideFlags.HideAndDontSave,
                layer = WorldMapRenderTextureSurface.RenderLayer
            };
            crossingObject.transform.SetParent(root.transform, false);
            crossingFilter = crossingObject.AddComponent<MeshFilter>();
            crossingRenderer = crossingObject.AddComponent<MeshRenderer>();
            crossingRenderer.sharedMaterial = material;
        }

        return true;
    }

    private RouteObject GetOrCreate(string id)
    {
        if (routeObjects.TryGetValue(id, out RouteObject existing))
            return existing;

        GameObject obj = new("Route_" + id)
        {
            hideFlags = HideFlags.HideAndDontSave,
            layer = WorldMapRenderTextureSurface.RenderLayer
        };
        obj.transform.SetParent(root.transform, false);
        MeshFilter filter = obj.AddComponent<MeshFilter>();
        MeshRenderer renderer = obj.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;

        RouteObject created = new()
        {
            Root = obj,
            Filter = filter,
            Renderer = renderer
        };
        routeObjects.Add(id, created);
        return created;
    }

    private static Mesh BuildRouteMesh(ConnectionRouteResource route)
    {
        Num.Vector2[] path = route.Points ?? Array.Empty<Num.Vector2>();
        if (path.Length < 2) return null;

        List<Vector3> vertices = new();
        List<Color32> colors = new();
        List<int> indices = new();

        Color32 shadow = new(4, 5, 7, 238);
        Color32 core = RouteColor(route);
        float coreHalfWidth =
            RouteCoreHalfWidth(route);
        float shadowHalfWidth =
            RouteShadowHalfWidth(route);

        if (route.Ambiguous)
        {
            AddDashedPath(
                vertices,
                colors,
                indices,
                path,
                shadowHalfWidth,
                shadow,
                0.08f);
            AddDashedPath(
                vertices,
                colors,
                indices,
                path,
                coreHalfWidth,
                core,
                0.04f);
        }
        else
        {
            AddPath(
                vertices,
                colors,
                indices,
                path,
                shadowHalfWidth,
                shadow,
                0.08f);
            AddPath(
                vertices,
                colors,
                indices,
                path,
                coreHalfWidth,
                core,
                0.04f);
        }

        AddDirectionArrows(
            vertices,
            colors,
            indices,
            path,
            route.Direction,
            route.DensityTier,
            shadow,
            core);

        if (indices.Count == 0) return null;
        Mesh mesh = NewMesh("DryCycle WorldMap V2 Route");
        EnsureIndexFormat(mesh, vertices.Count);
        mesh.vertices = vertices.ToArray();
        mesh.colors32 = colors.ToArray();
        mesh.triangles = indices.ToArray();
        mesh.RecalculateBounds();
        return mesh;
    }

    private static Mesh BuildCrossingMesh(
        WorldMapConnectionResourceStore resources,
        HashSet<string> visibleRouteIds)
    {
        if (resources == null ||
            visibleRouteIds == null ||
            visibleRouteIds.Count == 0 ||
            resources.Crossings.Count == 0)
            return null;

        int crossingCount =
            resources.Crossings.Count;
        int estimatedVertices =
            Math.Min(
                65536,
                crossingCount * 36);

        List<Vector3> vertices =
            new(estimatedVertices);
        List<Color32> colors =
            new(estimatedVertices);
        List<int> indices =
            new(
                Math.Min(
                    98304,
                    crossingCount * 54));

        Color32 mask = new(4, 5, 7, 255);
        Color32 bridgeShadow = new(4, 5, 7, 238);

        for (int i = 0; i < resources.Crossings.Count; i++)
        {
            WorldMapCrossingMark mark =
                resources.Crossings[i];

            if (!visibleRouteIds.Contains(
                    mark.OverRouteId) ||
                !visibleRouteIds.Contains(
                    mark.UnderRouteId))
                continue;

            if (!resources.TryGet(
                    mark.OverRouteId,
                    out ConnectionRouteResource overRoute))
                continue;

            Num.Vector2 tangent =
                Normalize(mark.Tangent);
            if (tangent.LengthSquared() < 0.5f)
                continue;

            Num.Vector2 point = mark.Point;
            Num.Vector2 normal =
                new(-tangent.Y, tangent.X);
            float radius =
                mark.Dense
                    ? DenseCrossingRadius
                    : CrossingRadius;
            float riseHeight =
                mark.Dense
                    ? DenseCrossingRise
                    : CrossingRise;

            float overShadowHalfWidth =
                RouteShadowHalfWidth(
                    overRoute);
            float overCoreHalfWidth =
                RouteCoreHalfWidth(
                    overRoute);

            AddThickSegment(
                vertices,
                colors,
                indices,
                point - tangent * (radius + 2.5f),
                point + tangent * (radius + 2.5f),
                overShadowHalfWidth + 1.2f,
                mask,
                -0.02f);

            Color32 core = RouteColor(overRoute);

            int arcSegments =
                mark.Dense ? 3 : 6;
            Num.Vector2 previous =
                point - tangent * radius;

            for (int segment = 1; segment <= arcSegments; segment++)
            {
                float t =
                    segment /
                    (float)arcSegments;
                float along =
                    (-1f + t * 2f) *
                    radius;
                float rise =
                    (float)Math.Sin(Math.PI * t) *
                    riseHeight;

                Num.Vector2 current =
                    point +
                    tangent * along +
                    normal * rise;

                AddThickSegment(
                    vertices,
                    colors,
                    indices,
                    previous,
                    current,
                    overShadowHalfWidth,
                    bridgeShadow,
                    -0.055f);

                AddThickSegment(
                    vertices,
                    colors,
                    indices,
                    previous,
                    current,
                    overCoreHalfWidth,
                    core,
                    -0.08f);

                previous = current;
            }
        }

        if (indices.Count == 0)
            return null;

        Mesh mesh =
            NewMesh("DryCycle WorldMap V2 Crossings");
        EnsureIndexFormat(mesh, vertices.Count);
        mesh.vertices = vertices.ToArray();
        mesh.colors32 = colors.ToArray();
        mesh.triangles = indices.ToArray();
        mesh.RecalculateBounds();
        return mesh;
    }

    private static void AddPath(
        List<Vector3> vertices,
        List<Color32> colors,
        List<int> indices,
        Num.Vector2[] path,
        float halfWidth,
        Color32 color,
        float z)
    {
        for (int i = 0; i < path.Length - 1; i++)
        {
            float segmentHalfWidth =
                i == 0 ||
                i == path.Length - 2
                    ? halfWidth * 0.72f
                    : halfWidth;

            AddThickSegment(
                vertices,
                colors,
                indices,
                path[i],
                path[i + 1],
                segmentHalfWidth,
                color,
                z);
        }

        for (int i = 1; i < path.Length - 1; i++)
        {
            float joinRadius =
                i == 1 ||
                i == path.Length - 2
                    ? halfWidth * 0.86f
                    : halfWidth;

            AddRoundJoin(
                vertices,
                colors,
                indices,
                path[i],
                joinRadius,
                color,
                z);
        }
    }

    private static void AddRoundJoin(
        List<Vector3> vertices,
        List<Color32> colors,
        List<int> indices,
        Num.Vector2 center,
        float radius,
        Color32 color,
        float z)
    {
        const int segments = 8;
        if (radius <= 0.001f)
            return;

        int first = vertices.Count;
        vertices.Add(ToUnity(center, z));
        colors.Add(color);

        for (int i = 0; i <= segments; i++)
        {
            float angle = (float)(Math.PI * 2.0 * i / segments);
            Num.Vector2 point = center + new Num.Vector2(
                (float)Math.Cos(angle),
                (float)Math.Sin(angle)) * radius;
            vertices.Add(ToUnity(point, z));
            colors.Add(color);
        }

        for (int i = 0; i < segments; i++)
        {
            indices.Add(first);
            indices.Add(first + i + 1);
            indices.Add(first + i + 2);
        }
    }

    private static void AddDashedPath(
        List<Vector3> vertices,
        List<Color32> colors,
        List<int> indices,
        Num.Vector2[] path,
        float halfWidth,
        Color32 color,
        float z)
    {
        float phase = 0f;
        float cycle = DashLength + DashGap;

        for (int i = 0; i < path.Length - 1; i++)
        {
            Num.Vector2 a = path[i];
            Num.Vector2 b = path[i + 1];
            Num.Vector2 delta = b - a;
            float length = delta.Length();
            if (length <= 0.001f) continue;
            Num.Vector2 direction = delta / length;

            float distance = 0f;
            while (distance < length)
            {
                float cyclePosition = (phase + distance) % cycle;
                float remainingDash =
                    cyclePosition < DashLength
                        ? DashLength - cyclePosition
                        : 0f;

                if (remainingDash <= 0f)
                {
                    distance += cycle - cyclePosition;
                    continue;
                }

                float end = Math.Min(length, distance + remainingDash);
                float segmentHalfWidth =
                    i == 0 ||
                    i == path.Length - 2
                        ? halfWidth * 0.72f
                        : halfWidth;

                AddThickSegment(
                    vertices,
                    colors,
                    indices,
                    a + direction * distance,
                    a + direction * end,
                    segmentHalfWidth,
                    color,
                    z);
                distance = end + DashGap;
            }

            phase = (phase + length) % cycle;
        }
    }

    private static void AddDirectionArrows(
        List<Vector3> vertices,
        List<Color32> colors,
        List<int> indices,
        Num.Vector2[] path,
        WorldConnectionDirection direction,
        byte densityTier,
        Color32 shadow,
        Color32 core)
    {
        float totalLength =
            PathLength(path);
        if (totalLength < 34f)
            return;

        float markerScale =
            densityTier >= 2
                ? 0.74f
                : densityTier == 1
                    ? 0.86f
                    : 1f;

        Num.Vector2 point;
        Num.Vector2 tangent;
        float straightLength;

        if (!TryLongestRouteSegment(
                path,
                out point,
                out tangent,
                out straightLength) ||
            straightLength <
                MinimumDirectionMarkerRun)
        {
            return;
        }

        if (direction ==
            WorldConnectionDirection.Bidirectional)
        {
            float separation =
                Math.Min(
                    9f,
                    Math.Max(
                        4f,
                        straightLength * 0.12f));

            AddChevronAtPoint(
                vertices,
                colors,
                indices,
                point - tangent * separation,
                -tangent,
                markerScale,
                shadow,
                core);
            AddChevronAtPoint(
                vertices,
                colors,
                indices,
                point + tangent * separation,
                tangent,
                markerScale,
                shadow,
                core);
            return;
        }

        if (direction ==
            WorldConnectionDirection.BToA)
        {
            tangent =
                -tangent;
        }

        AddChevronAtPoint(
            vertices,
            colors,
            indices,
            point,
            tangent,
            markerScale,
            shadow,
            core);
    }

    private static bool TryLongestRouteSegment(
        Num.Vector2[] path,
        out Num.Vector2 point,
        out Num.Vector2 tangent,
        out float length)
    {
        point =
            default;
        tangent =
            Num.Vector2.UnitX;
        length =
            0f;

        if (path == null ||
            path.Length < 2)
            return false;

        int firstSegment =
            path.Length >= 4
                ? 1
                : 0;
        int lastSegment =
            path.Length >= 4
                ? path.Length - 3
                : path.Length - 2;

        for (int i = firstSegment;
             i <= lastSegment;
             i++)
        {
            Num.Vector2 delta =
                path[i + 1] -
                path[i];
            float candidateLength =
                delta.Length();

            if (candidateLength <=
                length + 0.01f)
                continue;

            length =
                candidateLength;
            tangent =
                delta /
                candidateLength;
            point =
                (path[i] +
                 path[i + 1]) *
                0.5f;
        }

        return length > 0.001f;
    }

    private static void AddChevronAtPoint(
        List<Vector3> vertices,
        List<Color32> colors,
        List<int> indices,
        Num.Vector2 point,
        Num.Vector2 tangent,
        float sizeScale,
        Color32 shadow,
        Color32 core)
    {
        float tangentLength =
            tangent.Length();
        if (tangentLength <= 0.001f)
            return;

        tangent /=
            tangentLength;

        Num.Vector2 normal =
            new(-tangent.Y, tangent.X);

        float length =
            DirectionMarkerLength *
            sizeScale;
        float spread =
            DirectionMarkerSpread *
            sizeScale;

        Num.Vector2 tip =
            point +
            tangent *
            (length * 0.5f);
        Num.Vector2 back =
            point -
            tangent *
            (length * 0.5f);
        Num.Vector2 left =
            back +
            normal *
            spread;
        Num.Vector2 right =
            back -
            normal *
            spread;

        AddThickSegment(
            vertices,
            colors,
            indices,
            left,
            tip,
            DirectionMarkerShadowHalfWidth,
            shadow,
            -0.082f);
        AddThickSegment(
            vertices,
            colors,
            indices,
            right,
            tip,
            DirectionMarkerShadowHalfWidth,
            shadow,
            -0.082f);

        AddThickSegment(
            vertices,
            colors,
            indices,
            left,
            tip,
            DirectionMarkerCoreHalfWidth,
            core,
            -0.105f);
        AddThickSegment(
            vertices,
            colors,
            indices,
            right,
            tip,
            DirectionMarkerCoreHalfWidth,
            core,
            -0.105f);
    }

    private static void AddThickSegment(
        List<Vector3> vertices,
        List<Color32> colors,
        List<int> indices,
        Num.Vector2 a,
        Num.Vector2 b,
        float halfWidth,
        Color32 color,
        float z)
    {
        Num.Vector2 delta = b - a;
        float length = delta.Length();
        if (length <= 0.001f) return;

        Num.Vector2 direction = delta / length;
        Num.Vector2 normal = new(-direction.Y, direction.X);
        Num.Vector2 offset = normal * halfWidth;

        int first = vertices.Count;
        vertices.Add(ToUnity(a + offset, z));
        vertices.Add(ToUnity(b + offset, z));
        vertices.Add(ToUnity(b - offset, z));
        vertices.Add(ToUnity(a - offset, z));
        colors.Add(color);
        colors.Add(color);
        colors.Add(color);
        colors.Add(color);

        indices.Add(first);
        indices.Add(first + 1);
        indices.Add(first + 2);
        indices.Add(first);
        indices.Add(first + 2);
        indices.Add(first + 3);
    }

    private static float PathLength(Num.Vector2[] path)
    {
        float length = 0f;
        if (path == null) return 0f;
        for (int i = 0; i < path.Length - 1; i++)
            length += Num.Vector2.Distance(path[i], path[i + 1]);
        return length;
    }

    private void HideVisible()
    {
        foreach (string id in visiblePrevious)
        {
            if (routeObjects.TryGetValue(id, out RouteObject obj) && obj.Root.activeSelf)
                obj.Root.SetActive(false);
        }
        visibleNow.Clear();
        visiblePrevious.Clear();
    }

    private void RemoveRoute(string id)
    {
        if (!routeObjects.TryGetValue(id, out RouteObject obj)) return;
        routeObjects.Remove(id);
        DestroyRouteObject(obj);
    }

    private static void DestroyRouteObject(RouteObject obj)
    {
        if (obj == null) return;
        if (obj.Filter?.sharedMesh != null)
            UnityEngine.Object.Destroy(obj.Filter.sharedMesh);
        if (obj.Root != null)
            UnityEngine.Object.Destroy(obj.Root);
    }

    private static void ReplaceMesh(MeshFilter filter, Mesh next)
    {
        Mesh previous = filter.sharedMesh;
        filter.sharedMesh = next;
        if (previous != null)
            UnityEngine.Object.Destroy(previous);
    }

    private static Mesh NewMesh(string name) =>
        new()
        {
            name = name,
            hideFlags = HideFlags.HideAndDontSave
        };

    private static void EnsureIndexFormat(
        Mesh mesh,
        int vertexCount)
    {
        if (mesh == null ||
            vertexCount <= 65535)
            return;

        mesh.indexFormat =
            UnityEngine.Rendering.IndexFormat.UInt32;
    }

    private static Vector3 ToUnity(Num.Vector2 point, float z) =>
        new(point.X, -point.Y, z - 1f);

    private static Num.Vector2 Normalize(Num.Vector2 value)
    {
        float length = value.Length();
        return length <= 0.0001f ? Num.Vector2.Zero : value / length;
    }

    private static float RouteCoreHalfWidth(
        ConnectionRouteResource route)
    {
        byte tier =
            route?.DensityTier ?? 0;

        if (tier >= 2)
            return 0.76f;
        if (tier == 1)
            return 0.96f;
        return CoreHalfWidth;
    }

    private static float RouteShadowHalfWidth(
        ConnectionRouteResource route)
    {
        byte tier =
            route?.DensityTier ?? 0;

        if (tier >= 2)
            return 1.70f;
        if (tier == 1)
            return 2.20f;
        return ShadowHalfWidth;
    }

    private static Color32 RouteColor(ConnectionRouteResource route)
    {
        if (route?.Ambiguous == true)
            return new Color32(150, 155, 164, 220);

        return route?.Direction == WorldConnectionDirection.Bidirectional
            ? new Color32(235, 170, 74, 245)
            : new Color32(232, 238, 247, 245);
    }
}
