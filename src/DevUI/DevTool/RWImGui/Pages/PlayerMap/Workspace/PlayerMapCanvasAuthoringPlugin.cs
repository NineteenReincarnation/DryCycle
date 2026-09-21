using System;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Map.PlayerMap;
using ImGuiNET;
using UnityEngine;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Direct canvas authoring tools for the rebuilt Player Map. This extends only our ImGui workspace;
/// it does not invoke vanilla MapPage/Def_Mat interaction or any vanilla Update/Draw lifecycle.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(PlayerMapWorkspaceIntegrationPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class PlayerMapCanvasAuthoringPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.PlayerMap.CanvasAuthoring";
    public const string PluginName = "DryCycle Player Map Canvas Authoring";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() =>
        global::DryCycle.AuxiliaryPluginStartupGuard.Enable(
            PluginName + ".OnEnable",
            () => PlayerMapCanvasAuthoring.Enable(Logger),
            PlayerMapCanvasAuthoring.Disable);
    private void OnDisable() =>
        global::DryCycle.AuxiliaryPluginStartupGuard.Disable(
            PluginName + ".OnDisable",
            PlayerMapCanvasAuthoring.Disable);
}

internal static class PlayerMapCanvasAuthoring
{
    private enum DragKind
    {
        None,
        Create,
        Move,
        HandleA,
        HandleB
    }

    private static ManualLogSource log;
    private static bool enabled;

    private static bool createArmed;
    private static DragKind dragKind;
    private static int dragDefId = -1;
    private static Vector2 dragStartA;
    private static Vector2 dragStartB;
    private static Vector2 dragStartMouseCanon;
    private static Vector2 previewA;
    private static Vector2 previewB;
    private static bool consumedCanvasInput;

