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

    private const float CoreHalfWidth = 1.55f;
    private const float ShadowHalfWidth = 4.0f;
    private const float DashLength = 10f;
    private const float DashGap = 6f;
    private const float ArrowSize = 13f;
    private const float CrossingRadius = 7f;
    private const float CrossingRise = 5.5f;

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
        bool showConnections)
    {
        if (resources == null || visibleRouteIds == null)
            return false;
        if (!EnsureResources()) return false;

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

        if (crossingRevision != resources.Revision)
        {
            ReplaceMesh(crossingFilter, BuildCrossingMesh(resources));
            crossingRevision = resources.Revision;
        }

        if (crossingObject != null)
            crossingObject.SetActive(true);

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

        if (material != null)
            UnityEngine.Object.Destroy(material);
        material = null;
        shader = null;

        if (root != null)
            UnityEngine.Object.Destroy(root);
        root = null;
    }

    private bool EnsureResources()
    {
        if (root == null)
        {
            root = new GameObject("DryCycle.WorldMapV2.Connections")
            {
                hideFlags = HideFlags.HideAndDontSave,
                layer = WorldMapRenderTextureSurface.RenderLayer
            };
        }

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

        if (route.Ambiguous)
        {
            AddDashedPath(
                vertices,
                colors,
                indices,
                path,
                ShadowHalfWidth,
                shadow,
                0.08f);
            AddDashedPath(
                vertices,
                colors,
                indices,
                path,
                CoreHalfWidth,
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
                ShadowHalfWidth,
                shadow,
                0.08f);
            AddPath(
                vertices,
                colors,
                indices,
                path,
                CoreHalfWidth,
                core,
                0.04f);
        }

        AddDirectionArrows(
            vertices,
            colors,
            indices,
            path,
            route.Direction,
            shadow,
            core);

        if (indices.Count == 0) return null;
        Mesh mesh = NewMesh("DryCycle WorldMap V2 Route");
        mesh.vertices = vertices.ToArray();
        mesh.colors32 = colors.ToArray();
        mesh.triangles = indices.ToArray();
        mesh.RecalculateBounds();
        return mesh;
    }

    private static Mesh BuildCrossingMesh(
        WorldMapConnectionResourceStore resources)
    {
        if (resources == null || resources.Routes.Count < 2)
            return null;

        List<ConnectionRouteResource> routes =
            new(resources.Routes.Values);
        routes.Sort((a, b) =>
            string.CompareOrdinal(a.ConnectionId, b.ConnectionId));

        List<Vector3> vertices = new();
        List<Color32> colors = new();
        List<int> indices = new();

        Color32 mask = new(4, 5, 7, 255);

        for (int i = 0; i < routes.Count; i++)
        {
            Num.Vector2[] a = routes[i]?.Points ?? Array.Empty<Num.Vector2>();
            if (a.Length < 2) continue;

            for (int j = i + 1; j < routes.Count; j++)
            {
                ConnectionRouteResource overRoute = routes[j];
                Num.Vector2[] b = overRoute?.Points ?? Array.Empty<Num.Vector2>();
                if (b.Length < 2) continue;

                for (int ai = 0; ai < a.Length - 1; ai++)
                {
                    for (int bi = 0; bi < b.Length - 1; bi++)
                    {
                        if (!TrySegmentIntersection(
                                a[ai],
                                a[ai + 1],
                                b[bi],
                                b[bi + 1],
                                out Num.Vector2 point))
                            continue;

                        Num.Vector2 tangent = Normalize(b[bi + 1] - b[bi]);
                        if (tangent.LengthSquared() < 0.5f) continue;

                        AddThickSegment(
                            vertices,
                            colors,
                            indices,
                            point - tangent * (CrossingRadius + 2f),
                            point + tangent * (CrossingRadius + 2f),
                            ShadowHalfWidth + 1.2f,
                            mask,
                            -0.02f);

                        Num.Vector2 normal = new(-tangent.Y, tangent.X);
                        Num.Vector2 p0 = point - tangent * CrossingRadius;
                        Num.Vector2 p1 = point + normal * CrossingRise;
                        Num.Vector2 p2 = point + tangent * CrossingRadius;
                        Color32 core = RouteColor(overRoute);

                        AddThickSegment(
                            vertices,
                            colors,
                            indices,
                            p0,
                            p1,
                            CoreHalfWidth,
                            core,
                            -0.06f);
                        AddThickSegment(
                            vertices,
                            colors,
                            indices,
                            p1,
                            p2,
                            CoreHalfWidth,
                            core,
                            -0.06f);
                    }
                }
            }
        }

        if (indices.Count == 0) return null;
        Mesh mesh = NewMesh("DryCycle WorldMap V2 Crossings");
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
            AddThickSegment(
                vertices,
                colors,
                indices,
                path[i],
                path[i + 1],
                halfWidth,
                color,
                z);
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
                AddThickSegment(
                    vertices,
                    colors,
                    indices,
                    a + direction * distance,
                    a + direction * end,
                    halfWidth,
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
        Color32 shadow,
        Color32 core)
    {
        float length = PathLength(path);
        if (length < 24f) return;

        if (direction == WorldConnectionDirection.Bidirectional)
        {
            AddArrowAt(
                vertices,
                colors,
                indices,
                path,
                0.35f,
                reverse: true,
                shadow,
                core);
            AddArrowAt(
                vertices,
                colors,
                indices,
                path,
                0.65f,
                reverse: false,
                shadow,
                core);
            return;
        }

        int count = length >= 360f ? 3 : length >= 190f ? 2 : 1;
        bool reverse = direction == WorldConnectionDirection.BToA;
        for (int i = 0; i < count; i++)
        {
            float fraction = (i + 1f) / (count + 1f);
            AddArrowAt(
                vertices,
                colors,
                indices,
                path,
                fraction,
                reverse,
                shadow,
                core);
        }
    }

    private static void AddArrowAt(
        List<Vector3> vertices,
        List<Color32> colors,
        List<int> indices,
        Num.Vector2[] path,
        float fraction,
        bool reverse,
        Color32 shadow,
        Color32 core)
    {
        if (!TryPointAtFraction(
                path,
                fraction,
                out Num.Vector2 point,
                out Num.Vector2 tangent))
            return;

        if (reverse) tangent = -tangent;
        Num.Vector2 normal = new(-tangent.Y, tangent.X);

        AddArrowTriangle(
            vertices,
            colors,
            indices,
            point,
            tangent,
            normal,
            ArrowSize + 4f,
            shadow,
            -0.08f);
        AddArrowTriangle(
            vertices,
            colors,
            indices,
            point,
            tangent,
            normal,
            ArrowSize,
            core,
            -0.10f);
    }

    private static void AddArrowTriangle(
        List<Vector3> vertices,
        List<Color32> colors,
        List<int> indices,
        Num.Vector2 center,
        Num.Vector2 tangent,
        Num.Vector2 normal,
        float size,
        Color32 color,
        float z)
    {
        Num.Vector2 tip = center + tangent * (size * 0.55f);
        Num.Vector2 baseCenter = center - tangent * (size * 0.45f);
        float half = size * 0.42f;
        Num.Vector2 left = baseCenter + normal * half;
        Num.Vector2 right = baseCenter - normal * half;

        int first = vertices.Count;
        vertices.Add(ToUnity(tip, z));
        vertices.Add(ToUnity(left, z));
        vertices.Add(ToUnity(right, z));
        colors.Add(color);
        colors.Add(color);
        colors.Add(color);
        indices.Add(first);
        indices.Add(first + 1);
        indices.Add(first + 2);
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

    private static bool TryPointAtFraction(
        Num.Vector2[] path,
        float fraction,
        out Num.Vector2 point,
        out Num.Vector2 tangent)
    {
        point = default;
        tangent = Num.Vector2.UnitX;
        float total = PathLength(path);
        if (total <= 0.001f) return false;

        float target = total * Math.Max(0f, Math.Min(1f, fraction));
        float accumulated = 0f;

        for (int i = 0; i < path.Length - 1; i++)
        {
            Num.Vector2 delta = path[i + 1] - path[i];
            float length = delta.Length();
            if (length <= 0.001f) continue;

            if (accumulated + length >= target)
            {
                float t = (target - accumulated) / length;
                point = Num.Vector2.Lerp(path[i], path[i + 1], t);
                tangent = delta / length;
                return true;
            }

            accumulated += length;
        }

        Num.Vector2 lastDelta = path[path.Length - 1] - path[path.Length - 2];
        tangent = Normalize(lastDelta);
        point = path[path.Length - 1];
        return tangent.LengthSquared() > 0f;
    }

    private static float PathLength(Num.Vector2[] path)
    {
        float length = 0f;
        if (path == null) return 0f;
        for (int i = 0; i < path.Length - 1; i++)
            length += Num.Vector2.Distance(path[i], path[i + 1]);
        return length;
    }

    private static bool TrySegmentIntersection(
        Num.Vector2 a,
        Num.Vector2 b,
        Num.Vector2 c,
        Num.Vector2 d,
        out Num.Vector2 point)
    {
        point = default;
        Num.Vector2 r = b - a;
        Num.Vector2 s = d - c;
        float denominator = Cross(r, s);
        if (Math.Abs(denominator) < 0.001f) return false;

        Num.Vector2 ca = c - a;
        float t = Cross(ca, s) / denominator;
        float u = Cross(ca, r) / denominator;
        if (t <= 0.04f || t >= 0.96f || u <= 0.04f || u >= 0.96f)
            return false;

        Num.Vector2 rt = Normalize(r);
        Num.Vector2 st = Normalize(s);
        if (Math.Abs(Cross(rt, st)) < 0.35f)
            return false;

        point = a + r * t;
        return true;
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

    private static Vector3 ToUnity(Num.Vector2 point, float z) =>
        new(point.X, -point.Y, z - 1f);

    private static Num.Vector2 Normalize(Num.Vector2 value)
    {
        float length = value.Length();
        return length <= 0.0001f ? Num.Vector2.Zero : value / length;
    }

    private static float Cross(Num.Vector2 a, Num.Vector2 b) =>
        a.X * b.Y - a.Y * b.X;

    private static Color32 RouteColor(ConnectionRouteResource route)
    {
        if (route?.Ambiguous == true)
            return new Color32(150, 155, 164, 220);

        return route?.Direction == WorldConnectionDirection.Bidirectional
            ? new Color32(235, 170, 74, 245)
            : new Color32(232, 238, 247, 245);
    }
}
