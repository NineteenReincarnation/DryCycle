using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Map;
using ImGuiNET;
using UnityEngine;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Integration boundary between RWImGui and the retained Unity World Map renderer.
///
/// ImGui remains responsible for layout, toolbar, inspector, labels, direct manipulation and routed
/// topology links. Retained GPU layers own room raster plus high-frequency room highlights only.
/// Keeping connection rendering in one native WorldMapView path avoids competing DrawCanvas hooks
/// and guarantees the fallback and retained views use identical link geometry and semantics.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(BridgePlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency(WorldConnectionRoutingPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency(WorldMapPlayerLocatorPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency(WorldMapPipeLayerPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency(WorldMapPerformancePlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapGpuRendererPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMapGPU";
    public const string PluginName = "DryCycle DevTool Retained GPU World Map";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldMapGpuRuntime.Enable(Logger, Thread.CurrentThread.ManagedThreadId);

    private void Update() => WorldMapGpuRuntime.UpdateMainThread();

    private void OnDisable() => WorldMapGpuRuntime.Disable();
}

internal static class WorldMapGpuRuntime
{
    private const float SemanticRoomLodZoom = 0.52f;

    private delegate void OrigDrawCanvas(EditorMapPresentationSnapshot snapshot);
    private delegate void HookDrawCanvas(OrigDrawCanvas orig, EditorMapPresentationSnapshot snapshot);
    private delegate void OrigDrawRoomGeometry(
        ImDrawListPtr draw,
        EditorMapRoomSnapshot room,
        EditorMapRoomVisualSnapshot visual,
        Num.Vector2 roomMin,
        bool selected,
        bool hovered);
    private delegate void HookDrawRoomGeometry(
        OrigDrawRoomGeometry orig,
        ImDrawListPtr draw,
        EditorMapRoomSnapshot room,
        EditorMapRoomVisualSnapshot visual,
        Num.Vector2 roomMin,
        bool selected,
        bool hovered);
    private delegate void OrigDrawToolbar(EditorMapPresentationSnapshot snapshot);
    private delegate void HookDrawToolbar(OrigDrawToolbar orig, EditorMapPresentationSnapshot snapshot);
    private delegate void OrigGeometryPrime(EditorSession session);
    private delegate void HookGeometryPrime(OrigGeometryPrime orig, EditorSession session);
    private delegate EditorMapRoomVisualSnapshot OrigGeometryGet(int roomIndex);
    private delegate EditorMapRoomVisualSnapshot HookGeometryGet(OrigGeometryGet orig, int roomIndex);
    private delegate void OrigShortcutPrime(EditorSession session, int selectedRoomIndex);
    private delegate void HookShortcutPrime(OrigShortcutPrime orig, EditorSession session, int selectedRoomIndex);
    private delegate bool OrigTryGetExit(
        int roomIndex,
        int nodeIndex,
        out WorldMapShortcutPresentation.ShortcutMarker marker);
    private delegate bool HookTryGetExit(
        OrigTryGetExit orig,
        int roomIndex,
        int nodeIndex,
        out WorldMapShortcutPresentation.ShortcutMarker marker);
    private delegate WorldMapShortcutPresentation.ShortcutMarker[] OrigGetCreatureHoles(int roomIndex);
    private delegate WorldMapShortcutPresentation.ShortcutMarker[] HookGetCreatureHoles(
        OrigGetCreatureHoles orig,
        int roomIndex);

    private static readonly HookDrawCanvas DrawCanvasHookDelegate = DrawCanvasHook;
    private static readonly HookDrawRoomGeometry DrawRoomGeometryHookDelegate = DrawRoomGeometryHook;
    private static readonly HookDrawToolbar DrawToolbarHookDelegate = DrawToolbarHook;
    private static readonly HookGeometryPrime GeometryPrimeHookDelegate = GeometryPrimeHook;
    private static readonly HookGeometryGet GeometryGetHookDelegate = GeometryGetHook;
    private static readonly HookShortcutPrime ShortcutPrimeHookDelegate = ShortcutPrimeHook;
    private static readonly HookTryGetExit TryGetExitHookDelegate = TryGetExitHook;
    private static readonly HookGetCreatureHoles GetCreatureHolesHookDelegate = GetCreatureHolesHook;

