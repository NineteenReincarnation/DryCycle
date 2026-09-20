using System;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Map;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Correctness/performance gate for the current ImGui-backed World Map presentation.
///
/// All integration is explicit: retained Unity renderers expose suppression APIs and WorldMapView
/// calls the clip/stroke helpers directly. No DryCycle-owned method is RuntimeDetoured.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldMapImGuiPresentationFallbackPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency(WorldMapGpuPipeBatchPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapPresentationCorrectnessPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMap.PresentationCorrectness";
    public const string PluginName = "DryCycle DevTool World Map Presentation Correctness";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldMapPresentationCorrectness.Enable(Logger);
    private void LateUpdate() => WorldMapPresentationCorrectness.LateUpdate();
    private void OnDisable() => WorldMapPresentationCorrectness.Disable();
}

internal static class WorldMapPresentationCorrectness
{
    private static ManualLogSource log;
    private static bool enabled;

    internal static bool ShouldSuppressRetainedApply => enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        log = logger;
        SuppressRetainedPresentation();
        logger?.LogInfo("World Map correctness gate uses direct scene/view APIs; retained screen renderers paused with no self-detours.");
    }

    internal static void Disable()
    {
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

        SuppressRetainedPresentation();
    }

    internal static void SuppressRetainedPresentation()
    {
        if (!enabled) return;
        WorldMapGpuScene.SuppressScreenPresentation();
        WorldMapGpuPipeBatch.SuppressScreenPresentation();
    }

    internal static bool BeginCanvasClip(
        ImDrawListPtr draw,
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        Num.Vector2 canvasSize)
    {
        if (!enabled || snapshot?.Available != true || canvasSize.X < 1f || canvasSize.Y < 1f)
            return false;

        try
        {
            draw.PushClipRect(canvasMin, canvasMin + canvasSize, true);
            return true;
        }
        catch (Exception error)
        {
            log?.LogDebug("World Map canvas clip push failed: " + error.Message);
            return false;
        }
    }

    internal static void EndCanvasClip(ImDrawListPtr draw, bool pushed)
    {
        if (!pushed) return;
        try { draw.PopClipRect(); }
        catch (Exception error) { log?.LogDebug("World Map canvas clip pop failed: " + error.Message); }
    }

    internal static bool TryDrawBidirectionalStroke(
        ImDrawListPtr draw,
        Num.Vector2 a,
        Num.Vector2 b,
        uint shadow,
        uint core,
        float shadowThickness,
        float coreThickness,
        WorldConnectionDirection direction,
        bool dashed)
    {
        if (!enabled || dashed || direction != WorldConnectionDirection.Bidirectional)
            return false;

        Num.Vector2 delta = b - a;
        float length = delta.Length();
        if (length < 34f)
        {
            draw.AddLine(a, b, shadow, shadowThickness);
            draw.AddLine(a, b, core, coreThickness);
            return true;
        }

        Num.Vector2 forward = delta / length;
        Num.Vector2 normal = new(-forward.Y, forward.X);
        float railOffset = Math.Max(2f, coreThickness * 0.72f);
        float railThickness = Math.Max(1.6f, coreThickness * 0.72f);
        float lead = Math.Min(18f, Math.Max(9f, length * 0.12f));
        Num.Vector2 splitA = a + forward * lead;
        Num.Vector2 splitB = b - forward * lead;
        Num.Vector2 aPlus = splitA + normal * railOffset;
        Num.Vector2 aMinus = splitA - normal * railOffset;
        Num.Vector2 bPlus = splitB + normal * railOffset;
        Num.Vector2 bMinus = splitB - normal * railOffset;

        draw.AddLine(a, b, shadow, shadowThickness);
        draw.AddLine(a, aPlus, core, railThickness);
        draw.AddLine(a, aMinus, core, railThickness);
        draw.AddLine(aPlus, bPlus, core, railThickness);
        draw.AddLine(aMinus, bMinus, core, railThickness);
        draw.AddLine(bPlus, b, core, railThickness);
        draw.AddLine(bMinus, b, core, railThickness);

        float arrowSize = Math.Max(6.2f, Math.Min(9.2f, 6.2f + coreThickness * 0.55f));
        DrawArrowHead(draw, Num.Vector2.Lerp(aPlus, bPlus, 0.64f), forward, shadow, core, arrowSize);
        DrawArrowHead(draw, Num.Vector2.Lerp(aMinus, bMinus, 0.36f), -forward, shadow, core, arrowSize);
        return true;
    }

    private static void DrawArrowHead(
        ImDrawListPtr draw,
        Num.Vector2 tip,
        Num.Vector2 direction,
        uint shadow,
        uint core,
        float size)
    {
        float length = direction.Length();
        if (length <= 0.001f) return;
        Num.Vector2 forward = direction / length;
        Num.Vector2 normal = new(-forward.Y, forward.X);
        DrawArrowTriangle(draw, tip, forward, normal, shadow, size + 2.4f);
        DrawArrowTriangle(draw, tip, forward, normal, core, size);
    }

    private static void DrawArrowTriangle(
        ImDrawListPtr draw,
        Num.Vector2 tip,
        Num.Vector2 forward,
        Num.Vector2 normal,
        uint color,
        float size)
    {
        Num.Vector2 baseCenter = tip - forward * size;
        float wing = size * 0.58f;
        draw.AddTriangleFilled(tip, baseCenter + normal * wing, baseCenter - normal * wing, color);
    }
}
