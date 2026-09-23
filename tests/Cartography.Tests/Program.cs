using System;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using DryCycle.DevUI.DevTool.Map.Cartography;

internal static partial class Program
{
    private static int assertions;
    private static string output;

    private static int Main(string[] args)
    {
        output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "drycycle-cartography-tests-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(output);
        try
        {
            Authoring(); Persistence(); Rendering(); IncrementalScene(); Parity(); RegionLoading(); RuntimeLoading();
            if (args.Length > 2 && args[1] == "--game") GameRegions(args[2]);
            else if (args.Length > 1) CorniferRegion(args[1]);
            Console.WriteLine("PASS: " + assertions + " assertions; production authoring / atomic persistence / PNG, SVG and layer export.");
            Console.WriteLine("Visual fixture: " + Path.Combine(output, "cartography-preview.png"));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Authoring()
    {
        CartographySource source = Source();
        CartographyDocument original = source.CreateDocument("world-source|Survivor", "SU");
        string before = CartographyStorage.Serialize(original);
        CartographyDocument moved = CartographyEditing.Apply(original, source, new CartographyCommand { Kind = CartographyCommandKind.Move, Ids = new[] { "room:SU_A01", "room:SU_A02" }, X = 24, Y = -12 });
        Check(CartographyStorage.Serialize(original) == before, "Commands must not mutate their before snapshot.");
        Check(moved.Items[0].X == original.Items[0].X + 24 && moved.Items[1].Y == original.Items[1].Y - 12, "Group delta must preserve relative placement.");
        Check(source.Rooms["SU_A01"].X == 0, "Cartography edits must not write to source placement.");
        CartographyLayer locked = moved.Layer(moved.Items[0].LayerId).Clone(); locked.Locked = true;
        moved = CartographyEditing.Apply(moved, source, new CartographyCommand { Kind = CartographyCommandKind.UpdateLayer, Layer = locked });
        float position = moved.Items[0].X;
        CartographyDocument blocked = CartographyEditing.Apply(moved, source, new CartographyCommand { Kind = CartographyCommandKind.Move, Ids = new[] { moved.Items[0].Id }, X = 90 });
        Check(blocked.Items[0].X == position, "Locked layer movement must be ignored.");
        CartographyItem edited = moved.Items[0].Clone(); edited.X = 900;
        Throws(() => CartographyEditing.Apply(moved, source, new CartographyCommand { Kind = CartographyCommandKind.UpdateItem, Item = edited }), "Inspector cannot bypass a layer lock.");
        CartographyDocument annotated = Annotated();
        int count = annotated.Items.Count;
        CartographyDocument withoutNotes = CartographyEditing.Apply(annotated, source, new CartographyCommand { Kind = CartographyCommandKind.DeleteLayer, LayerId = "notes" });
        Check(withoutNotes.Items.Count == count && withoutNotes.Items.All(item => item.LayerId != "notes"), "Removing a layer must migrate, not delete, its objects.");
        Throws(() => CartographyEditing.Apply(annotated, source, new CartographyCommand { Kind = CartographyCommandKind.DeleteLayer, LayerId = "links" }), "The source connections layer must remain addressable.");
        CartographyItem invalid = annotated.Items.First(item => item.Kind == CartographyItemKind.Text).Clone(); invalid.Size = float.NaN;
        string stable = CartographyStorage.Serialize(annotated);
        Throws(() => CartographyEditing.Apply(annotated, source, new CartographyCommand { Kind = CartographyCommandKind.UpdateItem, Item = invalid }), "NaN must be rejected before mutation.");
        Check(CartographyStorage.Serialize(annotated) == stable, "A failed edit must leave author data intact.");
        CartographyDocument aligned = CartographyEditing.Apply(annotated, source, new CartographyCommand { Kind = CartographyCommandKind.Align, Ids = new[] { "room:SU_A01", "room:SU_A02" }, Integer = (int)CartographyAlignment.Left });
        Check(CartographySceneBuilder.Bounds(aligned.Items[0], source).X == CartographySceneBuilder.Bounds(aligned.Items[1], source).X, "Alignment uses room edges, not center points.");
        CartographyDocument duplicated = CartographyEditing.Apply(annotated, source, new CartographyCommand { Kind = CartographyCommandKind.Duplicate, Ids = annotated.Items.Select(item => item.Id).ToArray() });
        Check(duplicated.Items.Count(item => item.Kind == CartographyItemKind.Room) == 2, "Duplication cannot create duplicate source-room identities.");
        Check(duplicated.Items.Count > annotated.Items.Count, "Annotations can be duplicated.");
        source.Rooms.Add("SU_A03", new CartographyRoomSource { Name = "SU_A03", Layer = 1, Ready = true, Width = 20, Height = 10 });
        CartographyDocument synchronized = CartographyEditing.Apply(aligned, source, new CartographyCommand { Kind = CartographyCommandKind.AddMissingRooms });
        Check(synchronized.Items.Count == aligned.Items.Count + 1 && synchronized.Items[0].X == aligned.Items[0].X, "Source synchronization preserves authored layout.");
    }

    private static void Persistence()
    {
        CartographyDocument document = Annotated();
        string xml = CartographyStorage.Serialize(document);
        CartographyDocument loaded = CartographyStorage.Deserialize(xml, document.Identity);
        Check(CartographyStorage.Serialize(loaded) == xml, "All editable properties and CJK text round trip.");
        Check(!xml.Contains("<Runs") && !xml.Contains("terrain is not ready"), "Derived source/cache data must stay out of the author file.");
        Throws(() => CartographyStorage.Deserialize(xml, "other-source|Survivor"), "Wrong-source import must be rejected.");
        Throws(() => CartographyStorage.Deserialize(xml.Replace("version=\"" + CartographyDocument.FormatVersion + "\"", "version=\"999\""), document.Identity), "Future documents cannot be silently downgraded.");
        Throws(() => CartographyStorage.Deserialize("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///C:/Windows/win.ini'>]>" + xml, document.Identity), "External entities must be refused.");
        string path = Path.Combine(output, "author-" + Guid.NewGuid().ToString("N") + ".xml");
        string firstHash = CartographyStorage.Save(path, document, null);
        byte[] first = File.ReadAllBytes(path);
        document.Title = "Edited title";
        string secondHash = CartographyStorage.Save(path, document, firstHash);
        Check(File.ReadAllBytes(path + ".bak").SequenceEqual(first), "Replacement retains the last valid author file.");
        string external = xml.Replace("title=\"SU\"", "title=\"External edit\"");
        File.WriteAllText(path, external, new UTF8Encoding(false));
        Throws(() => CartographyStorage.Save(path, document, secondHash), "External edits must be a visible conflict.");
        Check(File.ReadAllText(path) == external, "Conflicts must not overwrite external edits.");
        string externalHash = CartographyStorage.HashFile(path);
        Throws(() => CartographyStorage.WriteAtomic(path, externalHash, stream => { stream.WriteByte(1); throw new IOException("Injected writer failure"); }), "A writer failure must propagate.");
        Check(File.ReadAllText(path) == external, "A failed write must preserve the last valid author data.");
        Check(!Directory.GetFiles(output, "*.tmp").Any(), "Failed writes must clean their own temporary file.");
        Throws(() => CartographyStorage.Save(path, document, null), "Save As cannot clobber an existing project.");
    }

    private static void Rendering()
    {
        CartographySource source = Source();
        CartographyDocument document = Annotated();
        CartographyScene scene = CartographySceneBuilder.Build(document, source);
        Check(scene.Errors.Length == 0 && scene.Warnings.Length == 0, "Ready terrain and exact ports are exportable.");
        CartographySceneNode connection = scene.Nodes.First(node => node.FromId != null);
        Check(Math.Abs(connection.FromX - 28.5f) < .01f, "Room-local source port X is translated exactly once.");
        Check(Math.Abs(connection.FromY + 1.5f) < .01f, "Room-local source port Y flips only once.");
        CartographyDocument missingPort = document.Clone();
        missingPort.Items.First(item => item.Kind == CartographyItemKind.Connection).Appearance.ToPort = 99;
        CartographyScene unresolved = CartographySceneBuilder.Build(missingPort, source);
        CartographySceneNode unresolvedLink = unresolved.Nodes.First(node => node.FromId != null);
        Check(unresolved.Warnings.Length == 1 && unresolvedLink.Ambiguous && unresolvedLink.Primitives.Any(primitive => primitive.Dashed), "Unresolved authored endpoints must remain visibly approximate (outlines and endpoint markers are separate primitives).");
        source.Rooms["SU_A02"].Ready = false;
        CartographyScene incomplete = CartographySceneBuilder.Build(document, source);
        Check(incomplete.Errors.Length == 1, "Missing visible terrain is reported.");
        Throws(() => CartographyExporter.Export(document, incomplete, Path.Combine(output, "must-not-exist.png"), CartographyExportFormat.Png, null), "Incomplete maps cannot be exported as complete.");
        CartographyDocument hidden = document.Clone(); hidden.Items.Find(item => item.Room == "SU_A02").Visible = false;
        Check(CartographySceneBuilder.Build(hidden, source).Errors.Length == 0, "Hidden missing rooms do not block export.");
        source.Rooms["SU_A02"].Ready = true;
        CartographyExporter.Dimensions(document, scene, out int width, out int height);
        string preview = Path.Combine(output, "cartography-preview.png");
        CartographyExporter.Export(document, scene, preview, CartographyExportFormat.Png, CartographyStorage.HashFile(preview));
        using (Bitmap image = new(preview))
        {
            Check(image.Width == width && image.Height == height, "PNG dimensions match the planned canvas.");
            Check(image.GetPixel(0, 0).A == 255, "Opaque export fills the background.");
        }
        document.Transparent = true;
        string transparent = Path.Combine(output, "cartography-transparent.png");
        CartographyExporter.Export(document, scene, transparent, CartographyExportFormat.Png, CartographyStorage.HashFile(transparent));
        using (Bitmap image = new(transparent))
        {
            Check(image.GetPixel(0, 0).A == 0, "Transparent PNG retains alpha in its padding.");
            bool ink = false;
            for (int y = 0; y < image.Height && !ink; y += 3)
                for (int x = 0; x < image.Width; x += 3) if (image.GetPixel(x, y).A > 0) { ink = true; break; }
            Check(ink, "Transparent output contains rendered content.");
        }
        string svg = Path.Combine(output, "cartography.svg");
        CartographyExporter.Export(document, scene, svg, CartographyExportFormat.Svg, CartographyStorage.HashFile(svg));
        XElement vector = XElement.Load(svg);
        Check(vector.Name.LocalName == "svg" && vector.Value.Contains("庇护所 <A> & 路线"), "SVG is valid XML and escapes Unicode annotation text.");
        Check(vector.Descendants().Count(e => e.Name.LocalName == "g") == document.Layers.Count, "SVG preserves named editable layer groups.");
        string zipPath = Path.Combine(output, "cartography-layers.zip");
        CartographyExporter.Export(document, scene, zipPath, CartographyExportFormat.LayerPngZip, CartographyStorage.HashFile(zipPath));
        using (FileStream file = File.OpenRead(zipPath))
        using (ZipArchive zip = new(file, ZipArchiveMode.Read))
        {
            Check(zip.Entries.Any(entry => entry.FullName == "layers.txt"), "Layer archive explains stacking order.");
            foreach (ZipArchiveEntry entry in zip.Entries.Where(entry => entry.FullName.EndsWith(".png")))
            using (Stream png = entry.Open())
            using (Bitmap image = new(png)) Check(image.Width == width && image.Height == height && image.GetPixel(0, 0).A == 0, "Layer PNGs share dimensions and transparent origin.");
        }
        CartographyScene oversized = new() { Bounds = new CartographyRect(0, 0, 1000000, 1000000) };
        Throws(() => CartographyExporter.Dimensions(document, oversized, out _, out _), "Unbounded output must be refused before bitmap allocation.");
        Throws(() => CartographyExporter.Export(document, scene, Path.Combine(output, "author.xml"), CartographyExportFormat.Png, null), "Image exports cannot use author-file extensions.");
    }

    private static CartographyDocument Annotated()
    {
        CartographyDocument document = Source().CreateDocument("world-source|Survivor", "SU");
        document.ExportScale = 3; document.Padding = 32;
        document.Items.Add(new CartographyItem { Id = "title", Kind = CartographyItemKind.Text, X = -80, Y = -150, Size = 26, Text = "OUTSKIRTS / 郊区\n庇护所 <A> & 路线" });
        document.Items.Add(new CartographyItem { Id = "shelter", Kind = CartographyItemKind.Marker, Marker = CartographyMarker.Shelter, X = -50, Y = 80, Size = 16 });
        document.Items.Add(new CartographyItem { Id = "gate", Kind = CartographyItemKind.Marker, Marker = CartographyMarker.Gate, X = 220, Y = 110, Size = 16 });
        document.Items.Add(new CartographyItem { Id = "caption", Kind = CartographyItemKind.Text, X = -20, Y = 75, Text = "Shelter / 庇护所", Size = 15 });
        return document;
    }

    private static void IncrementalScene()
    {
        CartographySource source = Source();
        CartographyDocument document = Annotated();
        CartographySceneCache cache = new();
        CartographyScene first = CartographySceneBuilder.Build(document, source, cache);
        CartographyScene idle = CartographySceneBuilder.Build(document, source, cache);
        Check(ReferenceEquals(first.Nodes.First(node => node.Id == "room:SU_A01"), idle.Nodes.First(node => node.Id == "room:SU_A01")), "Unchanged terrain must reuse retained visual nodes.");
        CartographyDocument moved = CartographyEditing.Apply(document, source, new CartographyCommand { Kind = CartographyCommandKind.Move, Ids = new[] { "room:SU_A01" }, X = 60 });
        CartographyScene afterMove = CartographySceneBuilder.Build(moved, source, cache);
        Check(!ReferenceEquals(first.Nodes.First(node => node.Id == "room:SU_A01"), afterMove.Nodes.First(node => node.Id == "room:SU_A01")), "Moved room geometry has a new placement.");
        Check(ReferenceEquals(first.Nodes.First(node => node.Id == "room:SU_A02"), afterMove.Nodes.First(node => node.Id == "room:SU_A02")), "Moving one room must not rebuild other room geometry.");
        Check(ReferenceEquals(first.Nodes.First(node => node.Id == "title"), afterMove.Nodes.First(node => node.Id == "title")), "Moving a room must not re-layout annotations.");
        Check(first.Nodes.First(node => node.FromId != null).FromX + 60 == afterMove.Nodes.First(node => node.FromId != null).FromX, "Connections must follow the new port position.");
        Check(first.Nodes.First(node => node.Id == "room:SU_A01").Bounds.X != afterMove.Nodes.First(node => node.Id == "room:SU_A01").Bounds.X, "Frozen scenes must remain stable for concurrent exports.");
        CartographyDocument restyled = moved.Clone(); restyled.Terrain = 0xFF778899;
        CartographyScene recolored = CartographySceneBuilder.Build(restyled, source, cache);
        Check(!ReferenceEquals(afterMove.Nodes.First(node => node.Id == "room:SU_A02"), recolored.Nodes.First(node => node.Id == "room:SU_A02")), "Terrain color changes must invalidate cached room visuals.");
        Check(ReferenceEquals(afterMove.Nodes.First(node => node.Id == "title"), recolored.Nodes.First(node => node.Id == "title")), "Terrain recoloring must preserve annotation nodes.");
    }

    private static CartographySource Source()
    {
        CartographySource source = new();
        foreach (string name in new[] { "SU_A01", "SU_A02" })
        {
            bool first = name == "SU_A01";
            CartographyRoomSource room = new() { Name = name, Ready = true, Width = first ? 20 : 28, Height = first ? 14 : 18, X = first ? 0 : 180, Y = first ? 0 : 40, Layer = first ? 0 : 1 };
            room.Runs = Enumerable.Range(0, room.Height).Select(y => new CartographyTileRun(0, y, room.Width, y == 0 || y == room.Height - 1 ? 2 : 0, !first && y < 5)).ToArray();
            room.Ports[0] = new CartographyRect(first ? 19.5f : .5f, first ? 7.5f : 9.5f, 0, 0);
            source.Rooms[name] = room;
        }
        source.Connections.Add(new CartographyConnectionSource { From = "SU_A01", To = "SU_A02", FromPort = 0, ToPort = 0 });
        return source;
    }

    private static void Check(bool value, string message)
    { assertions++; if (!value) throw new InvalidOperationException(message); }
    private static void Throws(Action action, string message)
    {
        assertions++;
        try { action(); } catch { return; }
        throw new InvalidOperationException(message);
    }
}
