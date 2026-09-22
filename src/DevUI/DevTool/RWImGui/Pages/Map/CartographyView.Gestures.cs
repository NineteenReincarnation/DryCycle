using System;
using System.Linq;
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
        if(node.FromId==null)return node.Bounds.Contains(mouse.X,mouse.Y);
        for(int n=1;n<node.Points.Length;n++)if(Distance(mouse,node.Points[n-1],node.Points[n])<Math.Max(4,8/zoom))return true;
        return false;
    }
    private static float Distance(Num.Vector2 p,CartographyPoint a,CartographyPoint b)
    {
        Num.Vector2 start=new(a.X,a.Y),end=new(b.X,b.Y),v=end-start;float t=v.LengthSquared()<.00001f?0:Math.Max(0,Math.Min(1,Num.Vector2.Dot(p-start,v)/v.LengthSquared()));
        return (p-start-t*v).Length();
    }
    private static void DrawRouteHandles(ImDrawListPtr draw,CartographyPresentation s,Num.Vector2 origin)
    {
        if(Selection.Count!=1)return;CartographyItem selected=s.Document.Items.Find(i=>i.Id==selectedItem);if(selected==null)return;
        if(selected.Kind==CartographyItemKind.Connection)
        {
            var node=s.Scene.Nodes.FirstOrDefault(n=>n.Id==selected.Id);if(node==null)return;
            if(routeGesture!=null)node=CartographySceneBuilder.Route(s.Document,s.Source,routeGesture,s.Document.Layer(routeGesture.LayerId))??node;
            for(int n=0;n<node.Points.Length;n++){var p=node.Points[n];draw.AddCircleFilled(Screen(origin,p.X,p.Y,default),n==0||n==node.Points.Length-1?3:5,0xFFF0D572,12);}
        }
        else if(selected.Kind==CartographyItemKind.Line)
        {
            var item=routeGesture??CartographySceneBuilder.Resolve(s.Document,selected);
            draw.AddCircleFilled(Screen(origin,item.X,item.Y,default),5,0xFFF0D572,12);draw.AddCircleFilled(Screen(origin,item.X+item.Width,item.Y+item.Height,default),5,0xFFF0D572,12);
        }
    }
    private static bool RouteGesture(CartographyPresentation s,bool hovered,Num.Vector2 mouse,ImGuiIOPtr io)
    {
        if(routeGesture!=null)
        {
            if(s.Revision!=routeRevision){routeGesture=null;routeHandle=-1;return false;}
            Num.Vector2 point=mouse;if(snap)point=new Num.Vector2((float)Math.Round(point.X/grid)*grid,(float)Math.Round(point.Y/grid)*grid);
            if(io.KeyShift){Num.Vector2 delta=point-handleOrigin;if(Math.Abs(delta.X)>Math.Abs(delta.Y))point.Y=handleOrigin.Y;else point.X=handleOrigin.X;}
            if(routeGesture.Kind==CartographyItemKind.Line)
            {
                CartographyItem original=s.Document.Items.Find(i=>i.Id==routeGesture.Id);
                CartographyItem world=CartographySceneBuilder.Resolve(s.Document,original);float ox=world.X-original.X,oy=world.Y-original.Y;
                if(routeHandle==0){float endX=routeGesture.X+routeGesture.Width,endY=routeGesture.Y+routeGesture.Height;routeGesture.X=point.X-ox;routeGesture.Y=point.Y-oy;routeGesture.Width=endX-routeGesture.X;routeGesture.Height=endY-routeGesture.Y;}
                else{routeGesture.Width=point.X-ox-routeGesture.X;routeGesture.Height=point.Y-oy-routeGesture.Y;}
            }
            else{routeGesture.Points[routeHandle].X=point.X;routeGesture.Points[routeHandle].Y=point.Y;}
            if(ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            {CartographyItem result=routeGesture;Send(CartographyCommandKind.UpdateItem,c=>{c.Item=result;c.Revision=routeRevision;});routeGesture=null;routeHandle=-1;draft=null;}
            return true;
        }
        if(!hovered||tool!=Tool.Select||Selection.Count!=1||!ImGui.IsMouseClicked(ImGuiMouseButton.Left))return false;
        CartographyItem item=s.Document.Items.Find(i=>i.Id==selectedItem);if(item==null||!s.Document.Editable(item))return false;
        if(item.Kind==CartographyItemKind.Line)
        {
            var world=CartographySceneBuilder.Resolve(s.Document,item);
            if((mouse-new Num.Vector2(world.X,world.Y)).Length()<8/zoom)routeHandle=0;
            else if((mouse-new Num.Vector2(world.X+world.Width,world.Y+world.Height)).Length()<8/zoom)routeHandle=1;else return false;
            routeGesture=item.Clone();routeRevision=s.Revision;handleOrigin=mouse;return true;
        }
        if(item.Kind!=CartographyItemKind.Connection)return false;
        var node=s.Scene.Nodes.FirstOrDefault(n=>n.Id==item.Id);if(node==null)return false;
        CartographyItem edit=item.Clone();edit.Appearance.Route=CartographyRouteMode.Manual;
        if(item.Appearance.Route!=CartographyRouteMode.Manual)edit.Points=node.Points.Skip(1).Take(Math.Max(0,node.Points.Length-2)).Select(p=>p.Clone()).ToList();
        for(int n=0;n<edit.Points.Count;n++)if((mouse-new Num.Vector2(edit.Points[n].X,edit.Points[n].Y)).Length()<9/zoom)
        {
            if(io.KeyAlt){edit.Points.RemoveAt(n);Send(CartographyCommandKind.UpdateItem,c=>c.Item=edit);draft=null;return true;}
            routeGesture=edit;routeHandle=n;routeRevision=s.Revision;handleOrigin=new Num.Vector2(edit.Points[n].X,edit.Points[n].Y);return true;
        }
        if(ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)&&HitNode(node,mouse))
        {
            int segment=Enumerable.Range(1,node.Points.Length-1).OrderBy(n=>Distance(mouse,node.Points[n-1],node.Points[n])).First();
            edit.Points.Insert(Math.Min(segment-1,edit.Points.Count),new CartographyPoint{X=mouse.X,Y=mouse.Y});
            routeGesture=edit;routeHandle=segment-1;routeRevision=s.Revision;handleOrigin=mouse;return true;
        }
        return false;
    }
    private static void ClipboardKeys(CartographyPresentation s,Num.Vector2 mouse,ImGuiIOPtr io)
    {
        bool copy=KeyChord(s.Document.Options.CopyKey),cut=KeyChord(s.Document.Options.CutKey);
        if(copy||cut)
        {
            clipboard=s.Document.Items.Where(i=>Selection.Contains(i.Id)&&i.Kind!=CartographyItemKind.Room&&i.Kind!=CartographyItemKind.Connection).Select(i=>CartographySceneBuilder.Resolve(s.Document,i).Clone()).ToArray();
            foreach(var i in clipboard)i.Appearance.ParentId="";
            if(cut)Send(CartographyCommandKind.Delete,c=>c.Ids=clipboard.Select(i=>i.Id).ToArray());
        }
        if(KeyChord(s.Document.Options.PasteKey)&&clipboard.Length>0)
            Send(CartographyCommandKind.Paste,c=>{c.Items=clipboard;c.LayerId=activeLayer;c.X=mouse.X-clipboard.Min(i=>i.X);c.Y=mouse.Y-clipboard.Min(i=>i.Y);});
    }
    private static bool KeyChord(string chord)
    {
        string[] keys=chord.Split('+');var io=ImGui.GetIO();
        bool ctrl=keys.Any(k=>k.Equals("Ctrl",StringComparison.OrdinalIgnoreCase)),shift=keys.Any(k=>k.Equals("Shift",StringComparison.OrdinalIgnoreCase)),alt=keys.Any(k=>k.Equals("Alt",StringComparison.OrdinalIgnoreCase));
        return io.KeyCtrl==ctrl&&io.KeyShift==shift&&io.KeyAlt==alt&&Enum.TryParse(keys.Last(),true,out ImGuiKey key)&&ImGui.IsKeyPressed(key);
    }
}
