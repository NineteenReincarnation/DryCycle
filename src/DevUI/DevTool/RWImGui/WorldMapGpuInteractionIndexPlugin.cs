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
            if (findHoveredRoom == null)
                throw new MissingMemberException("World Map spatial hover target was not found.");

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
        indexedSnapshot = null;
        roomLookup.Clear();
        WorldMapHotState.Invalidate();
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
            Num.Vector2 pan = WorldMapHotState.Pan;
            float zoom = WorldMapHotState.Zoom;
            Num.Vector2 mapPoint = (mouse - canvasMin - pan) / zoom;
            if (!WorldMapGpuScene.TryHitRoom(mapPoint, WorldMapHotState.LayerMask, out int roomIndex))
                return null;

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

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
