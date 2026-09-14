using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Map;
using DryCycle.DevUI.DevTool.World;
using ImGuiNET;
using UnityEngine;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Correctness/performance gate for the current ImGui-backed World Map presentation.
///
/// The retained scene is intentionally kept as an implementation/cache layer, but until it is
/// composited through an ImGui-owned RenderTexture none of its Unity MeshRenderers may become a
/// visible screen layer. This gate therefore blocks the retained scene Apply pass, disables any
/// renderer left alive by an earlier frame, clips every immediate map draw to the canvas, updates
/// the active room drag before drawing, and makes bidirectional links visually enter the actual
/// shortcut sockets instead of ending on two offset rails.
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

    // Defensive final suppression is only needed while the live Map tool can touch retained Unity
    // renderers. Dormant gameplay and unrelated DevTool pages are already parked by the lifecycle
    // controller, so skip all reflection/GameObject work there.
    private void LateUpdate() => WorldMapPresentationCorrectness.LateUpdate();

    private void OnDisable() => WorldMapPresentationCorrectness.Disable();
}

internal static class WorldMapPresentationCorrectness
{
    private const float TileDisplaySize = 2f;

    private delegate void OrigSceneApply(WorldMapGpuScene.FrameState frame, EditorSession session);
    private delegate void HookSceneApply(
        OrigSceneApply orig,
        WorldMapGpuScene.FrameState frame,
        EditorSession session);

    private delegate void OrigDrawCanvas(EditorMapPresentationSnapshot snapshot);
    private delegate void HookDrawCanvas(OrigDrawCanvas orig, EditorMapPresentationSnapshot snapshot);

    private delegate bool OrigTryConnectionSegment(
        EditorMapPresentationSnapshot snapshot,
        EditorMapConnectionSnapshot connection,
        Num.Vector2 canvasMin,
        out Num.Vector2 a,
        out Num.Vector2 b);
    private delegate bool HookTryConnectionSegment(
        OrigTryConnectionSegment orig,
        EditorMapPresentationSnapshot snapshot,
        EditorMapConnectionSnapshot connection,
        Num.Vector2 canvasMin,
        out Num.Vector2 a,
        out Num.Vector2 b);

    private delegate void OrigDrawConnectionStroke(
        ImDrawListPtr draw,
        Num.Vector2 a,
        Num.Vector2 b,
        uint shadow,
        uint core,
        float shadowThickness,
        float coreThickness,
        WorldConnectionDirection direction,
        bool dashed);
    private delegate void HookDrawConnectionStroke(
        OrigDrawConnectionStroke orig,
        ImDrawListPtr draw,
        Num.Vector2 a,
        Num.Vector2 b,
        uint shadow,
        uint core,
        float shadowThickness,
        float coreThickness,
        WorldConnectionDirection direction,
        bool dashed);

    private static readonly HookSceneApply SceneApplyHookDelegate = SceneApplyHook;
    private static readonly HookDrawCanvas DrawCanvasHookDelegate = DrawCanvasHook;
    private static readonly HookTryConnectionSegment ConnectionSegmentHookDelegate = TryConnectionSegmentHook;
    private static readonly HookDrawConnectionStroke ConnectionStrokeHookDelegate = DrawConnectionStrokeHook;

    private static ManualLogSource log;
    private static IDisposable sceneApplyHook;
    private static IDisposable drawCanvasHook;
    private static IDisposable connectionSegmentHook;
    private static IDisposable connectionStrokeHook;

    private static FieldInfo sceneReadyField;
    private static FieldInfo sceneRootField;
    private static FieldInfo sceneCameraField;
    private static FieldInfo pipeRootField;
    private static FieldInfo pipeRendererField;
    private static FieldInfo pipeRoomReadyField;
    private static FieldInfo pipeCreatureReadyField;

    private static FieldInfo draggingRoomField;
    private static FieldInfo dragStartMouseField;
    private static FieldInfo dragStartWorldField;
    private static FieldInfo zoomField;
    private static FieldInfo localPositionsField;

    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;

        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            Type mapType = typeof(WorldMapView);

            MethodInfo sceneApply = typeof(WorldMapGpuScene).GetMethod(
                "Apply",
                flags,
                null,
                new[] { typeof(WorldMapGpuScene.FrameState), typeof(EditorSession) },
                null);
            MethodInfo drawCanvas = mapType.GetMethod(
                "DrawCanvas",
                flags,
                null,
                new[] { typeof(EditorMapPresentationSnapshot) },
                null);
            MethodInfo tryConnectionSegment = mapType.GetMethod(
                "TryConnectionSegment",
                flags,
                null,
                new[]
                {
                    typeof(EditorMapPresentationSnapshot),
                    typeof(EditorMapConnectionSnapshot),
                    typeof(Num.Vector2),
                    typeof(Num.Vector2).MakeByRefType(),
                    typeof(Num.Vector2).MakeByRefType()
                },
                null);
            MethodInfo drawConnectionStroke = mapType.GetMethod(
                "DrawConnectionStroke",
                flags,
                null,
                new[]
                {
                    typeof(ImDrawListPtr), typeof(Num.Vector2), typeof(Num.Vector2),
                    typeof(uint), typeof(uint), typeof(float), typeof(float),
                    typeof(WorldConnectionDirection), typeof(bool)
                },
                null);

