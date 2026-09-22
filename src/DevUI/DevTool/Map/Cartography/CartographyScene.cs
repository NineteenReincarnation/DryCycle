using System;
using System.Collections.Generic;
using System.Linq;

namespace DryCycle.DevUI.DevTool.Map.Cartography;

internal enum CartographyPrimitiveKind { Fill, Outline, Line, Ellipse, Text, Image }

internal sealed class CartographyPrimitive
{
    internal CartographyPrimitiveKind Kind;
    internal CartographyRect Rect;
    internal uint Color;
    internal float Stroke = 1;
    internal float Size = 16;
    internal string Text = string.Empty;
    internal bool Dashed;
    internal CartographyRaster Raster;
}

internal sealed class CartographySceneNode
{
    internal string Id, LayerId;
    internal bool Locked;
    internal bool Room;
    internal CartographyRect Bounds;
    internal CartographyPrimitive[] Primitives = Array.Empty<CartographyPrimitive>();
    internal string FromId, ToId;
    internal float FromX, FromY, ToX, ToY;
    internal uint LinkColor;
    internal bool Ambiguous;
    internal CartographyPoint[] Points = Array.Empty<CartographyPoint>();
}

internal sealed class CartographyScene
{
    internal CartographySceneNode[] Nodes = Array.Empty<CartographySceneNode>();
    internal CartographyRect Bounds = new(0, 0, 640, 480);
    internal string[] Errors = Array.Empty<string>();
    internal string[] Warnings = Array.Empty<string>();
}

// Caches immutable visual nodes by semantic inputs. Selection/pan/zoom never invalidate terrain,
// moving a room rebuilds that room only, and worker exports can retain an older scene safely.
internal sealed class CartographySceneCache
{
    private sealed class Entry
    {
        internal CartographyItem Item;
        internal CartographyLayer Layer;
        internal CartographyRoomSource Room;
        internal bool Names, Crop;
        internal uint Terrain, Water;
        internal CartographySceneNode Node;
        internal string Error;
        internal string Style;
    }
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    internal bool TryGet(CartographyDocument document, CartographyItem item, CartographyLayer layer, CartographyRoomSource room, out CartographySceneNode node, out string error)
    {
        node = null; error = null;
        if (!entries.TryGetValue(item.Id, out Entry entry) || !CartographyEditing.SameItem(entry.Item, item) ||
            !CartographyEditing.SameLayer(entry.Layer, layer) || !ReferenceEquals(entry.Room, room) || entry.Style != StyleKey(document) ||
            (item.Kind == CartographyItemKind.Room && (entry.Names != document.ShowRoomNames || entry.Crop != document.CropSolid || entry.Terrain != document.Terrain || entry.Water != document.Water))) return false;
        node = entry.Node; error = entry.Error; return true;
    }

    internal void Store(CartographyDocument document, CartographyItem item, CartographyLayer layer, CartographyRoomSource room, CartographySceneNode node, string error) =>
        entries[item.Id] = new Entry { Item = item.Clone(), Layer = layer.Clone(), Room = room, Names = document.ShowRoomNames, Crop = document.CropSolid,
            Terrain = document.Terrain, Water = document.Water, Node = node, Error = error, Style = StyleKey(document) };

    private static string StyleKey(CartographyDocument d) => d.FontFamily + CartographyRecord.Key(d.Options) + string.Join(";", d.Palettes.Select(CartographyRecord.Key));

    internal void Prune(CartographyDocument document)
    {
        HashSet<string> ids = new(document.Items.Select(item => item.Id), StringComparer.Ordinal);
        foreach (string id in entries.Keys.Where(id => !ids.Contains(id)).ToArray()) entries.Remove(id);
    }
}

/// <summary>One semantic scene for the ImGui canvas, PNG and SVG. No live game objects or UI calls.</summary>
internal static class CartographySceneBuilder
{
    internal const float TileSize = 3;

