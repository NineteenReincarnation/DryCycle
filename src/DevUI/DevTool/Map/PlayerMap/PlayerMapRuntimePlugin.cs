using BepInEx;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

/// <summary>
/// Owns the rebuilt Player Map backend lifetime independently of the optional RWImGui frontend.
/// This keeps SaveMapConfig interception, room-bake cache lifetime and command semantics active as
/// one backend unit while allowing the presentation layer to be replaced without changing data flow.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(global::DryCycle.Plugin.ModId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class PlayerMapRuntimePlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.PlayerMap.Runtime";
    public const string PluginName = "DryCycle DevTool Player Map Runtime";
    public const string PluginVersion = global::DryCycle.Plugin.Version;

    private void OnEnable() =>
        global::DryCycle.AuxiliaryPluginStartupGuard.Enable(
            PluginName + ".OnEnable",
            PlayerMapWorkspaceRuntime.Enable,
            Shutdown);

    private void OnDisable() =>
        global::DryCycle.AuxiliaryPluginStartupGuard.Disable(
            PluginName + ".OnDisable",
            Shutdown);

    private static void Shutdown()
    {
        PlayerMapRenderPreparationController.Reset();
        PlayerMapRenderScheduler.Reset();
        PlayerMapActivityGate.Reset();
        PlayerMapWorkspaceRuntime.Disable();
    }
}

/// <summary>
/// Prevents the rebuilt Player Map from reintroducing hidden-page frame cost. The frontend marks
/// itself visible during Draw; the backend command phase is allowed for the current and next two
/// frames so commands enqueued at the end of a visible frame are still consumed on the following
/// update. A user-started Render chain (bake preparation or compositor) is the only exception: once
/// requested it keeps receiving its bounded frame budget even if the developer temporarily switches
/// back to World Layout.
/// </summary>
internal static class PlayerMapActivityGate
{
    private static int lastVisibleFrame = int.MinValue;

    internal static bool ShouldProcess
    {
        get
        {
            if (PlayerMapRenderPreparationController.IsRunning || PlayerMapRenderScheduler.IsRunning)
                return true;
            int frame = Time.frameCount;
            return lastVisibleFrame != int.MinValue && frame >= lastVisibleFrame && frame - lastVisibleFrame <= 2;
        }
    }

    internal static void MarkVisible() => lastVisibleFrame = Time.frameCount;
    internal static void Reset() => lastVisibleFrame = int.MinValue;
}
