using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DevInterface;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Transitional attachment for the retained World Map room-source service.
///
/// The optimization policy and all room-source ownership live behind
/// WorldMapLegacyRoomSourceService. RuntimeDetour remains here only until WorldMapGpuScene calls
/// that service directly; the hook adapter does not pass original DryCycle implementations back
/// into the service and owns no cache/data behavior.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldMapGpuRendererPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapGpuRetainedOptimizerPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMapGPU.RetainedOptimizer";
    public const string PluginName = "DryCycle DevTool GPU World Map Retained Optimizer";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldMapGpuRetainedOptimizer.Enable(Logger);
    private void OnDisable() => WorldMapGpuRetainedOptimizer.Disable();
}

internal static class WorldMapGpuRetainedOptimizer
{
    private delegate int OrigComputeRoomSourceHash(MapPage page, WorldMapGpuScene.FrameState frame);
    private delegate int HookComputeRoomSourceHash(
        OrigComputeRoomSourceHash orig,
        MapPage page,
        WorldMapGpuScene.FrameState frame);

    private delegate bool OrigTryFindRoomPanel(MapPage page, int roomIndex, out RoomPanel panel);
    private delegate bool HookTryFindRoomPanel(
        OrigTryFindRoomPanel orig,
        MapPage page,
        int roomIndex,
        out RoomPanel panel);

    private static readonly HookComputeRoomSourceHash SourceHashHookDelegate = ComputeRoomSourceHashHook;
    private static readonly HookTryFindRoomPanel RoomPanelHookDelegate = TryFindRoomPanelHook;

    private static ManualLogSource log;
    private static IDisposable sourceHashHook;
    private static IDisposable roomPanelHook;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;

        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            Type sceneType = typeof(WorldMapGpuScene);
            MethodInfo sourceHash = sceneType.GetMethod(
                "ComputeRoomSourceHash",
                flags,
                null,
                new[] { typeof(MapPage), typeof(WorldMapGpuScene.FrameState) },
                null);
            MethodInfo findRoomPanel = sceneType.GetMethod(
                "TryFindRoomPanel",
                flags,
                null,
                new[] { typeof(MapPage), typeof(int), typeof(RoomPanel).MakeByRefType() },
                null);
            if (sourceHash == null || findRoomPanel == null)
                throw new MissingMemberException("World Map retained CPU lookup targets were not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            if (hookType == null)
                throw new TypeLoadException("MonoMod.RuntimeDetour.Hook is unavailable.");
            ConstructorInfo constructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            sourceHashHook = constructor.Invoke(new object[] { sourceHash, SourceHashHookDelegate }) as IDisposable;
            roomPanelHook = constructor.Invoke(new object[] { findRoomPanel, RoomPanelHookDelegate }) as IDisposable;
            enabled = true;
            log?.LogInfo("GPU World Map retained room-source service attached through transitional hooks.");
        }
        catch (Exception error)
        {
            string message = Unwrap(error).Message;
            Disable();
            logger?.LogWarning("GPU World Map retained room-source service could not attach: " + message);
        }
    }

    internal static void Disable()
    {
        DisposeHook(ref roomPanelHook);
        DisposeHook(ref sourceHashHook);
        WorldMapLegacyRoomSourceService.Reset();
        enabled = false;
        log = null;
    }

    private static int ComputeRoomSourceHashHook(
        OrigComputeRoomSourceHash orig,
        MapPage page,
        WorldMapGpuScene.FrameState frame)
    {
        // RuntimeDetour requires the original delegate in the hook signature, but the service is now
        // fully authoritative and deliberately does not call back into the DryCycle method it hooks.
        if (!enabled) return orig(page, frame);
        return WorldMapLegacyRoomSourceService.ComputeSourceHash(page, frame);
    }

    private static bool TryFindRoomPanelHook(
        OrigTryFindRoomPanel orig,
        MapPage page,
        int roomIndex,
        out RoomPanel panel)
    {
        if (!enabled) return orig(page, roomIndex, out panel);
        return WorldMapLegacyRoomSourceService.TryFindRoomPanel(page, roomIndex, out panel);
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
