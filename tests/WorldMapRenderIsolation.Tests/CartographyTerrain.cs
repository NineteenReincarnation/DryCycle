using System;
using System.Collections;
using System.IO;
using System.Linq;
using UnityEngine;

public static partial class MapRenderIsolationTests
{
    private static void ExerciseCartographyTerrain(bool baseline)
    {
        object NewMap(string name) => Activator.CreateInstance(Core("Map.Cartography." + name), true);
        object room = NewMap("CartographyRoomSource");
        Set(room, "Name", "B5_POTDB01");
        string folder = Path.Combine(game, "RainWorld_Data/StreamingAssets/mods/Ancient Site/world/b5-rooms");
        Set(room, "Settings", File.ReadAllText(Path.Combine(folder, "b5_potdb01_settings.txt")));
        Core("Map.Cartography.CartographyRegionLoader").GetMethod("DecodeRoom", Flags).Invoke(null,
            new[] { room, File.ReadAllText(Path.Combine(folder, "b5_potdb01.txt")) });
        Check(((Array)Get(room, "CurvedTerrainCurves")).Length > 0, "The actual B5_POTDB01 settings compile into authored curve geometry.");
        object document = NewMap("CartographyDocument"), item = NewMap("CartographyItem");
        Set(document, "Terrain", 0xFFBDBD89u);
        object Render(object r) => Core("Map.Cartography.CartographyDrawing").GetMethod("Room", Flags)
            .Invoke(null, new[] { document, item, r });
        var timer = System.Diagnostics.Stopwatch.StartNew();
        object raster = Render(room);
        timer.Stop();
        results.Add("B5_POTDB01 terrain render: " + timer.Elapsed.TotalMilliseconds.ToString("F2") + " ms.");
        string suffix = baseline ? "before" : "after";
        SaveTerrainRaster(raster, "B5_POTDB01-" + suffix + ".png");
        // The unchanged tile-only footprint is also a useful reference for the user's room.
        object curves = Get(room, "CurvedTerrainCurves");
        Set(room, "CurvedTerrainCurves", Array.CreateInstance(Core("Map.EditorMapPolylineSnapshot"), 0));
        SaveTerrainRaster(Render(room), "B5_POTDB01-tiles.png");
        Set(room, "CurvedTerrainCurves", curves);
        if (baseline) return;

        // Closed authored polygons, using real production snapshots. No game physics or spline
        // geometry is reimplemented by the fixture.
        object Fixture(string material)
        {
            object fixture = NewMap("CartographyRoomSource");
            Set(fixture, "Name", material); Set(fixture, "Width", 20); Set(fixture, "Height", 16);
            Type pointType = Core("Map.EditorMapPointSnapshot");
            var coordinates = new[] { (0f, 7f), (10f, 10f), (20f, 7f), (20f, 0f), (0f, 0f) };
            Array points = Array.CreateInstance(pointType, coordinates.Length);
            for (int i = 0; i < coordinates.Length; i++)
                points.SetValue(Activator.CreateInstance(pointType, new object[] { coordinates[i].Item1, coordinates[i].Item2 }), i);
            Type curveType = Core("Map.EditorMapPolylineSnapshot"); object curve = Activator.CreateInstance(curveType);
            curveType.GetProperty("Closed").SetValue(curve, true);
            curveType.GetProperty("Kind").SetValue(curve, Enum.Parse(Core("Map.EditorMapGeometryKind"), material));
            curveType.GetProperty("Points").SetValue(curve, points);
            Array closed = Array.CreateInstance(curveType, 1); closed.SetValue(curve, 0);
            Set(fixture, "CurvedTerrainCurves", closed);
            return fixture;
        }
        Set(Get(document, "Options"), "Borders", false);
        object appearance = Get(item, "Appearance");
        Set(appearance, "BetterCutout", false);
        foreach (string kind in new[] { "Solid", "Structure", "QuicksandBody", "QuicksandMaterial" })
        {
            object fixture = Fixture(kind);
            object cropped = Render(fixture); uint[] p = (uint[])Get(cropped, "Pixels");
            int width = (int)Get(cropped, "Width"), height = (int)Get(cropped, "Height");
            Check((p[(height - 3) * width + width / 2] >> 24) == 0, kind + " shares exterior-wall cropping; the deep base is transparent.");
            Check(p.Any(v => v == 0xFF050505u), kind + " keeps its actual surface edge visible.");
            Set(document, "CropSolid", false);
            Check(((uint[])Get(Render(fixture), "Pixels"))[(height - 3) * width + width / 2] == 0xFF050505u,
                kind + " preserves the full body when solid cropping is disabled.");
            Set(document, "CropSolid", true); Set(appearance, "CutAllSolid", true);
            Check(!((uint[])Get(Render(fixture), "Pixels")).Any(v => v == 0xFF050505u), kind + " honors CutAllSolid.");
            Set(appearance, "CutAllSolid", false);
        }
        // Load custom data through the same path used by offline Cartography, without activating
        // runtime hooks, terrain physics or a room session.
        object custom = NewMap("CartographyRoomSource");
        Set(custom, "Width", 30); Set(custom, "Height", 20);
        Set(custom, "Settings", "PlacedObjects: QuicksandZone><40><180><V2~100~0.75~0.55~0.62~0.18~0^0~180^0~0^100~180^-100~~0.3|0.7");
        Core("Map.Cartography.CartographyRegionLoader").GetMethod("DecodeCurvedTerrain", Flags).Invoke(null, new[] { custom });
        Check(((Array)Get(custom, "CurvedTerrainCurves")).Length > 0, "DryCycle QuicksandZone is decoded by its real data class without runtime hooks.");
        Check(((IEnumerable)Get(custom, "CurvedTerrainCurves")).Cast<object>().Any(c =>
            c.GetType().GetProperty("Kind").GetValue(c).ToString() == "QuicksandMaterial"),
            "Custom quicksand retains its authored material intervals.");

        object floor = NewMap("CartographyRoomSource");
        Set(floor, "Width", 20); Set(floor, "Height", 40);
        Set(floor, "Settings", "PlacedObjects: TerrainHandle><0><200><-40~0~40~0~400, TerrainHandle><400><200><-40~0~40~0~400");
        Core("Map.Cartography.CartographyRegionLoader").GetMethod("DecodeCurvedTerrain", Flags).Invoke(null, new[] { floor });
        Set(document, "CropSolid", false);
        uint[] floorPixels = (uint[])Get(Render(floor), "Pixels");
        Check(floorPixels[(40 - 5) * 3 * 60 + 30] == 0xFF050505u && floorPixels[(40 - 15) * 3 * 60 + 30] == 0xFFBDBD89u,
            "TerrainHandle fills below its front surface; the raised background contour does not cover playable air.");
    }

    private static void SaveTerrainRaster(object raster, string name)
    {
        int w = (int)Get(raster, "Width"), h = (int)Get(raster, "Height"); uint[] source = (uint[])Get(raster, "Pixels");
        var pixels = new Color32[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        { uint p = source[y * w + x]; pixels[(h - 1 - y) * w + x] = new Color32((byte)(p >> 16), (byte)(p >> 8), (byte)p, (byte)(p >> 24)); }
        var texture = new Texture2D(w, h, TextureFormat.RGBA32, false); texture.SetPixels32(pixels); texture.Apply();
        File.WriteAllBytes(Path.Combine(output, name), texture.EncodeToPNG()); UnityEngine.Object.Destroy(texture);
    }
}
