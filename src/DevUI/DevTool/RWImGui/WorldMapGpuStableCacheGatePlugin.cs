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
/// Removes the last region-size scans from the persistent World Map cache after a snapshot is fully
/// baked. WorldMapGpuCache.Update deliberately validates/captures progressively, but its completed
/// CaptureSomeRooms pass still walks the whole room array looking for work on every frame, while
/// HasCompleteCachedData separately scans the same immutable room set from several callers.
///
/// Retain a completed (session, page, presentation room-membership, cache generation) key instead.
/// Presentation-only snapshot replacements such as selection/current-room flags or room placement
/// changes can promote themselves into the retained key after one allocation-free room-id check.
/// Any room membership change or explicit cache publication/invalidation immediately re-enters the
/// ordinary cache pipeline. The delayed disk write is preserved with one O(1) dirty check and a
/// single FlushNow after the normal 45-frame settling window.
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
    private delegate bool OrigHasCompleteCachedData(EditorMapPresentationSnapshot snapshot);
    private delegate bool HookHasCompleteCachedData(
        OrigHasCompleteCachedData orig,
        EditorMapPresentationSnapshot snapshot);

    private static readonly HookCacheUpdate CacheUpdateHookDelegate = CacheUpdateHook;
    private static readonly HookHasCompleteCachedData CompleteHookDelegate = HasCompleteCachedDataHook;

    private static ManualLogSource log;
    private static IDisposable updateHook;
    private static IDisposable completeHook;
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
            MethodInfo hasComplete = typeof(WorldMapGpuCache).GetMethod(
                "HasCompleteCachedData",
                flags,
                null,
                new[] { typeof(EditorMapPresentationSnapshot) },
                null);
            if (update == null || hasComplete == null)
                throw new MissingMethodException("World Map GPU cache gate targets were not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            if (hookType == null)
                throw new TypeLoadException("MonoMod.RuntimeDetour.Hook is unavailable.");
            ConstructorInfo constructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            updateHook = constructor.Invoke(new object[] { update, CacheUpdateHookDelegate }) as IDisposable;
            completeHook = constructor.Invoke(new object[] { hasComplete, CompleteHookDelegate }) as IDisposable;
            if (updateHook == null || completeHook == null)
                throw new InvalidOperationException("World Map stable-cache hooks were not created.");

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
        try { completeHook?.Dispose(); }
        catch { }
        completeHook = null;
        try { updateHook?.Dispose(); }
        catch { }
        updateHook = null;
        ResetStableKey();
        enabled = false;
        log = null;
    }

    /// <summary>
    /// Drops only the retained fast-path identity. The durable WorldMapGpuCache remains warm. Call
    /// this when the Map Page/session lifetime ends so this optimization cannot root a retired DevUI
    /// graph merely because WorldMapGpuRuntime no longer invokes Update outside the Map workspace.
    /// </summary>
    internal static void ReleaseRetainedKey() => ResetStableKey();

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
        if (TryPromoteStableSnapshot(session, page, snapshot, generation))
        {
            // The cache is known complete for this room membership and generation. Do not run
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

        // This one full completeness check is paid while room membership/cache generation changes
        // or while the progressive baker is still working. Once it succeeds, Update and all later
        // completeness queries stay constant-time until that structural key changes again.
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

    private static bool HasCompleteCachedDataHook(
        OrigHasCompleteCachedData orig,
        EditorMapPresentationSnapshot snapshot)
    {
        if (enabled && snapshot?.Available == true)
        {
            EditorSession session = DevToolRuntime.ActiveSession;
            Page page = session?.Owner?.activePage;
            if (TryPromoteStableSnapshot(session, page, snapshot, WorldMapGpuCache.Generation))
                return true;
        }

        return orig(snapshot);
    }

    private static bool TryPromoteStableSnapshot(
        EditorSession session,
        Page page,
        EditorMapPresentationSnapshot snapshot,
        int generation)
    {
        if (!ReferenceEquals(stableSession, session) ||
            !ReferenceEquals(stablePage, page) ||
            stableGeneration != generation ||
            stableSnapshot?.Available != true ||
            snapshot?.Available != true)
            return false;

        if (ReferenceEquals(stableSnapshot, snapshot))
            return true;

        if (!HasSameRoomMembership(stableSnapshot, snapshot))
            return false;

        // The cache is indexed by RoomIndex and readiness only. Selection/current-room flags,
        // positions, layers and other presentation-only data do not change cache completeness.
        // Promote the new immutable snapshot so subsequent calls return to the O(1) identity path.
        stableSnapshot = snapshot;
        return true;
    }

    private static bool HasSameRoomMembership(
        EditorMapPresentationSnapshot a,
        EditorMapPresentationSnapshot b)
    {
        EditorMapRoomSnapshot[] left = a?.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        EditorMapRoomSnapshot[] right = b?.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        if (left.Length != right.Length) return false;

        for (int i = 0; i < left.Length; i++)
        {
            EditorMapRoomSnapshot leftRoom = left[i];
            EditorMapRoomSnapshot rightRoom = right[i];
            if (leftRoom == null || rightRoom == null)
            {
                if (!ReferenceEquals(leftRoom, rightRoom)) return false;
                continue;
            }

            if (leftRoom.RoomIndex != rightRoom.RoomIndex)
                return false;
        }

        return true;
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
