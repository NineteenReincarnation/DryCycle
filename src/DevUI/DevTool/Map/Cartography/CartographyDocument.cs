using System;
using System.Collections.Generic;
using System.Linq;

namespace DryCycle.DevUI.DevTool.Map.Cartography;

// Author data only. Room tiles, resolved connections, selection and render caches never enter this model.
internal enum CartographyItemKind { Room, Text, Marker, Line, Box }
internal enum CartographyMarker { Pin, Shelter, Gate, Pearl, Danger }
internal enum CartographyAlignment { Left, CenterX, Right, Top, CenterY, Bottom, DistributeX, DistributeY }

internal readonly struct CartographyRect
{
    internal CartographyRect(float x, float y, float width, float height)
    { X = x; Y = y; Width = width; Height = height; }
    internal float X { get; }
    internal float Y { get; }
    internal float Width { get; }
    internal float Height { get; }
    internal float Right => X + Width;
    internal float Bottom => Y + Height;
    internal bool Contains(float x, float y) => x >= X && y >= Y && x <= Right && y <= Bottom;
    internal bool Intersects(CartographyRect other) => X <= other.Right && Right >= other.X && Y <= other.Bottom && Bottom >= other.Y;
    internal CartographyRect Offset(float x, float y) => new(X + x, Y + y, Width, Height);
    internal CartographyRect Inflate(float size) => new(X - size, Y - size, Width + size * 2, Height + size * 2);
    internal static CartographyRect Union(CartographyRect a, CartographyRect b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.Right, b.Right) - Math.Min(a.X, b.X), Math.Max(a.Bottom, b.Bottom) - Math.Min(a.Y, b.Y));
}

internal sealed class CartographyLayer
{
    public string Id = Guid.NewGuid().ToString("N");
    public string Name = "Layer";
    public bool Visible = true;
    public bool Locked;
    public float Opacity = 1;
    internal CartographyLayer Clone() => (CartographyLayer)MemberwiseClone();
}

internal sealed class CartographyItem
{
    public string Id = Guid.NewGuid().ToString("N");
    public CartographyItemKind Kind;
    public string LayerId = "notes";
    public string Room = string.Empty;
    public string Text = string.Empty;
    public float X;
    public float Y;
    public float Width = 100;
    public float Height = 60;
    public float Size = 20;
    public float Stroke = 2;
    public uint Color = 0xFFF2D9A6; // AARRGGBB, independent of renderer packing.
    public bool Visible = true;
    public CartographyMarker Marker;
    internal CartographyItem Clone() => (CartographyItem)MemberwiseClone();
}

internal sealed class CartographyDocument
{
    internal const int FormatVersion = 1;
    public string Identity = string.Empty;
    public string Region = string.Empty;
    public string Title = string.Empty;
    public string FontFamily = "Microsoft YaHei UI";
    public uint Background = 0xFF161E2B;
    public uint Terrain = 0xFFCFDAE3;
    public uint Water = 0xB34D9FD1;
    public uint Connections = 0xFFB8C3D0;
    public bool ShowRoomNames = true;
    public bool ShowConnections = true;
    public bool Transparent;
    public bool CropSolid = true;
    public float ExportScale = 2;
    public int Padding = 32;
    public readonly List<CartographyLayer> Layers = new(); // Back to front.
    public readonly List<CartographyItem> Items = new();

    internal CartographyDocument Clone()
    {
        CartographyDocument copy = new()
        {
            Identity = Identity, Region = Region, Title = Title, FontFamily = FontFamily,
            Background = Background, Terrain = Terrain, Water = Water, Connections = Connections,
            ShowRoomNames = ShowRoomNames, ShowConnections = ShowConnections, Transparent = Transparent,
            CropSolid = CropSolid, ExportScale = ExportScale, Padding = Padding
        };
        copy.Layers.AddRange(Layers.Select(layer => layer.Clone()));
        copy.Items.AddRange(Items.Select(item => item.Clone()));
        return copy;
    }