    internal static CartographyRect Bounds(CartographyItem item, CartographySource source)
    {
        if (item.Kind == CartographyItemKind.Room)
        {
            source.Rooms.TryGetValue(item.Room, out CartographyRoomSource room);
            float width = Math.Max(1, room?.Width > 0 ? room.Width : 8) * TileSize;
            float height = Math.Max(1, room?.Height > 0 ? room.Height : 8) * TileSize;
            return new CartographyRect(item.X - width / 2, item.Y - height / 2, width, height);
        }
        if (item.Kind == CartographyItemKind.Text)
        {
            // Conservative advance bounds also cover CJK and the PNG font fallback. Newlines are
            // laid out explicitly by both renderers; font metrics are never allowed to crop exports.
            string[] lines = (item.Text ?? string.Empty).Replace("\r", "").Split('\n');
            float width = 1;
            foreach (string line in lines)
            {
                float advance = 0;
                foreach (char c in line) advance += c == '\t' ? 4.4f : c > 255 ? 1.1f : 1.05f;
                width = Math.Max(width, advance * item.Size);
            }
            return new CartographyRect(item.X, item.Y, width + item.Size * 0.4f, Math.Max(1, lines.Length) * item.Size * 1.4f);
        }
        if (item.Kind == CartographyItemKind.Marker)
            return new CartographyRect(item.X - item.Size, item.Y - item.Size, item.Size * 2, item.Size * 2);
        return new CartographyRect(Math.Min(item.X, item.X + item.Width), Math.Min(item.Y, item.Y + item.Height), Math.Abs(item.Width), Math.Abs(item.Height));
    }

    internal static CartographyItem Resolve(CartographyDocument document, CartographyItem item)
    {
        if (item.Appearance.ParentId.Length == 0) return item;
        CartographyItem resolved = item.Clone(), parent = item;
        while (parent.Appearance.ParentId.Length > 0)
        {
            parent = document.Items.Find(i => i.Id == parent.Appearance.ParentId);
            if (parent == null) break;
            resolved.X += parent.X; resolved.Y += parent.Y;
        }
        return resolved;
    }

    internal static bool IsVisible(CartographyDocument d, CartographyItem i)
    {
        if (!Visible(d,i)) return false;
        if (i.Appearance.ParentId.Length > 0)
        { CartographyItem parent=d.Items.Find(p=>p.Id==i.Appearance.ParentId); if(parent==null||!IsVisible(d,parent))return false; }
        if (i.Kind==CartographyItemKind.Connection && !d.ShowConnections) return false;
        if (i.Kind==CartographyItemKind.Room && d.Palettes.Find(p=>p.Name==i.Appearance.Subregion)?.Visible==false) return false;
        string category=i.Appearance.Category;
        if(category=="Special"&&!d.Options.SpecialRooms || category=="Object"&&!d.Options.Objects || category=="Pickup"&&(!d.Options.Objects||!d.Options.Pickups))return false;
        return !d.Options.HiddenTypes.Split(',').Select(t=>t.Trim()).Any(t=>t.Length>0 && (t==i.Text || t==i.Appearance.Icon));
    }

