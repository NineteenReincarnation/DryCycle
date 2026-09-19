using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Allocation guard for the retained GPU map spatial queries.
///
/// WorldMapGpuScene publishes immutable route/room spatial-index snapshots. The original query
/// methods intentionally favor simple local HashSet/List scratch state; on a continuously hovered
/// or panned map those containers become frame-frequency garbage. This plugin keeps the scene's
/// public query semantics but replaces only the scratch bookkeeping with thread-local generation
/// stamps and a retained room list. Index internals are bound once through visibility-skipping
/// DynamicMethod accessors, so the hot path does not pay reflection invocation costs.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldMapGpuRendererPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapGpuHotQueryPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMapGPU.HotQuery";
    public const string PluginName = "DryCycle DevTool GPU World Map Hot Query";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldMapGpuHotQuery.Enable(Logger);
    private void OnDisable() => WorldMapGpuHotQuery.Disable();
}

internal static class WorldMapGpuHotQuery
{
    private delegate bool OrigTryHitConnection(
        Num.Vector2 mapPoint,
        float radius,
        out WorldMapGpuScene.RouteHit hit);
    private delegate bool HookTryHitConnection(
        OrigTryHitConnection orig,
        Num.Vector2 mapPoint,
        float radius,
        out WorldMapGpuScene.RouteHit hit);

    private delegate int[] OrigQueryVisibleRooms(Num.Vector2 mapMin, Num.Vector2 mapMax, int layerMask);
    private delegate int[] HookQueryVisibleRooms(
        OrigQueryVisibleRooms orig,
        Num.Vector2 mapMin,
        Num.Vector2 mapMax,
        int layerMask);

    private delegate object IndexGetter();
    private delegate WorldMapGpuScene.RouteHit[] RouteArrayGetter(object index);
    private delegate Dictionary<long, int[]> CellMapGetter(object index);
    private delegate int RoomCountGetter(object index);
    private delegate RoomHitData RoomHitGetter(object index, int candidateIndex);

    private readonly struct RoomHitData
    {
        internal RoomHitData(int roomIndex, int layer, int order, Num.Vector2 min, Num.Vector2 max)
        {
            RoomIndex = roomIndex;
            Layer = layer;
            Order = order;
            Min = min;
            Max = max;
        }

        internal int RoomIndex { get; }
        internal int Layer { get; }
        internal int Order { get; }
        internal Num.Vector2 Min { get; }
        internal Num.Vector2 Max { get; }
    }

    private sealed class RoomOrderComparer : IComparer<RoomHitData>
    {
        internal static readonly RoomOrderComparer Instance = new();
        public int Compare(RoomHitData a, RoomHitData b) => a.Order.CompareTo(b.Order);
    }

    private static readonly HookTryHitConnection ConnectionHookDelegate = TryHitConnectionHook;
    private static readonly HookQueryVisibleRooms VisibleRoomsHookDelegate = QueryVisibleRoomsHook;

    private static ManualLogSource log;
    private static IDisposable connectionHook;
    private static IDisposable visibleRoomsHook;
    private static IndexGetter getRouteIndex;
    private static RouteArrayGetter getRoutes;
    private static CellMapGetter getRouteCells;
    private static IndexGetter getRoomIndex;
    private static RoomCountGetter getRoomCount;
    private static CellMapGetter getRoomCells;
    private static RoomHitGetter getRoomHit;
    private static bool enabled;

    [ThreadStatic] private static int[] routeVisitStamps;
    [ThreadStatic] private static int routeVisitGeneration;
    [ThreadStatic] private static int[] roomVisitStamps;
    [ThreadStatic] private static int roomVisitGeneration;
    [ThreadStatic] private static List<RoomHitData> visibleRoomScratch;

    // Duplicate connection hit tests can occur in the same Unity frame. Keep that cache inside
    // the existing HotQuery detour instead of stacking a second RuntimeDetour hook on the same
    // by-ref method. Older Rain World/BepInEx/MonoMod combinations can hard-crash while building
    // that second trampoline before managed exception handling gets a chance to run.
    private static object cachedRouteIndex;
    private static int cachedRouteFrame = int.MinValue;
    private static Num.Vector2 cachedRoutePoint;
    private static float cachedRouteRadius;
    private static WorldMapGpuScene.RouteHit cachedRouteHit;
    private static bool cachedRouteResult;
    private static bool cachedRouteValid;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;

