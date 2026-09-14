using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Removes the remaining small but very frequent managed allocations from the retained World Map
/// pipe path. The pipe batch itself is already retained; this layer makes the occasional rebuild
/// cheap enough that zooming or editing does not create a burst of tiny arrays/boxed bools/region
/// strings on top of the real mesh upload.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldMapGpuPipeBatchPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapGpuPipeHotPathPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMapGPU.PipeHotPath";
    public const string PluginName = "DryCycle DevTool GPU World Map Pipe Hot Path";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldMapGpuPipeHotPath.Enable(Logger);
    private void OnDisable() => WorldMapGpuPipeHotPath.Disable();
}

internal static class WorldMapGpuPipeHotPath
{
    private delegate Vector2[] OrigOctagon(Vector2 center, float half, float bevel);
    private delegate Vector2[] HookOctagon(OrigOctagon orig, Vector2 center, float half, float bevel);
    private delegate bool OrigReadBool(FieldInfo field, bool fallback);
    private delegate bool HookReadBool(OrigReadBool orig, FieldInfo field, bool fallback);
    private delegate string OrigNormalizeRegion(string value);
    private delegate string HookNormalizeRegion(OrigNormalizeRegion orig, string value);
    private delegate void OrigPipeDisable();
    private delegate void HookPipeDisable(OrigPipeDisable orig);
    private delegate bool StaticBoolGetter();

    private static readonly HookOctagon OctagonHookDelegate = OctagonHook;
    private static readonly HookReadBool ReadBoolHookDelegate = ReadBoolHook;
    private static readonly HookNormalizeRegion NormalizeRegionHookDelegate = NormalizeRegionHook;
    private static readonly HookPipeDisable PipeDisableHookDelegate = PipeDisableHook;

    // AddOctagonRing needs two octagons alive at once. Four slots leave headroom for future nested
    // helper calls while keeping the scratch footprint trivial. World Map mesh construction is a
    // Unity-main-thread operation, so this ring intentionally has no locking.
    private static readonly Vector2[][] OctagonScratch =
    {
        new Vector2[8], new Vector2[8], new Vector2[8], new Vector2[8]
    };
    private static readonly Dictionary<FieldInfo, StaticBoolGetter> BoolGetters = new();

    private static ManualLogSource log;
    private static IDisposable octagonHook;
    private static IDisposable readBoolHook;
    private static IDisposable normalizeRegionHook;
    private static IDisposable pipeDisableHook;
    private static int octagonCursor;
    private static string lastRegionInput;
    private static string lastRegionOutput = string.Empty;
    private static bool regionCacheValid;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;

        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            Type pipeType = typeof(WorldMapGpuPipeBatch);
            MethodInfo octagon = pipeType.GetMethod(
                "Octagon", flags, null,
                new[] { typeof(Vector2), typeof(float), typeof(float) }, null);
            MethodInfo readBool = pipeType.GetMethod(
                "ReadBool", flags, null,
                new[] { typeof(FieldInfo), typeof(bool) }, null);
            MethodInfo normalizeRegion = pipeType.GetMethod(
                "NormalizeRegion", flags, null,
                new[] { typeof(string) }, null);
            MethodInfo pipeDisable = pipeType.GetMethod(
                "Disable", flags, null, Type.EmptyTypes, null);

