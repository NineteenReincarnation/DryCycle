using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Map.Cartography;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>Presentation and gestures only. Every author edit is a document-scoped backend command.</summary>
internal static partial class CartographyView
{
    private enum Tool { Select, Route, Text, Marker, Line, Box }
    private enum ExportAreaDrag
    {
        None,
        Move,
        Left,
        Right,
        Top,
        Bottom,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }
    private static CartographyPresentation observed;
    private static readonly HashSet<string> Selection = new(StringComparer.Ordinal);
    private static string activeLayer = "notes", exportPath = string.Empty, copyPath = string.Empty;
    private static Num.Vector2 pan, startMouse, delta, marqueeStart;
    private static float zoom = 1;
    private static bool fit = true, fitSelection, dragging, marquee, addSelection, subtractSelection;
    private static long gestureRevision;
    private static Tool tool;
    private static CartographyMarker marker = default;
    private static CartographyExportFormat format;
    private static CartographyItem draft;
    private static bool draftDirty;
    private static bool layerDirty, styleDirty;
    private static CartographyLayer layerDraft;
    private static CartographyDocument styleDraft;
    private static long draftRevision;
    private static string selectedItem = string.Empty;
    private static string pendingSelection = string.Empty;
    private static long pendingSelectionRevision;
    private static bool diagnosticsWindowOpen;
    private static ExportAreaDrag exportAreaDrag;
    private static Num.Vector2 exportAreaDragStart;
    private static Num.Vector4 exportAreaStartRect;
    private static Num.Vector4 exportAreaPreviewRect;

    internal static void Leave()
    {
        LeaveDrafts();
        CartographyRuntime.SetActive(false);
    }

    private static void FinishGesture()
    {
        // Release a live drag before hiding the view so author movement is not silently lost.
        if (dragging && observed != null && delta.LengthSquared() > 0.001f)
            Send(CartographyCommandKind.Move, command => { command.Ids = Selection.ToArray(); command.X = delta.X; command.Y = delta.Y; command.Revision = gestureRevision; });
        if (routeGesture != null && observed != null)
        {
            CartographyItem completed = routeGesture;
            Send(CartographyCommandKind.UpdateItem, command => { command.Item = completed; command.Revision = routeRevision; });
        }
        routeGesture = null;
        routeHandle = -1;
        exportAreaDrag = ExportAreaDrag.None;
        dragging = marquee = false; delta = default;
    }

