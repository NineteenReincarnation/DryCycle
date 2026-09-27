using System;
using System.Collections.Generic;
using System.Linq;

namespace DryCycle.DevUI.DevTool.Map.Cartography;

// One terrain silhouette owns both ordinary tiles and authored curves. Cropping must happen after
// their union; painting curve bodies over an already-cropped room restores invisible solid mass.
internal static class CartographyRoomRasterizer
{
    internal static CartographyRaster Render(CartographyDocument document, CartographyItem item, CartographyRoomSource room)
    {
        int w = room.Width, h = room.Height;
        byte[] kinds = new byte[w * h];
        bool[] water = new bool[w * h];
        foreach (CartographyTileRun run in room.Runs)
            for (int x = run.X; x < run.X + run.Length; x++)
            {
                int i = (h - 1 - run.Y) * w + x;
                kinds[i] = (byte)run.Kind;
                water[i] = run.Water;
            }

        EditorMapPolylineSnapshot[] polygons = room.CurvedTerrainCurves ?? Array.Empty<EditorMapPolylineSnapshot>();
        bool hasCurves = polygons.Any(p => p?.Closed == true && p.Points?.Length >= 3);
        // Preserve the original tile cutout exactly for rooms without continuous terrain. Curved
        // rooms use the map's native 3 pixels/tile so the cut follows the real surface, not its AABB.
        int scale = hasCurves ? 3 : 1;
        int width = w * scale, height = h * scale;
        byte[] curveKinds = new byte[width * height];
        if (hasCurves) RasterizePolygons(polygons, curveKinds, width, height, scale);

        bool[] solid = new bool[width * height];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int i = y * width + x, kind = kinds[(y / scale) * w + x / scale];
            // Exits/transports are authored openings and retain precedence over curved terrain.
            if (kind >= 4) curveKinds[i] = 0;
            solid[i] = kind == 2 || curveKinds[i] != 0;
        }

