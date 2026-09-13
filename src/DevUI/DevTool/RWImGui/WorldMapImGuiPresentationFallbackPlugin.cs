using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Map;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Correctness bridge for the retained World Map renderer.
///
/// WorldMapGpuScene currently renders its retained room/route meshes through a standalone Unity
/// Camera. Rain World's final presentation path and RWImGui are not guaranteed to composite that
/// camera into the ImGui workspace, which can leave the canvas with only immediate-mode grid and
/// shortcut markers. Keep the retained scene/cache alive for future integration, but force the
/// WorldMapView draw pass to use its complete ImGui room/connection fallback until the retained
/// output is explicitly presented through an ImGui-owned render target.
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

            if (drawCanvas == null || sceneReadyField == null)
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
            log?.LogInfo("World Map ImGui presentation fallback enabled; retained screen-camera replacement is bypassed.");
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
        enabled = false;
        log = null;
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

        // Both WorldMapGpuRuntime.DrawCanvasHook and DrawRoomGeometryHook consult the same ready
        // field. Mask it only for this ImGui draw stack so neither hook suppresses the complete
        // immediate fallback. The retained scene itself remains built and can still be evolved into
        // a RenderTexture-backed presentation path without throwing away its caches/batches.
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