    internal static CartographyScene Build(CartographyDocument document, CartographySource source, CartographySceneCache cache = null)
    {
        List<CartographySceneNode> nodes=new();List<string> errors=new(),warnings=new();
        foreach(CartographyLayer layer in document.Layers)
        {
            if(!layer.Visible||layer.Opacity<=0)continue;
            foreach(CartographyItem author in document.Items.Where(i=>i.LayerId==layer.Id))
            {
                if(!IsVisible(document,author))continue;
                CartographyItem item=Resolve(document,author);
                source.Rooms.TryGetValue(item.Room,out CartographyRoomSource room);
                if(item.Kind==CartographyItemKind.Connection)
                {
                    CartographySceneNode route=Route(document,source,item,layer);
                    if(route!=null){nodes.Add(route);if(route.Ambiguous)warnings.Add(item.Appearance.From+" → "+item.Appearance.To+": unresolved port");}continue;
                }
                if(cache!=null&&cache.TryGet(document,item,layer,room,out CartographySceneNode retained,out string retainedError))
                {nodes.Add(retained);if(retainedError!=null)errors.Add(retainedError);continue;}
                string error=null;List<CartographyPrimitive> shapes=new();CartographyRect bounds=Bounds(item,source);
                float opacity=layer.Opacity*item.Appearance.Opacity;uint color=Alpha(item.Color,opacity);
                switch(item.Kind)
                {
                    case CartographyItemKind.Room:
                        if(room?.Ready!=true){error=item.Room+": "+(room?.Error??"Missing geometry");shapes.Add(new CartographyPrimitive{Kind=CartographyPrimitiveKind.Outline,Rect=bounds,Color=0xFFFF6373,Stroke=2});}
                        else
                        {
                            CartographyRaster terrain=CartographyDrawing.Room(document,item,room);
                            float padding=(terrain.Width-room.Width*3)/2f;
                            shapes.Add(new CartographyPrimitive{Kind=CartographyPrimitiveKind.Image,Rect=bounds.Inflate(padding),Color=Alpha(0xFFFFFFFF,opacity),Raster=terrain});
                            if(document.Options.InRoomShortcuts&&item.Appearance.Shortcuts)
                                foreach(CartographyPoint[] path in room.Shortcuts)for(int n=1;n<path.Length;n++)
                                    shapes.Add(Line(bounds.X+path[n-1].X*3,bounds.Y+(room.Height-path[n-1].Y)*3,bounds.X+path[n].X*3,bounds.Y+(room.Height-path[n].Y)*3,Alpha(0xFFEEEEEE,opacity),1.2f));
                        }
                        if(document.ShowRoomNames)
                        {
                            CartographyItem label=new(){Kind=CartographyItemKind.Text,Text=item.Room,Size=13,X=bounds.X,Y=bounds.Y-16,Color=document.Options.RoomNameColor};
                            AddText(shapes,label,document.FontFamily,opacity);
                        }
                        break;
                    case CartographyItemKind.Text:AddText(shapes,item,document.FontFamily,opacity);break;
                    case CartographyItemKind.Line:
                        shapes.Add(Line(item.X,item.Y,item.X+item.Width,item.Y+item.Height,color,item.Stroke,item.Appearance.Dashed));break;
                    case CartographyItemKind.Box:shapes.Add(new CartographyPrimitive{Kind=CartographyPrimitiveKind.Outline,Rect=bounds,Color=color,Stroke=item.Stroke});break;
                    case CartographyItemKind.Image:
                        if(item.Appearance.Image.Length>0)shapes.Add(new CartographyPrimitive{Kind=CartographyPrimitiveKind.Image,Rect=bounds,Color=Alpha(0xFFFFFFFF,opacity),Raster=CartographyAssets.Image(item.Appearance.Image)});break;
                    case CartographyItemKind.Marker:
                        if(!CartographyDrawing.Marker(shapes,item,opacity))Marker(shapes,item,color);
                        if(document.Options.Slugcats&&item.Appearance.Availability.Length>0)CartographyDrawing.Availability(shapes,item,document.Options,opacity);
                        break;
                }
                if(shapes.Count>0)bounds=shapes.Select(p=>Normalized(p.Rect).Inflate(p.Stroke)).Aggregate(CartographyRect.Union);
                CartographySceneNode node=new(){Id=item.Id,LayerId=layer.Id,Locked=layer.Locked,Room=item.Kind==CartographyItemKind.Room,Bounds=bounds.Inflate(2),Primitives=shapes.ToArray()};
                nodes.Add(node);if(error!=null)errors.Add(error);cache?.Store(document,item,layer,room,node,error);
            }
        }
        cache?.Prune(document);
        return new CartographyScene{Nodes=nodes.ToArray(),Bounds=nodes.Count>0?nodes.Select(n=>n.Bounds).Aggregate(CartographyRect.Union):new CartographyRect(0,0,640,480),Errors=errors.ToArray(),Warnings=warnings.ToArray()};
    }
    private static CartographyRect Normalized(CartographyRect r)=>new(Math.Min(r.X,r.Right),Math.Min(r.Y,r.Bottom),Math.Abs(r.Width),Math.Abs(r.Height));
    private static void AddText(List<CartographyPrimitive> shapes,CartographyItem item,string font,float opacity)
    {
        CartographyRaster raster=CartographyText.Render(item,font);
        shapes.Add(new CartographyPrimitive{Kind=CartographyPrimitiveKind.Image,Rect=new CartographyRect(item.X,item.Y,raster.Width/2f,raster.Height/2f),Color=Alpha(0xFFFFFFFF,opacity),Raster=raster,Text=item.Text});
    }
    internal static CartographySceneNode Route(CartographyDocument d,CartographySource source,CartographyItem item,CartographyLayer layer)
    {
        var a=item.Appearance;
        CartographyItem from=d.Items.Find(i=>i.Kind==CartographyItemKind.Room&&i.Room==a.From),to=d.Items.Find(i=>i.Kind==CartographyItemKind.Room&&i.Room==a.To);
        if(from==null||to==null||!IsVisible(d,from)||!IsVisible(d,to))return null;
        bool exactA=Port(from,a.FromPort,source,out float ax,out float ay),exactB=Port(to,a.ToPort,source,out float bx,out float by);
        List<CartographyPoint> points=new(){new CartographyPoint{X=ax,Y=ay}};
        if(a.Route==CartographyRouteMode.Manual)points.AddRange(item.Points.Select(p=>p.Clone()));
        else if(a.Route==CartographyRouteMode.HorizontalFirst){float mid=(ax+bx)/2;points.Add(new CartographyPoint{X=mid,Y=ay});points.Add(new CartographyPoint{X=mid,Y=by});}
        else if(a.Route==CartographyRouteMode.VerticalFirst){float mid=(ay+by)/2;points.Add(new CartographyPoint{X=ax,Y=mid});points.Add(new CartographyPoint{X=bx,Y=mid});}
        points.Add(new CartographyPoint{X=bx,Y=by});
        List<CartographyPrimitive> shapes=new();float opacity=layer.Opacity*a.Opacity;bool ambiguous=!exactA||!exactB;
        for(int n=1;n<points.Count;n++)
        {
            var p=points[n-1];var q=points[n];
            if(a.Shade)shapes.Add(Line(p.X,p.Y,q.X,q.Y,Alpha(a.ShadeColor,opacity),item.Stroke+a.Outline*2));
            shapes.Add(Line(p.X,p.Y,q.X,q.Y,Alpha(item.Color,opacity),item.Stroke,a.Dashed||ambiguous));
        }
        if(a.WhiteRed)foreach(CartographyPoint p in new[]{points[0],points[points.Count-1]})shapes.Add(new CartographyPrimitive{Kind=CartographyPrimitiveKind.Fill,Rect=new CartographyRect(p.X-1.5f,p.Y-1.5f,3,3),Color=Alpha(0xFFFF3333,opacity)});
        return new CartographySceneNode{Id=item.Id,LayerId=layer.Id,Locked=layer.Locked,FromId=from.Id,ToId=to.Id,FromX=ax,FromY=ay,ToX=bx,ToY=by,LinkColor=item.Color,Ambiguous=ambiguous,Points=points.ToArray(),Primitives=shapes.ToArray(),Bounds=shapes.Select(p=>Normalized(p.Rect).Inflate(p.Stroke)).Aggregate(CartographyRect.Union)};
    }