            sceneReadyField = typeof(WorldMapGpuScene).GetField("ready", flags);
            sceneRootField = typeof(WorldMapGpuScene).GetField("root", flags);
            sceneCameraField = typeof(WorldMapGpuScene).GetField("mapCamera", flags);

            Type pipeType = typeof(WorldMapGpuPipeBatch);
            pipeRootField = pipeType.GetField("root", flags);
            pipeRendererField = pipeType.GetField("renderer", flags);
            pipeRoomReadyField = pipeType.GetField("roomPipeGpuReady", flags);
            pipeCreatureReadyField = pipeType.GetField("creaturePipeGpuReady", flags);

            draggingRoomField = mapType.GetField("draggingRoom", flags);
            dragStartMouseField = mapType.GetField("dragStartMouse", flags);
            dragStartWorldField = mapType.GetField("dragStartWorld", flags);
            zoomField = mapType.GetField("zoom", flags);
            localPositionsField = mapType.GetField("localPositions", flags);

            if (sceneApply == null || drawCanvas == null || tryConnectionSegment == null ||
                drawConnectionStroke == null || sceneReadyField == null || sceneRootField == null ||
                sceneCameraField == null || pipeRootField == null || pipeRendererField == null ||
                pipeRoomReadyField == null || pipeCreatureReadyField == null ||
                draggingRoomField == null || dragStartMouseField == null ||
                dragStartWorldField == null || zoomField == null || localPositionsField == null)
                throw new MissingMemberException("World Map presentation correctness targets were not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            if (hookType == null)
                throw new TypeLoadException("MonoMod.RuntimeDetour.Hook is unavailable.");
            ConstructorInfo constructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            sceneApplyHook = constructor.Invoke(new object[] { sceneApply, SceneApplyHookDelegate }) as IDisposable;
            drawCanvasHook = constructor.Invoke(new object[] { drawCanvas, DrawCanvasHookDelegate }) as IDisposable;
            connectionSegmentHook = constructor.Invoke(new object[] { tryConnectionSegment, ConnectionSegmentHookDelegate }) as IDisposable;
            connectionStrokeHook = constructor.Invoke(new object[] { drawConnectionStroke, ConnectionStrokeHookDelegate }) as IDisposable;

            if (sceneApplyHook == null || drawCanvasHook == null ||
                connectionSegmentHook == null || connectionStrokeHook == null)
                throw new InvalidOperationException("One or more World Map correctness hooks were not created.");

            enabled = true;
            SuppressRetainedPresentation();
            log?.LogInfo("World Map correctness gate enabled: retained screen renderers paused, canvas clipping active.");
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("World Map correctness gate could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        DisposeHook(ref connectionStrokeHook);
        DisposeHook(ref connectionSegmentHook);
        DisposeHook(ref drawCanvasHook);
        DisposeHook(ref sceneApplyHook);

        sceneReadyField = null;
        sceneRootField = null;
        sceneCameraField = null;
        pipeRootField = null;
        pipeRendererField = null;
        pipeRoomReadyField = null;
        pipeCreatureReadyField = null;
        draggingRoomField = null;
        dragStartMouseField = null;
        dragStartWorldField = null;
        zoomField = null;
        localPositionsField = null;
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

        try { sceneReadyField?.SetValue(null, false); }
        catch { }

        try
        {
            if (sceneCameraField?.GetValue(null) is Camera camera && camera != null)
                camera.enabled = false;
        }
        catch { }

        try
        {
            if (sceneRootField?.GetValue(null) is GameObject root && root != null && root.activeSelf)
                root.SetActive(false);
        }
        catch { }

        try { pipeRoomReadyField?.SetValue(null, false); }
        catch { }
        try { pipeCreatureReadyField?.SetValue(null, false); }
        catch { }

        try
        {
            if (pipeRendererField?.GetValue(null) is MeshRenderer renderer && renderer != null)
                renderer.enabled = false;
        }
        catch { }

        try
        {
            if (pipeRootField?.GetValue(null) is GameObject pipeRoot && pipeRoot != null && pipeRoot.activeSelf)
                pipeRoot.SetActive(false);
        }
        catch { }
    }

    private static void SceneApplyHook(
        OrigSceneApply orig,
        WorldMapGpuScene.FrameState frame,
        EditorSession session)
    {
        // Do not call orig while the visible map is ImGui-backed. The old Apply pass rebuilt room,
        // route and pipe meshes on every drag-frame even though those meshes were not the visible UI.
        // That produced the drag spikes and also gave Unity renderers a chance to leak over gameplay.
        SuppressRetainedPresentation();
    }

    private static void DrawCanvasHook(OrigDrawCanvas orig, EditorMapPresentationSnapshot snapshot)
    {
        if (!enabled || snapshot?.Available != true)
        {
            orig(snapshot);
            return;
        }

        // WorldMapView used to update localPositions after drawing the frame. Move the active room
        // before the draw stack starts so the room, its sockets and its links all follow the mouse in
        // the same frame instead of oscillating one frame behind.
        UpdateActiveDragBeforeDraw();

        Num.Vector2 canvasMin = ImGui.GetCursorScreenPos();
        Num.Vector2 canvasSize = ImGui.GetContentRegionAvail();
        if (canvasSize.X < 1f || canvasSize.Y < 1f)
        {
            orig(snapshot);
            return;
        }

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(canvasMin, canvasMin + canvasSize, true);
        try
        {
            orig(snapshot);
        }
        finally
        {
            draw.PopClipRect();
        }
    }

    private static void UpdateActiveDragBeforeDraw()
    {
        try
        {
            if (draggingRoomField?.GetValue(null) is not int roomIndex || roomIndex < 0 ||
                !ImGui.IsMouseDown(ImGuiMouseButton.Left))
                return;

            if (dragStartMouseField?.GetValue(null) is not Num.Vector2 dragStartMouse ||
                dragStartWorldField?.GetValue(null) is not Num.Vector2 dragStartWorld ||
                zoomField?.GetValue(null) is not float zoom || zoom <= 0.0001f ||
                localPositionsField?.GetValue(null) is not Dictionary<int, Num.Vector2> positions)
                return;

            Num.Vector2 mouse = ImGui.GetIO().MousePos;
            positions[roomIndex] = dragStartWorld + (mouse - dragStartMouse) / zoom;
        }
        catch
        {
            // The original interaction path remains authoritative; a failed pre-draw update merely
            // falls back to its old one-frame-late behaviour instead of breaking the editor.
        }
    }

    private static bool TryConnectionSegmentHook(
        OrigTryConnectionSegment orig,
        EditorMapPresentationSnapshot snapshot,
        EditorMapConnectionSnapshot connection,
        Num.Vector2 canvasMin,
        out Num.Vector2 a,
        out Num.Vector2 b)
    {
        // A line without two concrete node indices cannot be pipe-to-pipe. Do not invent a room
        // centre/edge endpoint for unresolved or ambiguous topology; keep it out of the authored
        // connection layer until both real sockets are known.
        if (connection == null || connection.FromNodeIndex < 0 || connection.ToNodeIndex < 0)
        {
            a = Num.Vector2.Zero;
            b = Num.Vector2.Zero;
            return false;
        }

        return orig(snapshot, connection, canvasMin, out a, out b);
    }

    private static void DrawConnectionStrokeHook(
        OrigDrawConnectionStroke orig,
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
        if (dashed || direction != WorldConnectionDirection.Bidirectional)
        {
            orig(draw, a, b, shadow, core, shadowThickness, coreThickness, direction, dashed);
            return;
        }

        Num.Vector2 delta = b - a;
        float length = delta.Length();
        if (length < 34f)
        {
            // For a short connection the dual-rail language has no useful interior span. A single
            // centre line is clearer and still terminates exactly in both pipe sockets.
            draw.AddLine(a, b, shadow, shadowThickness);
            draw.AddLine(a, b, core, coreThickness);
            return;
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

        // One continuous shadow channel anchors the relationship to both actual pipe centres.
        draw.AddLine(a, b, shadow, shadowThickness);

        // Split only after leaving the socket, run the two directional rails through the middle,
        // then merge before entering the destination socket. The visible core is therefore truly
        // pipe-centre -> pipe-centre instead of two parallel rails missing both sockets.
        draw.AddLine(a, aPlus, core, railThickness);
        draw.AddLine(a, aMinus, core, railThickness);
        draw.AddLine(aPlus, bPlus, core, railThickness);
        draw.AddLine(aMinus, bMinus, core, railThickness);
        draw.AddLine(bPlus, b, core, railThickness);
        draw.AddLine(bMinus, b, core, railThickness);

        float arrowSize = Math.Max(6.2f, Math.Min(9.2f, 6.2f + coreThickness * 0.55f));
        DrawArrowHead(draw, Num.Vector2.Lerp(aPlus, bPlus, 0.64f), forward, shadow, core, arrowSize);
        DrawArrowHead(draw, Num.Vector2.Lerp(aMinus, bMinus, 0.36f), -forward, shadow, core, arrowSize);
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