        CartographyAppearance appearance = item.Appearance;
        bool[] keep = document.CropSolid
            ? KeepSolidEdges(solid, width, height, scale, appearance)
            : null;
        CartographyPalette palette = document.Palettes.Find(p => p.Name == appearance.Subregion);
        uint background = appearance.OverridePalette ? appearance.Background : palette?.Background ?? document.Terrain;
        uint wall = appearance.OverridePalette ? appearance.Wall : palette?.Wall ?? document.Options.Wall;
        uint waterColor = appearance.Acid ? appearance.AcidColor : appearance.OverridePalette ? appearance.Water : palette?.Water ?? document.Water;
        int outputWidth = w * 3, outputHeight = h * 3, repeat = 3 / scale;
        uint[] pixels = new uint[outputWidth * outputHeight];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int i = y * width + x, tileX = x / scale, tileY = y / scale, tile = tileY * w + tileX;
            if (solid[i] && keep != null && !keep[i]) continue;
            int kind = kinds[tile];
            bool curved = curveKinds[i] != 0;
            bool solidMaterial = kind == 2 || curved &&
                (curveKinds[i] == (int)EditorMapGeometryKind.Solid + 1 || curveKinds[i] == (int)EditorMapGeometryKind.CurvedSlope + 1);
            uint color = curved || kind == 2 ? wall :
                kind == 1 && document.Options.TileWalls ? CartographyDrawing.Blend(wall, background, .75f) :
                kind == 3 ? CartographyDrawing.Blend(wall, background, .35f) : background;
            bool wet = appearance.WaterLevel == -2 ? water[tile] : appearance.WaterLevel >= 0 && h - 1 - tileY <= appearance.WaterLevel;
            if (wet && (!solidMaterial || appearance.WaterFront)) color = CartographyDrawing.Blend(color, waterColor, document.Options.WaterOpacity);
            if (appearance.Deathpit && tileY >= h - 5 && !solid[(height - 1) * width + x])
                color = CartographyDrawing.Blend(wall, color, (h - tileY - .5f) / 5f);
            if (kind >= 4 && document.Options.MarkShortcuts && (!document.Options.ExitsOnly || kind == 4)) color = 0xFFFF2020;
            else if (kind >= 4 && !document.Options.ShortcutBackground) color = 0xFFFFFFFF;
            for (int dy = 0; dy < repeat; dy++) for (int dx = 0; dx < repeat; dx++)
                pixels[(y * repeat + dy) * outputWidth + x * repeat + dx] = color;
        }
        return new CartographyRaster(outputWidth, outputHeight, pixels);
    }

    private static void RasterizePolygons(EditorMapPolylineSnapshot[] polygons, byte[] materials, int width, int height, int scale)
    {
        List<float> crossings = new(8);
        foreach (EditorMapPolylineSnapshot polygon in polygons)
        {
            if (polygon?.Closed != true || polygon.Points?.Length < 3) continue;
            EditorMapPointSnapshot[] points = polygon.Points;
            float minY = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < points.Length; i++)
            {
                float y = height - points[i].Y * scale;
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
            int first = Math.Max(0, (int)Math.Ceiling(minY - .5f));
            int last = Math.Min(height - 1, (int)Math.Floor(maxY - .5f));
            for (int y = first; y <= last; y++)
            {
                crossings.Clear();
                float sample = y + .5f;
                for (int i = 0, previous = points.Length - 1; i < points.Length; previous = i++)
                {
                    EditorMapPointSnapshot a = points[previous], b = points[i];
                    float ay = height - a.Y * scale, by = height - b.Y * scale;
                    // Half-open edge intervals make adjacent compiler quads meet without cracks.
                    if ((ay <= sample && by > sample) || (by <= sample && ay > sample))
                        crossings.Add((a.X + (b.X - a.X) * ((sample - ay) / (by - ay))) * scale);
                }
                crossings.Sort();
                for (int pair = 0; pair + 1 < crossings.Count; pair += 2)
                {
                    int left = Math.Max(0, (int)Math.Ceiling(crossings[pair] - .5f));
                    int right = Math.Min(width - 1, (int)Math.Ceiling(crossings[pair + 1] - .5f) - 1);
                    for (int x = left; x <= right; x++) materials[y * width + x] = (byte)((int)polygon.Kind + 1);
                }
            }
        }
    }

    private static bool[] KeepSolidEdges(bool[] solid, int width, int height, int scale, CartographyAppearance appearance)
    {
        bool[] keep = new bool[solid.Length];
        if (appearance.CutAllSolid) return keep;
        bool[] exterior = new bool[solid.Length];
        Queue<int> queue = new();
        void Seed(int i)
        {
            if (!solid[i] || exterior[i]) return;
            exterior[i] = true; queue.Enqueue(i);
        }
        for (int x = 0; x < width; x++) { Seed(x); Seed((height - 1) * width + x); }
        for (int y = 0; y < height; y++) { Seed(y * width); Seed(y * width + width - 1); }
        while (queue.Count > 0)
        {
            int i = queue.Dequeue(), x = i % width, y = i / width;
            if (x > 0) Seed(i - 1); if (x + 1 < width) Seed(i + 1);
            if (y > 0) Seed(i - width); if (y + 1 < height) Seed(i + width);
        }

        // Keep one tile of wall around playable air, including at curve/tile joins. Interior solid
        // islands remain intact, just as ordinary wall islands do in Cornifer's cutout.
        byte[] distance = new byte[solid.Length];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int i = y * width + x;
            if (!solid[i]) continue;
            if (!exterior[i]) { keep[i] = true; continue; }
            if (x > 0 && !solid[i - 1] || x + 1 < width && !solid[i + 1] ||
                y > 0 && !solid[i - width] || y + 1 < height && !solid[i + width])
            { distance[i] = 1; keep[i] = true; queue.Enqueue(i); }
        }
        void Grow(int i, byte depth)
        {
            if (!solid[i] || distance[i] != 0) return;
            distance[i] = depth; keep[i] = true; queue.Enqueue(i);
        }
        while (queue.Count > 0)
        {
            int i = queue.Dequeue(), x = i % width, y = i / width;
            if (distance[i] >= scale) continue;
            byte next = (byte)(distance[i] + 1);
            if (x > 0) Grow(i - 1, next); if (x + 1 < width) Grow(i + 1, next);
            if (y > 0) Grow(i - width, next); if (y + 1 < height) Grow(i + width, next);
        }
        if (appearance.BetterCutout) KeepBraces(solid, keep, width, height, 20 * scale);
        return keep;
    }

    private static void KeepBraces(bool[] solid, bool[] keep, int width, int height, int range)
    {
        // Preserve narrow walls with air on opposite sides. Four linear scans replace per-pixel
        // 20-tile ray searches; cost stays proportional to raster area for large curved rooms.
        int[] distance = new int[Math.Max(width, height)];
        for (int axis = 0; axis < 2; axis++)
        {
            int rows = axis == 0 ? height : width, length = axis == 0 ? width : height;
            for (int row = 0; row < rows; row++)
            {
                int lastAir = -range - 1;
                for (int p = 0; p < length; p++)
                {
                    int i = axis == 0 ? row * width + p : p * width + row;
                    if (!solid[i]) lastAir = p;
                    distance[p] = p - lastAir;
                }
                lastAir = length + range;
                for (int p = length - 1; p >= 0; p--)
                {
                    int i = axis == 0 ? row * width + p : p * width + row;
                    if (!solid[i]) lastAir = p;
                    else if (distance[p] <= range && lastAir - p <= range) keep[i] = true;
                }
            }
        }
    }
}
