using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using BepInEx;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Input;
using DryCycle.DevUI.DevTool.Map;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Closes the lifetime gap between retained World Map services and DevUI/RWImGui presentation.
///
/// Most World Map helpers are hosted by always-on BepInEx components, while DevUI.Update stops as
/// soon as Rain World closes DevTools. Without an explicit lifetime edge, their last FrameState,
/// MapPage indexes and shortcut caches can outlive the editor page that produced them. This observer
/// runs in LateUpdate so it executes after the ordinary map pumps and retires the complete transient
/// map runtime exactly once when the live DevTools session disappears.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldMapGpuRendererPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapGpuLifecyclePlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMapGPULifetime";
    public const string PluginName = "DryCycle DevTool World Map GPU Lifetime";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private bool observedLiveSession;
    private bool runtimeSuspendedForDormantSession;

    private void OnEnable()
    {
        observedLiveSession = false;
        runtimeSuspendedForDormantSession = false;
    }

    private void LateUpdate()
    {
        bool live = DevToolSessionHub.IsCurrentSessionLive;
        if (!live)
        {
            if (observedLiveSession && !runtimeSuspendedForDormantSession)
            {
                SuspendDormantMapRuntime();
                runtimeSuspendedForDormantSession = true;
            }
            return;
        }

        observedLiveSession = true;
        if (runtimeSuspendedForDormantSession)
        {
            ResumeDormantMapRuntime();
            runtimeSuspendedForDormantSession = false;
        }

        EditorSession session = DevToolRuntime.ActiveSession;
        if (session?.ToolMode != EditorToolMode.Map)
            WorldMapGpuStableCacheGate.ReleaseRetainedKey();

        bool rebuiltMapVisible =
            EditorInputRouter.FrontendAttached &&
            session?.ToolMode == EditorToolMode.Map &&
            !EditorUiModeState.UseVanilla &&
            !EditorUiModeState.OverlayHidden &&
            !session.LegacyUiVisible;

        if (!rebuiltMapVisible)
        {
            // Tool switches, detached frontend, explicit legacy UI and Escape-hidden presentation all
            // keep retained scene/cache data warm, but none of them owns the screen. Apply(null)
            // disables the high-depth camera and, through the pipe-batch hook, hides retained map
            // sockets too. This is the final same-frame ownership arbiter after all map pumps.
            WorldMapGpuScene.Apply(null, session);
        }
    }

    private void SuspendDormantMapRuntime()
    {
        // The stable-cache gate and region preload layer can otherwise retain the retired MapPage,
        // immutable snapshots and up to several regions of managed bake data for the entire gameplay
        // session. Dispose their hooks/cache in dependency order; WorldMapGpuCache.FlushNow in the
        // renderer shutdown below still preserves the active durable bake on disk.
        WorldMapGpuStableCacheGate.Disable();
        WorldMapGpuRegionPreload.Disable();

        // Retire helpers which keep live Page/snapshot/route state. Their BepInEx components remain
        // enabled, but the static runtimes are idempotent and therefore safe to park until the next
        // real DevTools lifetime. This also makes their Update methods O(1) no-ops while gameplay is
        // running without the editor.
        WorldMapPlayerLocator.Disable();
        WorldMapExactShortcuts.Disable();
        WorldMapGpuInteractionIndex.Disable();
        WorldMapGpuPipeBatch.Disable();
        WorldMapGpuIncrementalRouter.Disable();
        WorldMapGpuRetainedOptimizer.Disable();
        WorldMapPerformance.Disable();

        // The basic shortcut presentation owns AbstractRoom/RoomRepresentation references but is
        // intentionally an internal presentation cache rather than a BepInEx runtime. Clear its
        // private cache once at the lifetime boundary without adding a reverse dependency from the
        // core DevTool assembly to the RWImGui frontend.
        ClearShortcutPresentationCache();

        // Geometry is also cleared by the core Page lifetime release. Calling it here is idempotent
        // and closes the edge even if DevUI disappears before that observer sees the retired page.
        MapRoomGeometryPresentationHub.Clear();

        // Disable the renderer first so the active cache is flushed durably. Then release the cache's
        // in-memory active-region snapshot as well; otherwise one full region bake remains rooted for
        // the whole gameplay session even after the multi-region preload cache has been cleared.
        WorldMapGpuRuntime.Disable();
        ClearGpuCacheWorkingSet();
    }

    private void ResumeDormantMapRuntime()
    {
        // Rebuild the dependency order used during normal BepInEx startup. Region preload installs
        // before the stable-cache gate so the latter remains the outer O(1) fast path once baking is
        // complete. The first reopened Map frame reloads the durable active-region bake from disk.
        WorldMapPlayerLocator.Enable(Logger);
        WorldMapPerformance.Enable(Logger);
        WorldMapExactShortcuts.Enable(Logger);
        WorldMapGpuRuntime.Enable(Logger, Thread.CurrentThread.ManagedThreadId);
        WorldMapGpuRetainedOptimizer.Enable(Logger);
        WorldMapGpuIncrementalRouter.Enable(Logger);
        WorldMapGpuPipeBatch.Enable(Logger);
        WorldMapGpuInteractionIndex.Enable(Logger);
        WorldMapGpuRegionPreload.Enable(Logger);
        WorldMapGpuStableCacheGate.Enable(Logger);
    }

    private static void ClearShortcutPresentationCache()
    {
        try
        {
            MethodInfo clear = typeof(WorldMapShortcutPresentation).GetMethod(
                "Clear",
                BindingFlags.Static | BindingFlags.NonPublic);
            clear?.Invoke(null, null);
        }
        catch
        {
            // Cache retirement is best-effort during shutdown; a later Prime() also resets a stale
            // region before publishing any shortcut data.
        }
    }

    private static void ClearGpuCacheWorkingSet()
    {
        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            Type cacheType = typeof(WorldMapGpuCache);
            FieldInfo currentField = cacheType.GetField("current", flags);
            Type snapshotType = currentField?.FieldType;
            object empty = snapshotType?.GetField("Empty", flags)?.GetValue(null);
            if (currentField != null && empty != null)
                currentField.SetValue(null, empty);

            cacheType.GetField("activeRegion", flags)?.SetValue(null, string.Empty);
            cacheType.GetField("activePath", flags)?.SetValue(null, string.Empty);
            cacheType.GetField("validationCursor", flags)?.SetValue(null, 0);
            cacheType.GetField("captureCursor", flags)?.SetValue(null, 0);
            cacheType.GetField("dirtyFrame", flags)?.SetValue(null, -1);
            cacheType.GetField("dirty", flags)?.SetValue(null, false);
            cacheType.GetField("lastError", flags)?.SetValue(null, string.Empty);
            cacheType.GetField("cacheHits", flags)?.SetValue(null, 0);
            cacheType.GetField("cacheMisses", flags)?.SetValue(null, 0);

            if (cacheType.GetField("validatedRooms", flags)?.GetValue(null) is HashSet<int> validated)
                validated.Clear();
            if (cacheType.GetField("liveSignatures", flags)?.GetValue(null) is Dictionary<int, ulong> signatures)
                signatures.Clear();
        }
        catch
        {
            // The durable cache has already been flushed. Failing to trim the optional in-memory
            // working set is therefore a memory-only fallback, not a data-integrity failure.
        }
    }

    private void OnDisable()
    {
        // Plugin shutdown can arrive in any component order. Hide presentation immediately; the
        // owning plugin runtimes perform their own idempotent final Disable calls afterwards.
        WorldMapGpuStableCacheGate.ReleaseRetainedKey();
        WorldMapGpuScene.Apply(null, DevToolRuntime.ActiveSession);
        observedLiveSession = false;
        runtimeSuspendedForDormantSession = false;
    }
}