    private static ManualLogSource log;
    private static IDisposable canvasHook;
    private static IDisposable roomGeometryHook;
    private static IDisposable toolbarHook;
    private static IDisposable geometryPrimeHook;
    private static IDisposable geometryGetHook;
    private static IDisposable shortcutPrimeHook;
    private static IDisposable shortcutExitHook;
    private static IDisposable creatureHolesHook;

    private static FieldInfo panField;
    private static FieldInfo zoomField;
    private static FieldInfo localPositionsField;
    private static FieldInfo layerVisibleField;
    private static FieldInfo showConnectionsField;
    private static FieldInfo selectedConnectionIdField;
    private static FieldInfo hoveredConnectionIdField;
    private static FieldInfo draggingRoomField;

    private static volatile WorldMapGpuScene.FrameState latestFrame;
    private static int mainThreadId;
    private static bool enabled;
    private static int requestRebuild;
    private static int frameHoveredRoomIndex = -1;
    private static WorldMapGpuScene.RoomPlacement[] cachedPlacements = Array.Empty<WorldMapGpuScene.RoomPlacement>();
    private static int cachedLayoutHash = int.MinValue;

    internal static void Enable(ManualLogSource logger, int unityMainThreadId)
    {
        if (enabled) return;
        log = logger;
        mainThreadId = unityMainThreadId;

        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            Type mapType = typeof(WorldMapView);
            MethodInfo drawCanvas = mapType.GetMethod(
                "DrawCanvas", flags, null, new[] { typeof(EditorMapPresentationSnapshot) }, null);
            MethodInfo drawRoomGeometry = mapType.GetMethod(
                "DrawRoomGeometry",
                flags,
                null,
                new[]
                {
                    typeof(ImDrawListPtr), typeof(EditorMapRoomSnapshot), typeof(EditorMapRoomVisualSnapshot),
                    typeof(Num.Vector2), typeof(bool), typeof(bool)
                },
                null);
            MethodInfo drawToolbar = mapType.GetMethod(
                "DrawToolbar", flags, null, new[] { typeof(EditorMapPresentationSnapshot) }, null);

            panField = mapType.GetField("pan", flags);
            zoomField = mapType.GetField("zoom", flags);
            localPositionsField = mapType.GetField("localPositions", flags);
            layerVisibleField = mapType.GetField("layerVisible", flags);
            showConnectionsField = mapType.GetField("showConnections", flags);
            selectedConnectionIdField = mapType.GetField("selectedConnectionId", flags);
            hoveredConnectionIdField = mapType.GetField("hoveredConnectionId", flags);
            draggingRoomField = mapType.GetField("draggingRoom", flags);

            Type geometryType = typeof(MapRoomGeometryPresentationHub);
            MethodInfo geometryPrime = geometryType.GetMethod(
                "Prime", flags, null, new[] { typeof(EditorSession) }, null);
            MethodInfo geometryGet = geometryType.GetMethod(
                "Get", flags, null, new[] { typeof(int) }, null);

            Type shortcutType = typeof(WorldMapShortcutPresentation);
            MethodInfo shortcutPrime = shortcutType.GetMethod(
                "Prime", flags, null, new[] { typeof(EditorSession), typeof(int) }, null);
            MethodInfo tryGetExit = shortcutType.GetMethod(
                "TryGetExitMouth",
                flags,
                null,
                new[]
                {
                    typeof(int), typeof(int),
                    typeof(WorldMapShortcutPresentation.ShortcutMarker).MakeByRefType()
                },
                null);
            MethodInfo getCreatureHoles = shortcutType.GetMethod(
                "GetCreatureHoles", flags, null, new[] { typeof(int) }, null);

            if (drawCanvas == null || drawRoomGeometry == null || drawToolbar == null ||
                panField == null || zoomField == null || localPositionsField == null ||
                layerVisibleField == null || showConnectionsField == null ||
                selectedConnectionIdField == null || hoveredConnectionIdField == null ||
                draggingRoomField == null || geometryPrime == null || geometryGet == null ||
                shortcutPrime == null || tryGetExit == null || getCreatureHoles == null)
                throw new MissingMemberException("World Map GPU integration targets were not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            if (hookType == null) throw new TypeLoadException("MonoMod.RuntimeDetour.Hook is unavailable.");
            ConstructorInfo constructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            canvasHook = constructor.Invoke(new object[] { drawCanvas, DrawCanvasHookDelegate }) as IDisposable;
            roomGeometryHook = constructor.Invoke(new object[] { drawRoomGeometry, DrawRoomGeometryHookDelegate }) as IDisposable;
            toolbarHook = constructor.Invoke(new object[] { drawToolbar, DrawToolbarHookDelegate }) as IDisposable;
            geometryPrimeHook = constructor.Invoke(new object[] { geometryPrime, GeometryPrimeHookDelegate }) as IDisposable;
            geometryGetHook = constructor.Invoke(new object[] { geometryGet, GeometryGetHookDelegate }) as IDisposable;
            shortcutPrimeHook = constructor.Invoke(new object[] { shortcutPrime, ShortcutPrimeHookDelegate }) as IDisposable;
            shortcutExitHook = constructor.Invoke(new object[] { tryGetExit, TryGetExitHookDelegate }) as IDisposable;
            creatureHolesHook = constructor.Invoke(new object[] { getCreatureHoles, GetCreatureHolesHookDelegate }) as IDisposable;

            enabled = true;
            log?.LogInfo("Retained GPU World Map room integration enabled; routed links stay on the native map layer.");
        }
        catch (Exception error)
        {
            Disable();
            log?.LogWarning("Retained GPU World Map could not attach; keeping fallback map: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        DisposeHook(ref creatureHolesHook);
        DisposeHook(ref shortcutExitHook);
        DisposeHook(ref shortcutPrimeHook);
        DisposeHook(ref geometryGetHook);
        DisposeHook(ref geometryPrimeHook);
        DisposeHook(ref toolbarHook);
        DisposeHook(ref roomGeometryHook);
        DisposeHook(ref canvasHook);
        latestFrame = null;
        cachedPlacements = Array.Empty<WorldMapGpuScene.RoomPlacement>();
        cachedLayoutHash = int.MinValue;
        frameHoveredRoomIndex = -1;
        requestRebuild = 0;
        panField = null;
        zoomField = null;
        localPositionsField = null;
        layerVisibleField = null;
        showConnectionsField = null;
        selectedConnectionIdField = null;
        hoveredConnectionIdField = null;
        draggingRoomField = null;
        WorldMapGpuCache.FlushNow();
        WorldMapGpuScene.Disable();
        enabled = false;
        log = null;
    }

    internal static void UpdateMainThread()
    {
        if (!enabled) return;
        EditorSession session = DevToolRuntime.ActiveSession;
        EditorMapPresentationSnapshot snapshot = MapEditorPresentationHub.Current;

        if (Interlocked.Exchange(ref requestRebuild, 0) != 0)
        {
            string region = snapshot?.RegionName ?? session?.World?.name ?? string.Empty;
            WorldMapGpuCache.ClearDiskCache(region);
            MapRoomGeometryPresentationHub.Clear();
            WorldMapGpuScene.Disable();
        }

        if (session?.ToolMode == EditorToolMode.Map && snapshot?.Available == true)
        {
            MapRoomGeometryPresentationHub.Prime(session);
            WorldMapShortcutPresentation.Prime(session, snapshot.SelectedRoomIndex);
            WorldMapGpuCache.Update(session, snapshot);
        }
        else
        {
            WorldMapGpuCache.FlushNow();
        }

        WorldMapGpuScene.Apply(latestFrame, session);
    }

    private static void DrawCanvasHook(OrigDrawCanvas orig, EditorMapPresentationSnapshot snapshot)
    {
        if (!enabled || snapshot?.Available != true)
        {
            orig(snapshot);
            return;
        }

        Num.Vector2 canvasMin = ImGui.GetCursorScreenPos();
        Num.Vector2 canvasSize = ImGui.GetContentRegionAvail();
        if (canvasSize.X < 80f || canvasSize.Y < 80f)
        {
            orig(snapshot);
            return;
        }

        bool gpuReady = WorldMapGpuScene.Ready;
        ImGuiIOPtr io = ImGui.GetIO();
        float zoom = zoomField?.GetValue(null) is float z ? z : 1f;
        Num.Vector2 pan = panField?.GetValue(null) is Num.Vector2 p ? p : Num.Vector2.Zero;
        int layerMask = CurrentLayerMask();

        bool mouseInside = PointInside(io.MousePos, canvasMin, canvasMin + canvasSize);
        frameHoveredRoomIndex = -1;
        if (gpuReady && mouseInside)
        {
            Num.Vector2 mapPoint = ScreenToMap(io.MousePos, canvasMin, pan, zoom);
            WorldMapGpuScene.TryHitRoom(mapPoint, layerMask, out frameHoveredRoomIndex);
        }

        // The retained renderer now owns room pixels only. Do not suppress WorldMapView's Links
        // state: the native routed layer must execute in both fallback and GPU modes.
        if (gpuReady) ImGui.PushStyleColor(ImGuiCol.ChildBg, new Num.Vector4(0f, 0f, 0f, 0f));
        try
        {
            orig(snapshot);
        }
        finally
        {
            if (gpuReady) ImGui.PopStyleColor();
        }

        // Connections deliberately stay out of the retained scene. This removes the duplicate GPU
        // route pipeline and makes one orthogonal route set authoritative for visuals and hit tests.
        PublishFrame(snapshot, canvasMin, canvasSize, io.DisplaySize, pan, zoom, showConnections: false, layerMask);
    }

    private static void DrawRoomGeometryHook(
        OrigDrawRoomGeometry orig,
        ImDrawListPtr draw,
        EditorMapRoomSnapshot room,
        EditorMapRoomVisualSnapshot visual,
        Num.Vector2 roomMin,
        bool selected,
        bool hovered)
    {
        if (!WorldMapGpuScene.Ready)
        {
            orig(draw, room, visual, roomMin, selected, hovered);
            return;
        }

        if (hovered && room != null) frameHoveredRoomIndex = room.RoomIndex;

        float zoom = zoomField?.GetValue(null) is float z ? z : 1f;
        if (zoom < SemanticRoomLodZoom && visual?.DetailedRasterAvailable == true)
        {
            DrawSemanticRoomLod(draw, room, visual, roomMin, zoom);
            return;
        }

        if (selected || hovered) return;

        float width = Math.Max(1f, visual?.WidthTiles ?? 12f) * WorldMapGpuScene.TileDisplaySize * zoom;
        float height = Math.Max(1f, visual?.HeightTiles ?? 6f) * WorldMapGpuScene.TileDisplaySize * zoom;
        Num.Vector2 max = roomMin + new Num.Vector2(width, height);
        uint color = ImGui.GetColorU32(
            room?.CurrentRoom == true ? ImGuiCol.Header :
            room?.Disabled == true ? ImGuiCol.TextDisabled : ImGuiCol.Border);
        draw.AddRect(roomMin, max, color, Math.Max(1f, 3f * zoom), ImDrawFlags.None, 1f);
    }

    private static void DrawSemanticRoomLod(
        ImDrawListPtr draw,
        EditorMapRoomSnapshot room,
        EditorMapRoomVisualSnapshot visual,
        Num.Vector2 roomMin,
        float zoom)
    {
        float tileScale = WorldMapGpuScene.TileDisplaySize * zoom;
        float widthTiles = Math.Max(1f, visual.WidthTiles);
        float heightTiles = Math.Max(1f, visual.HeightTiles);
        Num.Vector2 roomMax = roomMin + new Num.Vector2(widthTiles * tileScale, heightTiles * tileScale);

        draw.AddRectFilled(roomMin, roomMax, SemanticGeometryColor(EditorMapGeometryKind.Air));

        EditorMapRectSnapshot[] runs = visual.RasterRuns ?? Array.Empty<EditorMapRectSnapshot>();
        for (int i = 0; i < runs.Length; i++)
        {
            EditorMapRectSnapshot run = runs[i];
            if (run.Width <= 0f || run.Height <= 0f ||
                run.Kind == EditorMapGeometryKind.Air ||
                run.Kind == EditorMapGeometryKind.Water)
                continue;

            float x0 = roomMin.X + run.X * tileScale;
            float x1 = roomMin.X + (run.X + run.Width) * tileScale;
            float y0 = roomMin.Y + (heightTiles - (run.Y + run.Height)) * tileScale;
            float y1 = roomMin.Y + (heightTiles - run.Y) * tileScale;
            draw.AddRectFilled(
                new Num.Vector2(Math.Min(x0, x1), Math.Min(y0, y1)),
                new Num.Vector2(Math.Max(x0, x1), Math.Max(y0, y1)),
                SemanticGeometryColor(run.Kind));
        }

        uint outline = ImGui.GetColorU32(
            room?.CurrentRoom == true ? ImGuiCol.Header :
            room?.Disabled == true ? ImGuiCol.TextDisabled : ImGuiCol.Border);
        draw.AddRect(roomMin, roomMax, outline, Math.Max(1f, 3f * zoom), ImDrawFlags.None, 1f);
    }

    private static uint SemanticGeometryColor(EditorMapGeometryKind kind)
    {
        return kind switch
        {
            EditorMapGeometryKind.Air => ImGui.GetColorU32(new Num.Vector4(0.58f, 0.59f, 0.60f, 1.00f)),
            EditorMapGeometryKind.BackWall => ImGui.GetColorU32(new Num.Vector4(0.47f, 0.48f, 0.49f, 1.00f)),
            EditorMapGeometryKind.Solid => ImGui.GetColorU32(new Num.Vector4(0.29f, 0.30f, 0.31f, 1.00f)),
            EditorMapGeometryKind.Structure => ImGui.GetColorU32(new Num.Vector4(0.58f, 0.31f, 0.31f, 1.00f)),
            EditorMapGeometryKind.Shortcut => ImGui.GetColorU32(new Num.Vector4(0.84f, 0.85f, 0.84f, 1.00f)),
            EditorMapGeometryKind.Transport => ImGui.GetColorU32(new Num.Vector4(0.72f, 0.20f, 0.28f, 1.00f)),
            EditorMapGeometryKind.Water => ImGui.GetColorU32(new Num.Vector4(0.12f, 0.34f, 0.78f, 0.24f)),
            EditorMapGeometryKind.LocalTerrain => ImGui.GetColorU32(new Num.Vector4(0.73f, 0.46f, 0.39f, 1.00f)),
            EditorMapGeometryKind.CurvedSlope => ImGui.GetColorU32(new Num.Vector4(0.88f, 0.89f, 0.90f, 1.00f)),
            EditorMapGeometryKind.QuicksandMaterial => ImGui.GetColorU32(ImGuiCol.ButtonHovered),
            EditorMapGeometryKind.QuicksandBody => ImGui.GetColorU32(ImGuiCol.Separator),
            _ => ImGui.GetColorU32(ImGuiCol.Border)
        };
    }

    private static void DrawToolbarHook(OrigDrawToolbar orig, EditorMapPresentationSnapshot snapshot)
    {
        orig(snapshot);
        if (snapshot?.Available != true) return;

        if (DevToolWidgets.SameLineIfFits(285f, 8f))
        {
            string state = WorldMapGpuScene.Ready
                ? "GPU " + WorldMapGpuScene.RetainedChunkCount + "/" + WorldMapGpuScene.RetainedRouteCount +
                  " · view " + WorldMapGpuScene.VisibleRoomCount + "/" + WorldMapGpuScene.RetainedRoomCount
                : DevToolUiSettings.T("GPU 初始化", "GPU init");
            ImGui.TextDisabled("· " + state + " · " + DevToolUiSettings.T("缓存 ", "cache ") + WorldMapGpuCache.CachedRoomCount);
            ImGui.SameLine();
            if (DevToolWidgets.ActionButton(
                    DevToolUiSettings.T("重烘焙", "Rebake"),
                    "WorldMapGpuRebake",
                    DevToolButtonTone.Subtle))
                Interlocked.Exchange(ref requestRebuild, 1);
        }

        if (!string.IsNullOrEmpty(WorldMapGpuScene.Error) && ImGui.IsItemHovered())
            DevToolTooltip.Draw(WorldMapGpuScene.Error);
    }

    private static void GeometryPrimeHook(OrigGeometryPrime orig, EditorSession session)
    {
        if (Thread.CurrentThread.ManagedThreadId != mainThreadId) return;
        orig(session);
    }

    private static EditorMapRoomVisualSnapshot GeometryGetHook(OrigGeometryGet orig, int roomIndex)
    {
        return orig(roomIndex);
    }

    private static void ShortcutPrimeHook(OrigShortcutPrime orig, EditorSession session, int selectedRoomIndex)
    {
        if (Thread.CurrentThread.ManagedThreadId != mainThreadId) return;
        orig(session, selectedRoomIndex);
    }

    private static bool TryGetExitHook(
        OrigTryGetExit orig,
        int roomIndex,
        int nodeIndex,
        out WorldMapShortcutPresentation.ShortcutMarker marker)
    {
        return orig(roomIndex, nodeIndex, out marker);
    }

    private static WorldMapShortcutPresentation.ShortcutMarker[] GetCreatureHolesHook(
        OrigGetCreatureHoles orig,
        int roomIndex)
    {
        return orig(roomIndex);
    }

    private static void PublishFrame(
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        Num.Vector2 canvasSize,
        Num.Vector2 displaySize,
        Num.Vector2 pan,
        float zoom,
        bool showConnections,
        int layerMask)
    {
        Dictionary<int, Num.Vector2> positions = localPositionsField?.GetValue(null) as Dictionary<int, Num.Vector2>;
        EditorMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        int layoutHash = ComputeLayoutHash(rooms, positions);
        if (layoutHash != cachedLayoutHash || cachedPlacements.Length != rooms.Length)
        {
            WorldMapGpuScene.RoomPlacement[] placements = new WorldMapGpuScene.RoomPlacement[rooms.Length];
            for (int i = 0; i < rooms.Length; i++)
            {
                EditorMapRoomSnapshot room = rooms[i];
                if (room == null) continue;
                Num.Vector2 position = positions != null && positions.TryGetValue(room.RoomIndex, out Num.Vector2 local)
                    ? local
                    : new Num.Vector2(room.X, room.Y);
                placements[i] = new WorldMapGpuScene.RoomPlacement(room.RoomIndex, position.X, position.Y);
            }
            cachedPlacements = placements;
            cachedLayoutHash = layoutHash;
        }

        latestFrame = new WorldMapGpuScene.FrameState
        {
            Visible = true,
            Region = snapshot.RegionName ?? string.Empty,
            CanvasMin = canvasMin,
            CanvasSize = canvasSize,
            DisplaySize = displaySize,
            Pan = pan,
            Zoom = zoom,
            LayerMask = layerMask,
            LayoutHash = layoutHash,
            ShowConnections = showConnections,
            SelectedRoomIndex = snapshot.SelectedRoomIndex,
            HoveredRoomIndex = frameHoveredRoomIndex,
            SelectedConnectionId = selectedConnectionIdField?.GetValue(null) as string ?? string.Empty,
            HoveredConnectionId = hoveredConnectionIdField?.GetValue(null) as string ?? string.Empty,
            Snapshot = snapshot,
            Placements = cachedPlacements
        };
    }

    private static int CurrentLayerMask()
    {
        bool[] layers = layerVisibleField?.GetValue(null) as bool[];
        int layerMask = 0;
        for (int i = 0; i < 3; i++)
            if (layers == null || i >= layers.Length || layers[i]) layerMask |= 1 << i;
        return layerMask;
    }

    private static int ComputeLayoutHash(EditorMapRoomSnapshot[] rooms, Dictionary<int, Num.Vector2> positions)
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 397 ^ rooms.Length;
            for (int i = 0; i < rooms.Length; i++)
            {
                EditorMapRoomSnapshot room = rooms[i];
                if (room == null) continue;
                Num.Vector2 position = positions != null && positions.TryGetValue(room.RoomIndex, out Num.Vector2 local)
                    ? local
                    : new Num.Vector2(room.X, room.Y);
                hash = hash * 397 ^ room.RoomIndex;
                hash = hash * 397 ^ Quantize(position.X);
                hash = hash * 397 ^ Quantize(position.Y);
            }
            return hash;
        }
    }

    private static int Quantize(float value) => (int)Math.Round(value * 16f);

    private static Num.Vector2 ScreenToMap(
        Num.Vector2 screen,
        Num.Vector2 canvasMin,
        Num.Vector2 pan,
        float zoom)
    {
        float safeZoom = Math.Max(0.0001f, zoom);
        return (screen - canvasMin - pan) / safeZoom;
    }

    private static bool PointInside(Num.Vector2 point, Num.Vector2 min, Num.Vector2 max) =>
        point.X >= min.X && point.X <= max.X && point.Y >= min.Y && point.Y <= max.Y;

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