    internal static unsafe void Draw(EditorPresentationSnapshot editor)
    {
        // Layer names, room/subregion names and author text may be Chinese in either UI language.
        ImGuiIOPtr io = ImGui.GetIO();
        float oldScale = io.FontGlobalScale;
        float oldBaseSize = ImGui.GetFont().FontSize;
        bool resolvedFont = DevToolFontCatalog.TryResolveRegisteredFace(DevToolUiSettings.ChineseFontFamily,
            DevToolUiSettings.FontWeight, true, out ImFontPtr font, out _, out _, out _);
        bool pushedFont = resolvedFont &&
            DevToolFrontend.TryPushRegisteredFont(font, "Cartography content font");
        if (pushedFont)
            io.FontGlobalScale = oldScale * oldBaseSize / Math.Max(1, font.FontSize);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Num.Vector4(.045f, .06f, .08f, 1));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Num.Vector4(.10f, .14f, .19f, 1));
        ImGui.PushStyleColor(ImGuiCol.PopupBg, new Num.Vector4(.06f, .08f, .11f, 1));
        try
        {
            DrawContent(editor);
            DrawDiagnosticsWindow(CartographyRuntime.Presentation);
        }
        finally
        {
            ImGui.PopStyleColor(3);
            io.FontGlobalScale = oldScale;
            if (pushedFont) ImGui.PopFont();
        }
    }

    private static void DrawContent(EditorPresentationSnapshot editor)
    {
        CartographyPresentation snapshot = CartographyRuntime.Presentation;
        SourcePicker();
        if (snapshot.Document == null || snapshot.Scene == null)
        {
            ImGui.TextWrapped(snapshot.Status.Length > 0 ? snapshot.Status : T("正在准备制图工作区...", "Preparing cartography workspace..."));
            if (snapshot.Identity.Length > 0 && ImGui.Button(T("重试读取项目", "Retry project load")))
                CartographyRuntime.Enqueue(new CartographyCommand { DocumentId = snapshot.Identity, Revision = snapshot.Revision, Kind = CartographyCommandKind.Open, Path = snapshot.ProjectPath });
            return;
        }
        if (observed?.Identity != snapshot.Identity)
        {
            LeaveDrafts();
            Selection.Clear(); selectedItem = pendingSelection = string.Empty; selectedRoutePoint = -1; draft = null; layerDraft = null; styleDraft = null;
            exportAreaDrag = ExportAreaDrag.None;
            activeLayer = "notes"; fit = true; fitSelection = false;
            string directory = Path.Combine(Path.GetDirectoryName(snapshot.ProjectPath), "Exports");
            exportPath = Path.Combine(directory, snapshot.Document.Region + "-map.png");
            copyPath = Path.Combine(Path.GetDirectoryName(snapshot.ProjectPath), snapshot.Document.Region + "-copy.xml");
        }
        if (observed?.Revision != snapshot.Revision || observed?.Identity != snapshot.Identity)
        {
            if (draftDirty && CartographyEditing.SameItem(snapshot.Document.Items.Find(item => item.Id == draft?.Id), draft)) draftDirty = false;
            if (!draftDirty) draft = null;
            if (layerDirty && CartographyEditing.SameLayer(snapshot.Document.Layer(layerDraft?.Id), layerDraft)) layerDirty = false;
            if (!layerDirty) layerDraft = null;
            if (!styleDirty) styleDraft = null;
            if (dragging || marquee) { dragging = marquee = false; delta = default; }
        }
        observed = snapshot;
        if (snapshot.Revision != pendingSelectionRevision || snapshot.Document.Items.Any(item => item.Id == pendingSelection)) pendingSelection = string.Empty;
        Selection.RemoveWhere(id => id != pendingSelection && !snapshot.Document.Items.Any(item => item.Id == id));
        if (snapshot.Document.Layer(activeLayer) == null) activeLayer = snapshot.Document.Layers.Last().Id;

        Toolbar(snapshot);
        Num.Vector2 available = ImGui.GetContentRegionAvail();
        float em = ImGui.GetFontSize();
        bool inspector = editor.InspectorOpen && available.X >= em * 30;
        float right = inspector ? Math.Min(em * 24, available.X * .35f) : 0;
        float center = Math.Max(180, available.X - right - (inspector ? 8 : 0));
        // The map composition itself has no baked background. Use a transparent child so the
        // editor preview follows the same compositing model as PNG/SVG export; map content is
        // still drawn explicitly on top.
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Num.Vector4(0f, 0f, 0f, 0f));
        if (ImGui.BeginChild("##CartographyCanvas", new Num.Vector2(center, available.Y), ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)) Canvas(snapshot);
        ImGui.EndChild();
        ImGui.PopStyleColor();
        if (inspector)
        {
            ImGui.SameLine(0, 8);
            if (ImGui.BeginChild("##CartographyInspector", new Num.Vector2(0, available.Y), ImGuiChildFlags.Borders))
            {
                ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X * .52f);
                try { Inspector(snapshot); }
                finally { ImGui.PopItemWidth(); }
            }
            ImGui.EndChild();
        }
    }

    private static void Toolbar(CartographyPresentation snapshot)
    {
        ImGui.TextUnformatted(T("制图", "CARTOGRAPHY") + " | " + snapshot.Document.Region + (snapshot.Dirty ? " *" : string.Empty));
        Inline(T("定位所选", "Focus selection"));
        if (ImGui.Button(T("定位所选##AtlasFocus", "Focus selection##AtlasFocus"))) { fit = true; fitSelection = true; }
        Inline("100%"); ImGui.TextDisabled((zoom * 100).ToString("0") + "%");

        ToolButton(Tool.Select, T("选择", "Select"), false);
        ToolButton(Tool.Text, T("文字", "Text"));
        ImGui.TextWrapped(tool == Tool.Route
            ? T("点击连线加拐点并拖动 | X / Delete 删点 | Shift 限制方向 | Esc 取消", "Click a connection to add/drag a bend | X / Delete removes it | Shift constrains | Esc cancels")
            : tool == Tool.Select ? T("左键拖动 / 框选 | 点击连线编辑 | X / Delete 删除所选拐点 | 右键平移 | 滚轮缩放", "Drag / marquee | Click connections to edit | X / Delete removes the selected bend | Right drag pan | Wheel zoom")
            : T("在画布点击放置文字，右键取消。", "Click to place text. Right click cancels."));
        ImGui.Separator();
    }

    private static void Inline(string label, float extra = 0) =>
        DevToolWidgets.SameLineIfFits(DevToolWidgets.ButtonWidth(label) + extra);

    private static void ToolButton(Tool value, string label, bool inline = true)
    {
        if (inline) Inline(label);
        bool active = tool == value;
        if (active) ImGui.PushStyleColor(ImGuiCol.Button, new Num.Vector4(.2f, .43f, .69f, 1));
        if (ImGui.Button(label + "##AtlasTool" + value)) { CommitDraft(); CancelRoute(); tool = value; }
        if (active) ImGui.PopStyleColor();
    }

    private static void Canvas(CartographyPresentation snapshot)
    {
        if (CartographyCanvasImages.Error.Length > 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Num.Vector4(1, .65f, .3f, 1));
            ImGui.TextWrapped(T("地图图片上传失败，正在重试：", "Map image upload failed; retrying: ") + CartographyCanvasImages.Error);
            ImGui.PopStyleColor();
        }
        Num.Vector2 origin = ImGui.GetCursorScreenPos(), size = ImGui.GetContentRegionAvail();
        if (size.X < 40 || size.Y < 40) return;
        ImGui.InvisibleButton("##AtlasCanvasInput", size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        bool hovered = ImGui.IsItemHovered();
        ImGuiIOPtr io = ImGui.GetIO();
        if (fit)
        {
            CartographySceneNode[] selected = fitSelection ? snapshot.Scene.Nodes.Where(node => Selection.Contains(node.Id)).ToArray() : Array.Empty<CartographySceneNode>();
            CartographyRect bounds = selected.Length > 0 ? selected.Select(node => node.Bounds).Aggregate(CartographyRect.Union) : snapshot.Scene.Bounds;
            zoom = Math.Max(.03f, Math.Min(5, Math.Min((size.X - 50) / Math.Max(1, bounds.Width), (size.Y - 50) / Math.Max(1, bounds.Height))));
            pan = size / 2 - new Num.Vector2(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2) * zoom;
            fit = false;
        }
        if (hovered && !dragging && !marquee && routeGesture == null && Math.Abs(io.MouseWheel) > .001f)
        {
            Num.Vector2 point = (io.MousePos - origin - pan) / zoom;
            zoom = Math.Max(.03f, Math.Min(12, zoom * (float)Math.Pow(1.12, io.MouseWheel)));
            pan = io.MousePos - origin - point * zoom;
        }
        if (hovered && !dragging && !marquee && routeGesture == null && (ImGui.IsMouseDragging(ImGuiMouseButton.Right) || ImGui.IsMouseDragging(ImGuiMouseButton.Middle))) pan += io.MouseDelta;
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right)) { tool = Tool.Select; dragging = marquee = false; delta = default; }
        Num.Vector2 mouse = (io.MousePos - origin - pan) / zoom;
        CartographyRect viewport = new(-pan.X / zoom, -pan.Y / zoom, size.X / zoom, size.Y / zoom);
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(origin, origin + size, true);
        draw.AddRectFilled(origin, origin + size, Color(snapshot.Document.Options.Canvas));
        CartographyDocument moving = snapshot.Document;
        if (dragging)
        {
            moving = snapshot.Document.Clone();
            foreach (CartographyItem item in moving.Items.Where(i => Selection.Contains(i.Id) && !Selection.Contains(i.Appearance.ParentId)))
            {
                if (item.Kind == CartographyItemKind.Connection)
                    foreach (CartographyPoint p in item.Points) { p.X += delta.X; p.Y += delta.Y; }
                else { item.X += delta.X; item.Y += delta.Y; }
            }
        }
        var routePreviews = dragging || routeGesture != null ? new Dictionary<string, CartographySceneNode>(StringComparer.Ordinal) : null;
        foreach (CartographyRenderLayer renderLayer in snapshot.Scene.RenderLayers)
        foreach (CartographySceneNode original in renderLayer.Nodes)
        {
            CartographySceneNode node = original;
            Num.Vector2 offset = dragging && MovesWithSelection(snapshot.Document, node.Id) ? delta : default;
            if ((dragging || routeGesture?.Id == node.Id) && node.FromId != null)
            {
                if (routePreviews.TryGetValue(node.Id, out var preview)) node = preview;
                else
                {
                    CartographyItem route = moving.Items.Find(i => i.Id == node.Id);
                    if (route != null) node = PreviewRoute(node, route, moving, snapshot.Document);
                    routePreviews[node.Id] = node;
                }
                offset = default;
            }
            if (routeGesture?.Id == node.Id && routeGesture.Kind == CartographyItemKind.Line)
            {
                node = CartographySceneBuilder.AnnotationLine(CartographySceneBuilder.Resolve(snapshot.Document, routeGesture), snapshot.Document.Layer(routeGesture.LayerId));
                offset = default;
            }
            if (!viewport.Intersects(node.Bounds.Offset(offset.X, offset.Y))) continue;
            foreach (CartographyPrimitive shape in node.Primitives)
                if (renderLayer.Accepts(shape)) DrawPrimitive(draw, shape, origin, offset, viewport);
            if (renderLayer.Pass == CartographyRenderPass.Content && Selection.Contains(node.Id) && node.FromId == null)
                draw.AddRect(Screen(origin, node.Bounds.X, node.Bounds.Y, offset), Screen(origin, node.Bounds.Right, node.Bounds.Bottom, offset), 0xFFDCC36C, 0, ImDrawFlags.None, 1.5f);
        }
        if (marquee)
        {
            Num.Vector2 a = origin + pan + marqueeStart * zoom, b = io.MousePos;
            draw.AddRectFilled(Num.Vector2.Min(a, b), Num.Vector2.Max(a, b), 0x3358C6EF);
            draw.AddRect(Num.Vector2.Min(a, b), Num.Vector2.Max(a, b), 0xCC58C6EF);
        }
        DrawRouteHandles(draw, snapshot, origin);
        bool exportAreaConsumed = DrawExportAreaEditor(draw, snapshot, origin, hovered, mouse, io);
        draw.PopClipRect();
        if (!exportAreaConsumed && !RouteGesture(snapshot, hovered, mouse, io))
            Interaction(snapshot, hovered, mouse, io);
    }

    private static bool DrawExportAreaEditor(
        ImDrawListPtr draw,
        CartographyPresentation snapshot,
        Num.Vector2 origin,
        bool hovered,
        Num.Vector2 mouse,
        ImGuiIOPtr io)
    {
        CartographyOptions persisted = snapshot?.Document?.Options;
        if (persisted?.ExportArea != true)
        {
            exportAreaDrag = ExportAreaDrag.None;
            return false;
        }

        Num.Vector4 area = exportAreaDrag != ExportAreaDrag.None
            ? exportAreaPreviewRect
            : new Num.Vector4(
                persisted.AreaX,
                persisted.AreaY,
                Math.Max(1f, persisted.AreaWidth),
                Math.Max(1f, persisted.AreaHeight));

        Num.Vector2 a = Screen(origin, area.X, area.Y, default);
        Num.Vector2 b = Screen(origin, area.X + area.Z, area.Y + area.W, default);
        Num.Vector2 min = Num.Vector2.Min(a, b);
        Num.Vector2 max = Num.Vector2.Max(a, b);

        const float handleRadius = 6f;
        ExportAreaDrag hoverHandle = hovered
            ? HitExportAreaHandle(io.MousePos, min, max, handleRadius)
            : ExportAreaDrag.None;

        uint outline = 0xFF77CFFF;
        uint handle = 0xFFB7E8FF;
        draw.AddRect(min, max, outline, 0f, ImDrawFlags.None, 2f);

        Num.Vector2 center = (min + max) * 0.5f;
        DrawExportAreaHandle(draw, min, handleRadius, handle);
        DrawExportAreaHandle(draw, new Num.Vector2(max.X, min.Y), handleRadius, handle);
        DrawExportAreaHandle(draw, new Num.Vector2(min.X, max.Y), handleRadius, handle);
        DrawExportAreaHandle(draw, max, handleRadius, handle);
        DrawExportAreaHandle(draw, new Num.Vector2(center.X, min.Y), handleRadius, handle);
        DrawExportAreaHandle(draw, new Num.Vector2(center.X, max.Y), handleRadius, handle);
        DrawExportAreaHandle(draw, new Num.Vector2(min.X, center.Y), handleRadius, handle);
        DrawExportAreaHandle(draw, new Num.Vector2(max.X, center.Y), handleRadius, handle);

        if (exportAreaDrag == ExportAreaDrag.None && hoverHandle != ExportAreaDrag.None)
        {
            ImGui.SetMouseCursor(
                hoverHandle == ExportAreaDrag.Move
                    ? ImGuiMouseCursor.ResizeAll
                    : hoverHandle == ExportAreaDrag.Left || hoverHandle == ExportAreaDrag.Right
                        ? ImGuiMouseCursor.ResizeEW
                        : hoverHandle == ExportAreaDrag.Top || hoverHandle == ExportAreaDrag.Bottom
                            ? ImGuiMouseCursor.ResizeNS
                            : hoverHandle == ExportAreaDrag.TopLeft || hoverHandle == ExportAreaDrag.BottomRight
                                ? ImGuiMouseCursor.ResizeNWSE
                                : ImGuiMouseCursor.ResizeNESW);
        }

        if (hovered &&
            exportAreaDrag == ExportAreaDrag.None &&
            hoverHandle != ExportAreaDrag.None &&
            ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            SaveStyle();
            exportAreaDrag = hoverHandle;
            exportAreaDragStart = mouse;
            exportAreaStartRect = new Num.Vector4(
                persisted.AreaX,
                persisted.AreaY,
                Math.Max(1f, persisted.AreaWidth),
                Math.Max(1f, persisted.AreaHeight));
            exportAreaPreviewRect = exportAreaStartRect;
        }

        if (exportAreaDrag != ExportAreaDrag.None)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                Num.Vector2 d = mouse - exportAreaDragStart;
                exportAreaPreviewRect = ResizeExportArea(exportAreaStartRect, d, exportAreaDrag);
            }

            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            {
                styleDraft = snapshot.Document.Clone();
                CartographyOptions options = styleDraft.Options;
                options.ExportArea = true;
                options.AreaX = exportAreaPreviewRect.X;
                options.AreaY = exportAreaPreviewRect.Y;
                options.AreaWidth = exportAreaPreviewRect.Z;
                options.AreaHeight = exportAreaPreviewRect.W;
                styleDirty = true;
                SaveStyle();
                exportAreaDrag = ExportAreaDrag.None;
            }

            return true;
        }

        return hoverHandle != ExportAreaDrag.None && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
    }

    private static ExportAreaDrag HitExportAreaHandle(
        Num.Vector2 mouse,
        Num.Vector2 min,
        Num.Vector2 max,
        float radius)
    {
        Num.Vector2 center = (min + max) * 0.5f;
        bool Near(Num.Vector2 p) => Num.Vector2.DistanceSquared(mouse, p) <= radius * radius * 2.25f;

        if (Near(min)) return ExportAreaDrag.TopLeft;
        if (Near(new Num.Vector2(max.X, min.Y))) return ExportAreaDrag.TopRight;
        if (Near(new Num.Vector2(min.X, max.Y))) return ExportAreaDrag.BottomLeft;
        if (Near(max)) return ExportAreaDrag.BottomRight;
        if (Near(new Num.Vector2(center.X, min.Y))) return ExportAreaDrag.Top;
        if (Near(new Num.Vector2(center.X, max.Y))) return ExportAreaDrag.Bottom;
        if (Near(new Num.Vector2(min.X, center.Y))) return ExportAreaDrag.Left;
        if (Near(new Num.Vector2(max.X, center.Y))) return ExportAreaDrag.Right;

        // The empty interior is the move handle. Keep a small tolerance so a click directly on the
        // border still manipulates the frame instead of selecting a room underneath it.
        if (mouse.X >= min.X - 2f &&
            mouse.X <= max.X + 2f &&
            mouse.Y >= min.Y - 2f &&
            mouse.Y <= max.Y + 2f)
            return ExportAreaDrag.Move;

        return ExportAreaDrag.None;
    }

    private static void DrawExportAreaHandle(
        ImDrawListPtr draw,
        Num.Vector2 center,
        float radius,
        uint color)
    {
        float half = Math.Max(2f, radius * 0.45f);
        draw.AddRectFilled(
            center - new Num.Vector2(half, half),
            center + new Num.Vector2(half, half),
            color);
    }

    private static Num.Vector4 ResizeExportArea(
        Num.Vector4 start,
        Num.Vector2 delta,
        ExportAreaDrag mode)
    {
        float left = start.X;
        float top = start.Y;
        float right = start.X + Math.Max(1f, start.Z);
        float bottom = start.Y + Math.Max(1f, start.W);

        if (mode == ExportAreaDrag.Move)
        {
            left += delta.X;
            right += delta.X;
            top += delta.Y;
            bottom += delta.Y;
        }
        else
        {
            if (mode == ExportAreaDrag.Left ||
                mode == ExportAreaDrag.TopLeft ||
                mode == ExportAreaDrag.BottomLeft)
                left += delta.X;
            if (mode == ExportAreaDrag.Right ||
                mode == ExportAreaDrag.TopRight ||
                mode == ExportAreaDrag.BottomRight)
                right += delta.X;
            if (mode == ExportAreaDrag.Top ||
                mode == ExportAreaDrag.TopLeft ||
                mode == ExportAreaDrag.TopRight)
                top += delta.Y;
            if (mode == ExportAreaDrag.Bottom ||
                mode == ExportAreaDrag.BottomLeft ||
                mode == ExportAreaDrag.BottomRight)
                bottom += delta.Y;
        }

        const float minimum = 8f;
        if (right - left < minimum)
        {
            if (mode == ExportAreaDrag.Left ||
                mode == ExportAreaDrag.TopLeft ||
                mode == ExportAreaDrag.BottomLeft)
                left = right - minimum;
            else
                right = left + minimum;
        }
        if (bottom - top < minimum)
        {
            if (mode == ExportAreaDrag.Top ||
                mode == ExportAreaDrag.TopLeft ||
                mode == ExportAreaDrag.TopRight)
                top = bottom - minimum;
            else
                bottom = top + minimum;
        }

        return new Num.Vector4(left, top, right - left, bottom - top);
    }

    private static void Interaction(CartographyPresentation snapshot, bool hovered, Num.Vector2 mouse, ImGuiIOPtr io)
    {
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            CommitDraft();
            startMouse = io.MousePos; marqueeStart = mouse; gestureRevision = snapshot.Revision;
            addSelection = io.KeyShift; subtractSelection = io.KeyCtrl;
            if (tool == Tool.Route) return;
            if (tool != Tool.Select)
            {
                if (tool == Tool.Text || tool == Tool.Marker) AddAnnotation(mouse, mouse);
                else marquee = true;
                return;
            }
            CartographySceneNode hit = snapshot.Scene.Nodes.LastOrDefault(node => node.Id.Length > 0 && !node.Locked && HitNode(node, mouse));
            if (hit == null) { marquee = true; if (!addSelection && !subtractSelection) Selection.Clear(); }
            else
            {
                if (!Selection.Contains(hit.Id) || addSelection || subtractSelection) Select(hit.Id, addSelection, subtractSelection);
                selectedItem = hit.Id;
                // Hidden/locked members do not take part in a group drag or its preview.
                Selection.RemoveWhere(id => !snapshot.Document.Items.Any(item => item.Id == id && snapshot.Document.Editable(item)));
                dragging = !subtractSelection && Selection.Count > 0 && hit.FromId == null; delta = default;
            }
        }
        if (dragging && ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            delta = (io.MousePos - startMouse) / zoom;
        }
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            if (dragging && delta.LengthSquared() > .001f)
                Send(CartographyCommandKind.Move, command => { command.Ids = Selection.ToArray(); command.X = delta.X; command.Y = delta.Y; command.Revision = gestureRevision; });
            if (marquee)
            {
                if (tool != Tool.Select) AddAnnotation(marqueeStart, mouse);
                else
                {
                    CartographyRect rect = new(Math.Min(mouse.X, marqueeStart.X), Math.Min(mouse.Y, marqueeStart.Y), Math.Abs(mouse.X - marqueeStart.X), Math.Abs(mouse.Y - marqueeStart.Y));
                    foreach (CartographySceneNode node in snapshot.Scene.Nodes)
                        if (node.Id.Length > 0 && !node.Locked && rect.Intersects(node.Bounds))
                        { if (subtractSelection) Selection.Remove(node.Id); else Selection.Add(node.Id); }
                    selectedItem = Selection.FirstOrDefault() ?? string.Empty; draft = null;
                }
            }
            dragging = marquee = false; delta = default;
        }
        // Save and Undo remain owned by EditorInputRouter; these local keys only affect this canvas.
        if (!hovered || io.WantTextInput || ImGui.IsAnyItemActive() || dragging || marquee) return;
        ClipboardKeys(snapshot, mouse, io);

        if (KeyChord(snapshot.Document.Options.DeleteKey, false))
        {
            bool available = Selection.Count > 0;
            if (available)
                Send(CartographyCommandKind.Delete, command => command.Ids = Selection.ToArray());
            EditorShortcutFeedback.PublishCustom(
                available ? "已删除制图元素" : "没有可删除的制图元素",
                available ? "Cartography items deleted" : "Nothing to delete",
                snapshot.Document.Options.DeleteKey,
                available,
                available ? EditorShortcutFeedbackVisual.Delete : EditorShortcutFeedbackVisual.Warning);
        }

        if (KeyChord(snapshot.Document.Options.DuplicateKey))
        {
            bool available = Selection.Count > 0;
            if (available)
                Send(CartographyCommandKind.Duplicate, command => command.Ids = Selection.ToArray());
            EditorShortcutFeedback.PublishCustom(
                available ? "已复制制图元素" : "没有可复制的制图元素",
                available ? "Cartography items duplicated" : "Nothing to duplicate",
                snapshot.Document.Options.DuplicateKey,
                available,
                available ? EditorShortcutFeedbackVisual.Duplicate : EditorShortcutFeedbackVisual.Warning);
        }

        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.A))
        {
            Selection.Clear();
            foreach (CartographyItem item in snapshot.Document.Items.Where(snapshot.Document.Editable))
                Selection.Add(item.Id);
            bool selected = Selection.Count > 0;
            EditorShortcutFeedback.PublishCustom(
                selected ? "已全选可编辑制图元素" : "当前没有可编辑制图元素",
                selected ? "All editable cartography items selected" : "No editable cartography items",
                "Ctrl+A",
                selected,
                selected ? EditorShortcutFeedbackVisual.Select : EditorShortcutFeedbackVisual.Warning);
        }

        float step = io.KeyShift ? 10 : 1;
        Num.Vector2 nudge = default;
        string nudgeKey = string.Empty;
        if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow)) { nudge.X -= step; nudgeKey = DevToolGlyphs.ArrowLeft; }
        if (ImGui.IsKeyPressed(ImGuiKey.RightArrow)) { nudge.X += step; nudgeKey = DevToolGlyphs.ArrowRight; }
        if (ImGui.IsKeyPressed(ImGuiKey.UpArrow)) { nudge.Y -= step; nudgeKey = DevToolGlyphs.ArrowUp; }
        if (ImGui.IsKeyPressed(ImGuiKey.DownArrow)) { nudge.Y += step; nudgeKey = DevToolGlyphs.ArrowDown; }
        if (nudge.LengthSquared() > 0)
        {
            bool available = Selection.Count > 0;
            if (available)
                Send(CartographyCommandKind.Move, command => { command.Ids = Selection.ToArray(); command.X = nudge.X; command.Y = nudge.Y; });
            EditorShortcutFeedback.PublishCustom(
                available ? "微调制图元素" : "没有选中的制图元素",
                available ? "Nudge cartography items" : "No cartography selection",
                (io.KeyShift ? "Shift+" : string.Empty) + nudgeKey,
                available,
                available ? EditorShortcutFeedbackVisual.Move : EditorShortcutFeedbackVisual.Warning);
        }
    }

    private static void AddAnnotation(Num.Vector2 a, Num.Vector2 b)
    {
        CartographyItem item = new() { Kind = tool == Tool.Text ? CartographyItemKind.Text : tool == Tool.Marker ? CartographyItemKind.Marker : tool == Tool.Line ? CartographyItemKind.Line : CartographyItemKind.Box,
            LayerId = activeLayer, X = a.X, Y = a.Y, Width = b.X - a.X, Height = b.Y - a.Y, Marker = marker, Text = tool == Tool.Text ? T("标注", "Label") : string.Empty };
        Send(CartographyCommandKind.Add, command => command.Item = item);
        pendingSelection = item.Id; pendingSelectionRevision = observed.Revision;
        Selection.Clear(); Selection.Add(item.Id); selectedItem = item.Id; tool = Tool.Select; draft = null;
    }

    private static void Inspector(CartographyPresentation snapshot)
    {
        if (snapshot.Status.Length > 0) { ImGui.TextWrapped(snapshot.Status); ImGui.Separator(); }
        CartographyItem item = snapshot.Document.Items.Find(candidate => candidate.Id == selectedItem);
        if (Selection.Count > 1) SelectionInspector(snapshot);
        else if (item != null) ItemInspector(item, snapshot);
        ExtendedWorkspaceInspector(snapshot);

        StyleInspector(snapshot);
        ExportInspector(snapshot);
    }

    private static void ItemInspector(CartographyItem item, CartographyPresentation snapshot)
    {
        if (draft == null || draft.Id != item.Id) { draft = item.Clone(); draftRevision = snapshot.Revision; draftDirty = false; }
        if (!ImGui.CollapsingHeader(T("所选对象##AtlasItem", "SELECTED OBJECT##AtlasItem"), ImGuiTreeNodeFlags.DefaultOpen)) return;
        ImGui.TextWrapped(ItemName(item));
        bool locked = snapshot.Document.Layer(item.LayerId)?.Locked != false;
        if (locked) ImGui.BeginDisabled();
        Num.Vector2 position = new(draft.X, draft.Y);
        if (ImGui.DragFloat2(T("位置##AtlasPos", "Position##AtlasPos"), ref position, 1)) { draft.X = position.X; draft.Y = position.Y; draftDirty = true; }
        if (ImGui.IsItemDeactivatedAfterEdit()) CommitDraft();
        bool visible = draft.Visible;
        if (ImGui.Checkbox(T("显示对象##AtlasVisible", "Visible##AtlasVisible"), ref visible)) { draft.Visible = visible; draftDirty = true; CommitDraft(); }
        if (draft.Kind == CartographyItemKind.Text)
        {
            if (ImGui.InputTextMultiline("##AtlasTextValue", ref draft.Text, 4096, new Num.Vector2(-1, 88))) draftDirty = true;
            if (ImGui.IsItemDeactivatedAfterEdit()) CommitDraft();
        }
        if (draft.Kind == CartographyItemKind.Text || draft.Kind == CartographyItemKind.Marker)
        {
            if (ImGui.DragFloat(T("大小##AtlasSize", "Size##AtlasSize"), ref draft.Size, 1, 4, 256)) draftDirty = true;
            if (ImGui.IsItemDeactivatedAfterEdit()) CommitDraft();
        }
        if (draft.Kind == CartographyItemKind.Line || draft.Kind == CartographyItemKind.Box)
        {
            Num.Vector2 extent = new(draft.Width, draft.Height);
            if (ImGui.DragFloat2(T("范围##AtlasExtent", "Extent##AtlasExtent"), ref extent, 1)) { draft.Width = extent.X; draft.Height = extent.Y; draftDirty = true; }
            if (ImGui.IsItemDeactivatedAfterEdit()) CommitDraft();
        }
        if (draft.Kind != CartographyItemKind.Room)
        {
            if (EditColor(T("颜色##AtlasItemColor", "Color##AtlasItemColor"), ref draft.Color)) draftDirty = true;
            if (ImGui.IsItemDeactivatedAfterEdit()) CommitDraft();
            if (ImGui.DragFloat(T("线宽##AtlasStroke", "Stroke##AtlasStroke"), ref draft.Stroke, .25f, .25f, 32)) draftDirty = true;
            if (ImGui.IsItemDeactivatedAfterEdit()) CommitDraft();
        }
        ExtendedItemInspector(snapshot);
        if (ImGui.Button(T("删除对象##AtlasDelete", "Delete object##AtlasDelete"))) Send(CartographyCommandKind.Delete, command => command.Ids = new[] { item.Id });
        if (draft.Kind != CartographyItemKind.Room)
        { ImGui.SameLine(); if (ImGui.Button(T("复制##AtlasCopy", "Duplicate##AtlasCopy"))) Send(CartographyCommandKind.Duplicate, command => command.Ids = new[] { item.Id }); }
        if (locked) ImGui.EndDisabled();
        if (draftDirty) Stage(CartographyCommandKind.UpdateItem, "item:" + draft.Id, command => { command.Item = draft; command.Revision = draftRevision; });
    }

    private static void SelectionInspector(CartographyPresentation snapshot)
    {
        ImGui.TextUnformatted(Selection.Count + T(" 项选择", " SELECTED"));
        string[] labels = { T("左", "Left"), T("水平居中", "Center X"), T("右", "Right"), T("上", "Top"), T("垂直居中", "Center Y"), T("下", "Bottom"), T("水平等距", "Space X"), T("垂直等距", "Space Y") };
        for (int i = 0; i < labels.Length; i++)
        {
            if (i % 3 != 0) ImGui.SameLine();
            int mode = i;
            if (ImGui.SmallButton(labels[i] + "##AtlasAlign" + i)) Send(CartographyCommandKind.Align, command => { command.Integer = mode; command.Ids = Selection.ToArray(); });
        }
        if (ImGui.Button(T("删除所选##AtlasDeleteMany", "Delete selected##AtlasDeleteMany"))) Send(CartographyCommandKind.Delete, command => command.Ids = Selection.ToArray());
    }

    private static void StyleInspector(CartographyPresentation snapshot)
    {
        if (!ImGui.CollapsingHeader(T("地图样式##AtlasStyle", "MAP STYLE##AtlasStyle"))) return;
        styleDraft ??= snapshot.Document.Clone();
        if (ImGui.InputText(T("标题##AtlasTitle", "Title##AtlasTitle"), ref styleDraft.Title, 512)) styleDirty = true;
        if (ImGui.IsItemDeactivatedAfterEdit()) SaveStyle();
        if (ImGui.InputText(T("导出字体##AtlasFont", "Export font##AtlasFont"), ref styleDraft.FontFamily, 128)) styleDirty = true;
        if (ImGui.IsItemDeactivatedAfterEdit()) SaveStyle();
        if (ImGui.Checkbox(T("房间名称##AtlasNames", "Room names##AtlasNames"), ref styleDraft.ShowRoomNames)) { styleDirty = true; SaveStyle(); }
        if (ImGui.Checkbox(T("连接线##AtlasLinks", "Connections##AtlasLinks"), ref styleDraft.ShowConnections)) { styleDirty = true; SaveStyle(); }
        if (ImGui.Checkbox(T("房间轮廓裁剪##AtlasCrop", "Crop room outlines##AtlasCrop"), ref styleDraft.CropSolid)) { styleDirty = true; SaveStyle(); }
        if (EditColor(T("默认房间底色##AtlasTerrain", "Default room background##AtlasTerrain"), ref styleDraft.Terrain)) styleDirty = true; if (ImGui.IsItemDeactivatedAfterEdit()) SaveStyle();
        if (EditColor(T("水体##AtlasWater", "Water##AtlasWater"), ref styleDraft.Water)) styleDirty = true; if (ImGui.IsItemDeactivatedAfterEdit()) SaveStyle();
        if (EditColor(T("连线##AtlasLinkColor", "Links##AtlasLinkColor"), ref styleDraft.Connections)) styleDirty = true; if (ImGui.IsItemDeactivatedAfterEdit()) SaveStyle();
        if (ImGui.Button(T("补充新房间##AtlasSync", "Add new rooms##AtlasSync"))) Send(CartographyCommandKind.AddMissingRooms);
        ImGui.TextWrapped(T("只补充新发现的房间，已有排版保持不变。", "Adds newly discovered rooms and preserves existing composition."));
        if (styleDirty) Stage(CartographyCommandKind.Style, "style", command => command.Style = styleDraft);
    }

    private static void ExportInspector(CartographyPresentation snapshot)
    {
        if (!ImGui.CollapsingHeader(T("保存与导出##AtlasExport", "SAVE & EXPORT##AtlasExport"), ImGuiTreeNodeFlags.DefaultOpen)) return;
        styleDraft ??= snapshot.Document.Clone();
        if (ImGui.Button(snapshot.Dirty ? T("保存制图 *", "Save project *") : T("保存制图", "Save project"))) { CommitDraft(); Send(CartographyCommandKind.Save); }
        ImGui.TextWrapped(snapshot.ProjectPath);
        ImGui.SetNextItemWidth(-1); ImGui.InputText("##AtlasCopyPath", ref copyPath, 1024);
        if (ImGui.SmallButton(T("另存项目副本##AtlasSaveCopy", "Save project copy##AtlasSaveCopy"))) Send(CartographyCommandKind.Save, command => command.Path = copyPath);
        if (ImGui.SmallButton(T("打开此项目（先保存当前）##AtlasOpen", "Open this project (save current first)##AtlasOpen"))) Send(CartographyCommandKind.Open, command => command.Path = copyPath);
        ImGui.Separator();
        int choice = (int)format;
        if (ImGui.Combo(T("格式##AtlasFormat", "Format##AtlasFormat"), ref choice, "PNG\0SVG\0Layer PNGs (.zip)\0PSD (layers)\0Image map (.json)\0"))
        { format = (CartographyExportFormat)choice; exportPath = Path.ChangeExtension(exportPath, format == CartographyExportFormat.Png ? ".png" : format == CartographyExportFormat.Svg ? ".svg" : format == CartographyExportFormat.Psd ? ".psd" : format == CartographyExportFormat.ImageMap ? ".json" : ".zip"); }
        if (ImGui.SliderFloat(T("倍率##AtlasScale", "Scale##AtlasScale"), ref styleDraft.ExportScale, .25f, 8, "%.2fx")) styleDirty = true;
        if (ImGui.IsItemDeactivatedAfterEdit()) SaveStyle();
        if (ImGui.DragInt(T("边距##AtlasPadding", "Padding##AtlasPadding"), ref styleDraft.Padding, 1, 0, 1024)) styleDirty = true;
        if (ImGui.IsItemDeactivatedAfterEdit()) SaveStyle();
        bool valid = true;
        try { CartographyExporter.Dimensions(styleDraft, snapshot.Scene, out int width, out int height, format); ImGui.TextDisabled(width + " x " + height + " px"); }
        catch (InvalidOperationException error) { ImGui.TextWrapped(error.Message); valid = false; }
        string[] blockingWarnings =
            snapshot.Scene.Warnings
                .Where(CartographyRuntime.IsExportBlockingWarning)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

        if (snapshot.Scene.Errors.Length > 0)
        {
            valid = false;
            ImGui.PushStyleColor(
                ImGuiCol.Text,
                new Num.Vector4(1f, .48f, .36f, 1f));
            ImGui.TextWrapped(
                T(
                    "导出已阻止：存在尚未就绪的房间资源。",
                    "Export blocked: some room resources are not ready."));
            ImGui.PopStyleColor();
        }

        if (blockingWarnings.Length > 0)
        {
            ImGui.PushStyleColor(
                ImGuiCol.Text,
                new Num.Vector4(1f, .48f, .36f, 1f));
            ImGui.TextWrapped(
                T(
                    "检测到缺失图标。点击导出会被后端拒绝并自动打开诊断窗口，不会生成假成功结果。",
                    "Missing sprites detected. Export will be rejected and the diagnostics window will open; no false success will be reported."));
            ImGui.PopStyleColor();
        }
        else if (snapshot.Scene.Warnings.Length > 0)
        {
            ImGui.PushStyleColor(
                ImGuiCol.Text,
                new Num.Vector4(1f, .72f, .32f, 1f));
            ImGui.TextWrapped(
                T(
                    "当前制图存在警告；导出可以继续，但请先查看诊断。",
                    "Cartography has warnings; export can continue, but review diagnostics first."));
            ImGui.PopStyleColor();
        }

        int diagnosticCount =
            CurrentDiagnosticCount(snapshot);

        if (diagnosticCount > 0)
        {
            if (ImGui.Button(
                    T(
                        "打开导出诊断",
                        "Open export diagnostics") +
                    " (" +
                    diagnosticCount +
                    ")##AtlasDiagnostics"))
            {
                diagnosticsWindowOpen = true;
            }
        }

        ImGui.SetNextItemWidth(-1); ImGui.InputText("##AtlasExportPath", ref exportPath, 1024);
        if (!valid || snapshot.Exporting) ImGui.BeginDisabled();
        if (ImGui.Button(snapshot.Exporting ? T("正在导出...", "Exporting...") : T("导出图片##AtlasRender", "Export image##AtlasRender")))
            Send(CartographyCommandKind.Export, command => { command.Path = exportPath; command.Integer = (int)format; });
        if (!valid || snapshot.Exporting) ImGui.EndDisabled();
        if (styleDirty) Stage(CartographyCommandKind.Style, "style", command => command.Style = styleDraft);
    }

    private static int CurrentDiagnosticCount(
        CartographyPresentation snapshot)
    {
        var keys =
            new HashSet<string>(
                StringComparer.Ordinal);

        foreach (string error in
                 snapshot?.Scene?.Errors ??
                 Array.Empty<string>())
        {
            keys.Add(
                "E\n" +
                error);
        }

        foreach (string warning in
                 snapshot?.Scene?.Warnings ??
                 Array.Empty<string>())
        {
            keys.Add(
                "W\n" +
                warning);
        }

        foreach (CartographyDiagnosticEntry entry in
                 CartographyDiagnostics.Snapshot())
        {
            keys.Add(
                (entry.Severity ==
                 CartographyDiagnosticSeverity.Error
                    ? "E\n"
                    : "W\n") +
                entry.Message);
        }

        return keys.Count;
    }

    private static void DrawDiagnosticsWindow(
        CartographyPresentation snapshot)
    {
        if (CartographyDiagnostics.ConsumeOpenRequest())
            diagnosticsWindowOpen = true;

        if (!diagnosticsWindowOpen)
            return;

        bool open = diagnosticsWindowOpen;

        ImGui.SetNextWindowSize(
            new Num.Vector2(
                720f,
                460f),
            ImGuiCond.FirstUseEver);

        if (ImGui.Begin(
                T(
                    "制图导出诊断##CartographyDiagnostics",
                    "Cartography Export Diagnostics##CartographyDiagnostics"),
                ref open))
        {
            string[] liveErrors =
                snapshot?.Scene?.Errors ??
                Array.Empty<string>();
            string[] liveWarnings =
                snapshot?.Scene?.Warnings ??
                Array.Empty<string>();
            CartographyDiagnosticEntry[] history =
                CartographyDiagnostics.Snapshot();

            var liveKeys =
                new HashSet<string>(
                    StringComparer.Ordinal);

            foreach (string error in liveErrors)
                liveKeys.Add(
                    "E\n" +
                    error);

            foreach (string warning in liveWarnings)
                liveKeys.Add(
                    "W\n" +
                    warning);

            CartographyDiagnosticEntry[] uniqueHistory =
                history
                    .Where(
                        entry =>
                            !liveKeys.Contains(
                                (entry.Severity ==
                                 CartographyDiagnosticSeverity.Error
                                    ? "E\n"
                                    : "W\n") +
                                entry.Message))
                    .ToArray();

            int errors =
                liveErrors
                    .Distinct(StringComparer.Ordinal)
                    .Count() +
                uniqueHistory.Count(
                    entry =>
                        entry.Severity ==
                        CartographyDiagnosticSeverity.Error);
            int warnings =
                liveWarnings
                    .Distinct(StringComparer.Ordinal)
                    .Count() +
                uniqueHistory.Count(
                    entry =>
                        entry.Severity ==
                        CartographyDiagnosticSeverity.Warning);

            ImGui.TextUnformatted(
                T("错误", "Errors") +
                ": " +
                errors +
                "    " +
                T("警告", "Warnings") +
                ": " +
                warnings);

            if (ImGui.Button(
                    T(
                        "复制全部",
                        "Copy all") +
                    "##CartographyDiagnosticsCopy"))
            {
                ImGui.SetClipboardText(
                    BuildDiagnosticsCopyText(
                        snapshot,
                        uniqueHistory));
            }

            ImGui.SameLine();

            if (ImGui.Button(
                    T(
                        "清除历史",
                        "Clear history") +
                    "##CartographyDiagnosticsClear"))
            {
                CartographyDiagnostics.Clear();
                history =
                    Array.Empty<CartographyDiagnosticEntry>();
            }

            ImGui.SameLine();
            ImGui.TextDisabled(
                T(
                    "当前场景问题无法手动清除；修复资源后会自动消失。",
                    "Live scene issues disappear automatically after the resource is fixed."));

            ImGui.Separator();

            if (ImGui.BeginChild(
                    "##CartographyDiagnosticsScroll",
                    new Num.Vector2(
                        0f,
                        0f),
                    ImGuiChildFlags.Borders))
            {
                foreach (string error in liveErrors
                    .Distinct(StringComparer.Ordinal))
                {
                    DrawDiagnosticLine(
                        CartographyDiagnosticSeverity.Error,
                        T(
                            "当前场景",
                            "Live scene"),
                        error);
                }

                foreach (string warning in liveWarnings
                    .Distinct(StringComparer.Ordinal))
                {
                    DrawDiagnosticLine(
                        CartographyDiagnosticSeverity.Warning,
                        T(
                            "当前场景",
                            "Live scene"),
                        warning);
                }

                foreach (CartographyDiagnosticEntry entry in uniqueHistory)
                {
                    string header =
                        entry.Category;

                    if (!string.IsNullOrEmpty(
                            entry.Context))
                    {
                        header +=
                            " | " +
                            entry.Context;
                    }

                    DrawDiagnosticLine(
                        entry.Severity,
                        header,
                        entry.Message);
                }
            }

            ImGui.EndChild();
        }

        ImGui.End();
        diagnosticsWindowOpen = open;
    }

    private static void DrawDiagnosticLine(
        CartographyDiagnosticSeverity severity,
        string header,
        string message)
    {
        Num.Vector4 color =
            severity ==
            CartographyDiagnosticSeverity.Error
                ? new Num.Vector4(
                    1f,
                    .46f,
                    .38f,
                    1f)
                : new Num.Vector4(
                    1f,
                    .72f,
                    .32f,
                    1f);

        ImGui.PushStyleColor(
            ImGuiCol.Text,
            color);
        ImGui.TextUnformatted(
            severity ==
            CartographyDiagnosticSeverity.Error
                ? "ERROR"
                : "WARNING");
        ImGui.PopStyleColor();

        if (!string.IsNullOrEmpty(header))
        {
            ImGui.SameLine();
            ImGui.TextDisabled(header);
        }

        ImGui.TextWrapped(
            message ??
            string.Empty);
        ImGui.Separator();
    }

    private static string BuildDiagnosticsCopyText(
        CartographyPresentation snapshot,
        CartographyDiagnosticEntry[] history)
    {
        var text =
            new System.Text.StringBuilder();

        text.AppendLine(
            "Cartography Export Diagnostics");
        text.AppendLine(
            "Project: " +
            (snapshot?.Identity ??
             "<none>"));
        text.AppendLine();

        foreach (string error in
                 snapshot?.Scene?.Errors ??
                 Array.Empty<string>())
        {
            text.AppendLine(
                "ERROR | Live scene | " +
                error);
        }

        foreach (string warning in
                 snapshot?.Scene?.Warnings ??
                 Array.Empty<string>())
        {
            text.AppendLine(
                "WARNING | Live scene | " +
                warning);
        }

        if (history != null &&
            history.Length > 0)
        {
            if (text.Length > 0)
                text.AppendLine();

            text.AppendLine(
                "History:");

            foreach (CartographyDiagnosticEntry entry in history)
                text.AppendLine(
                    entry.CopyText);
        }

        return text.ToString();
    }

    private static void Select(string id, bool add, bool subtract)
    {
        CommitDraft();
        if (selectedItem != id) selectedRoutePoint = -1;
        if (subtract) Selection.Remove(id);
        else { if (!add) Selection.Clear(); Selection.Add(id); }
        selectedItem = Selection.Contains(id) ? id : Selection.FirstOrDefault() ?? string.Empty;
        draft = null;
    }

    private static void CommitDraft()
    {
        if (!draftDirty || draft == null || observed == null) return;
        Stage(CartographyCommandKind.UpdateItem, "item:" + draft.Id, command => { command.Item = draft; command.Revision = draftRevision; });
        CartographyRuntime.CommitDraft(observed.Identity + "/item:" + draft.Id);
        draftDirty = false;
    }

    private static void CommitLayer()
    {
        if (!layerDirty || layerDraft == null || observed == null) return;
        Stage(CartographyCommandKind.UpdateLayer, "layer:" + layerDraft.Id, command => command.Layer = layerDraft);
        CartographyRuntime.CommitDraft(observed.Identity + "/layer:" + layerDraft.Id);
        layerDirty = false;
    }

    private static void SaveStyle()
    {
        if (!styleDirty || styleDraft == null || observed == null) return;
        Stage(CartographyCommandKind.Style, "style", command => command.Style = styleDraft);
        CartographyRuntime.CommitDraft(observed.Identity + "/style");
        styleDirty = false;
    }

    private static void Stage(CartographyCommandKind kind, string key, Action<CartographyCommand> configure)
    {
        CartographyCommand command = new() { DocumentId = observed.Identity, Revision = observed.Revision, Kind = kind };
        configure(command); CartographyRuntime.StageDraft(observed.Identity + "/" + key, command);
    }
    private static void Send(CartographyCommandKind kind, Action<CartographyCommand> configure = null)
    {
        if (observed == null) return;
        CartographyCommand command = new() { DocumentId = observed.Identity, Revision = observed.Revision, Kind = kind };
        configure?.Invoke(command); CartographyRuntime.Enqueue(command);
    }

    private static void DrawPrimitive(ImDrawListPtr draw, CartographyPrimitive shape, Num.Vector2 origin, Num.Vector2 offset, CartographyRect viewport)
    {
        CartographyRect r = shape.Rect;
        // Clip long route segments before generating dash vertices. A map containing distant rooms
        // must not spend millions of iterations drawing invisible dashes beyond the canvas.
        if (shape.Kind == CartographyPrimitiveKind.Line && !ClipLine(ref r, viewport.Offset(-offset.X, -offset.Y).Inflate(shape.ScreenSpace ? shape.Stroke/zoom : shape.Stroke))) return;
        if (shape.Kind != CartographyPrimitiveKind.Line && !viewport.Intersects(r.Offset(offset.X, offset.Y))) return;
        Num.Vector2 a = Screen(origin, r.X, r.Y, offset), b = Screen(origin, r.Right, r.Bottom, offset);
        uint color = Color(shape.Color); float stroke = shape.ScreenSpace ? shape.Stroke : Math.Max(1, shape.Stroke * zoom);
        switch (shape.Kind)
        {
            case CartographyPrimitiveKind.Image:
                if (shape.PixelPerfect)
                {
                    a = new Num.Vector2((float)Math.Round(a.X), (float)Math.Round(a.Y));
                    b = new Num.Vector2((float)Math.Round(b.X), (float)Math.Round(b.Y));
                }
                CartographyCanvasImages.Draw(draw, shape, a, b, color);
                break;
            case CartographyPrimitiveKind.Fill:
                if (shape.PixelPerfect)
                {
                    a = new Num.Vector2((float)Math.Round(a.X), (float)Math.Round(a.Y));
                    b = new Num.Vector2((float)Math.Round(b.X), (float)Math.Round(b.Y));
                }
                draw.AddRectFilled(a, b, color);
                break;
            case CartographyPrimitiveKind.Outline: draw.AddRect(a, b, color, 0, ImDrawFlags.None, stroke); break;
            case CartographyPrimitiveKind.Ellipse: draw.AddCircle((a + b) / 2, (b.X - a.X) / 2, color, 24, stroke); break;
            case CartographyPrimitiveKind.Line:
                if (shape.PixelPerfect &&
                    (Math.Abs(a.X - b.X) < 0.01f ||
                     Math.Abs(a.Y - b.Y) < 0.01f))
                {
                    DrawPixelPerfectRouteLine(
                        draw,
                        shape,
                        r,
                        a,
                        b,
                        color);
                    break;
                }

                if (!shape.Dashed) draw.AddLine(a, b, color, stroke);
                else
                {
                    float distance = (b - a).Length();
                    if (distance < .1f) break;
                    Num.Vector2 direction = (b - a) / distance;
                    float dashScale = shape.ScreenSpace ? 1 : zoom;
                    float dash = Math.Max(1, shape.DashLength * dashScale), gap = Math.Max(1, shape.DashGap * dashScale);
                    float clipped = new Num.Vector2(r.X-shape.Rect.X,r.Y-shape.Rect.Y).Length();
                    float phase = (clipped*zoom+shape.DashOffset*dashScale) % (dash+gap);
                    for (float step = -phase; step < distance; step += dash + gap)
                        if(step+dash>0) draw.AddLine(a + direction * Math.Max(0,step), a + direction * Math.Min(distance, step + dash), color, stroke);
                }
                break;
            case CartographyPrimitiveKind.Text:
                if (shape.Size * zoom < 5) break;
                string[] lines = shape.Text.Replace("\r", "").Replace("\t", "    ").Split('\n');
                for (int i = 0; i < lines.Length; i++) draw.AddText(ImGui.GetFont(), shape.Size * zoom, a + new Num.Vector2(0, i * shape.Size * 1.4f * zoom), color, lines[i]);
                break;
        }
    }

    private static void DrawPixelPerfectRouteLine(
        ImDrawListPtr draw,
        CartographyPrimitive shape,
        CartographyRect clippedRect,
        Num.Vector2 a,
        Num.Vector2 b,
        uint color)
    {
        bool horizontal =
            Math.Abs(
                a.Y -
                b.Y) < 0.01f;

        float stroke =
            Math.Max(
                1f,
                (float)Math.Round(
                    shape.Stroke *
                    (shape.ScreenSpace ? 1 : zoom)));
        float half =
            stroke *
            0.5f;

        Num.Vector2 start =
            new(
                (float)Math.Round(a.X),
                (float)Math.Round(a.Y));
        Num.Vector2 end =
            new(
                (float)Math.Round(b.X),
                (float)Math.Round(b.Y));

        if (!shape.Dashed)
        {
            if (horizontal)
            {
                float x0 =
                    Math.Min(
                        start.X,
                        end.X);
                float x1 =
                    Math.Max(
                        start.X,
                        end.X);
                float y =
                    (float)Math.Round(
                        (start.Y +
                         end.Y) *
                        0.5f);

                draw.AddRectFilled(
                    new Num.Vector2(
                        x0,
                        y - half),
                    new Num.Vector2(
                        x1,
                        y + half),
                    color);
            }
            else
            {
                float y0 =
                    Math.Min(
                        start.Y,
                        end.Y);
                float y1 =
                    Math.Max(
                        start.Y,
                        end.Y);
                float x =
                    (float)Math.Round(
                        (start.X +
                         end.X) *
                        0.5f);

                draw.AddRectFilled(
                    new Num.Vector2(
                        x - half,
                        y0),
                    new Num.Vector2(
                        x + half,
                        y1),
                    color);
            }

            return;
        }

        float distance =
            horizontal
                ? Math.Abs(
                    end.X -
                    start.X)
                : Math.Abs(
                    end.Y -
                    start.Y);
        if (distance < 0.1f)
            return;

        float dash =
            Math.Max(
                1f,
                (float)Math.Round(
                    shape.DashLength *
                    zoom));
        float gap =
            Math.Max(
                1f,
                (float)Math.Round(
                    shape.DashGap *
                    zoom));
        float clipped =
            new Num.Vector2(
                clippedRect.X -
                shape.Rect.X,
                clippedRect.Y -
                shape.Rect.Y)
            .Length();
        float phase =
            ((clipped +
              shape.DashOffset) *
             zoom) %
            (dash + gap);

        float sign =
            horizontal
                ? Math.Sign(
                    end.X -
                    start.X)
                : Math.Sign(
                    end.Y -
                    start.Y);
        if (Math.Abs(sign) < 0.5f)
            sign = 1f;

        for (float step = -phase;
             step < distance;
             step += dash + gap)
        {
            float from =
                Math.Max(
                    0f,
                    step);
            float to =
                Math.Min(
                    distance,
                    step + dash);
            if (to <= from)
                continue;

            if (horizontal)
            {
                float x0 =
                    start.X +
                    sign *
                    from;
                float x1 =
                    start.X +
                    sign *
                    to;
                float minX =
                    Math.Min(
                        x0,
                        x1);
                float maxX =
                    Math.Max(
                        x0,
                        x1);
                float y =
                    (float)Math.Round(
                        start.Y);

                draw.AddRectFilled(
                    new Num.Vector2(
                        minX,
                        y - half),
                    new Num.Vector2(
                        maxX,
                        y + half),
                    color);
            }
            else
            {
                float y0 =
                    start.Y +
                    sign *
                    from;
                float y1 =
                    start.Y +
                    sign *
                    to;
                float minY =
                    Math.Min(
                        y0,
                        y1);
                float maxY =
                    Math.Max(
                        y0,
                        y1);
                float x =
                    (float)Math.Round(
                        start.X);

                draw.AddRectFilled(
                    new Num.Vector2(
                        x - half,
                        minY),
                    new Num.Vector2(
                        x + half,
                        maxY),
                    color);
            }
        }
    }

    private static bool ClipLine(ref CartographyRect line, CartographyRect clip)
    {
        float first = 0, last = 1;
        if (!ClipEdge(-line.Width, line.X - clip.X, ref first, ref last) ||
            !ClipEdge(line.Width, clip.Right - line.X, ref first, ref last) ||
            !ClipEdge(-line.Height, line.Y - clip.Y, ref first, ref last) ||
            !ClipEdge(line.Height, clip.Bottom - line.Y, ref first, ref last)) return false;
        line = new CartographyRect(line.X + first * line.Width, line.Y + first * line.Height, (last - first) * line.Width, (last - first) * line.Height);
        return true;
    }

    private static bool ClipEdge(float direction, float distance, ref float first, ref float last)
    {
        if (Math.Abs(direction) < .000001f) return distance >= 0;
        float t = distance / direction;
        if (direction < 0) first = Math.Max(first, t); else last = Math.Min(last, t);
        return first <= last;
    }

    private static bool EditColor(string label, ref uint argb)
    {
        Num.Vector4 value = new(((argb >> 16) & 255) / 255f, ((argb >> 8) & 255) / 255f, (argb & 255) / 255f, (argb >> 24) / 255f);
        if (!ImGui.ColorEdit4(label, ref value)) return false;
        argb = ((uint)Math.Round(value.W * 255) << 24) | ((uint)Math.Round(value.X * 255) << 16) | ((uint)Math.Round(value.Y * 255) << 8) | (uint)Math.Round(value.Z * 255);
        return true;
    }
    private static uint Color(uint argb) => (argb & 0xFF00FF00) | ((argb & 0x00FF0000) >> 16) | ((argb & 0x000000FF) << 16);
    private static Num.Vector2 Screen(Num.Vector2 origin, float x, float y, Num.Vector2 offset) => origin + pan + (new Num.Vector2(x, y) + offset) * zoom;
    private static string ItemName(CartographyItem item) => item.Kind == CartographyItemKind.Room ? item.Room : item.Kind == CartographyItemKind.Text ? item.Text.Replace('\n', ' ') : item.Kind == CartographyItemKind.Marker ? item.Marker + " " + item.Appearance.Icon : item.Kind == CartographyItemKind.Connection ? item.Appearance.From + " -> " + item.Appearance.To : item.Kind.ToString();
    private static string T(string chinese, string english) => DevToolUiSettings.T(chinese, english);
}
