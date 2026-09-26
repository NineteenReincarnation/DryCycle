using System;
using System.Collections;
using System.IO;
using System.Linq;
using ImGuiNET;
using UnityEngine;
using Num = System.Numerics;

public static partial class MapRenderIsolationTests
{
    private static void ExerciseCorniferRoomCrossing()
    {
        object Make(string name) => Activator.CreateInstance(Core("Map.Cartography." + name), true);
        object Rect(float x, float y, float w, float h) => Activator.CreateInstance(Core("Map.Cartography.CartographyRect"), Flags, null, new object[] { x, y, w, h }, null);
        object source = Make("CartographySource");
        foreach (var data in new[] { ("A", 80, 50, 150f, 100f), ("B", 30, 40, 80f, 380f) })
        {
            object room = Make("CartographyRoomSource");
            Set(room, "Name", data.Item1); Set(room, "Ready", true);
            Set(room, "Width", data.Item2); Set(room, "Height", data.Item3);
            Set(room, "X", data.Item4); Set(room, "Y", data.Item5);
            ((IDictionary)Get(room, "Ports"))[0] = Rect(data.Item2 / 2f, data.Item3 / 2f, 0, 0);
            ((IDictionary)Get(source, "Rooms"))[data.Item1] = room;
        }
        object connection = Make("CartographyConnectionSource");
        Set(connection, "From", "A"); Set(connection, "To", "B"); Set(connection, "FromPort", 0); Set(connection, "ToPort", 0);
        ((IList)Get(source, "Connections")).Add(connection);
        object document = Invoke(source, "CreateDocument", "room-crossing", "TEST");
        Set(document, "ShowRoomNames", false); Set(document, "CropSolid", false); Set(document, "Terrain", 0xFFC53D0Fu); Set(document, "ExportScale", 1f);
        object options = Get(document, "Options"); Set(options, "Canvas", 0xFF6495EDu);
        Set(options, "ExportArea", true); Set(options, "AreaWidth", 360f); Set(options, "AreaHeight", 480f);
        object link = ((IEnumerable)Get(document, "Items")).Cast<object>().Single(i => Get(i, "Kind").ToString() == "Connection");
        Set(link, "Color", 0xFFFFFFFFu);
        Set(Get(link, "Appearance"), "Route", Enum.Parse(Core("Map.Cartography.CartographyRouteMode"), "Manual"));
        foreach (float y in new[] { 100f, 380f })
        {
            object point = Make("CartographyPoint"); Set(point, "X", 195f); Set(point, "Y", y);
            ((IList)Get(link, "Points")).Add(point);
        }
        object scene = Core("Map.Cartography.CartographySceneBuilder").GetMethod("Build", Flags).Invoke(null, new[] { document, source, null });
        object snapshot = Make("CartographyPresentation");
        Set(snapshot, "Identity", "room-crossing"); Set(snapshot, "Revision", 1L);
        Set(snapshot, "Document", document); Set(snapshot, "Source", source); Set(snapshot, "Scene", scene);
        Type view = Front("CartographyView");
        Invoke(view.GetField("Selection", Flags).GetValue(null), "Clear");
        foreach (string name in new[] { "fit", "snap", "dragging", "marquee" }) view.GetField(name, Flags).SetValue(null, false);
        view.GetField("selectedItem", Flags).SetValue(null, ""); view.GetField("selectedRoutePoint", Flags).SetValue(null, -1);
        view.GetField("routeGesture", Flags).SetValue(null, null); view.GetField("observed", Flags).SetValue(null, snapshot);
        view.GetField("zoom", Flags).SetValue(null, 1f); view.GetField("pan", Flags).SetValue(null, Num.Vector2.Zero);
        var io = ImGui.GetIO(); io.DisplaySize = new Num.Vector2(360, 480); io.AddMousePosEvent(-100, -100);
        Color32[] pixels = null;
        for (int frame = 0; frame < 4; frame++)
        {
            Front("WorldMapTextureFrame").GetMethod("Begin", Flags).Invoke(null, null);
            newFrame(); ImGui.NewFrame();
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Num.Vector2.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
            ImGui.SetNextWindowPos(Num.Vector2.Zero); ImGui.SetNextWindowSize(io.DisplaySize);
            ImGui.Begin("Room crossing", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings);
            view.GetMethod("Canvas", Flags).Invoke(null, new[] { snapshot });
            ImGui.End(); ImGui.PopStyleVar(2); ImGui.Render();
            if (frame == 3) pixels = RenderGui(360, 480, "cornifer-room-crossing-preview.png");
            Front("CartographyCanvasImages").GetMethod("UpdateMainThread", Flags).Invoke(null, null);
        }
        bool White(Color32 c) => c.r > 220 && c.g > 220 && c.b > 220;
        Check(Enumerable.Range(194, 5).Any(x => White(pixels[150 * 360 + x])), "The production canvas keeps the route core visible over room terrain.");
        Check(pixels[150 * 360 + 206].r > 180 && pixels[150 * 360 + 206].b < 30, "The wide outside border does not erase the room's interior terrain.");
        Check(pixels[260 * 360 + 206].r < 10 && pixels[260 * 360 + 206].a > 240, "The same path retains its wide black channel outside the room.");

        string path = Path.Combine(output, "cornifer-room-crossing-export.png");
        object hash = Core("Map.Cartography.CartographyStorage").GetMethod("HashFile", Flags).Invoke(null, new object[] { path });
        object format = Enum.Parse(Core("Map.Cartography.CartographyExportFormat"), "Png");
        Core("Map.Cartography.CartographyExporter").GetMethod("Export", Flags).Invoke(null, new[] { document, scene, path, format, hash, null });
        var image = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        Check(image.LoadImage(File.ReadAllBytes(path)), "The crossing fixture exports a valid PNG through the production exporter.");
        var exported = image.GetPixels32();
        Check(Enumerable.Range(130, 25).Any(y => White(exported[(479 - y) * 360 + 196])), "The exported connection remains visible inside the room, without editor guides.");
        Check(exported[(479 - 150) * 360 + 206].r > 180 && exported[(479 - 260) * 360 + 206].r < 10,
            "PNG shares the canvas's inside/outside shadow composition.");
        UnityEngine.Object.Destroy(image);
    }
}
