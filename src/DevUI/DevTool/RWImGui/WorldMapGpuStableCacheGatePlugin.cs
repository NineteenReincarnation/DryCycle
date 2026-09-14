using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Map;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Removes the last region-size scan from the persistent World Map cache after a snapshot is fully
/// baked. WorldMapGpuCache.Update deliberately validates/captures progressively, but its completed
/// CaptureSomeRooms pass still walks the whole room array looking for work on every frame. Retain a
/// completed (session, page, immutable presentation snapshot, cache generation) key instead.
///
/// Any map presentation replacement or explicit cache publication/invalidation changes one of those
/// keys and immediately re-enters the ordinary cache pipeline. The delayed disk write is preserved
/// with one O(1) dirty check and a single FlushNow after the normal 45-frame settling window.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldMapGpuRegionPreloadPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapGpuStableCacheGatePlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMapGPU.StableCacheGate";
    public const string PluginName = "DryCycle DevTool GPU World Map Stable Cache Gate";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldMapGpuStableCacheGate.Enable(Logger);
    private void OnDisable() => WorldMapGpuStableCacheGate.Disable();
}

internal static class WorldMapGpuStableCacheGate
{
    private const int SaveDelayFrames = 45;

    private delegate void OrigCacheUpdate(EditorSession session, EditorMapPresentationSnapshot snapshot);
    private delegate void HookCacheUpdate(
        OrigCacheUpdate orig,
        EditorSession session,
        EditorMapPresentationSnapshot snapshot);

    private static readonly HookCacheUpdate CacheUpdateHookDelegate = CacheUpdateHook;

    private static ManualLogSource log;
    private static IDisposable updateHook;
    private static EditorSession stableSession;
    private static Page stablePage;
    private static EditorMapPresentationSnapshot stableSnapshot;
    private static int stableGeneration = int.MinValue;
    private static int flushDueFrame = -1;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;

        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            MethodInfo update = typeof(WorldMapGpuCache).GetMethod(
                "Update",
                flags,
                null,
                new[] { typeof(EditorSession), typeof(EditorMapPresentationSnapshot) },
                null);
            if (update == null)
                throw new MissingMethodException("WorldMapGpuCache.Update was not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            if (hookType == null)
                throw new TypeLoadException("MonoMod.RuntimeDetour.Hook is unavailable.");
            ConstructorInfo constructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            updateHook = constructor.Invoke(new object[] { update, CacheUpdateHookDelegate }) as IDisposable;
            if (updateHook == null)
                throw new InvalidOperationException("World Map stable-cache hook was not created.");

            enabled = true;
            ResetStableKey();
            log?.LogInfo("GPU World Map stable cache gate enabled.");
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("GPU World Map stable cache gate could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        try { updateHook?.Dispose(); }
        catch { }
        updateHook = null;
        ResetStableKey();
        enabled = false;
        log = null;
    }

    private static void CacheUpdateHook(
        OrigCacheUpdate orig,
        EditorSession session,
        EditorMapPresentationSnapshot snapshot)
    {
        if (!enabled || session?.ToolMode != EditorToolMode.Map ||
            session.Owner?.activePage is not MapPage page || snapshot?.Available != true)
        {
            ResetStableKey();
            orig(session, snapshot);
            return;
        }

        int generation = WorldMapGpuCache.Generation;
        bool exactStableKey =
            ReferenceEquals(stableSession, session) &&
            ReferenceEquals(stablePage, page) &&
            ReferenceEquals(stableSnapshot, snapshot) &&
            stableGeneration == generation;

        if (exactStableKey)
        {
            // The cache is known complete for this immutable presentation generation. Do not run
            // CaptureSomeRooms again: that old path performs an O(region room count) unsuccessful
            // search every stable frame. Preserve only the delayed durable write contract.
            if (WorldMapGpuCache.Dirty && flushDueFrame >= 0 && Time.frameCount >= flushDueFrame)
            {
                WorldMapGpuCache.FlushNow();
                flushDueFrame = -1;
            }
            return;
        }

        orig(session, snapshot);

        // HasCompleteCachedData is O(room count), but it is paid only while the key is changing or
        // the progressive baker is still working. Once it succeeds, all subsequent stable frames use
        // the constant-time key above until cache Generation or presentation identity changes.
        if (!WorldMapGpuCache.HasCompleteCachedData(snapshot))
        {
            ResetStableKey();
            return;
        }

        stableSession = session;
        stablePage = page;
        stableSnapshot = snapshot;
        stableGeneration = WorldMapGpuCache.Generation;
        flushDueFrame = WorldMapGpuCache.Dirty ? Time.frameCount + SaveDelayFrames : -1;
    }

    private static void ResetStableKey()
    {
        stableSession = null;
        stablePage = null;
        stableSnapshot = null;
        stableGeneration = int.MinValue;
        flushDueFrame = -1;
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