            if (octagon == null || readBool == null || normalizeRegion == null || pipeDisable == null)
                throw new MissingMemberException("GPU pipe hot-path targets were not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            if (hookType == null)
                throw new TypeLoadException("MonoMod.RuntimeDetour.Hook is unavailable.");
            ConstructorInfo constructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            octagonHook = constructor.Invoke(new object[] { octagon, OctagonHookDelegate }) as IDisposable;
            readBoolHook = constructor.Invoke(new object[] { readBool, ReadBoolHookDelegate }) as IDisposable;
            normalizeRegionHook = constructor.Invoke(new object[] { normalizeRegion, NormalizeRegionHookDelegate }) as IDisposable;
            pipeDisableHook = constructor.Invoke(new object[] { pipeDisable, PipeDisableHookDelegate }) as IDisposable;
            if (octagonHook == null || readBoolHook == null || normalizeRegionHook == null || pipeDisableHook == null)
                throw new InvalidOperationException("GPU pipe hot-path hooks were not created.");

            ResetTransientState(clearAccessors: false);
            enabled = true;
            log?.LogInfo("GPU World Map pipe hot-path allocation trimming enabled.");
        }
        catch (Exception error)
        {
            string message = Unwrap(error).Message;
            Disable();
            logger?.LogWarning("GPU pipe hot-path optimization could not attach: " + message);
        }
    }

    internal static void Disable()
    {
        DisposeHook(ref pipeDisableHook);
        DisposeHook(ref normalizeRegionHook);
        DisposeHook(ref readBoolHook);
        DisposeHook(ref octagonHook);
        ResetTransientState(clearAccessors: true);
        enabled = false;
        log = null;
    }

    private static Vector2[] OctagonHook(
        OrigOctagon orig,
        Vector2 center,
        float half,
        float bevel)
    {
        if (!enabled)
            return orig(center, half, bevel);

        Vector2[] ring = OctagonScratch[octagonCursor];
        octagonCursor = (octagonCursor + 1) & (OctagonScratch.Length - 1);

        ring[0] = new Vector2(center.x - half + bevel, center.y + half);
        ring[1] = new Vector2(center.x + half - bevel, center.y + half);
        ring[2] = new Vector2(center.x + half, center.y + half - bevel);
        ring[3] = new Vector2(center.x + half, center.y - half + bevel);
        ring[4] = new Vector2(center.x + half - bevel, center.y - half);
        ring[5] = new Vector2(center.x - half + bevel, center.y - half);
        ring[6] = new Vector2(center.x - half, center.y - half + bevel);
        ring[7] = new Vector2(center.x - half, center.y + half - bevel);
        return ring;
    }

    private static bool ReadBoolHook(OrigReadBool orig, FieldInfo field, bool fallback)
    {
        if (!enabled || field == null || field.FieldType != typeof(bool) || !field.IsStatic)
            return orig(field, fallback);

        try
        {
            if (!BoolGetters.TryGetValue(field, out StaticBoolGetter getter))
            {
                getter = BuildBoolGetter(field);
                if (getter == null) return orig(field, fallback);
                BoolGetters[field] = getter;
            }
            return getter();
        }
        catch
        {
            return fallback;
        }
    }

    private static string NormalizeRegionHook(OrigNormalizeRegion orig, string value)
    {
        if (!enabled) return orig(value);

        if (regionCacheValid &&
            (ReferenceEquals(value, lastRegionInput) || string.Equals(value, lastRegionInput, StringComparison.Ordinal)))
            return lastRegionOutput;

        string normalized = orig(value);
        lastRegionInput = value;
        lastRegionOutput = normalized ?? string.Empty;
        regionCacheValid = true;
        return lastRegionOutput;
    }

    private static void PipeDisableHook(OrigPipeDisable orig)
    {
        try { orig(); }
        finally { ResetTransientState(clearAccessors: false); }
    }

    private static StaticBoolGetter BuildBoolGetter(FieldInfo field)
    {
        DynamicMethod method = new(
            "ReadGpuPipeBool_" + field.Name,
            typeof(bool),
            Type.EmptyTypes,
            typeof(WorldMapGpuPipeHotPath).Module,
            true);
        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Ldsfld, field);
        il.Emit(OpCodes.Ret);
        return (StaticBoolGetter)method.CreateDelegate(typeof(StaticBoolGetter));
    }

    private static void ResetTransientState(bool clearAccessors)
    {
        octagonCursor = 0;
        lastRegionInput = null;
        lastRegionOutput = string.Empty;
        regionCacheValid = false;
        if (clearAccessors) BoolGetters.Clear();
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
