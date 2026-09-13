using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Map;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Gives the ImGui-backed World Map an explicit render stack.
///
/// Immediate draw commands used to rely on call order. DrawConnections happened before DrawRooms,
/// so the room preview raster covered links that should visually run above the map thumbnail. ImGui
/// draw-list channels let us keep the existing interaction/control flow while making composition
/// deterministic:
///
///   channel 0: canvas background/grid + room preview geometry
///   channel 1: authored world connections
///   channel 2: room labels, pipe sockets, node ids and interaction overlays
///
/// The channels are merged only once at the end of WorldMapView.DrawCanvas. This also avoids
/// introducing another mesh/camera layer while the retained renderer is intentionally suppressed.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldMapPresentationCorrectnessPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapRenderOrderPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMap.RenderOrder";
    public const string PluginName = "DryCycle DevTool World Map Render Order";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldMapRenderOrder.Enable(Logger);
    private void OnDisable() => WorldMapRenderOrder.Disable();
}

internal static class WorldMapRenderOrder
{
    private const int BaseChannel = 0;
    private const int ConnectionChannel = 1;
    private const int OverlayChannel = 2;
    private const int ChannelCount = 3;

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

    private delegate void OrigDrawConnections(
        ImDrawListPtr draw,
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        Num.Vector2 canvasSize);
    private delegate void HookDrawConnections(
        OrigDrawConnections orig,
        ImDrawListPtr draw,
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        Num.Vector2 canvasSize);

    private delegate void OrigDrawRoomLabel(
        ImDrawListPtr draw,
        EditorMapRoomSnapshot room,
        Num.Vector2 min,
        Num.Vector2 max,
        bool selected,
        bool hovered);
    private delegate void HookDrawRoomLabel(
        OrigDrawRoomLabel orig,
        ImDrawListPtr draw,
        EditorMapRoomSnapshot room,
        Num.Vector2 min,
        Num.Vector2 max,
        bool selected,
        bool hovered);

    private static readonly HookDrawCanvas DrawCanvasHookDelegate = DrawCanvasHook;
    private static readonly HookDrawRoomGeometry DrawRoomGeometryHookDelegate = DrawRoomGeometryHook;
    private static readonly HookDrawConnections DrawConnectionsHookDelegate = DrawConnectionsHook;
    private static readonly HookDrawRoomLabel DrawRoomLabelHookDelegate = DrawRoomLabelHook;

    private static ManualLogSource log;
    private static IDisposable canvasHook;
    private static IDisposable roomGeometryHook;
    private static IDisposable connectionsHook;
    private static IDisposable roomLabelHook;
    private static bool enabled;
    private static bool channelsActive;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;

        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            Type mapType = typeof(WorldMapView);

            MethodInfo drawCanvas = mapType.GetMethod(
                "DrawCanvas",
                flags,
                null,
                new[] { typeof(EditorMapPresentationSnapshot) },
                null);
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
            MethodInfo drawConnections = mapType.GetMethod(
                "DrawConnections",
                flags,
                null,
                new[]
                {
                    typeof(ImDrawListPtr), typeof(EditorMapPresentationSnapshot),
                    typeof(Num.Vector2), typeof(Num.Vector2)
                },
                null);
            MethodInfo drawRoomLabel = mapType.GetMethod(
                "DrawRoomLabel",
                flags,
                null,
                new[]
                {
                    typeof(ImDrawListPtr), typeof(EditorMapRoomSnapshot),
                    typeof(Num.Vector2), typeof(Num.Vector2), typeof(bool), typeof(bool)
                },
                null);

            if (drawCanvas == null || drawRoomGeometry == null || drawConnections == null || drawRoomLabel == null)
                throw new MissingMemberException("World Map render-order targets were not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            if (hookType == null)
                throw new TypeLoadException("MonoMod.RuntimeDetour.Hook is unavailable.");
            ConstructorInfo constructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            canvasHook = constructor.Invoke(new object[] { drawCanvas, DrawCanvasHookDelegate }) as IDisposable;
            roomGeometryHook = constructor.Invoke(new object[] { drawRoomGeometry, DrawRoomGeometryHookDelegate }) as IDisposable;
            connectionsHook = constructor.Invoke(new object[] { drawConnections, DrawConnectionsHookDelegate }) as IDisposable;
            roomLabelHook = constructor.Invoke(new object[] { drawRoomLabel, DrawRoomLabelHookDelegate }) as IDisposable;

            if (canvasHook == null || roomGeometryHook == null || connectionsHook == null || roomLabelHook == null)
                throw new InvalidOperationException("One or more World Map render-order hooks were not created.");

            enabled = true;
            log?.LogInfo("World Map render order enabled: rooms < connections < overlays.");
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("World Map render order could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        DisposeHook(ref roomLabelHook);
        DisposeHook(ref connectionsHook);
        DisposeHook(ref roomGeometryHook);
        DisposeHook(ref canvasHook);
        channelsActive = false;
        enabled = false;
        log = null;
    }

    private static void DrawCanvasHook(OrigDrawCanvas orig, EditorMapPresentationSnapshot snapshot)
    {
        if (!enabled || snapshot?.Available != true || channelsActive)
        {
            orig(snapshot);
            return;
        }

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        bool split = false;
        try
        {
            draw.ChannelsSplit(ChannelCount);
            split = true;
            channelsActive = true;
            draw.ChannelsSetCurrent(BaseChannel);
            orig(snapshot);
        }
        finally
        {
            channelsActive = false;
            if (split)
            {
                try
                {
                    draw.ChannelsSetCurrent(BaseChannel);
                    draw.ChannelsMerge();
                }
                catch (Exception error)
                {
                    log?.LogDebug("World Map channel merge failed: " + error.Message);
                }
            }
        }
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
        if (channelsActive) draw.ChannelsSetCurrent(BaseChannel);
        orig(draw, room, visual, roomMin, selected, hovered);
    }

    private static void DrawConnectionsHook(
        OrigDrawConnections orig,
        ImDrawListPtr draw,
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        Num.Vector2 canvasSize)
    {
        if (channelsActive) draw.ChannelsSetCurrent(ConnectionChannel);
        try
        {
            orig(draw, snapshot, canvasMin, canvasSize);
        }
        finally
        {
            // Everything following the connection pass is interaction/UI unless the next room
            // explicitly switches back to BaseChannel in DrawRoomGeometryHook.
            if (channelsActive) draw.ChannelsSetCurrent(OverlayChannel);
        }
    }

    private static void DrawRoomLabelHook(
        OrigDrawRoomLabel orig,
        ImDrawListPtr draw,
        EditorMapRoomSnapshot room,
        Num.Vector2 min,
        Num.Vector2 max,
        bool selected,
        bool hovered)
    {
        if (channelsActive) draw.ChannelsSetCurrent(OverlayChannel);
        orig(draw, room, min, max, selected, hovered);
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
