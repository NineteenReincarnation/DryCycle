using System;
using System.Collections.Generic;
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

    private static ManualLogSource log;

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
        enabled = true;
        WorldMapFrontendBridge.RegisterAllowPresentationPrime(AllowPresentationPrime);
        logger?.LogInfo("Retained GPU World Map integration enabled through direct view/presentation APIs; no self-detours attached.");
    }

    internal static void Disable()
    {
        WorldMapFrontendBridge.UnregisterAllowPresentationPrime(AllowPresentationPrime);
        latestFrame = null;
        cachedPlacements = Array.Empty<WorldMapGpuScene.RoomPlacement>();
        cachedLayoutHash = int.MinValue;
        frameHoveredRoomIndex = -1;
        requestRebuild = 0;
        WorldMapGpuCache.FlushNow();
        WorldMapGpuScene.Disable();
        enabled = false;
        log = null;
    }

    internal static bool AllowPresentationPrime() =>
        !enabled || Thread.CurrentThread.ManagedThreadId == mainThreadId;

    internal static void SetHoveredRoom(int roomIndex) =>
        frameHoveredRoomIndex = roomIndex;

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

    internal static bool TryDrawRoomGeometry(
        ImDrawListPtr draw,
        EditorMapRoomSnapshot room,
        EditorMapRoomVisualSnapshot visual,
        Num.Vector2 roomMin,
        bool selected,
        bool hovered,
        float zoom)
    {
        if (!enabled || !WorldMapGpuScene.Ready)
            return false;

        if (hovered && room != null) frameHoveredRoomIndex = room.RoomIndex;

        if (zoom < SemanticRoomLodZoom && visual?.DetailedRasterAvailable == true)
        {
            DrawSemanticRoomLod(draw, room, visual, roomMin, zoom);
            return true;
        }

        if (selected || hovered)
            return true;

        float width = Math.Max(1f, visual?.WidthTiles ?? 12f) * WorldMapGpuScene.TileDisplaySize * zoom;
        float height = Math.Max(1f, visual?.HeightTiles ?? 6f) * WorldMapGpuScene.TileDisplaySize * zoom;
        Num.Vector2 max = roomMin + new Num.Vector2(width, height);
        uint color = ImGui.GetColorU32(
            room?.CurrentRoom == true ? ImGuiCol.Header :
            room?.Disabled == true ? ImGuiCol.TextDisabled : ImGuiCol.Border);
        draw.AddRect(roomMin, max, color, Math.Max(1f, 3f * zoom), ImDrawFlags.None, 1f);
        return true;
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

    internal static void DrawToolbar(EditorMapPresentationSnapshot snapshot)
    {
        if (!enabled || snapshot?.Available != true) return;

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

    internal static void PublishFrame(
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        Num.Vector2 canvasSize,
        Num.Vector2 displaySize,
        Num.Vector2 pan,
        float zoom,
        bool showConnections,
        bool[] layerVisible,
        Dictionary<int, Num.Vector2> positions,
        string selectedConnectionId,
        string hoveredConnectionId)
    {
        if (!enabled || snapshot?.Available != true) return;

        int layerMask = 0;
        for (int i = 0; i < 3; i++)
            if (layerVisible == null || i >= layerVisible.Length || layerVisible[i]) layerMask |= 1 << i;

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
            SelectedConnectionId = selectedConnectionId ?? string.Empty,
            HoveredConnectionId = hoveredConnectionId ?? string.Empty,
            Snapshot = snapshot,
            Placements = cachedPlacements
        };
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

}
