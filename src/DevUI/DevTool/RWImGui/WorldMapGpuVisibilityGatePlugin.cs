using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Map;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Retains the GPU map chunk-visibility decision while all culling inputs are unchanged.
///
/// WorldMapGpuScene.ApplyRoomVisibility otherwise performs a spatial query, allocates an exact
/// visible-room result array, creates a temporary visible-chunk HashSet and walks every retained
/// chunk on every rendered frame. None of that work can change while the immutable map snapshot,
/// bake generation, layout, viewport and layer mask are stable. This gate turns that common idle
/// path into an O(1) return and lets the original implementation run unchanged on every real edge.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldMapGpuHotQueryPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapGpuVisibilityGatePlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMapGPU.VisibilityGate";
    public const string PluginName = "DryCycle DevTool GPU World Map Visibility Gate";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldMapGpuVisibilityGate.Enable(Logger);
    private void OnDisable() => WorldMapGpuVisibilityGate.Disable();
}

internal static class WorldMapGpuVisibilityGate
{
    private delegate void OrigApplyRoomVisibility(WorldMapGpuScene.FrameState frame);
    private delegate void HookApplyRoomVisibility(
        OrigApplyRoomVisibility orig,
        WorldMapGpuScene.FrameState frame);
    private delegate void OrigDisableScene();
    private delegate void HookDisableScene(OrigDisableScene orig);

    private static readonly HookApplyRoomVisibility VisibilityHookDelegate = ApplyRoomVisibilityHook;
    private static readonly HookDisableScene DisableSceneHookDelegate = DisableSceneHook;

    private static ManualLogSource log;
    private static IDisposable visibilityHook;
    private static IDisposable disableSceneHook;
    private static EditorMapPresentationSnapshot lastSnapshot;
    private static int lastGeneration = int.MinValue;
    private static int lastLayoutHash = int.MinValue;
    private static int lastLayerMask = int.MinValue;
    private static Num.Vector2 lastPan;
    private static Num.Vector2 lastCanvasSize;
    private static float lastZoom = float.NaN;
    private static bool retained;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;

        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            MethodInfo applyVisibility = typeof(WorldMapGpuScene).GetMethod(
                "ApplyRoomVisibility",
                flags,
                null,
                new[] { typeof(WorldMapGpuScene.FrameState) },
                null);
            MethodInfo disableScene = typeof(WorldMapGpuScene).GetMethod(
                "Disable",
                flags,
                null,
                Type.EmptyTypes,
                null);
            if (applyVisibility == null || disableScene == null)
                throw new MissingMemberException("GPU World Map visibility-gate targets were not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            if (hookType == null)
                throw new TypeLoadException("MonoMod.RuntimeDetour.Hook is unavailable.");
            ConstructorInfo constructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            visibilityHook = constructor.Invoke(new object[] { applyVisibility, VisibilityHookDelegate }) as IDisposable;
            disableSceneHook = constructor.Invoke(new object[] { disableScene, DisableSceneHookDelegate }) as IDisposable;
            if (visibilityHook == null || disableSceneHook == null)
                throw new InvalidOperationException("GPU World Map visibility-gate hooks were not created.");

            Reset();
            enabled = true;
            log?.LogInfo("GPU World Map stable visibility gate enabled.");
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("GPU World Map visibility gate could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        DisposeHook(ref disableSceneHook);
        DisposeHook(ref visibilityHook);
        Reset();
        enabled = false;
        log = null;
    }

    private static void ApplyRoomVisibilityHook(
        OrigApplyRoomVisibility orig,
        WorldMapGpuScene.FrameState frame)
    {
        if (!enabled || frame == null)
        {
            Reset();
            orig(frame);
            return;
        }

        EditorMapPresentationSnapshot snapshot = frame.Snapshot;
        int generation = WorldMapGpuCache.Generation;
        if (retained &&
            ReferenceEquals(lastSnapshot, snapshot) &&
            lastGeneration == generation &&
            lastLayoutHash == frame.LayoutHash &&
            lastLayerMask == frame.LayerMask &&
            lastPan.Equals(frame.Pan) &&
            lastCanvasSize.Equals(frame.CanvasSize) &&
            lastZoom.Equals(frame.Zoom))
        {
            return;
        }

        orig(frame);
        lastSnapshot = snapshot;
        lastGeneration = generation;
        lastLayoutHash = frame.LayoutHash;
        lastLayerMask = frame.LayerMask;
        lastPan = frame.Pan;
        lastCanvasSize = frame.CanvasSize;
        lastZoom = frame.Zoom;
        retained = true;
    }

    private static void DisableSceneHook(OrigDisableScene orig)
    {
        Reset();
        orig();
    }

    private static void Reset()
    {
        lastSnapshot = null;
        lastGeneration = int.MinValue;
        lastLayoutHash = int.MinValue;
        lastLayerMask = int.MinValue;
        lastPan = default;
        lastCanvasSize = default;
        lastZoom = float.NaN;
        retained = false;
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
