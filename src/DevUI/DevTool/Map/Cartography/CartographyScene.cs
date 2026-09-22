using System;
using System.Collections.Generic;
using System.Linq;

namespace DryCycle.DevUI.DevTool.Map.Cartography;

internal enum CartographyPrimitiveKind { Fill, Outline, Line, Ellipse, Text }

internal sealed class CartographyPrimitive
{
    internal CartographyPrimitiveKind Kind;
    internal CartographyRect Rect;
    internal uint Color;
    internal float Stroke = 1;
    internal float Size = 16;
    internal string Text = string.Empty;
    internal bool Dashed;
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
    }
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    internal bool TryGet(CartographyDocument document, CartographyItem item, CartographyLayer layer, CartographyRoomSource room, out CartographySceneNode node, out string error)
    {
        node = null; error = null;
        if (!entries.TryGetValue(item.Id, out Entry entry) || !CartographyEditing.SameItem(entry.Item, item) ||
            !CartographyEditing.SameLayer(entry.Layer, layer) || !ReferenceEquals(entry.Room, room) ||
            (item.Kind == CartographyItemKind.Room && (entry.Names != document.ShowRoomNames || entry.Crop != document.CropSolid || entry.Terrain != document.Terrain || entry.Water != document.Water))) return false;
        node = entry.Node; error = entry.Error; return true;
    }

    internal void Store(CartographyDocument document, CartographyItem item, CartographyLayer layer, CartographyRoomSource room, CartographySceneNode node, string error) =>
        entries[item.Id] = new Entry { Item = item.Clone(), Layer = layer.Clone(), Room = room, Names = document.ShowRoomNames, Crop = document.CropSolid,
            Terrain = document.Terrain, Water = document.Water, Node = node, Error = error };

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

    internal static CartographyScene Build(CartographyDocument document, CartographySource source, CartographySceneCache cache = null)
    {
        List<CartographySceneNode> nodes = new();
        List<string> errors = new(), warnings = new();
        Dictionary<string, CartographyItem> rooms = document.Items.Where(item => item.Kind == CartographyItemKind.Room)
            .ToDictionary(item => item.Room, StringComparer.OrdinalIgnoreCase);
        foreach (CartographyLayer layer in document.Layers)
        {
            if (!layer.Visible || layer.Opacity <= 0) continue;
            if (layer.Id == "links" && document.ShowConnections)
            {
                foreach (CartographyConnectionSource link in source.Connections)
                {
                    if (!rooms.TryGetValue(link.From, out CartographyItem from) || !rooms.TryGetValue(link.To, out CartographyItem to) ||
                        !Visible(document, from) || !Visible(document, to)) continue;
                    bool exactFrom = Port(from, link.FromPort, source, out float ax, out float ay);
                    bool exactTo = Port(to, link.ToPort, source, out float bx, out float by);
                    bool ambiguous = link.Ambiguous || !exactFrom || !exactTo;
                    if (ambiguous) warnings.Add(link.From + " → " + link.To + ": unresolved port; dashed guide shown.");
                    nodes.Add(Connection(from.Id, to.Id, ax, ay, bx, by, Alpha(document.Connections, layer.Opacity), ambiguous));
                }
            }
            foreach (CartographyItem item in document.Items)
            {
                if (item.LayerId != layer.Id || !item.Visible) continue;
                source.Rooms.TryGetValue(item.Room, out CartographyRoomSource geometry);
                if (cache != null && cache.TryGet(document, item, layer, geometry, out CartographySceneNode retained, out string retainedError))
                { nodes.Add(retained); if (retainedError != null) errors.Add(retainedError); continue; }
                string itemError = null;
                CartographyRect bounds = Bounds(item, source);
                List<CartographyPrimitive> primitives = new();
                uint color = Alpha(item.Color, layer.Opacity);
                switch (item.Kind)
                {
                    case CartographyItemKind.Room:
                        if (!source.Rooms.TryGetValue(item.Room, out CartographyRoomSource room) || !room.Ready)
                        {
                            itemError = item.Room + ": " + (room?.Error.Length > 0 ? room.Error : "terrain is not ready");
                            errors.Add(itemError);
                            primitives.Add(new CartographyPrimitive { Kind = CartographyPrimitiveKind.Outline, Rect = bounds, Color = 0xFFFF6373, Stroke = 2 });
                        }
                        else
                        {
                            foreach (CartographyTileRun run in room.Runs)
                            {
                                if (document.CropSolid && run.Kind == 2) continue;
                                uint tileColor = run.Kind switch
                                {
                                    2 => Shade(document.Terrain, 0.25f),
                                    1 => Shade(document.Terrain, 0.6f),
                                    3 => Shade(document.Terrain, 0.8f),
                                    4 => 0xFFE5BC6B,
                                    5 => 0xFFC991BB,
                                    >= 6 => 0xFFE6EDF4,
                                    _ => document.Terrain
                                };
                                CartographyRect rect = new(bounds.X + run.X * TileSize, bounds.Y + (room.Height - 1 - run.Y) * TileSize, run.Length * TileSize, TileSize);
                                primitives.Add(new CartographyPrimitive { Kind = CartographyPrimitiveKind.Fill, Rect = rect, Color = Alpha(tileColor, layer.Opacity) });
                                if (run.Water) primitives.Add(new CartographyPrimitive { Kind = CartographyPrimitiveKind.Fill, Rect = rect, Color = Alpha(document.Water, layer.Opacity) });
                            }
                        }
                        if (document.ShowRoomNames)
                        {
                            CartographyItem label = new() { Kind = CartographyItemKind.Text, Text = item.Room, Size = 13, X = bounds.X, Y = bounds.Bottom + 3 };
                            CartographyRect labelBounds = Bounds(label, source);
                            primitives.Add(new CartographyPrimitive { Kind = CartographyPrimitiveKind.Text, Rect = labelBounds, Color = Alpha(0xFFE2EAF2, layer.Opacity), Size = 13, Text = item.Room });
                            bounds = CartographyRect.Union(bounds, labelBounds);
                        }
                        break;
                    case CartographyItemKind.Text:
                        primitives.Add(new CartographyPrimitive { Kind = CartographyPrimitiveKind.Text, Rect = bounds, Color = color, Size = item.Size, Text = item.Text });
                        break;
                    case CartographyItemKind.Line:
                        primitives.Add(Line(item.X, item.Y, item.X + item.Width, item.Y + item.Height, color, item.Stroke));
                        break;
                    case CartographyItemKind.Box:
                        primitives.Add(new CartographyPrimitive { Kind = CartographyPrimitiveKind.Outline, Rect = bounds, Color = color, Stroke = item.Stroke });
                        break;
                    case CartographyItemKind.Marker:
                        Marker(primitives, item, color);
                        break;
                }
                CartographySceneNode node = new() { Id = item.Id, LayerId = layer.Id, Locked = layer.Locked,
                    Room = item.Kind == CartographyItemKind.Room, Bounds = bounds.Inflate(Math.Max(2, item.Stroke)), Primitives = primitives.ToArray() };
                nodes.Add(node);
                cache?.Store(document, item, layer, geometry, node, itemError);
            }
        }
        CartographyRect total = nodes.Count > 0 ? nodes.Select(node => node.Bounds).Aggregate(CartographyRect.Union) : new CartographyRect(0, 0, 640, 480);
        cache?.Prune(document);
        return new CartographyScene { Nodes = nodes.ToArray(), Bounds = total, Errors = errors.ToArray(), Warnings = warnings.ToArray() };
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

    private static bool Port(CartographyItem item, int port, CartographySource source, out float x, out float y)
    {
        x = item.X; y = item.Y;
        if (!source.Rooms.TryGetValue(item.Room, out CartographyRoomSource room) || !room.Ports.TryGetValue(port, out CartographyRect point)) return false;
        x += (point.X + 0.5f - room.Width / 2f) * TileSize;
        y += (room.Height / 2f - point.Y - 0.5f) * TileSize;
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
