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
/// ImGui remains responsible for layout, toolbar, inspector, labels and direct manipulation. Static
/// room raster/topology and the high-frequency selection/hover highlights live in retained GPU
/// layers. If GPU setup fails, the original ImGui map remains usable.
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
            // The old routed overlay routes in screen space and therefore has an unavoidable
            // zoom-time CPU cost. Keep it only as fallback/reference while retained mode is active.
            WorldConnectionOverlay.Disable();

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
            log?.LogInfo("Retained GPU World Map integration enabled.");
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
            WorldMapGpuCache.Update(session, snapshot);
            if (!WorldMapGpuCache.HasCompleteCachedData(snapshot))
            {
                MapRoomGeometryPresentationHub.Prime(session);
                WorldMapShortcutPresentation.Prime(session, snapshot.SelectedRoomIndex);
                WorldMapGpuCache.Update(session, snapshot);
            }
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
        bool showConnections = showConnectionsField?.GetValue(null) is bool links && links;
        int layerMask = CurrentLayerMask();

        bool mouseInside = PointInside(io.MousePos, canvasMin, canvasMin + canvasSize);
        WorldMapGpuScene.RouteHit preHit = null;
        frameHoveredRoomIndex = -1;
        if (gpuReady && mouseInside)
        {
            Num.Vector2 mapPoint = ScreenToMap(io.MousePos, canvasMin, pan, zoom);
            WorldMapGpuScene.TryHitRoom(mapPoint, layerMask, out frameHoveredRoomIndex);
            if (showConnections)
                WorldMapGpuScene.TryHitConnection(mapPoint, 12f / Math.Max(0.20f, zoom), out preHit);
        }

        // The base canvas keeps input, labels and shortcut authoring, but static routes and room
        // focus outlines are now supplied by retained GPU meshes.
        bool suppressImmediateConnections = gpuReady && showConnections;
        if (suppressImmediateConnections) showConnectionsField.SetValue(null, false);
        if (gpuReady) ImGui.PushStyleColor(ImGuiCol.ChildBg, new Num.Vector4(0f, 0f, 0f, 0f));
        try
        {
            orig(snapshot);
        }
        finally
        {
            if (gpuReady) ImGui.PopStyleColor();
            if (suppressImmediateConnections) showConnectionsField.SetValue(null, true);
        }

        if (gpuReady && showConnections)
        {
            WorldMapGpuScene.RouteHit hit = preHit;
            if (mouseInside)
            {
                Num.Vector2 mapPoint = ScreenToMap(io.MousePos, canvasMin, pan, zoom);
                WorldMapGpuScene.TryHitConnection(mapPoint, 12f / Math.Max(0.20f, zoom), out hit);
            }

            string hovered = hit?.Connection?.ConnectionId ?? string.Empty;
            hoveredConnectionIdField.SetValue(null, hovered);
            if (hit != null)
            {
                // A focused route owns the interaction layer above a room at the same pixel.
                frameHoveredRoomIndex = -1;
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    draggingRoomField.SetValue(null, -1);
                    WorldMapView.SelectConnection(hovered);
                }
                if (mouseInside) DrawRouteTooltip(hit.Connection);
            }
        }

        PublishFrame(snapshot, canvasMin, canvasSize, io.DisplaySize, pan, zoom, showConnections, layerMask);
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

        // Selection/hover have moved to a separate dynamic retained mesh. Keeping them out of the
        // ImGui room pass prevents the whole interaction effect from being regenerated as immediate
        // draw commands every frame.
        if (selected || hovered) return;

        float zoom = zoomField?.GetValue(null) is float z ? z : 1f;
        float width = Math.Max(1f, visual?.WidthTiles ?? 12f) * WorldMapGpuScene.TileDisplaySize * zoom;
        float height = Math.Max(1f, visual?.HeightTiles ?? 6f) * WorldMapGpuScene.TileDisplaySize * zoom;
        Num.Vector2 max = roomMin + new Num.Vector2(width, height);
        uint color = ImGui.GetColorU32(
            room?.CurrentRoom == true ? ImGuiCol.Header :
            room?.Disabled == true ? ImGuiCol.TextDisabled : ImGuiCol.Border);
        draw.AddRect(roomMin, max, color, Math.Max(1f, 3f * zoom), ImDrawFlags.None, 1f);
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
        if (MapEditorPresentationHub.Current.Available &&
            WorldMapGpuCache.HasCompleteCachedData(MapEditorPresentationHub.Current))
            return;
        orig(session);
    }

    private static EditorMapRoomVisualSnapshot GeometryGetHook(OrigGeometryGet orig, int roomIndex)
    {
        if (WorldMapGpuCache.TryGetRoom(roomIndex, out WorldMapGpuCache.RoomBake bake) &&
            bake.Visual?.Available == true)
            return bake.Visual;
        return orig(roomIndex);
    }

    private static void ShortcutPrimeHook(OrigShortcutPrime orig, EditorSession session, int selectedRoomIndex)
    {
        if (Thread.CurrentThread.ManagedThreadId != mainThreadId) return;
        if (MapEditorPresentationHub.Current.Available &&
            WorldMapGpuCache.HasCompleteCachedData(MapEditorPresentationHub.Current))
            return;
        orig(session, selectedRoomIndex);
    }

    private static bool TryGetExitHook(
        OrigTryGetExit orig,
        int roomIndex,
        int nodeIndex,
        out WorldMapShortcutPresentation.ShortcutMarker marker)
    {
        if (WorldMapGpuCache.TryGetExit(roomIndex, nodeIndex, out marker)) return true;
        return orig(roomIndex, nodeIndex, out marker);
    }

    private static WorldMapShortcutPresentation.ShortcutMarker[] GetCreatureHolesHook(
        OrigGetCreatureHoles orig,
        int roomIndex)
    {
        if (WorldMapGpuCache.TryGetRoom(roomIndex, out WorldMapGpuCache.RoomBake bake) && bake.ShortcutsReady)
            return bake.CreatureHoles ?? Array.Empty<WorldMapShortcutPresentation.ShortcutMarker>();
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

    private static void DrawRouteTooltip(EditorMapConnectionSnapshot connection)
    {
        if (connection == null) return;
        ImGui.BeginTooltip();
        ImGui.TextUnformatted(
            connection.FromRoomIndex + ":" + connection.FromNodeIndex + " " +
            DirectionText(connection.Direction) + " " +
            connection.ToRoomIndex + ":" + connection.ToNodeIndex);
        ImGui.EndTooltip();
    }

    private static string DirectionText(DryCycle.DevUI.DevTool.World.WorldConnectionDirection direction) =>
        direction switch
        {
            DryCycle.DevUI.DevTool.World.WorldConnectionDirection.AToB => "->",
            DryCycle.DevUI.DevTool.World.WorldConnectionDirection.BToA => "<-",
            _ => "<->"
        };

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