        try
        {
            const BindingFlags staticFlags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            Type sceneType = typeof(WorldMapGpuScene);

            MethodInfo tryHitConnection = sceneType.GetMethod(
                "TryHitConnection",
                staticFlags,
                null,
                new[]
                {
                    typeof(Num.Vector2), typeof(float),
                    typeof(WorldMapGpuScene.RouteHit).MakeByRefType()
                },
                null);
            MethodInfo queryVisibleRooms = sceneType.GetMethod(
                "QueryVisibleRooms",
                staticFlags,
                null,
                new[] { typeof(Num.Vector2), typeof(Num.Vector2), typeof(int) },
                null);
            FieldInfo routeIndexField = sceneType.GetField("routeIndex", staticFlags);
            FieldInfo roomIndexField = sceneType.GetField("roomIndex", staticFlags);
            if (tryHitConnection == null || queryVisibleRooms == null || routeIndexField == null || roomIndexField == null)
                throw new MissingMemberException("World Map GPU spatial-query targets were not found.");

            Type routeIndexType = routeIndexField.FieldType;
            PropertyInfo routesProperty = routeIndexType.GetProperty("Routes", instanceFlags);
            PropertyInfo routeCellsProperty = routeIndexType.GetProperty("Cells", instanceFlags);
            if (routesProperty == null || routeCellsProperty == null)
                throw new MissingMemberException("World Map GPU route-index members were not found.");

            Type roomIndexType = roomIndexField.FieldType;
            PropertyInfo roomsProperty = roomIndexType.GetProperty("Rooms", instanceFlags);
            PropertyInfo roomCellsProperty = roomIndexType.GetProperty("Cells", instanceFlags);
            Type roomHitType = roomsProperty?.PropertyType.GetElementType();
            FieldInfo roomIndexValueField = roomHitType?.GetField("RoomIndex", instanceFlags);
            FieldInfo roomLayerField = roomHitType?.GetField("Layer", instanceFlags);
            FieldInfo roomOrderField = roomHitType?.GetField("Order", instanceFlags);
            FieldInfo roomMinField = roomHitType?.GetField("Min", instanceFlags);
            FieldInfo roomMaxField = roomHitType?.GetField("Max", instanceFlags);
            if (roomsProperty == null || roomCellsProperty == null || roomHitType == null ||
                roomIndexValueField == null || roomLayerField == null || roomOrderField == null ||
                roomMinField == null || roomMaxField == null)
                throw new MissingMemberException("World Map GPU room-index members were not found.");

            getRouteIndex = BuildStaticIndexGetter(routeIndexField, "GetWorldMapRouteIndex");
            getRoutes = BuildRouteArrayGetter(routeIndexType, routesProperty);
            getRouteCells = BuildCellMapGetter(routeIndexType, routeCellsProperty, "GetWorldMapRouteCells");
            getRoomIndex = BuildStaticIndexGetter(roomIndexField, "GetWorldMapRoomIndex");
            getRoomCount = BuildRoomCountGetter(roomIndexType, roomsProperty);
            getRoomCells = BuildCellMapGetter(roomIndexType, roomCellsProperty, "GetWorldMapRoomCells");
            getRoomHit = BuildRoomHitGetter(
                roomIndexType,
                roomsProperty,
                roomHitType,
                roomIndexValueField,
                roomLayerField,
                roomOrderField,
                roomMinField,
                roomMaxField);

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            if (hookType == null)
                throw new TypeLoadException("MonoMod.RuntimeDetour.Hook is unavailable.");
            ConstructorInfo constructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            connectionHook = constructor.Invoke(new object[] { tryHitConnection, ConnectionHookDelegate }) as IDisposable;
            visibleRoomsHook = constructor.Invoke(new object[] { queryVisibleRooms, VisibleRoomsHookDelegate }) as IDisposable;
            if (connectionHook == null || visibleRoomsHook == null)
                throw new InvalidOperationException("World Map GPU hot-query hooks were not created.");

            enabled = true;
            log?.LogInfo("GPU World Map allocation-free spatial scratch enabled.");
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("GPU World Map hot-query optimization could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        DisposeHook(ref visibleRoomsHook);
        DisposeHook(ref connectionHook);
        getRouteIndex = null;
        getRoutes = null;
        getRouteCells = null;
        getRoomIndex = null;
        getRoomCount = null;
        getRoomCells = null;
        getRoomHit = null;
        routeVisitStamps = null;
        routeVisitGeneration = 0;
        roomVisitStamps = null;
        roomVisitGeneration = 0;
        visibleRoomScratch = null;
        ResetRouteHitCache();
        enabled = false;
        log = null;
    }

    private static bool TryHitConnectionHook(
        OrigTryHitConnection orig,
        Num.Vector2 mapPoint,
        float radius,
        out WorldMapGpuScene.RouteHit hit)
    {
        hit = null;
        if (!enabled)
            return orig(mapPoint, radius, out hit);

        try
        {
            object index = getRouteIndex();
            int frame = Time.frameCount;
            if (cachedRouteValid &&
                cachedRouteFrame == frame &&
                ReferenceEquals(cachedRouteIndex, index) &&
                cachedRoutePoint.Equals(mapPoint) &&
                cachedRouteRadius.Equals(radius))
            {
                hit = cachedRouteHit;
                return cachedRouteResult;
            }

            WorldMapGpuScene.RouteHit[] routes = index == null ? null : getRoutes(index);
            Dictionary<long, int[]> cells = index == null ? null : getRouteCells(index);
            if (routes == null || routes.Length == 0 || cells == null || cells.Count == 0)
            {
                CacheRouteHit(index, frame, mapPoint, radius, null, false);
                return false;
            }

            int[] stamps = EnsureStampCapacity(ref routeVisitStamps, routes.Length);
            int generation = NextGeneration(ref routeVisitGeneration, stamps);
            int minCellX = FloorToInt((mapPoint.X - radius) / 256f);
            int maxCellX = FloorToInt((mapPoint.X + radius) / 256f);
            int minCellY = FloorToInt((mapPoint.Y - radius) / 256f);
            int maxCellY = FloorToInt((mapPoint.Y + radius) / 256f);
            float best = radius * radius;

            for (int y = minCellY; y <= maxCellY; y++)
            {
                for (int x = minCellX; x <= maxCellX; x++)
                {
                    if (!cells.TryGetValue(CellKey(x, y), out int[] candidates)) continue;
                    for (int c = 0; c < candidates.Length; c++)
                    {
                        int candidateIndex = candidates[c];
                        if ((uint)candidateIndex >= (uint)routes.Length || stamps[candidateIndex] == generation)
                            continue;
                        stamps[candidateIndex] = generation;

                        WorldMapGpuScene.RouteHit candidate = routes[candidateIndex];
                        if (candidate == null ||
                            mapPoint.X < candidate.Min.X - radius || mapPoint.X > candidate.Max.X + radius ||
                            mapPoint.Y < candidate.Min.Y - radius || mapPoint.Y > candidate.Max.Y + radius)
                            continue;

                        Num.Vector2[] points = candidate.Points;
                        if (points == null) continue;
                        for (int p = 1; p < points.Length; p++)
                        {
                            float distance = DistanceSqToSegment(mapPoint, points[p - 1], points[p]);
                            if (distance > best) continue;
                            best = distance;
                            hit = candidate;
                        }
                    }
                }
            }

            bool result = hit != null;
            CacheRouteHit(index, frame, mapPoint, radius, hit, result);
            return result;
        }
        catch
        {
            return orig(mapPoint, radius, out hit);
        }
    }

    private static void CacheRouteHit(
        object index,
        int frame,
        Num.Vector2 mapPoint,
        float radius,
        WorldMapGpuScene.RouteHit hit,
        bool result)
    {
        cachedRouteIndex = index;
        cachedRouteFrame = frame;
        cachedRoutePoint = mapPoint;
        cachedRouteRadius = radius;
        cachedRouteHit = hit;
        cachedRouteResult = result;
        cachedRouteValid = true;
    }

    private static void ResetRouteHitCache()
    {
        cachedRouteIndex = null;
        cachedRouteFrame = int.MinValue;
        cachedRoutePoint = default;
        cachedRouteRadius = 0f;
        cachedRouteHit = null;
        cachedRouteResult = false;
        cachedRouteValid = false;
    }

    private static int[] QueryVisibleRoomsHook(
        OrigQueryVisibleRooms orig,
        Num.Vector2 mapMin,
        Num.Vector2 mapMax,
        int layerMask)
    {
        if (!enabled)
            return orig(mapMin, mapMax, layerMask);

        try
        {
            object index = getRoomIndex();
            int roomCount = index == null ? 0 : getRoomCount(index);
            Dictionary<long, int[]> cells = index == null ? null : getRoomCells(index);
            if (roomCount <= 0 || cells == null || cells.Count == 0)
                return Array.Empty<int>();

            int[] stamps = EnsureStampCapacity(ref roomVisitStamps, roomCount);
            int generation = NextGeneration(ref roomVisitGeneration, stamps);
            List<RoomHitData> visible = visibleRoomScratch ??= new List<RoomHitData>(Math.Min(roomCount, 64));
            visible.Clear();

            int minCellX = FloorToInt(mapMin.X / 256f);
            int maxCellX = FloorToInt(mapMax.X / 256f);
            int minCellY = FloorToInt(mapMin.Y / 256f);
            int maxCellY = FloorToInt(mapMax.Y / 256f);

            for (int y = minCellY; y <= maxCellY; y++)
            {
                for (int x = minCellX; x <= maxCellX; x++)
                {
                    if (!cells.TryGetValue(CellKey(x, y), out int[] candidates)) continue;
                    for (int i = 0; i < candidates.Length; i++)
                    {
                        int candidateIndex = candidates[i];
                        if ((uint)candidateIndex >= (uint)roomCount || stamps[candidateIndex] == generation)
                            continue;
                        stamps[candidateIndex] = generation;

                        RoomHitData candidate = getRoomHit(index, candidateIndex);
                        if ((layerMask & (1 << candidate.Layer)) == 0 ||
                            candidate.Max.X < mapMin.X || candidate.Min.X > mapMax.X ||
                            candidate.Max.Y < mapMin.Y || candidate.Min.Y > mapMax.Y)
                            continue;
                        visible.Add(candidate);
                    }
                }
            }

            visible.Sort(RoomOrderComparer.Instance);
            int[] result = new int[visible.Count];
            for (int i = 0; i < visible.Count; i++) result[i] = visible[i].RoomIndex;
            return result;
        }
        catch
        {
            return orig(mapMin, mapMax, layerMask);
        }
    }

    private static IndexGetter BuildStaticIndexGetter(FieldInfo field, string name)
    {
        DynamicMethod method = new(name, typeof(object), Type.EmptyTypes, typeof(WorldMapGpuHotQuery).Module, true);
        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Ldsfld, field);
        il.Emit(OpCodes.Ret);
        return (IndexGetter)method.CreateDelegate(typeof(IndexGetter));
    }

