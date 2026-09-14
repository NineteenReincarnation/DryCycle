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
        // The stable-cache gate deliberately retains Page/session identity while Map stays active.
        // Drop that optimization key before the live DevUI owner disappears; the durable baked cache
        // itself remains available for the next editor lifetime.
        WorldMapGpuStableCacheGate.ReleaseRetainedKey();

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

        // Disable the renderer last so hooked retained helpers can hide/destroy their own resources
        // before the scene camera/chunks are torn down.
        WorldMapGpuRuntime.Disable();
    }

    private void ResumeDormantMapRuntime()
    {
        // Rebuild the dependency order used during normal BepInEx startup. The first reopened Map
        // frame can then consume the durable GPU cache immediately without retaining any old Page.
        WorldMapPlayerLocator.Enable(Logger);
        WorldMapPerformance.Enable(Logger);
        WorldMapExactShortcuts.Enable(Logger);
        WorldMapGpuRuntime.Enable(Logger, Thread.CurrentThread.ManagedThreadId);
        WorldMapGpuRetainedOptimizer.Enable(Logger);
        WorldMapGpuIncrementalRouter.Enable(Logger);
        WorldMapGpuPipeBatch.Enable(Logger);
        WorldMapGpuInteractionIndex.Enable(Logger);
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