    internal static CartographySceneNode Connection(string from, string to, float ax, float ay, float bx, float by, uint color, bool ambiguous)
    {
        float mid = (ax + bx) * 0.5f;
        return new CartographySceneNode
        {
            Id = string.Empty, LayerId = "links", Locked = true, FromId = from, ToId = to,
            FromX = ax, FromY = ay, ToX = bx, ToY = by, LinkColor = color, Ambiguous = ambiguous,
            Bounds = new CartographyRect(Math.Min(ax, bx), Math.Min(ay, by), Math.Abs(ax - bx), Math.Abs(ay - by)).Inflate(3),
            Primitives = new[] { Line(ax, ay, mid, ay, color, 1.8f, ambiguous), Line(mid, ay, mid, by, color, 1.8f, ambiguous), Line(mid, by, bx, by, color, 1.8f, ambiguous) }
        };
    }

    internal static bool Port(CartographyItem item, int port, CartographySource source, out float x, out float y)
    {
        x = item.X; y = item.Y;
        if (!source.Rooms.TryGetValue(item.Room, out CartographyRoomSource room) || !room.Ports.TryGetValue(port, out CartographyRect point)) return false;
        x += (point.X - room.Width / 2f) * TileSize;
        y += (room.Height / 2f - point.Y) * TileSize;
        return true;
    }

