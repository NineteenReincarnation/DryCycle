using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Map;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Routes legacy WorldMapView room hover queries through the retained scene spatial index.
/// The GPU renderer already owns authoritative map-space room bounds for culling and dynamic
/// overlays; reusing that index avoids a second full-room scan in the immediate interaction layer.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldMapGpuRegionPreloadPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapGpuInteractionIndexPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMapGPU.InteractionIndex";
    public const string PluginName = "DryCycle DevTool GPU World Map Interaction Index";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldMapGpuInteractionIndex.Enable(Logger);
    private void OnDisable() => WorldMapGpuInteractionIndex.Disable();
}

internal static class WorldMapGpuInteractionIndex
{
    private delegate EditorMapRoomSnapshot OrigFindHoveredRoom(
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        Num.Vector2 canvasSize,
        Num.Vector2 mouse);
    private delegate EditorMapRoomSnapshot HookFindHoveredRoom(
        OrigFindHoveredRoom orig,
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        Num.Vector2 canvasSize,
        Num.Vector2 mouse);

    private static readonly HookFindHoveredRoom FindHoveredRoomHookDelegate = FindHoveredRoomHook;
    private static readonly Dictionary<int, EditorMapRoomSnapshot> roomLookup = new();

    private static ManualLogSource log;
    private static IDisposable hoverHook;
    private static FieldInfo panField;
    private static FieldInfo zoomField;
    private static FieldInfo layerVisibleField;
    private static EditorMapPresentationSnapshot indexedSnapshot;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;
        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            Type mapType = typeof(WorldMapView);
            MethodInfo findHoveredRoom = mapType.GetMethod(
                "FindHoveredRoom",
                flags,
                null,
                new[]
                {
                    typeof(EditorMapPresentationSnapshot), typeof(Num.Vector2),
                    typeof(Num.Vector2), typeof(Num.Vector2)
                },
                null);
            panField = mapType.GetField("pan", flags);
            zoomField = mapType.GetField("zoom", flags);
            layerVisibleField = mapType.GetField("layerVisible", flags);
            if (findHoveredRoom == null || panField == null || zoomField == null || layerVisibleField == null)
                throw new MissingMemberException("World Map spatial hover targets were not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            if (hookType == null)
                throw new TypeLoadException("MonoMod.RuntimeDetour.Hook is unavailable.");
            ConstructorInfo constructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            hoverHook = constructor.Invoke(new object[] { findHoveredRoom, FindHoveredRoomHookDelegate }) as IDisposable;
            enabled = true;
            log?.LogInfo("GPU World Map spatial hover index enabled.");
        }
        catch (Exception error)
        {
            string message = Unwrap(error).Message;
            Disable();
            logger?.LogWarning("GPU World Map spatial hover index could not attach: " + message);
        }
    }

    internal static void Disable()
    {
        try { hoverHook?.Dispose(); }
        catch { }
        hoverHook = null;
        panField = null;
        zoomField = null;
        layerVisibleField = null;
        indexedSnapshot = null;
        roomLookup.Clear();
        enabled = false;
        log = null;
    }

    private static EditorMapRoomSnapshot FindHoveredRoomHook(
        OrigFindHoveredRoom orig,
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        Num.Vector2 canvasSize,
        Num.Vector2 mouse)
    {
        if (!enabled || !WorldMapGpuScene.Ready || snapshot?.Available != true)
            return orig(snapshot, canvasMin, canvasSize, mouse);

        try
        {
            Num.Vector2 pan = panField.GetValue(null) is Num.Vector2 p ? p : Num.Vector2.Zero;
            float zoom = zoomField.GetValue(null) is float z ? Math.Max(0.0001f, z) : 1f;
            Num.Vector2 mapPoint = (mouse - canvasMin - pan) / zoom;
            int layerMask = CurrentLayerMask();
            if (!WorldMapGpuScene.TryHitRoom(mapPoint, layerMask, out int roomIndex)) return null;

            EnsureLookup(snapshot);
            return roomLookup.TryGetValue(roomIndex, out EditorMapRoomSnapshot room) ? room : null;
        }
        catch
        {
            return orig(snapshot, canvasMin, canvasSize, mouse);
        }
    }

    private static void EnsureLookup(EditorMapPresentationSnapshot snapshot)
    {
        if (ReferenceEquals(indexedSnapshot, snapshot)) return;
        indexedSnapshot = snapshot;
        roomLookup.Clear();
        EditorMapRoomSnapshot[] rooms = snapshot?.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
        {
            EditorMapRoomSnapshot room = rooms[i];
            if (room != null) roomLookup[room.RoomIndex] = room;
        }
    }

    private static int CurrentLayerMask()
    {
        bool[] layers = layerVisibleField?.GetValue(null) as bool[];
        int mask = 0;
        for (int i = 0; i < 3; i++)
            if (layers == null || i >= layers.Length || layers[i]) mask |= 1 << i;
        return mask;
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
