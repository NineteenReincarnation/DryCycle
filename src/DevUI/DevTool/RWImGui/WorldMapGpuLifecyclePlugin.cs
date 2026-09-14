using System.Threading;
using BepInEx;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Closes the lifetime gap between the retained Unity map renderer and DevUI/RWImGui presentation.
///
/// WorldMapGpuRuntime is hosted by an always-on BepInEx component, while DevUI.Update stops as soon
/// as Rain World closes DevTools. Its last ImGui FrameState can therefore remain valid-looking after
/// the editor is gone. This observer runs in LateUpdate so it executes after the renderer's ordinary
/// Update and can reliably prevent a stale retained Camera from drawing over gameplay/Vanilla UI.
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
            // Before the first DevUI lifetime there is nothing to retire. Once a real editor session
            // has existed, however, disabling the runtime is preferable to merely hiding its camera:
            // the renderer Update would otherwise consume the stale last FrameState and recreate the
            // scene every frame after a close. Reopening DevTools reattaches the hooks lazily below.
            if (observedLiveSession && !runtimeSuspendedForDormantSession)
            {
                WorldMapGpuRuntime.Disable();
                runtimeSuspendedForDormantSession = true;
            }
            return;
        }

        observedLiveSession = true;
        if (runtimeSuspendedForDormantSession)
        {
            WorldMapGpuRuntime.Enable(Logger, Thread.CurrentThread.ManagedThreadId);
            runtimeSuspendedForDormantSession = false;
        }

        EditorSession session = DevToolRuntime.ActiveSession;
        bool rebuiltMapVisible =
            session?.ToolMode == EditorToolMode.Map &&
            !EditorUiModeState.UseVanilla &&
            !EditorUiModeState.OverlayHidden;

        if (!rebuiltMapVisible)
        {
            // Tool switches keep the retained meshes/materials warm. Vanilla mode and Escape-hidden
            // overlays must still suppress the high-depth Unity camera immediately; passing no frame
            // uses WorldMapGpuScene's existing cheap camera-off path without destroying GPU caches.
            WorldMapGpuScene.Apply(null, session);
        }
    }

    private void OnDisable()
    {
        // WorldMapGpuRendererPlugin owns the actual hook lifetime and performs the full final Disable.
        // We only guarantee that this helper cannot leave its retained camera visible if plugin
        // shutdown ordering invokes us first.
        WorldMapGpuScene.Apply(null, DevToolRuntime.ActiveSession);
        observedLiveSession = false;
        runtimeSuspendedForDormantSession = false;
    }
}
