using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Map;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Keeps World Map room thumbnails readable independently of zoom and hover state.
///
/// At overview zoom a room tile is often smaller than one screen pixel. The old dark FrameBg/
/// Border fallback and low-luminance solid terrain then merged into the canvas after rasterization,
/// making a room appear only when hover switched its outline to ButtonHovered. This presentation
/// layer gives the immediate renderer a stable, higher-contrast room palette. Zoom remains purely
/// geometric; hover/selection can emphasize a room but never determines whether it is visible.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldMapImGuiPresentationFallbackPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapThumbnailVisibilityPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMap.ThumbnailVisibility";
    public const string PluginName = "DryCycle DevTool World Map Thumbnail Visibility";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldMapThumbnailVisibility.Enable(Logger);
    private void OnDisable() => WorldMapThumbnailVisibility.Disable();
}

internal static class WorldMapThumbnailVisibility
{
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

    private delegate uint OrigGeometryColor(EditorMapGeometryKind kind);
    private delegate uint HookGeometryColor(OrigGeometryColor orig, EditorMapGeometryKind kind);

    private static readonly HookDrawRoomGeometry DrawRoomGeometryHookDelegate = DrawRoomGeometryHook;
    private static readonly HookGeometryColor GeometryColorHookDelegate = GeometryColorHook;

    private static IDisposable roomGeometryHook;
    private static IDisposable geometryColorHook;
    private static ManualLogSource log;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;

        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            Type mapType = typeof(WorldMapView);

            MethodInfo drawRoomGeometry = mapType.GetMethod(
                "DrawRoomGeometry",
                flags,
                null,
                new[]
                {
                    typeof(ImDrawListPtr),
                    typeof(EditorMapRoomSnapshot),
                    typeof(EditorMapRoomVisualSnapshot),
                    typeof(Num.Vector2),
                    typeof(bool),
                    typeof(bool)
                },
                null);
            MethodInfo geometryColor = mapType.GetMethod(
                "GeometryColor",
                flags,
                null,
                new[] { typeof(EditorMapGeometryKind) },
                null);

            if (drawRoomGeometry == null || geometryColor == null)
                throw new MissingMemberException("World Map thumbnail presentation targets were not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            if (hookType == null)
                throw new TypeLoadException("MonoMod.RuntimeDetour.Hook is unavailable.");

            ConstructorInfo constructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            roomGeometryHook = constructor.Invoke(new object[]
            {
                drawRoomGeometry,
                DrawRoomGeometryHookDelegate
            }) as IDisposable;
            geometryColorHook = constructor.Invoke(new object[]
            {
                geometryColor,
                GeometryColorHookDelegate
            }) as IDisposable;

            if (roomGeometryHook == null || geometryColorHook == null)
                throw new InvalidOperationException("World Map thumbnail visibility hooks were not created.");

            enabled = true;
            log?.LogInfo("World Map thumbnails use zoom-independent readable contrast.");
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("World Map thumbnail visibility could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        DisposeHook(ref geometryColorHook);
        DisposeHook(ref roomGeometryHook);
        enabled = false;
        log = null;
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
        // The fallback fill and the ordinary room outline are the two theme colors that used to
        // disappear into the dark World Map canvas. Override them only for this room draw, then
        // restore the caller's ImGui style immediately. There is deliberately no zoom branch here.
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Num.Vector4(0.35f, 0.36f, 0.38f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Num.Vector4(0.62f, 0.64f, 0.66f, 0.95f));
        try
        {
            orig(draw, room, visual, roomMin, selected, hovered);
        }
        finally
        {
            ImGui.PopStyleColor(2);
        }
    }

    private static uint GeometryColorHook(OrigGeometryColor orig, EditorMapGeometryKind kind)
    {
        // Keep semantic/special-terrain colors owned by WorldMapView. Only lift the dark bulk
        // materials that lose contrast during sub-pixel overview rendering.
        return kind switch
        {
            EditorMapGeometryKind.Air =>
                ImGui.GetColorU32(new Num.Vector4(0.68f, 0.69f, 0.70f, 1.00f)),
            EditorMapGeometryKind.BackWall =>
                ImGui.GetColorU32(new Num.Vector4(0.56f, 0.57f, 0.58f, 1.00f)),
            EditorMapGeometryKind.Solid =>
                ImGui.GetColorU32(new Num.Vector4(0.41f, 0.42f, 0.43f, 1.00f)),
            EditorMapGeometryKind.Structure =>
                ImGui.GetColorU32(new Num.Vector4(0.66f, 0.35f, 0.35f, 1.00f)),
            _ => orig(kind)
        };
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