    internal CartographyLayer Layer(string id) => Layers.Find(layer => layer.Id == id);
    internal bool Editable(CartographyItem item) => item.Visible && Layer(item.LayerId) is { Visible: true, Locked: false };

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Identity) || Identity.Length > 4096 || string.IsNullOrWhiteSpace(Region))
            throw new InvalidOperationException("The cartography document has no valid source identity.");
        if (Title == null || Title.Length > 512 || string.IsNullOrWhiteSpace(FontFamily) || FontFamily.Length > 128)
            throw new InvalidOperationException("Invalid map title or font.");
        if (Layers.Count == 0 || Layers.Count > 128 || Items.Count > 20000)
            throw new InvalidOperationException("A map requires 1–128 layers and at most 20,000 objects.");
        HashSet<string> layerIds = new(StringComparer.Ordinal);
        foreach (CartographyLayer layer in Layers)
        {
            if (string.IsNullOrWhiteSpace(layer.Id) || !layerIds.Add(layer.Id) || string.IsNullOrWhiteSpace(layer.Name) || layer.Name.Length > 160)
                throw new InvalidOperationException("Layer names and unique IDs are required.");
            RequireRange(layer.Opacity, 0, 1, "layer opacity");
        }
        if (!layerIds.Contains("links")) throw new InvalidOperationException("The connections layer is required.");
        HashSet<string> ids = new(StringComparer.Ordinal);
        HashSet<string> rooms = new(StringComparer.OrdinalIgnoreCase);
        foreach (CartographyItem item in Items)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || !ids.Add(item.Id) || !layerIds.Contains(item.LayerId) ||
                !Enum.IsDefined(typeof(CartographyItemKind), item.Kind) || !Enum.IsDefined(typeof(CartographyMarker), item.Marker))
                throw new InvalidOperationException("Invalid object identity, kind or layer reference.");
            if (item.Text == null || item.Text.Length > 4096 || item.Room == null || item.Room.Length > 256)
                throw new InvalidOperationException("Object text is too long.");
            if (item.Kind == CartographyItemKind.Room && (string.IsNullOrWhiteSpace(item.Room) || !rooms.Add(item.Room)))
                throw new InvalidOperationException("Each source room can appear only once in a map.");
            RequireRange(item.X, -1000000, 1000000, "X");
            RequireRange(item.Y, -1000000, 1000000, "Y");
            RequireRange(item.Width, -100000, 100000, "width");
            RequireRange(item.Height, -100000, 100000, "height");
            RequireRange(item.Size, 4, 256, "text/icon size");
            RequireRange(item.Stroke, 0.25f, 32, "stroke");
        }
        RequireRange(ExportScale, 0.25f, 8, "export scale");
        if (Padding < 0 || Padding > 1024) throw new InvalidOperationException("Invalid export padding.");
    }

    private static void RequireRange(float value, float min, float max, string name)
    {
        if (float.IsNaN(value) || float.IsInfinity(value) || value < min || value > max)
            throw new InvalidOperationException("Invalid " + name + ".");
    }
}

// Detached source geometry is supplied by the existing Player Map bake pipeline. It is never saved.
internal readonly struct CartographyTileRun
{
    internal CartographyTileRun(int x, int y, int length, int kind, bool water)
    { X = x; Y = y; Length = length; Kind = kind; Water = water; }
    internal int X { get; }
    internal int Y { get; }
    internal int Length { get; }
    internal int Kind { get; }
    internal bool Water { get; }
}

internal sealed class CartographyRoomSource
{
    internal string Name = string.Empty;
    internal int Layer;
    internal float X, Y;
    internal int Width, Height;
    internal bool Ready;
    internal bool Disabled = false;
    internal string Error = string.Empty;
    internal CartographyTileRun[] Runs = Array.Empty<CartographyTileRun>();
    internal Dictionary<int, CartographyRect> Ports = new();
}

internal sealed class CartographyConnectionSource
{
    internal string From = string.Empty, To = string.Empty;
    internal int FromPort = -1, ToPort = -1;
    internal bool Ambiguous = false;
}

internal sealed class CartographySource
{
    internal readonly Dictionary<string, CartographyRoomSource> Rooms = new(StringComparer.OrdinalIgnoreCase);
    internal readonly List<CartographyConnectionSource> Connections = new();

    internal CartographyDocument CreateDocument(string identity, string region)
    {
        CartographyDocument document = new() { Identity = identity, Region = region, Title = region };
        document.Layers.Add(new CartographyLayer { Id = "links", Name = "Connections / 连线" });
        for (int i = 0; i < 3; i++) document.Layers.Add(new CartographyLayer { Id = "rooms" + i, Name = "Rooms / 房间 L" + i });
        document.Layers.Add(new CartographyLayer { Id = "notes", Name = "Annotations / 标注" });
        AddMissingRooms(document);
        return document;
    }

    internal void AddMissingRooms(CartographyDocument document)
    {
        HashSet<string> known = new(document.Items.Where(item => item.Kind == CartographyItemKind.Room).Select(item => item.Room), StringComparer.OrdinalIgnoreCase);
        foreach (CartographyRoomSource room in Rooms.Values.OrderBy(room => room.Name, StringComparer.Ordinal))
        {
            if (known.Contains(room.Name)) continue;
            string layer = "rooms" + room.Layer;
            if (document.Layer(layer) == null) layer = document.Layers.First(candidate => candidate.Id != "links").Id;
            document.Items.Add(new CartographyItem { Id = "room:" + room.Name, Kind = CartographyItemKind.Room,
                LayerId = layer, Room = room.Name, X = room.X, Y = room.Y, Visible = !room.Disabled });
        }
    }
}
