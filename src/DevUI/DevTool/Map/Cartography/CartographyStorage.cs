using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace DryCycle.DevUI.DevTool.Map.Cartography;

internal static class CartographyStorage
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    internal static string Serialize(CartographyDocument document)
    {
        document.Validate();
        XElement root = new("cartography", A("version", CartographyDocument.FormatVersion), A("identity", document.Identity), A("region", document.Region),
            A("title", document.Title), A("font", document.FontFamily), A("background", document.Background), A("terrain", document.Terrain), A("water", document.Water),
            A("connections", document.Connections), A("roomNames", document.ShowRoomNames), A("links", document.ShowConnections), A("transparent", document.Transparent),
            A("cropSolid", document.CropSolid), A("scale", document.ExportScale), A("padding", document.Padding),
            new XElement("layers", document.Layers.Select(layer => new XElement("layer", A("id", layer.Id), A("name", layer.Name), A("visible", layer.Visible), A("locked", layer.Locked), A("opacity", layer.Opacity)))),
            new XElement("objects", document.Items.Select(item => new XElement("object", A("id", item.Id), A("kind", item.Kind), A("layer", item.LayerId), A("room", item.Room),
                A("x", item.X), A("y", item.Y), A("width", item.Width), A("height", item.Height), A("size", item.Size), A("stroke", item.Stroke), A("color", item.Color),
                A("visible", item.Visible), A("marker", item.Marker), new XElement("text", item.Text), CartographyRecord.Write("appearance", item.Appearance),
                new XElement("points", item.Points.Select(p => CartographyRecord.Write("point", p)))))),
            CartographyRecord.Write("options", document.Options), new XElement("palettes", document.Palettes.Select(p => CartographyRecord.Write("palette", p))));
        return root.ToString(SaveOptions.DisableFormatting);
    }

    internal static CartographyDocument Deserialize(string xml, string identity)
    {
        using StringReader text = new(xml);
        using XmlReader reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 * 1024 });
        XElement root = XElement.Load(reader);
        if (root.Name != "cartography" || Int(root, "version") < 1 || Int(root, "version") > CartographyDocument.FormatVersion)
            throw new InvalidDataException("Unsupported cartography document version; the original file was preserved.");
        if (Str(root, "identity") != identity) throw new InvalidDataException("This map belongs to a different source/campaign.");
        CartographyDocument document = new()
        {
            Identity = identity, Region = Str(root, "region"), Title = Str(root, "title"), FontFamily = Str(root, "font"),
            Background = UInt(root, "background"), Terrain = UInt(root, "terrain"), Water = UInt(root, "water"), Connections = UInt(root, "connections"),
            ShowRoomNames = Bool(root, "roomNames"), ShowConnections = Bool(root, "links"), Transparent = Bool(root, "transparent"), CropSolid = Bool(root, "cropSolid"),
            ExportScale = Float(root, "scale"), Padding = Int(root, "padding")
        };
        foreach (XElement layer in root.Element("layers")?.Elements("layer") ?? throw new InvalidDataException("Missing layers."))
            document.Layers.Add(new CartographyLayer { Id = Str(layer, "id"), Name = Str(layer, "name"), Visible = Bool(layer, "visible"), Locked = Bool(layer, "locked"), Opacity = Float(layer, "opacity") });
        foreach (XElement item in root.Element("objects")?.Elements("object") ?? throw new InvalidDataException("Missing objects."))
            document.Items.Add(new CartographyItem
            {
                Id = Str(item, "id"), Kind = (CartographyItemKind)Enum.Parse(typeof(CartographyItemKind), Str(item, "kind")),
                LayerId = Str(item, "layer"), Room = Str(item, "room"), X = Float(item, "x"), Y = Float(item, "y"), Width = Float(item, "width"), Height = Float(item, "height"),
                Size = Float(item, "size"), Stroke = Float(item, "stroke"), Color = UInt(item, "color"), Visible = Bool(item, "visible"),
                Marker = (CartographyMarker)Enum.Parse(typeof(CartographyMarker), Str(item, "marker")), Text = item.Element("text")?.Value ?? string.Empty
            });
        CartographyRecord.Read(root.Element("options"), document.Options);

        // BorderSize was historically hard-coded to 3 and was not exposed by the editor. That
        // produced a noticeably thinner silhouette than Cornifer. Treat that exact legacy default
        // as old presentation data and migrate it to the corrected 5 px outline; non-default values
        // remain untouched.
        if (Math.Abs(document.Options.BorderSize - 3f) < 0.001f)
            document.Options.BorderSize = 5f;

        // Cartography background is now an invariant rather than an author option. Older project
        // files may contain an opaque export/background setting; migrate them on load so reopening
        // an existing map behaves exactly like a new transparent composition.
        document.Transparent = true;
        document.Options.Canvas &= 0x00FFFFFF;

        foreach (XElement p in root.Element("palettes")?.Elements("palette") ?? Enumerable.Empty<XElement>())
        { CartographyPalette palette = new(); CartographyRecord.Read(p, palette); document.Palettes.Add(palette); }
        foreach (XElement element in root.Element("objects").Elements("object"))
        {
            CartographyItem item = document.Items.Find(i => i.Id == Str(element, "id"));
            CartographyRecord.Read(element.Element("appearance"), item.Appearance);
            // Room opacity was never meant to be a visual authoring dimension. Older project
            // states can nevertheless contain a reduced value, making a room mysteriously dark.
            // Normalize it on load so the next save permanently cleans that stale state.
            if (item.Kind == CartographyItemKind.Room)
                item.Appearance.Opacity = 1f;
            foreach (XElement p in element.Element("points")?.Elements("point") ?? Enumerable.Empty<XElement>())
            { CartographyPoint point = new(); CartographyRecord.Read(p, point); item.Points.Add(point); }
        }
        document.Validate();
        return document;
    }

    internal static string HashFile(string path)
    {
        if (!File.Exists(path)) return null;
        using FileStream stream = File.OpenRead(path);
        using SHA256 hash = SHA256.Create();
        return Hex(hash.ComputeHash(stream));
    }
    internal static string HashBytes(byte[] bytes) => Hash(bytes);
    internal static string HashText(string text) => Hash(Encoding.UTF8.GetBytes(text));

    private static string Hash(byte[] bytes)
    {
        using SHA256 hash = SHA256.Create();
        return Hex(hash.ComputeHash(bytes));
    }
    private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();

    // Same-directory temp + atomic replacement. No delete-then-move fallback can destroy the last
    // author file. A changed on-disk hash is a visible conflict, including files created after load.
    internal static void WriteAtomic(string path, string expectedHash, Action<Stream> write, Action<string> log = null)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { write(stream); stream.Flush(true); }
            if (!string.Equals(HashFile(path), expectedHash, StringComparison.Ordinal))
                throw new IOException("The file changed outside the editor. Save to a new path or resolve the conflict: " + path);
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
        }
        catch (Exception error)
        {
            log?.Invoke("Cartography write failed: " + error);
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                log?.Invoke("Cartography temporary-file cleanup completed: " + temporary);
            }
            catch (Exception cleanup) { log?.Invoke("Cartography temporary-file cleanup failed: " + cleanup); }
            throw;
        }
    }

    internal static string Save(string path, CartographyDocument document, string expectedHash, Action<string> log = null)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(Serialize(document));
        WriteAtomic(path, expectedHash, stream => stream.Write(bytes, 0, bytes.Length), log);
        return Hash(bytes);
    }

    private static XAttribute A(string name, object value) => new(name, Convert.ToString(value, Culture));
    private static string Str(XElement e, string name) => (string)e.Attribute(name) ?? throw new InvalidDataException("Missing " + name + ".");
    private static int Int(XElement e, string name) => int.Parse(Str(e, name), NumberStyles.Integer, Culture);
    private static uint UInt(XElement e, string name) => uint.Parse(Str(e, name), NumberStyles.Integer, Culture);
    private static float Float(XElement e, string name) => float.Parse(Str(e, name), NumberStyles.Float, Culture);
    private static bool Bool(XElement e, string name) => bool.Parse(Str(e, name));
}
