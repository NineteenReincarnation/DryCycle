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

    private void OnEnable() =>
        global::DryCycle.AuxiliaryPluginStartupGuard.Enable(
            PluginName + ".OnEnable",
            InitializeState,
            Shutdown);

    private void InitializeState()
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
            // disables the high-depth camera and direct pipe-batch presentation as well. This is
            // the final same-frame ownership arbiter after all map pumps.
            WorldMapGpuScene.Apply(null, session);
        }
    }

    private void SuspendDormantMapRuntime()
    {
        // The region preload layer can otherwise retain immutable snapshots and several regions of
        // managed bake data for the entire gameplay session. Retire it before releasing the active
        // GPU working set; renderer shutdown below still flushes the durable active-region bake.
        WorldMapGpuRegionPreload.Disable();

        // Retire active helpers which keep live Page/snapshot/route state. Their runtimes are
        // idempotent and can be resumed when a new DevTools session opens.
        WorldMapPlayerLocator.Disable();
        WorldMapExactShortcuts.Disable();
        WorldMapGpuPipeBatch.Disable();
        WorldMapPerformance.Disable();

        // The basic shortcut presentation owns AbstractRoom/RoomRepresentation references but is
        // intentionally an internal presentation cache rather than a BepInEx runtime. Clear its
        // cache once at the lifetime boundary through its explicit lifecycle API.
        WorldMapShortcutPresentation.Clear();

        // Geometry is also cleared by the core Page lifetime release. Calling it here is idempotent
        // and closes the edge even if DevUI disappears before that observer sees the retired page.
        MapRoomGeometryPresentationHub.Clear();

        // Disable the renderer first so the active cache is flushed durably. Then release the cache's
        // in-memory active-region snapshot as well; otherwise one full region bake remains rooted for
        // the whole gameplay session even after the multi-region preload cache has been cleared.
        WorldMapGpuRuntime.Disable();
        WorldMapGpuCache.ReleaseWorkingSet();
    }

    private void ResumeDormantMapRuntime()
    {
        // Rebuild the active World Map runtime in dependency order. The first reopened Map frame
        // reloads the durable active-region bake from disk before the preload layer warms neighbors.
        WorldMapPlayerLocator.Enable(Logger);
        WorldMapPerformance.Enable(Logger);
        WorldMapExactShortcuts.Enable(Logger);
        WorldMapGpuRuntime.Enable(Logger, Thread.CurrentThread.ManagedThreadId);
        WorldMapGpuPipeBatch.Enable(Logger);
        WorldMapGpuRegionPreload.Enable(Logger);
    }

    private void OnDisable() =>
        global::DryCycle.AuxiliaryPluginStartupGuard.Disable(
            PluginName + ".OnDisable",
            Shutdown);

    private void Shutdown()
    {
        // Plugin shutdown can arrive in any component order. Hide presentation immediately.
        // If this lifecycle parked the shared runtimes while DevTools was dormant, restore them
        // before this component disappears: their owning BepInEx plugins may remain enabled and
        // must not be left permanently disabled just because this observer shut down first.
        WorldMapGpuScene.Apply(null, DevToolRuntime.ActiveSession);
        if (runtimeSuspendedForDormantSession)
            ResumeDormantMapRuntime();
        observedLiveSession = false;
        runtimeSuspendedForDormantSession = false;
    }
}