    private static bool Visible(CartographyDocument document, CartographyItem item) =>
        item.Visible && document.Layer(item.LayerId) is { Visible: true, Opacity: > 0 };

    private static CartographyPrimitive Line(float ax, float ay, float bx, float by, uint color, float stroke, bool dashed = false) =>
        new() { Kind = CartographyPrimitiveKind.Line, Rect = new CartographyRect(ax, ay, bx - ax, by - ay), Color = color, Stroke = stroke, Dashed = dashed };

    private static void Marker(List<CartographyPrimitive> shapes, CartographyItem item, uint color)
    {
        float x = item.X, y = item.Y, s = item.Size, stroke = item.Stroke;
        if (item.Marker == CartographyMarker.Pin || item.Marker == CartographyMarker.Pearl)
        {
            shapes.Add(new CartographyPrimitive { Kind = CartographyPrimitiveKind.Ellipse, Rect = new CartographyRect(x - s * .6f, y - s * .6f, s * 1.2f, s * 1.2f), Color = color, Stroke = stroke });
            if (item.Marker == CartographyMarker.Pin) shapes.Add(Line(x, y + s * .6f, x, y + s, color, stroke));
            else shapes.Add(Line(x - s * .25f, y - s * .2f, x + s * .05f, y - s * .4f, color, stroke));
        }
        else if (item.Marker == CartographyMarker.Shelter)
        {
            shapes.Add(Line(x - s, y, x, y - s, color, stroke)); shapes.Add(Line(x, y - s, x + s, y, color, stroke));
            shapes.Add(Line(x - s * .65f, y - s * .3f, x - s * .65f, y + s * .75f, color, stroke));
            shapes.Add(Line(x + s * .65f, y - s * .3f, x + s * .65f, y + s * .75f, color, stroke));
            shapes.Add(Line(x - s * .65f, y + s * .75f, x + s * .65f, y + s * .75f, color, stroke));
        }
        else if (item.Marker == CartographyMarker.Gate)
        {
            shapes.Add(Line(x - s * .6f, y - s, x - s * .6f, y + s, color, stroke));
            shapes.Add(Line(x + s * .6f, y - s, x + s * .6f, y + s, color, stroke));
            shapes.Add(Line(x - s, y - s * .5f, x + s, y - s * .5f, color, stroke));
            shapes.Add(Line(x - s, y + s * .5f, x + s, y + s * .5f, color, stroke));
        }
        else
        {
            shapes.Add(Line(x, y - s, x - s, y + s * .8f, color, stroke));
            shapes.Add(Line(x - s, y + s * .8f, x + s, y + s * .8f, color, stroke));
            shapes.Add(Line(x + s, y + s * .8f, x, y - s, color, stroke));
            shapes.Add(Line(x, y - s * .3f, x, y + s * .25f, color, stroke));
            shapes.Add(Line(x, y + s * .5f, x, y + s * .55f, color, stroke * 1.5f));
        }
    }

    internal static uint Alpha(uint color, float opacity) => (color & 0x00FFFFFF) | ((uint)Math.Round((color >> 24) * opacity) << 24);
    private static uint Shade(uint color, float factor) => (color & 0xFF000000) |
        ((uint)(((color >> 16) & 255) * factor) << 16) | ((uint)(((color >> 8) & 255) * factor) << 8) | (uint)((color & 255) * factor);
}
