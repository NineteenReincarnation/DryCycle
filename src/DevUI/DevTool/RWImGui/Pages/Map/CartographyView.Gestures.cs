using System;
using System.Linq;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Map.Cartography;
using ImGuiNET;
using Num=System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal static partial class CartographyView
{
    private static CartographyItem routeGesture;
    private static int routeHandle=-1;
    private static long routeRevision;
    private static Num.Vector2 handleOrigin;
    private static CartographyItem[] clipboard=Array.Empty<CartographyItem>();
    private static bool MovesWithSelection(CartographyDocument d,string id)
    {
        CartographyItem i=d.Items.Find(p=>p.Id==id);
        while(i!=null){if(Selection.Contains(i.Id))return true;i=d.Items.Find(p=>p.Id==i.Appearance.ParentId);}
        return false;
    }
    private static CartographySceneNode PreviewRoute(CartographySceneNode original,CartographyItem item,CartographyDocument moving,CartographyDocument stable) =>
        CartographySceneBuilder.Route(moving,observed.Source,routeGesture?.Id==item.Id?routeGesture:item,moving.Layer(item.LayerId))??original;
    private static bool HitNode(CartographySceneNode node,Num.Vector2 mouse)
    {
        return node.Points.Length >= 2
            ? CartographyRouteEditing.NearestSegment(node.Points, mouse.X, mouse.Y, 8 / zoom, out _) >= 0
            : node.Bounds.Contains(mouse.X, mouse.Y);
    }
    private static void DrawRouteHandles(ImDrawListPtr draw,CartographyPresentation s,Num.Vector2 origin)
    {
        if(Selection.Count!=1)return;CartographyItem selected=s.Document.Items.Find(i=>i.Id==selectedItem);if(selected==null)return;
        if(selected.Kind==CartographyItemKind.Connection)
        {
            var node=s.Scene.Nodes.FirstOrDefault(n=>n.Id==selected.Id);if(node==null)return;
            if(routeGesture!=null)node=CartographySceneBuilder.Route(s.Document,s.Source,routeGesture,s.Document.Layer(routeGesture.LayerId))??node;
            for (int n = 1; n < node.Points.Length; n++)
            {
                var a = node.Points[n - 1]; var b = node.Points[n];
                draw.AddLine(Screen(origin, a.X, a.Y, default), Screen(origin, b.X, b.Y, default), 0xFFF0D572, 2);
            }
            for (int n = 0; n < node.Points.Length; n++)
            {
                var p = node.Points[n];
                bool endpoint = n == 0 || n == node.Points.Length - 1;
                Num.Vector2 position = Screen(origin, p.X, p.Y, default);
                draw.AddCircleFilled(position, endpoint ? 3 : 6, !endpoint && n - 1 == selectedRoutePoint ? 0xFFFFFFFF : 0xFFF0D572, 16);
                if (!endpoint) draw.AddCircle(position, 6, 0xFF202733, 16, 1.5f);
            }
        }
        else if(selected.Kind==CartographyItemKind.Line)
        {
            var item=CartographySceneBuilder.Resolve(s.Document,routeGesture??selected);
            draw.AddCircleFilled(Screen(origin,item.X,item.Y,default),5,0xFFF0D572,12);draw.AddCircleFilled(Screen(origin,item.X+item.Width,item.Y+item.Height,default),5,0xFFF0D572,12);
        }
    }
    private static int selectedRoutePoint = -1;
    private static Num.Vector2 routeMouseOrigin;
    private static string routeIdentity;
    private static void CancelRoute()
    {
        routeGesture = null; routeHandle = -1;
    }

    private static bool RouteGesture(CartographyPresentation s, bool hovered, Num.Vector2 mouse, ImGuiIOPtr io)
    {
        if (routeGesture != null)
        {
            if (s.Identity != routeIdentity || s.Revision != routeRevision)
            { CancelRoute(); selectedRoutePoint = -1; return true; }
            if (ImGui.IsKeyPressed(ImGuiKey.Escape) || ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            {
                bool keyboardCancel =
                    ImGui.IsKeyPressed(ImGuiKey.Escape);
                CancelRoute();
                if (keyboardCancel)
                {
                    EditorShortcutFeedback.PublishCustom(
                        "已取消路线编辑",
                        "Route edit canceled",
                        "Esc",
                        true,
                        EditorShortcutFeedbackVisual.Cancel);
                }
                return true;
            }
            Num.Vector2 delta = mouse - routeMouseOrigin;
            CartographyPoint point = CartographyRouteEditing.Drag(handleOrigin.X + delta.X, handleOrigin.Y + delta.Y,
                new CartographyPoint { X = handleOrigin.X, Y = handleOrigin.Y }, delta.LengthSquared() > .001f && snap ? grid : 0, io.KeyShift);
            if (routeGesture.Kind == CartographyItemKind.Line)
            {
                CartographyItem original = s.Document.Items.Find(i => i.Id == routeGesture.Id);
                CartographyItem world = CartographySceneBuilder.Resolve(s.Document, original);
                float ox = world.X - original.X, oy = world.Y - original.Y;
                if (routeHandle == 0)
                {
                    float endX = routeGesture.X + routeGesture.Width, endY = routeGesture.Y + routeGesture.Height;
                    routeGesture.X = point.X - ox; routeGesture.Y = point.Y - oy;
                    routeGesture.Width = endX - routeGesture.X; routeGesture.Height = endY - routeGesture.Y;
                }
                else { routeGesture.Width = point.X - ox - routeGesture.X; routeGesture.Height = point.Y - oy - routeGesture.Y; }
            }
            else routeGesture.Points[routeHandle] = point;
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            {
                CartographyItem result = routeGesture;
                if (!CartographyEditing.SameItem(s.Document.Items.Find(i => i.Id == result.Id), result))
                    Send(CartographyCommandKind.UpdateItem, c => { c.Item = result; c.Revision = routeRevision; });
                selectedRoutePoint = result.Kind == CartographyItemKind.Connection ? routeHandle : -1;
                CancelRoute(); draft = null;
            }
            return true;
        }

        if (!hovered || io.WantTextInput || (tool != Tool.Select && tool != Tool.Route)) return false;
        CartographyItem item = Selection.Count == 1 ? s.Document.Items.Find(i => i.Id == selectedItem) : null;
        if (item?.Kind == CartographyItemKind.Connection && s.Document.Editable(item) && selectedRoutePoint >= 0 &&
            KeyChord(s.Document.Options.DeleteKey) && !ImGui.IsAnyItemActive())
        {
            CartographyItem edit = item.Clone();
            if (selectedRoutePoint < edit.Points.Count)
            {
                edit.Points.RemoveAt(selectedRoutePoint);
                Send(CartographyCommandKind.UpdateItem, c => c.Item = edit);
                selectedRoutePoint = -1; draft = null; return true;
            }
        }
        if (!ImGui.IsMouseClicked(ImGuiMouseButton.Left) || io.KeyCtrl || io.KeyShift) return false;
        float radius = 9 / zoom;
        if (item?.Kind == CartographyItemKind.Line && s.Document.Editable(item))
        {
            CartographyItem world = CartographySceneBuilder.Resolve(s.Document, item);
            Num.Vector2 start = new(world.X, world.Y), end = new(world.X + world.Width, world.Y + world.Height);
            int handle = (mouse - start).Length() <= radius ? 0 : (mouse - end).Length() <= radius ? 1 : -1;
            if (handle >= 0) { BeginRoute(s, item.Clone(), handle, handle == 0 ? start : end, mouse); return true; }
        }

        // A selected bend takes precedence over the room beneath it. Otherwise normal selection
        // keeps rooms draggable; the explicit route tool can select connections crossing rooms.
        CartographySceneNode selected = item?.Kind == CartographyItemKind.Connection
            ? s.Scene.Nodes.FirstOrDefault(n => n.Id == item.Id) : null;
        int selectedHandle = selected == null ? -1 : CartographyRouteEditing.NearestHandle(selected.Points, mouse.X, mouse.Y, radius);
        CartographySceneNode node = selectedHandle >= 0 ? selected : null;
        if (node == null)
        {
            if (tool == Tool.Select && s.Scene.Nodes.Any(n => n.FromId == null && !n.Locked && HitNode(n, mouse))) return false;
            float nearest = float.MaxValue;
            foreach (CartographySceneNode candidate in s.Scene.Nodes.Where(n => n.FromId != null && !n.Locked))
            {
                if (CartographyRouteEditing.NearestSegment(candidate.Points, mouse.X, mouse.Y, radius, out CartographyPoint hit) < 0) continue;
                float distance = (mouse - new Num.Vector2(hit.X, hit.Y)).LengthSquared();
                if (distance <= nearest) { node = candidate; nearest = distance; }
            }
        }
        if (node == null) return false;
        item = s.Document.Items.Find(i => i.Id == node.Id);
        if (item == null || !s.Document.Editable(item)) return false;
        CartographyItem manual = CartographyRouteEditing.Manual(item, node);
        int index = CartographyRouteEditing.NearestHandle(node.Points, mouse.X, mouse.Y, radius);
        if (index >= 0 && io.KeyAlt)
        {
            Select(item.Id, false, false);
            manual.Points.RemoveAt(index);
            Send(CartographyCommandKind.UpdateItem, c => c.Item = manual);
            selectedRoutePoint = -1; draft = null; return true;
        }
        if (io.KeyAlt) return false;
        if (index < 0)
        {
            index = CartographyRouteEditing.NearestSegment(node.Points, mouse.X, mouse.Y, radius, out CartographyPoint point);
            if (index < 0) return false;
            manual.Points.Insert(index, point);
        }
        Select(item.Id, false, false);
        selectedRoutePoint = index;
        CartographyPoint anchor = manual.Points[index];
        BeginRoute(s, manual, index, new Num.Vector2(anchor.X, anchor.Y), mouse);
        return true;
    }

    private static void BeginRoute(CartographyPresentation s, CartographyItem edit, int index, Num.Vector2 anchor, Num.Vector2 mouse)
    {
        CommitDraft();
        dragging = marquee = false; delta = default;
        routeGesture = edit; routeHandle = index; routeRevision = s.Revision; routeIdentity = s.Identity;
        handleOrigin = anchor; routeMouseOrigin = mouse;
    }

    private static void ClipboardKeys(CartographyPresentation s,Num.Vector2 mouse,ImGuiIOPtr io)
    {
        bool copy=KeyChord(s.Document.Options.CopyKey),cut=KeyChord(s.Document.Options.CutKey);
        if(copy||cut)
        {
            clipboard=s.Document.Items.Where(i=>Selection.Contains(i.Id)&&i.Kind!=CartographyItemKind.Room&&i.Kind!=CartographyItemKind.Connection).Select(i=>CartographySceneBuilder.Resolve(s.Document,i).Clone()).ToArray();
            foreach(var i in clipboard)i.Appearance.ParentId="";
            bool available=clipboard.Length>0;
            if(cut&&available)Send(CartographyCommandKind.Delete,c=>c.Ids=clipboard.Select(i=>i.Id).ToArray());
            EditorShortcutFeedback.PublishCustom(
                available
                    ? (cut?"已剪切制图元素":"已复制制图元素")
                    : "没有可复制的制图元素",
                available
                    ? (cut?"Cartography items cut":"Cartography items copied")
                    : "No cartography items to copy",
                cut?s.Document.Options.CutKey:s.Document.Options.CopyKey,
                available,
                available
                    ? (cut?EditorShortcutFeedbackVisual.Delete:EditorShortcutFeedbackVisual.Copy)
                    : EditorShortcutFeedbackVisual.Warning);
        }

        bool paste=KeyChord(s.Document.Options.PasteKey);
        if(paste)
        {
            bool available=clipboard.Length>0;
            if(available)
                Send(CartographyCommandKind.Paste,c=>{c.Items=clipboard;c.LayerId=activeLayer;c.X=mouse.X-clipboard.Min(i=>i.X);c.Y=mouse.Y-clipboard.Min(i=>i.Y);});
            EditorShortcutFeedback.PublishCustom(
                available?"已粘贴制图元素":"剪贴板为空",
                available?"Cartography items pasted":"Clipboard is empty",
                s.Document.Options.PasteKey,
                available,
                available?EditorShortcutFeedbackVisual.Paste:EditorShortcutFeedbackVisual.Warning);
        }
    }
    private static bool KeyChord(string chord)
    {
        string[] keys=chord.Split('+');var io=ImGui.GetIO();
        bool ctrl=keys.Any(k=>k.Equals("Ctrl",StringComparison.OrdinalIgnoreCase)),shift=keys.Any(k=>k.Equals("Shift",StringComparison.OrdinalIgnoreCase)),alt=keys.Any(k=>k.Equals("Alt",StringComparison.OrdinalIgnoreCase));
        return io.KeyCtrl==ctrl&&io.KeyShift==shift&&io.KeyAlt==alt&&Enum.TryParse(keys.Last(),true,out ImGuiKey key)&&ImGui.IsKeyPressed(key);
    }
}
