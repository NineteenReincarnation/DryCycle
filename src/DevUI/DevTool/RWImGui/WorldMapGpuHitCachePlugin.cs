using System;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Coalesces identical retained-route hit tests performed more than once in the same Unity frame.
///
/// The GPU canvas intentionally performs a pre-input route hit and then reuses the same map-space
/// coordinates after the legacy interaction pass. With immutable route-index snapshots, an exact
/// duplicate query in the same frame cannot produce a different answer. Retaining that one result
/// removes the second spatial-cell walk without changing any interaction semantics.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldMapGpuHotQueryPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapGpuHitCachePlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMapGPU.HitCache";
    public const string PluginName = "DryCycle DevTool GPU World Map Hit Cache";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldMapGpuHitCache.Enable(Logger);
    private void OnDisable() => WorldMapGpuHitCache.Disable();
}

internal static class WorldMapGpuHitCache
{
    private delegate bool OrigTryHitConnection(
        Num.Vector2 mapPoint,
        float radius,
        out WorldMapGpuScene.RouteHit hit);
    private delegate bool HookTryHitConnection(
        OrigTryHitConnection orig,
        Num.Vector2 mapPoint,
        float radius,
        out WorldMapGpuScene.RouteHit hit);
    private delegate object RouteIndexGetter();
    private delegate void OrigDisableScene();
    private delegate void HookDisableScene(OrigDisableScene orig);

    private static readonly HookTryHitConnection HitHookDelegate = TryHitConnectionHook;
    private static readonly HookDisableScene DisableSceneHookDelegate = DisableSceneHook;

    private static ManualLogSource log;
    private static IDisposable hitHook;
    private static IDisposable disableSceneHook;
    private static RouteIndexGetter getRouteIndex;
    private static object cachedIndex;
    private static int cachedFrame = int.MinValue;
    private static Num.Vector2 cachedPoint;
    private static float cachedRadius;
    private static WorldMapGpuScene.RouteHit cachedHit;
    private static bool cachedResult;
    private static bool cacheValid;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;

        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            MethodInfo hitMethod = typeof(WorldMapGpuScene).GetMethod(
                "TryHitConnection",
                flags,
                null,
                new[]
                {
                    typeof(Num.Vector2), typeof(float),
                    typeof(WorldMapGpuScene.RouteHit).MakeByRefType()
                },
                null);
            FieldInfo routeIndexField = typeof(WorldMapGpuScene).GetField("routeIndex", flags);
            MethodInfo disableScene = typeof(WorldMapGpuScene).GetMethod(
                "Disable",
                flags,
                null,
                Type.EmptyTypes,
                null);
            if (hitMethod == null || routeIndexField == null || disableScene == null)
                throw new MissingMemberException("GPU World Map hit-cache targets were not found.");

            getRouteIndex = BuildRouteIndexGetter(routeIndexField);
            if (getRouteIndex == null)
                throw new MissingMemberException("GPU World Map route-index getter was not created.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            if (hookType == null)
                throw new TypeLoadException("MonoMod.RuntimeDetour.Hook is unavailable.");
            ConstructorInfo constructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            hitHook = constructor.Invoke(new object[] { hitMethod, HitHookDelegate }) as IDisposable;
            disableSceneHook = constructor.Invoke(new object[] { disableScene, DisableSceneHookDelegate }) as IDisposable;
            if (hitHook == null || disableSceneHook == null)
                throw new InvalidOperationException("GPU World Map hit-cache hooks were not created.");

            Reset();
            enabled = true;
            log?.LogInfo("GPU World Map duplicate route hit-test cache enabled.");
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("GPU World Map hit cache could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        DisposeHook(ref disableSceneHook);
        DisposeHook(ref hitHook);
        getRouteIndex = null;
        Reset();
        enabled = false;
        log = null;
    }

    private static bool TryHitConnectionHook(
        OrigTryHitConnection orig,
        Num.Vector2 mapPoint,
        float radius,
        out WorldMapGpuScene.RouteHit hit)
    {
        if (!enabled)
            return orig(mapPoint, radius, out hit);

        object index;
        try { index = getRouteIndex?.Invoke(); }
        catch { index = null; }
        int frame = Time.frameCount;

        if (cacheValid &&
            cachedFrame == frame &&
            ReferenceEquals(cachedIndex, index) &&
            cachedPoint.Equals(mapPoint) &&
            cachedRadius.Equals(radius))
        {
            hit = cachedHit;
            return cachedResult;
        }

        bool result = orig(mapPoint, radius, out hit);
        cachedIndex = index;
        cachedFrame = frame;
        cachedPoint = mapPoint;
        cachedRadius = radius;
        cachedHit = hit;
        cachedResult = result;
        cacheValid = true;
        return result;
    }

    private static void DisableSceneHook(OrigDisableScene orig)
    {
        Reset();
        orig();
    }

    private static RouteIndexGetter BuildRouteIndexGetter(FieldInfo field)
    {
        DynamicMethod method = new(
            "ReadWorldMapRouteIndexObject",
            typeof(object),
            Type.EmptyTypes,
            typeof(WorldMapGpuHitCache).Module,
            true);
        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Ldsfld, field);
        il.Emit(OpCodes.Ret);
        return (RouteIndexGetter)method.CreateDelegate(typeof(RouteIndexGetter));
    }

    private static void Reset()
    {
        cachedIndex = null;
        cachedFrame = int.MinValue;
        cachedPoint = default;
        cachedRadius = 0f;
        cachedHit = null;
        cachedResult = false;
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
