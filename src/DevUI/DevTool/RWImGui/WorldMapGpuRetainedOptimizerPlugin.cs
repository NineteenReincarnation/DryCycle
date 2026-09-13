using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DevInterface;
using DryCycle.DevUI.DevTool.Map;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Lightweight source-revision guard for the retained GPU World Map.
///
/// Dirty chunk ownership now lives directly inside WorldMapGpuScene. This compatibility plugin keeps
/// only the useful source-hash optimization from the earlier second-stage optimizer: source validity
/// is driven by WorldMapGpuCache.Generation instead of repeatedly searching MapPage.subNodes for
/// every room on every frame. Keeping a single owner for room meshes avoids double retained scenes,
/// double hooks and conflicting visibility state.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldMapGpuRendererPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapGpuRetainedOptimizerPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMapGPU.RetainedOptimizer";
    public const string PluginName = "DryCycle DevTool GPU World Map Retained Optimizer";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldMapGpuRetainedOptimizer.Enable(Logger);
    private void OnDisable() => WorldMapGpuRetainedOptimizer.Disable();
}

internal static class WorldMapGpuRetainedOptimizer
{
    private delegate int OrigComputeRoomSourceHash(MapPage page, WorldMapGpuScene.FrameState frame);
    private delegate int HookComputeRoomSourceHash(
        OrigComputeRoomSourceHash orig,
        MapPage page,
        WorldMapGpuScene.FrameState frame);

    private static readonly HookComputeRoomSourceHash SourceHashHookDelegate = ComputeRoomSourceHashHook;

    private static ManualLogSource log;
    private static IDisposable sourceHashHook;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;

        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            MethodInfo sourceHash = typeof(WorldMapGpuScene).GetMethod(
                "ComputeRoomSourceHash",
                flags,
                null,
                new[] { typeof(MapPage), typeof(WorldMapGpuScene.FrameState) },
                null);
            if (sourceHash == null)
                throw new MissingMemberException("World Map source revision target was not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            if (hookType == null)
                throw new TypeLoadException("MonoMod.RuntimeDetour.Hook is unavailable.");
            ConstructorInfo constructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            sourceHashHook = constructor.Invoke(new object[] { sourceHash, SourceHashHookDelegate }) as IDisposable;
            enabled = true;
            log?.LogInfo("GPU World Map source revision cache enabled.");
        }
        catch (Exception error)
        {
            Disable();
            log?.LogWarning("GPU World Map source revision cache could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        try { sourceHashHook?.Dispose(); }
        catch { }
        sourceHashHook = null;
        enabled = false;
        log = null;
    }

    private static int ComputeRoomSourceHashHook(
        OrigComputeRoomSourceHash orig,
        MapPage page,
        WorldMapGpuScene.FrameState frame)
    {
        if (!enabled || frame?.Snapshot?.Available != true)
            return orig(page, frame);

        // WorldMapGpuCache already owns source validation. Its generation changes only when cached
        // room data is invalidated/replaced, so the renderer does not need an O(N²) MapPage search.
        unchecked
        {
            int hash = 17;
            hash = hash * 397 ^ WorldMapGpuCache.Generation;
            EditorMapRoomSnapshot[] rooms = frame.Snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
            hash = hash * 397 ^ rooms.Length;
            for (int i = 0; i < rooms.Length; i++)
            {
                EditorMapRoomSnapshot room = rooms[i];
                if (room == null) continue;
                hash = hash * 397 ^ room.RoomIndex;
                hash = hash * 397 ^ room.Layer;
            }
            return hash;
        }
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