    internal static bool OwnsCanvas =>
        enabled && (consumedCanvasInput || createArmed || dragKind != DragKind.None);

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        log = logger;
        logger?.LogInfo("Player Map direct Def_Mat authoring/output bounds enabled through direct view calls; no self-detour attached.");
    }

    internal static void Disable()
    {
        createArmed = false;
        ResetDrag();
        enabled = false;
        log = null;
    }

    internal static void DrawInspectorTools(
        PlayerMapPresentationSnapshot snapshot,
        PlayerMapRoomSnapshot room)
    {
        if (!enabled) return;

        ImGui.Spacing();
        string label = createArmed
            ? DevToolUiSettings.T("取消画矩形", "Cancel Rectangle Tool")
            : DevToolUiSettings.T("画 Def_Mat 矩形", "Draw Def_Mat Rectangle");
        if (DevToolWidgets.ActionButton(
                label,
                "PlayerMapDefRectTool",
                createArmed ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
        {
            createArmed = !createArmed;
            ResetDrag();
        }
        if (ImGui.IsItemHovered())
            DevToolTooltip.Show(DevToolUiSettings.T(
                "在中央画布拖拽创建矩形；选中矩形后可直接拖两个角点或拖矩形内部整体移动。",
                "Drag on the center canvas to create a rectangle. Select one, then drag either corner or the body to move it."));
    }

    internal static void DrawCanvasOverlay(
        ImDrawListPtr draw,
        PlayerMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin)
    {
        consumedCanvasInput = false;
        if (!enabled || snapshot?.Available != true)
            return;

        Num.Vector2 pan = PlayerMapWorkspaceView.Pan;
        float zoom = PlayerMapWorkspaceView.Zoom;
        if (zoom <= 0f || float.IsNaN(zoom) || float.IsInfinity(zoom))
            return;

        DrawOutputBounds(draw, snapshot, canvasMin, pan, zoom);

        bool canvasHovered = ImGui.IsItemHovered();
        Num.Vector2 mouse = ImGui.GetIO().MousePos;
        Vector2 mouseCanon = ScreenToCanon(mouse, canvasMin, pan, zoom);
        int selectedId = PlayerMapWorkspaceView.SelectedDefMaterial;
        PlayerMapDefMaterialSnapshot? selected = FindDef(snapshot, selectedId);

        // Arm/Create mode owns the canvas until mouse-up so room dragging can never start beneath it.
        if (createArmed)
        {
            consumedCanvasInput = canvasHovered || dragKind == DragKind.Create;
            if (canvasHovered && dragKind == DragKind.None && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                dragKind = DragKind.Create;
                dragStartMouseCanon = mouseCanon;
                previewA = mouseCanon;
                previewB = mouseCanon;
            }

            if (dragKind == DragKind.Create)
            {
                previewB = mouseCanon;
                DrawPreviewRect(draw, canvasMin, pan, zoom, previewA, previewB, true);
                if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
                {
                    if ((previewB - previewA).sqrMagnitude >= 9f)
                    {
                        PlayerMapCommandQueue.Enqueue(new PlayerMapCommand(
                            PlayerMapCommandKind.CreateDefaultMaterial,
                            value: previewA,
                            valueB: previewB,
                            flag: false));
                    }
                    createArmed = false;
                    ResetDrag();
                }
            }
            return;
        }

        if (selected.HasValue)
            DrawSelectedHandles(draw, selected.Value, canvasMin, pan, zoom);

        if (dragKind == DragKind.None && canvasHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            PlayerMapDefMaterialSnapshot? hit = HitDef(snapshot, mouseCanon, zoom);
            if (hit.HasValue)
            {
                PlayerMapDefMaterialSnapshot def = hit.Value;
                PlayerMapWorkspaceView.SelectedDefMaterial = def.Id;
                selected = def;
                selectedId = def.Id;
                dragDefId = def.Id;
                dragStartA = def.A;
                dragStartB = def.B;
                previewA = def.A;
                previewB = def.B;
                dragStartMouseCanon = mouseCanon;
                dragKind = HitHandle(def.A, mouse, canvasMin, pan, zoom)
                    ? DragKind.HandleA
                    : HitHandle(def.B, mouse, canvasMin, pan, zoom)
                        ? DragKind.HandleB
                        : DragKind.Move;
                consumedCanvasInput = true;
            }
        }

        if (dragKind != DragKind.None && dragKind != DragKind.Create && dragDefId >= 0)
        {
            consumedCanvasInput = true;
            Vector2 delta = mouseCanon - dragStartMouseCanon;
            switch (dragKind)
            {
                case DragKind.HandleA:
                    previewA = dragStartA + delta;
                    previewB = dragStartB;
                    break;
                case DragKind.HandleB:
                    previewA = dragStartA;
                    previewB = dragStartB + delta;
                    break;
                case DragKind.Move:
                    previewA = dragStartA + delta;
                    previewB = dragStartB + delta;
                    break;
            }

            DrawPreviewRect(draw, canvasMin, pan, zoom, previewA, previewB, false);
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                if ((previewA - dragStartA).sqrMagnitude > 0.0001f ||
                    (previewB - dragStartB).sqrMagnitude > 0.0001f)
                {
                    PlayerMapCommandQueue.Enqueue(new PlayerMapCommand(
                        PlayerMapCommandKind.SetDefaultMaterialRect,
                        integer: dragDefId,
                        value: previewA,
                        valueB: previewB));
                }
                ResetDrag();
            }
        }
    }

    private static void DrawOutputBounds(
        ImDrawListPtr draw,
        PlayerMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        Num.Vector2 pan,
        float zoom)
    {
        bool any = false;
        float minX = float.MaxValue;
        float minY = float.MaxValue;
        float maxX = float.MinValue;
        float maxY = float.MinValue;
        PlayerMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<PlayerMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
        {
            PlayerMapRoomSnapshot room = rooms[i];
            if (room == null || room.Disabled) continue;
            float halfW = Math.Max(1, room.Bake?.Width ?? 1) * PlayerMapCoordinateSystem.CanonPixelsPerTile * 0.5f;
            float halfH = Math.Max(1, room.Bake?.Height ?? 1) * PlayerMapCoordinateSystem.CanonPixelsPerTile * 0.5f;
            minX = Math.Min(minX, room.EffectivePosition.x - halfW);
            minY = Math.Min(minY, room.EffectivePosition.y - halfH);
            maxX = Math.Max(maxX, room.EffectivePosition.x + halfW);
            maxY = Math.Max(maxY, room.EffectivePosition.y + halfH);
            any = true;
        }
        if (!any) return;

        float pad = PlayerMapCoordinateSystem.OutputPadding * PlayerMapCoordinateSystem.CanonPixelsPerTile;
        Vector2 a = new(minX - pad, minY - pad);
        Vector2 b = new(maxX + pad, maxY + pad);
        Num.Vector2 sa = CanonToScreen(a, canvasMin, pan, zoom);
        Num.Vector2 sb = CanonToScreen(b, canvasMin, pan, zoom);
        Num.Vector2 min = new(Math.Min(sa.X, sb.X), Math.Min(sa.Y, sb.Y));
        Num.Vector2 max = new(Math.Max(sa.X, sb.X), Math.Max(sa.Y, sb.Y));
        uint color = ImGui.GetColorU32(ImGuiCol.TextDisabled);
        draw.AddRect(min, max, color, 0f, ImDrawFlags.None, 1.2f);

        int width = (int)((maxX - minX) / PlayerMapCoordinateSystem.CanonPixelsPerTile) +
                    PlayerMapCoordinateSystem.OutputPadding * 2;
        int layerHeight = (int)((maxY - minY) / PlayerMapCoordinateSystem.CanonPixelsPerTile) +
                          PlayerMapCoordinateSystem.OutputPadding * 2;
        int height = layerHeight * PlayerMapCoordinateSystem.LayerCount;
        string label = "Render " + width + " × " + height + "  (" + PlayerMapCoordinateSystem.LayerCount + " layers)";
        draw.AddText(min + new Num.Vector2(5f, 4f), color, label);
    }

    private static void DrawSelectedHandles(
        ImDrawListPtr draw,
        PlayerMapDefMaterialSnapshot def,
        Num.Vector2 canvasMin,
        Num.Vector2 pan,
        float zoom)
    {
        uint color = ImGui.GetColorU32(ImGuiCol.HeaderActive);
        DrawHandle(draw, CanonToScreen(def.A, canvasMin, pan, zoom), color);
        DrawHandle(draw, CanonToScreen(def.B, canvasMin, pan, zoom), color);
    }

    private static void DrawPreviewRect(
        ImDrawListPtr draw,
        Num.Vector2 canvasMin,
        Num.Vector2 pan,
        float zoom,
        Vector2 a,
        Vector2 b,
        bool creating)
    {
        Num.Vector2 sa = CanonToScreen(a, canvasMin, pan, zoom);
        Num.Vector2 sb = CanonToScreen(b, canvasMin, pan, zoom);
        Num.Vector2 min = new(Math.Min(sa.X, sb.X), Math.Min(sa.Y, sb.Y));
        Num.Vector2 max = new(Math.Max(sa.X, sb.X), Math.Max(sa.Y, sb.Y));
        uint color = ImGui.GetColorU32(creating ? ImGuiCol.HeaderHovered : ImGuiCol.HeaderActive);
        draw.AddRect(min, max, color, 0f, ImDrawFlags.None, 2f);
        DrawHandle(draw, sa, color);
        DrawHandle(draw, sb, color);
    }

    private static void DrawHandle(ImDrawListPtr draw, Num.Vector2 point, uint color)
    {
        const float r = 4.5f;
        draw.AddRectFilled(point - new Num.Vector2(r, r), point + new Num.Vector2(r, r), color);
    }

    private static PlayerMapDefMaterialSnapshot? HitDef(
        PlayerMapPresentationSnapshot snapshot,
        Vector2 point,
        float zoom)
    {
        PlayerMapDefMaterialSnapshot[] defs = snapshot.DefaultMaterials ?? Array.Empty<PlayerMapDefMaterialSnapshot>();
        // Reverse order: last authored rect is visually/topologically dominant in the compositor.
        for (int i = defs.Length - 1; i >= 0; i--)
        {
            PlayerMapDefMaterialSnapshot def = defs[i];
            float handleCanon = Math.Max(4f, 8f / Math.Max(0.05f, zoom));
            if ((def.A - point).sqrMagnitude <= handleCanon * handleCanon ||
                (def.B - point).sqrMagnitude <= handleCanon * handleCanon ||
                (point.x >= def.Left && point.x <= def.Right && point.y >= def.Bottom && point.y <= def.Top))
                return def;
        }
        return null;
    }

    private static bool HitHandle(
        Vector2 handle,
        Num.Vector2 mouse,
        Num.Vector2 canvasMin,
        Num.Vector2 pan,
        float zoom)
    {
        Num.Vector2 point = CanonToScreen(handle, canvasMin, pan, zoom);
        Num.Vector2 delta = mouse - point;
        return delta.X * delta.X + delta.Y * delta.Y <= 64f;
    }

    private static PlayerMapDefMaterialSnapshot? FindDef(PlayerMapPresentationSnapshot snapshot, int id)
    {
        PlayerMapDefMaterialSnapshot[] defs = snapshot?.DefaultMaterials ?? Array.Empty<PlayerMapDefMaterialSnapshot>();
        for (int i = 0; i < defs.Length; i++)
            if (defs[i].Id == id) return defs[i];
        return null;
    }

    private static Vector2 ScreenToCanon(
        Num.Vector2 screen,
        Num.Vector2 canvasMin,
        Num.Vector2 pan,
        float zoom)
    {
        Num.Vector2 local = (screen - canvasMin - pan) / Math.Max(0.0001f, zoom);
        return new Vector2(local.X, local.Y);
    }

    private static Num.Vector2 CanonToScreen(
        Vector2 point,
        Num.Vector2 canvasMin,
        Num.Vector2 pan,
        float zoom) =>
        canvasMin + pan + new Num.Vector2(point.x, point.y) * zoom;

    private static void ResetDrag()
    {
        dragKind = DragKind.None;
        dragDefId = -1;
        dragStartA = default;
        dragStartB = default;
        dragStartMouseCanon = default;
        previewA = default;
        previewB = default;
        consumedCanvasInput = false;
    }

}
