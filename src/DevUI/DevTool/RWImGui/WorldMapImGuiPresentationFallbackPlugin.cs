using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Map;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Correctness bridge for the retained World Map renderer.
///
/// WorldMapGpuScene currently renders its retained room/route meshes through a standalone Unity
/// Camera. Rain World's final presentation path and RWImGui do not reliably composite that camera
/// into the ImGui workspace: the retained map can be drawn over the gameplay viewport while the
/// actual ImGui canvas receives only grid/labels/shortcut markers. Until the retained scene is
/// presented through an ImGui-owned RenderTexture, keep its caches and spatial indices alive but
/// use WorldMapView's complete immediate presentation as the authoritative visible map.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldMapGpuRendererPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency(WorldMapLegacyVisualGuardPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapImGuiPresentationFallbackPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMapGPU.ImGuiPresentationFallback";
    public const string PluginName = "DryCycle DevTool World Map ImGui Presentation Fallback";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldMapImGuiPresentationFallback.Enable(Logger);

    // WorldMapGpuRuntime.UpdateMainThread can enable the retained screen camera while the Map tool
    // is live. Suppress it immediately before camera rendering, but do not pay reflection cost during
    // ordinary gameplay or unrelated DevTool pages where the lifecycle controller already owns hide.
    private void LateUpdate() => WorldMapImGuiPresentationFallback.LateUpdate();

    private void OnDisable() => WorldMapImGuiPresentationFallback.Disable();
}

internal static class WorldMapImGuiPresentationFallback
{
    private delegate void OrigDrawCanvas(EditorMapPresentationSnapshot snapshot);
    private delegate void HookDrawCanvas(OrigDrawCanvas orig, EditorMapPresentationSnapshot snapshot);

    private static readonly HookDrawCanvas DrawCanvasHookDelegate = DrawCanvasHook;

    private static ManualLogSource log;
    private static IDisposable canvasHook;
    private static FieldInfo sceneReadyField;
    private static FieldInfo sceneCameraField;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;

        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            MethodInfo drawCanvas = typeof(WorldMapView).GetMethod(
                "DrawCanvas",
                flags,
                null,
                new[] { typeof(EditorMapPresentationSnapshot) },
                null);
            sceneReadyField = typeof(WorldMapGpuScene).GetField("ready", flags);
            sceneCameraField = typeof(WorldMapGpuScene).GetField("mapCamera", flags);

            if (drawCanvas == null || sceneReadyField == null || sceneCameraField == null)
                throw new MissingMemberException("World Map ImGui fallback targets were not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            if (hookType == null)
                throw new TypeLoadException("MonoMod.RuntimeDetour.Hook is unavailable.");

            ConstructorInfo constructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            canvasHook = constructor.Invoke(new object[] { drawCanvas, DrawCanvasHookDelegate }) as IDisposable;
            if (canvasHook == null)
                throw new InvalidOperationException("World Map ImGui fallback hook was not created.");

            enabled = true;
            SuppressStandaloneCamera();
            log?.LogInfo("World Map ImGui presentation fallback enabled; retained screen camera is suppressed.");
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("World Map ImGui presentation fallback could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        try { canvasHook?.Dispose(); }
        catch { }
        canvasHook = null;
        sceneReadyField = null;
        sceneCameraField = null;
        enabled = false;
        log = null;
    }

    internal static void LateUpdate()
    {
        if (!enabled || !DevToolSessionHub.IsCurrentSessionLive)
            return;

        EditorSession session = DevToolRuntime.ActiveSession;
        if (session?.ToolMode != EditorToolMode.Map)
            return;

        SuppressStandaloneCamera();
    }

    internal static void SuppressStandaloneCamera()
    {
        if (!enabled || sceneCameraField == null) return;
        try
        {
            if (sceneCameraField.GetValue(null) is Camera camera && camera != null && camera.enabled)
                camera.enabled = false;
        }
        catch
        {
            // The retained scene may be torn down during a process/page transition. There is
            // nothing to suppress in that frame, and Apply() will rebuild it if needed later.
        }
    }

    private static void DrawCanvasHook(OrigDrawCanvas orig, EditorMapPresentationSnapshot snapshot)
    {
        if (!enabled || sceneReadyField == null)
        {
            orig(snapshot);
            return;
        }

        bool retainedReady;
        try
        {
            retainedReady = sceneReadyField.GetValue(null) is bool value && value;
        }
        catch
        {
            orig(snapshot);
            return;
        }

        if (!retainedReady)
        {
            orig(snapshot);
            return;
        }

        // WorldMapGpuRuntime.DrawCanvasHook and DrawRoomGeometryHook both use Ready as their signal
        // that retained output is already visible. It is not visible in the ImGui canvas on this
        // presentation path, so mask Ready only while WorldMapView emits the current frame. This
        // restores complete immediate rooms and links without discarding retained caches/indices.
        sceneReadyField.SetValue(null, false);
        try
        {
            orig(snapshot);
        }
        finally
        {
            try { sceneReadyField.SetValue(null, true); }
            catch { }
        }
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