    private static RouteArrayGetter BuildRouteArrayGetter(Type indexType, PropertyInfo routesProperty)
    {
        MethodInfo getter = routesProperty.GetGetMethod(true) ??
                            throw new MissingMethodException("World Map route array getter is unavailable.");
        DynamicMethod method = new(
            "GetWorldMapRoutes",
            typeof(WorldMapGpuScene.RouteHit[]),
            new[] { typeof(object) },
            typeof(WorldMapGpuHotQuery).Module,
            true);
        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, indexType);
        il.Emit(OpCodes.Callvirt, getter);
        il.Emit(OpCodes.Ret);
        return (RouteArrayGetter)method.CreateDelegate(typeof(RouteArrayGetter));
    }

    private static CellMapGetter BuildCellMapGetter(Type indexType, PropertyInfo cellsProperty, string name)
    {
        MethodInfo getter = cellsProperty.GetGetMethod(true) ??
                            throw new MissingMethodException("World Map cell-map getter is unavailable.");
        DynamicMethod method = new(
            name,
            typeof(Dictionary<long, int[]>),
            new[] { typeof(object) },
            typeof(WorldMapGpuHotQuery).Module,
            true);
        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, indexType);
        il.Emit(OpCodes.Callvirt, getter);
        il.Emit(OpCodes.Ret);
        return (CellMapGetter)method.CreateDelegate(typeof(CellMapGetter));
    }

    private static RoomCountGetter BuildRoomCountGetter(Type indexType, PropertyInfo roomsProperty)
    {
        MethodInfo getter = roomsProperty.GetGetMethod(true) ??
                            throw new MissingMethodException("World Map room array getter is unavailable.");
        DynamicMethod method = new(
            "GetWorldMapRoomCount",
            typeof(int),
            new[] { typeof(object) },
            typeof(WorldMapGpuHotQuery).Module,
            true);
        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, indexType);
        il.Emit(OpCodes.Callvirt, getter);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Ret);
        return (RoomCountGetter)method.CreateDelegate(typeof(RoomCountGetter));
    }

    private static RoomHitGetter BuildRoomHitGetter(
        Type indexType,
        PropertyInfo roomsProperty,
        Type roomHitType,
        FieldInfo roomIndexField,
        FieldInfo layerField,
        FieldInfo orderField,
        FieldInfo minField,
        FieldInfo maxField)
    {
        MethodInfo roomsGetter = roomsProperty.GetGetMethod(true) ??
                                 throw new MissingMethodException("World Map room array getter is unavailable.");
        ConstructorInfo constructor = typeof(RoomHitData).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            null,
            new[] { typeof(int), typeof(int), typeof(int), typeof(Num.Vector2), typeof(Num.Vector2) },
            null) ?? throw new MissingMethodException("World Map room hit data constructor is unavailable.");

        DynamicMethod method = new(
            "GetWorldMapRoomHit",
            typeof(RoomHitData),
            new[] { typeof(object), typeof(int) },
            typeof(WorldMapGpuHotQuery).Module,
            true);
        ILGenerator il = method.GetILGenerator();
        LocalBuilder room = il.DeclareLocal(roomHitType);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, indexType);
        il.Emit(OpCodes.Callvirt, roomsGetter);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Stloc, room);

        il.Emit(OpCodes.Ldloc, room);
        il.Emit(OpCodes.Ldfld, roomIndexField);
        il.Emit(OpCodes.Ldloc, room);
        il.Emit(OpCodes.Ldfld, layerField);
        il.Emit(OpCodes.Ldloc, room);
        il.Emit(OpCodes.Ldfld, orderField);
        il.Emit(OpCodes.Ldloc, room);
        il.Emit(OpCodes.Ldfld, minField);
        il.Emit(OpCodes.Ldloc, room);
        il.Emit(OpCodes.Ldfld, maxField);
        il.Emit(OpCodes.Newobj, constructor);
        il.Emit(OpCodes.Ret);
        return (RoomHitGetter)method.CreateDelegate(typeof(RoomHitGetter));
    }

    private static int[] EnsureStampCapacity(ref int[] stamps, int count)
    {
        if (stamps != null && stamps.Length >= count) return stamps;
        int size = Math.Max(16, stamps?.Length ?? 0);
        while (size < count)
        {
            int next = size <= int.MaxValue / 2 ? size * 2 : count;
            if (next <= size)
            {
                size = count;
                break;
            }
            size = next;
        }
        stamps = new int[size];
        return stamps;
    }

    private static int NextGeneration(ref int generation, int[] stamps)
    {
        if (generation == int.MaxValue)
        {
            Array.Clear(stamps, 0, stamps.Length);
            generation = 1;
            return generation;
        }

        generation++;
        if (generation <= 0) generation = 1;
        return generation;
    }

    private static long CellKey(int x, int y) => ((long)(uint)x << 32) | (uint)y;
    private static int FloorToInt(float value) => (int)Math.Floor(value);
    private static float Clamp(float value, float min, float max) => Math.Max(min, Math.Min(max, value));

    private static float DistanceSqToSegment(Num.Vector2 point, Num.Vector2 a, Num.Vector2 b)
    {
        Num.Vector2 ab = b - a;
        float lengthSq = ab.LengthSquared();
        if (lengthSq < 0.0001f) return Num.Vector2.DistanceSquared(point, a);
        float t = Num.Vector2.Dot(point - a, ab) / lengthSq;
        t = Clamp(t, 0f, 1f);
        return Num.Vector2.DistanceSquared(point, a + ab * t);
    }

    private static void DisposeHook(ref IDisposable hook)
    {
        try { hook?.Dispose(); }
        catch { }
        hook = null;
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
