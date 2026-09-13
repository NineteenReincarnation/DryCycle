using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Map;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Spreads non-essential World Map preview discovery over time. Current and selected rooms are
/// still refreshed by the priority path in each presentation cache; this gate only limits the
/// background sweep across the rest of the region.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldMapPerformancePlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapBackgroundBudgetPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMapBackgroundBudget";
    public const string PluginName = "DryCycle DevTool World Map Background Budget";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldMapBackgroundBudget.Enable(Logger);

    private void OnDisable() => WorldMapBackgroundBudget.Disable();
}

internal static class WorldMapBackgroundBudget
{
    // Below the renderer's overview LOD threshold detailed RoomSettings/MapTex geometry is not
    // visible anyway. Avoid decoding every other room until the author actually zooms in.
    private const float DetailedBackgroundZoom = 0.42f;
    private const int GeometrySweepIntervalFrames = 4;
    private const int ShortcutSweepIntervalFrames = 4;

    private delegate void OrigGeometryBackground(global::World world, int currentRoom, int selectedRoom);
    private delegate void HookGeometryBackground(
        OrigGeometryBackground orig,
        global::World world,
        int currentRoom,
        int selectedRoom);

    private delegate void OrigShortcutBackground(int currentRoom, int selectedRoom);
    private delegate void HookShortcutBackground(
        OrigShortcutBackground orig,
        int currentRoom,
        int selectedRoom);

    private static readonly HookGeometryBackground GeometryBackgroundHookDelegate = GeometryBackgroundHook;
    private static readonly HookShortcutBackground ShortcutBackgroundHookDelegate = ShortcutBackgroundHook;

    private static ManualLogSource log;
    private static IDisposable geometryHook;
    private static IDisposable shortcutHook;
    private static FieldInfo zoomField;
    private static bool enabled;
    private static int lastGeometrySweepFrame = -1000;
    private static int lastShortcutSweepFrame = -1000;
    private static string geometryRegion = string.Empty;
    private static string shortcutRegion = string.Empty;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;

        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            if (hookType == null)
                throw new TypeLoadException("MonoMod.RuntimeDetour.Hook is unavailable.");

            ConstructorInfo constructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            MethodInfo geometryBackground = typeof(MapRoomGeometryPresentationHub).GetMethod(
                "ProcessBackground",
                flags,
                null,
                new[] { typeof(global::World), typeof(int), typeof(int) },
                null);
            MethodInfo shortcutBackground = typeof(WorldMapShortcutPresentation).GetMethod(
                "ProcessBackground",
                flags,
                null,
                new[] { typeof(int), typeof(int) },
                null);
            zoomField = typeof(WorldMapView).GetField("zoom", flags);

            if (geometryBackground == null || shortcutBackground == null || zoomField == null)
                throw new MissingMemberException("World Map background-budget hook targets were not found.");

            geometryHook = constructor.Invoke(
                new object[] { geometryBackground, GeometryBackgroundHookDelegate }) as IDisposable;
            shortcutHook = constructor.Invoke(
                new object[] { shortcutBackground, ShortcutBackgroundHookDelegate }) as IDisposable;

            enabled = true;
            log?.LogInfo("World Map background preview budget enabled.");
        }
        catch (Exception error)
        {
            Disable();
            log?.LogWarning("World Map background preview budget could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        DisposeHook(ref shortcutHook);
        DisposeHook(ref geometryHook);
        zoomField = null;
        lastGeometrySweepFrame = -1000;
        lastShortcutSweepFrame = -1000;
        geometryRegion = string.Empty;
        shortcutRegion = string.Empty;
        enabled = false;
        log = null;
    }

    private static void GeometryBackgroundHook(
        OrigGeometryBackground orig,
        global::World world,
        int currentRoom,
        int selectedRoom)
    {
        string region = world?.name ?? string.Empty;
        if (!string.Equals(region, geometryRegion, StringComparison.OrdinalIgnoreCase))
        {
            geometryRegion = region;
            lastGeometrySweepFrame = Time.frameCount;
            return;
        }

        float zoom = zoomField?.GetValue(null) is float value ? value : 1f;
        if (zoom < DetailedBackgroundZoom)
            return;

        if (Time.frameCount - lastGeometrySweepFrame < GeometrySweepIntervalFrames)
            return;

        lastGeometrySweepFrame = Time.frameCount;
        orig(world, currentRoom, selectedRoom);
    }

    private static void ShortcutBackgroundHook(
        OrigShortcutBackground orig,
        int currentRoom,
        int selectedRoom)
    {
        string region = DevToolRuntime.ActiveSession?.World?.name ?? string.Empty;
        if (!string.Equals(region, shortcutRegion, StringComparison.OrdinalIgnoreCase))
        {
            shortcutRegion = region;
            lastShortcutSweepFrame = Time.frameCount;
            return;
        }

        if (Time.frameCount - lastShortcutSweepFrame < ShortcutSweepIntervalFrames)
            return;

        lastShortcutSweepFrame = Time.frameCount;
        orig(currentRoom, selectedRoom);
    }

    private static void DisposeHook(ref IDisposable hook)
    {
        try
        {
            hook?.Dispose();
        }
        catch
        {
        }
        finally
        {
            hook = null;
        }
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
