using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Reuses transient path arrays created while rebuilding retained World Map connection meshes.
/// Topology changes and zoom/selection edges are intentionally allowed to do real work, but they
/// should not also create one managed array for every bidirectional offset and every bridge arc.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldMapGpuRendererPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapGpuPathScratchPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMapGPU.PathScratch";
    public const string PluginName = "DryCycle DevTool GPU World Map Path Scratch";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldMapGpuPathScratch.Enable(Logger);
    private void OnDisable() => WorldMapGpuPathScratch.Disable();
}

internal static class WorldMapGpuPathScratch
{
    private delegate Vector2[] OrigOffsetPath(Vector2[] points, float amount);
    private delegate Vector2[] HookOffsetPath(OrigOffsetPath orig, Vector2[] points, float amount);
    private delegate Vector2[] OrigBuildBridgeArc(Vector2 a, Vector2 b, Vector2 c, Vector2 d);
    private delegate Vector2[] HookBuildBridgeArc(
        OrigBuildBridgeArc orig,
        Vector2 a,
        Vector2 b,
        Vector2 c,
        Vector2 d);
    private delegate void OrigSceneDisable();
    private delegate void HookSceneDisable(OrigSceneDisable orig);

    private sealed class OffsetPair
    {
        internal Vector2[] First;
        internal Vector2[] Second;
        internal bool NextSecond;
    }

    private static readonly HookOffsetPath OffsetHookDelegate = OffsetPathHook;
    private static readonly HookBuildBridgeArc BridgeHookDelegate = BuildBridgeArcHook;
    private static readonly HookSceneDisable SceneDisableHookDelegate = SceneDisableHook;
    private static readonly Dictionary<int, OffsetPair> OffsetScratch = new();
    private static readonly Vector2[][] BridgeScratch = { new Vector2[9], new Vector2[9] };

    private static ManualLogSource log;
    private static IDisposable offsetHook;
    private static IDisposable bridgeHook;
    private static IDisposable sceneDisableHook;
    private static int bridgeCursor;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;

        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            Type scene = typeof(WorldMapGpuScene);
            MethodInfo offsetPath = scene.GetMethod(
                "OffsetPath", flags, null,
                new[] { typeof(Vector2[]), typeof(float) }, null);
            MethodInfo bridgeArc = scene.GetMethod(
                "BuildBridgeArc", flags, null,
                new[] { typeof(Vector2), typeof(Vector2), typeof(Vector2), typeof(Vector2) }, null);
            MethodInfo disableScene = scene.GetMethod(
                "Disable", flags, null, Type.EmptyTypes, null);
            if (offsetPath == null || bridgeArc == null || disableScene == null)
                throw new MissingMemberException("GPU World Map path-scratch targets were not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            if (hookType == null)
                throw new TypeLoadException("MonoMod.RuntimeDetour.Hook is unavailable.");
            ConstructorInfo constructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            offsetHook = constructor.Invoke(new object[] { offsetPath, OffsetHookDelegate }) as IDisposable;
            bridgeHook = constructor.Invoke(new object[] { bridgeArc, BridgeHookDelegate }) as IDisposable;
            sceneDisableHook = constructor.Invoke(new object[] { disableScene, SceneDisableHookDelegate }) as IDisposable;
            if (offsetHook == null || bridgeHook == null || sceneDisableHook == null)
                throw new InvalidOperationException("GPU World Map path-scratch hooks were not created.");

            ResetScratch();
            enabled = true;
            log?.LogInfo("GPU World Map retained path scratch enabled.");
        }
        catch (Exception error)
        {
            string message = Unwrap(error).Message;
            Disable();
            logger?.LogWarning("GPU World Map path scratch could not attach: " + message);
        }
    }

    internal static void Disable()
    {
        DisposeHook(ref sceneDisableHook);
        DisposeHook(ref bridgeHook);
        DisposeHook(ref offsetHook);
        ResetScratch();
        enabled = false;
        log = null;
    }

    private static Vector2[] OffsetPathHook(OrigOffsetPath orig, Vector2[] points, float amount)
    {
        if (!enabled || points == null || points.Length < 2 || Math.Abs(amount) < 0.001f)
            return orig(points, amount);

        if (!OffsetScratch.TryGetValue(points.Length, out OffsetPair pair))
        {
            pair = new OffsetPair
            {
                First = new Vector2[points.Length],
                Second = new Vector2[points.Length]
            };
            OffsetScratch.Add(points.Length, pair);
        }

        Vector2[] result = pair.NextSecond ? pair.Second : pair.First;
        pair.NextSecond = !pair.NextSecond;
        for (int i = 0; i < points.Length; i++)
        {
            Vector2 before = points[Math.Max(0, i - 1)];
            Vector2 after = points[Math.Min(points.Length - 1, i + 1)];
            Vector2 tangent = after - before;
            if (tangent.LengthSquared() < 0.0001f) tangent = new Vector2(1f, 0f);
            tangent = Vector2.Normalize(tangent);
            result[i] = points[i] + new Vector2(-tangent.Y, tangent.X) * amount;
        }
        return result;
    }

    private static Vector2[] BuildBridgeArcHook(
        OrigBuildBridgeArc orig,
        Vector2 a,
        Vector2 b,
        Vector2 c,
        Vector2 d)
    {
        if (!enabled) return orig(a, b, c, d);

        Vector2[] result = BridgeScratch[bridgeCursor];
        bridgeCursor ^= 1;
        Vector2 midpoint = (b + c) * 0.5f;
        result[0] = a;
        for (int i = 1; i <= 4; i++)
        {
            float t = i / 4f;
            result[i] = Quadratic(a, b, midpoint, t);
        }
        for (int i = 1; i <= 4; i++)
        {
            float t = i / 4f;
            result[4 + i] = Quadratic(midpoint, c, d, t);
        }
        return result;
    }

    private static void SceneDisableHook(OrigSceneDisable orig)
    {
        try { orig(); }
        finally { ResetScratch(); }
    }

    private static Vector2 Quadratic(Vector2 a, Vector2 b, Vector2 c, float t)
    {
        float u = 1f - t;
        return a * (u * u) + b * (2f * u * t) + c * (t * t);
    }

    private static void ResetScratch()
    {
        OffsetScratch.Clear();
        bridgeCursor = 0;
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
