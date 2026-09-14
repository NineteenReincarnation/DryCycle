using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Map;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Retains the structural hash used by the GPU pipe batch.
///
/// PipeBatch only rebuilds its mesh when the hash changes, but computing that hash used to rescan
/// every room node and connection on every stable frame. EditorMapPresentationSnapshot is immutable,
/// so snapshot identity plus the explicit layout/view/cache keys fully describes the original hash
/// inputs. The expensive structural walk is therefore paid once per real key change.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldMapGpuPipeBatchPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapGpuPipeHashCachePlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMapGPU.PipeHashCache";
    public const string PluginName = "DryCycle DevTool GPU World Map Pipe Hash Cache";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldMapGpuPipeHashCache.Enable(Logger);
    private void OnDisable() => WorldMapGpuPipeHashCache.Disable();
}

internal static class WorldMapGpuPipeHashCache
{
    private delegate int OrigComputeHash(
        WorldMapGpuScene.FrameState frame,
        bool roomVisible,
        bool creatureVisible);
    private delegate int HookComputeHash(
        OrigComputeHash orig,
        WorldMapGpuScene.FrameState frame,
        bool roomVisible,
        bool creatureVisible);
    private delegate void OrigDisableScene();
    private delegate void HookDisableScene(OrigDisableScene orig);

    private static readonly HookComputeHash ComputeHashHookDelegate = ComputeHashHook;
    private static readonly HookDisableScene DisableSceneHookDelegate = DisableSceneHook;

    private static ManualLogSource log;
    private static IDisposable computeHashHook;
    private static IDisposable disableSceneHook;
    private static EditorMapPresentationSnapshot cachedSnapshot;
    private static int cachedGeneration = int.MinValue;
    private static int cachedLayoutHash = int.MinValue;
    private static int cachedLayerMask = int.MinValue;
    private static int cachedZoomKey = int.MinValue;
    private static bool cachedRoomVisible;
    private static bool cachedCreatureVisible;
    private static int cachedHash;
    private static bool cacheValid;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;

        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            MethodInfo computeHash = typeof(WorldMapGpuPipeBatch).GetMethod(
                "ComputeHash",
                flags,
                null,
                new[] { typeof(WorldMapGpuScene.FrameState), typeof(bool), typeof(bool) },
                null);
            MethodInfo disableScene = typeof(WorldMapGpuScene).GetMethod(
                "Disable",
                flags,
                null,
                Type.EmptyTypes,
                null);
            if (computeHash == null || disableScene == null)
                throw new MissingMemberException("GPU pipe hash-cache targets were not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            if (hookType == null)
                throw new TypeLoadException("MonoMod.RuntimeDetour.Hook is unavailable.");
            ConstructorInfo constructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            computeHashHook = constructor.Invoke(new object[] { computeHash, ComputeHashHookDelegate }) as IDisposable;
            disableSceneHook = constructor.Invoke(new object[] { disableScene, DisableSceneHookDelegate }) as IDisposable;
            if (computeHashHook == null || disableSceneHook == null)
                throw new InvalidOperationException("GPU pipe hash-cache hooks were not created.");

            Reset();
            enabled = true;
            log?.LogInfo("GPU World Map pipe structural hash cache enabled.");
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("GPU pipe hash cache could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        DisposeHook(ref disableSceneHook);
        DisposeHook(ref computeHashHook);
        Reset();
        enabled = false;
        log = null;
    }

    private static int ComputeHashHook(
        OrigComputeHash orig,
        WorldMapGpuScene.FrameState frame,
        bool roomVisible,
        bool creatureVisible)
    {
        if (!enabled || frame?.Snapshot == null)
            return orig(frame, roomVisible, creatureVisible);

        int generation = WorldMapGpuCache.Generation;
        int zoomKey = Quantize(frame.Zoom, 1000f);
        if (cacheValid &&
            ReferenceEquals(cachedSnapshot, frame.Snapshot) &&
            cachedGeneration == generation &&
            cachedLayoutHash == frame.LayoutHash &&
            cachedLayerMask == frame.LayerMask &&
            cachedZoomKey == zoomKey &&
            cachedRoomVisible == roomVisible &&
            cachedCreatureVisible == creatureVisible)
        {
            return cachedHash;
        }

        int hash = orig(frame, roomVisible, creatureVisible);
        cachedSnapshot = frame.Snapshot;
        cachedGeneration = generation;
        cachedLayoutHash = frame.LayoutHash;
        cachedLayerMask = frame.LayerMask;
        cachedZoomKey = zoomKey;
        cachedRoomVisible = roomVisible;
        cachedCreatureVisible = creatureVisible;
        cachedHash = hash;
        cacheValid = true;
        return hash;
    }

    private static void DisableSceneHook(OrigDisableScene orig)
    {
        Reset();
        orig();
    }

    private static int Quantize(float value, float scale)
    {
        if (float.IsNaN(value)) return int.MinValue;
        if (float.IsPositiveInfinity(value)) return int.MaxValue;
        if (float.IsNegativeInfinity(value)) return int.MinValue + 1;
        double scaled = Math.Round(value * scale);
        if (scaled >= int.MaxValue) return int.MaxValue;
        if (scaled <= int.MinValue) return int.MinValue;
        return (int)scaled;
    }

    private static void Reset()
    {
        cachedSnapshot = null;
        cachedGeneration = int.MinValue;
        cachedLayoutHash = int.MinValue;
        cachedLayerMask = int.MinValue;
        cachedZoomKey = int.MinValue;
        cachedRoomVisible = false;
        cachedCreatureVisible = false;
        cachedHash = 0;
        cacheValid = false;
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
