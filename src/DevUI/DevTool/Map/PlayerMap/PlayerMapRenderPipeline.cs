using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using DevInterface;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

public readonly struct PlayerMapPreviewRun
{
    public PlayerMapPreviewRun(int x, int y, int length, Color32 color)
    {
        X = x;
        Y = y;
        Length = length;
        Color = color;
    }

    public int X { get; }
    public int Y { get; }
    public int Length { get; }
    public Color32 Color { get; }
}

public sealed class PlayerMapRenderPreview
{
    public static readonly PlayerMapRenderPreview Empty = new();

    public bool Available { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public PlayerMapPreviewRun[] Runs { get; init; } = Array.Empty<PlayerMapPreviewRun>();
}

public sealed class PlayerMapRenderReport
{
    public static readonly PlayerMapRenderReport Empty = new();

    public bool Attempted { get; init; }
    public bool Success { get; init; }
    public bool Exported { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int IncludedRooms { get; init; }
    public string OutputPath { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string[] Errors { get; init; } = Array.Empty<string>();
    public string[] Warnings { get; init; } = Array.Empty<string>();

    internal static PlayerMapRenderReport Failure(string message, params string[] errors) => new()
    {
        Attempted = true,
        Success = false,
        Message = message ?? "Render failed.",
        Errors = errors ?? Array.Empty<string>()
    };
}

internal sealed class PlayerMapRenderRoom
{
    internal int RoomIndex;
    internal string Name = string.Empty;
    internal int Layer;
    internal Vector2 CanonicalPosition;
    internal RoomMapBake Bake;
    internal int PixelX;
    internal int PixelYInLayer;
}

internal sealed class PlayerMapRenderPlan
{
    internal int Width;
    internal int LayerHeight;
    internal int Height;
    internal float CanonMinX;
    internal float CanonMinY;
    internal readonly List<PlayerMapRenderRoom> Rooms = new();
    internal readonly List<PlayerMapDefMaterialState> DefaultMaterials = new();
    internal readonly List<string> Warnings = new();
}

internal sealed class PlayerMapRenderOutcome
{
    internal PlayerMapRenderReport Report = PlayerMapRenderReport.Empty;
    internal PlayerMapRenderPreview Preview = PlayerMapRenderPreview.Empty;
}

internal static class PlayerMapRenderPipeline
{
    private static readonly Color32 Background = new(0, 255, 0, 255);
    private static readonly Color32 SolidColor = new(0, 0, 0, 255);
    private static readonly Color32 BrightDry = new(255, 0, 0, 255);
    private static readonly Color32 BrightWet = new(255, 0, 255, 255);
    private static readonly Color32 StructureDry = new(153, 0, 0, 255);
    private static readonly Color32 StructureWet = new(153, 0, 255, 255);
    private const int MaxDimension = 16384;
    private const long MaxPixels = 160_000_000L;

    internal static PlayerMapRenderOutcome Build(MapPage page, PlayerMapSessionState state, bool export)
    {
        PlayerMapRenderOutcome outcome = new();
        if (!TryPlan(page, state, out PlayerMapRenderPlan plan, out string[] errors))
        {
            outcome.Report = new PlayerMapRenderReport
            {
                Attempted = true,
                Success = false,
                Exported = false,
                Message = "Player Map preflight failed. No output was written.",
                Errors = errors
            };
            return outcome;
        }

        try
        {
            Color32[] pixels = Compose(plan);
            PlayerMapRenderPreview preview = BuildPreview(plan.Width, plan.Height, pixels);
            outcome.Preview = preview;

            string outputPath = string.Empty;
            if (export)
                outputPath = Export(page, plan, pixels);

            outcome.Report = new PlayerMapRenderReport
            {
                Attempted = true,
                Success = true,
                Exported = export,
                Width = plan.Width,
                Height = plan.Height,
                IncludedRooms = plan.Rooms.Count,
                OutputPath = outputPath,
                Message = export ? "Player Map rendered and exported." : "Player Map preview built from final output pixels.",
                Warnings = plan.Warnings.ToArray()
            };
            return outcome;
        }
        catch (Exception exception)
        {
            outcome.Report = new PlayerMapRenderReport
            {
                Attempted = true,
                Success = false,
                Exported = false,
                Width = plan.Width,
                Height = plan.Height,
                IncludedRooms = plan.Rooms.Count,
                Message = "Player Map render failed. Existing output files were preserved.",
                Errors = new[] { exception.Message },
                Warnings = plan.Warnings.ToArray()
            };
            return outcome;
        }
    }

    private static bool TryPlan(MapPage page, PlayerMapSessionState state, out PlayerMapRenderPlan plan, out string[] errors)
    {
        plan = new PlayerMapRenderPlan();
        List<string> failures = new();
        if (page?.world == null || state == null)
        {
            errors = new[] { "MapPage or Player Map state is unavailable." };
            return false;
        }

        HashSet<string> disabled = new(StringComparer.OrdinalIgnoreCase);
        if (page.world.DisabledMapRooms != null)
            for (int i = 0; i < page.world.DisabledMapRooms.Count; i++)
                if (!string.IsNullOrWhiteSpace(page.world.DisabledMapRooms[i])) disabled.Add(page.world.DisabledMapRooms[i]);

        float minX = float.MaxValue;
        float minY = float.MaxValue;
        float maxX = float.MinValue;
        float maxY = float.MinValue;

        if (page.subNodes != null)
        {
            for (int i = 0; i < page.subNodes.Count; i++)
            {
                if (page.subNodes[i] is not RoomPanel panel || panel.roomRep?.room == null) continue;
                AbstractRoom room = panel.roomRep.room;
                if (disabled.Contains(room.name ?? string.Empty)) continue;
                if (!state.Rooms.TryGetValue(room.index, out PlayerMapRoomState roomState))
                {
                    failures.Add((room.name ?? room.index.ToString()) + ": Player Map placement is unavailable.");
                    continue;
                }
                if (!RoomMapBakeCache.TryGetReady(room.index, out RoomMapBake bake))
                {
                    RoomMapBakeSnapshot status = RoomMapBakeCache.GetSnapshot(room.index);
                    failures.Add((room.name ?? room.index.ToString()) + ": room bake is " + status.Status +
                                 (string.IsNullOrEmpty(status.Error) ? "." : " (" + status.Error + ")"));
                    continue;
                }

                Vector2 canon = PlayerMapWorkspaceRuntime.Effective(roomState, panel);
                if (!Finite(canon.x) || !Finite(canon.y))
                {
                    failures.Add((room.name ?? room.index.ToString()) + ": Canon Position contains NaN/Infinity.");
                    continue;
                }
                int layer = Mathf.Clamp(panel.layer, 0, PlayerMapCoordinateSystem.LayerCount - 1);
                PlayerMapRenderRoom renderRoom = new()
                {
                    RoomIndex = room.index,
                    Name = room.name ?? string.Empty,
                    Layer = layer,
                    CanonicalPosition = canon,
                    Bake = bake
                };
                plan.Rooms.Add(renderRoom);

                float halfW = bake.Width * PlayerMapCoordinateSystem.CanonPixelsPerTile * 0.5f;
                float halfH = bake.Height * PlayerMapCoordinateSystem.CanonPixelsPerTile * 0.5f;
                minX = Mathf.Min(minX, canon.x - halfW);
                minY = Mathf.Min(minY, canon.y - halfH);
                maxX = Mathf.Max(maxX, canon.x + halfW);
                maxY = Mathf.Max(maxY, canon.y + halfH);
            }
        }

        if (plan.Rooms.Count == 0 && failures.Count == 0)
            failures.Add("No enabled rooms are available for map rendering.");
        if (failures.Count > 0)
        {
            errors = failures.ToArray();
            return false;
        }

        plan.Rooms.Sort((a, b) =>
        {
            int layer = a.Layer.CompareTo(b.Layer);
            if (layer != 0) return layer;
            int index = a.RoomIndex.CompareTo(b.RoomIndex);
            return index != 0 ? index : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
        });

        plan.CanonMinX = minX;
        plan.CanonMinY = minY;
        plan.Width = (int)((maxX - minX) / PlayerMapCoordinateSystem.CanonPixelsPerTile) + PlayerMapCoordinateSystem.OutputPadding * 2;
        plan.LayerHeight = (int)((maxY - minY) / PlayerMapCoordinateSystem.CanonPixelsPerTile) + PlayerMapCoordinateSystem.OutputPadding * 2;
        plan.Height = checked(plan.LayerHeight * PlayerMapCoordinateSystem.LayerCount);

        if (plan.Width <= 0 || plan.Height <= 0 || plan.Width > MaxDimension || plan.Height > MaxDimension ||
            (long)plan.Width * plan.Height > MaxPixels)
        {
            errors = new[] { "Output dimensions are unsafe: " + plan.Width + "x" + plan.Height + "." };
            return false;
        }

        for (int i = 0; i < plan.Rooms.Count; i++)
        {
            PlayerMapRenderRoom room = plan.Rooms[i];
            float left = room.CanonicalPosition.x - room.Bake.Width * PlayerMapCoordinateSystem.CanonPixelsPerTile * 0.5f;
            float bottom = room.CanonicalPosition.y - room.Bake.Height * PlayerMapCoordinateSystem.CanonPixelsPerTile * 0.5f;
            room.PixelX = (int)((left - minX) / PlayerMapCoordinateSystem.CanonPixelsPerTile) + PlayerMapCoordinateSystem.OutputPadding;
            room.PixelYInLayer = (int)((bottom - minY) / PlayerMapCoordinateSystem.CanonPixelsPerTile) + PlayerMapCoordinateSystem.OutputPadding;
            if (room.PixelX < 0 || room.PixelYInLayer < 0 ||
                room.PixelX + room.Bake.Width > plan.Width || room.PixelYInLayer + room.Bake.Height > plan.LayerHeight)
                failures.Add(room.Name + ": computed output rectangle exceeds render bounds.");
        }
        if (failures.Count > 0)
        {
            errors = failures.ToArray();
            return false;
        }

        for (int i = 0; i < state.DefaultMaterials.Count; i++)
            plan.DefaultMaterials.Add(state.DefaultMaterials[i].Clone());
        DetectOverlaps(plan);
        errors = Array.Empty<string>();
        return true;
    }

    private static void DetectOverlaps(PlayerMapRenderPlan plan)
    {
        for (int i = 0; i < plan.Rooms.Count; i++)
        {
            PlayerMapRenderRoom a = plan.Rooms[i];
            int ax2 = a.PixelX + a.Bake.Width;
            int ay2 = a.PixelYInLayer + a.Bake.Height;
            for (int j = i + 1; j < plan.Rooms.Count; j++)
            {
                PlayerMapRenderRoom b = plan.Rooms[j];
                if (b.Layer != a.Layer) continue;
                int bx2 = b.PixelX + b.Bake.Width;
                int by2 = b.PixelYInLayer + b.Bake.Height;
                if (a.PixelX >= bx2 || b.PixelX >= ax2 || a.PixelYInLayer >= by2 || b.PixelYInLayer >= ay2) continue;
                plan.Warnings.Add("Overlap on L" + a.Layer + ": " + a.Name + " / " + b.Name +
                                  ". Deterministic RoomIndex order will be used.");
            }
        }
    }

    private static Color32[] Compose(PlayerMapRenderPlan plan)
    {
        Color32[] pixels = new Color32[checked(plan.Width * plan.Height)];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = Background;

        for (int r = 0; r < plan.Rooms.Count; r++)
        {
            PlayerMapRenderRoom room = plan.Rooms[r];
            int layerBase = room.Layer * plan.LayerHeight;
            for (int y = 0; y < room.Bake.Height; y++)
            {
                for (int x = 0; x < room.Bake.Width; x++)
                {
                    int targetX = room.PixelX + x;
                    int targetYInLayer = room.PixelYInLayer + y;
                    int targetY = layerBase + targetYInLayer;
                    int targetIndex = targetY * plan.Width + targetX;
                    RoomMapPixel source = room.Bake.Pixels[y * room.Bake.Width + x];
                    int materialMode = MaterialMode(plan.DefaultMaterials,
                        new Vector2(
                            plan.CanonMinX + targetX * PlayerMapCoordinateSystem.CanonPixelsPerTile,
                            plan.CanonMinY + targetYInLayer * PlayerMapCoordinateSystem.CanonPixelsPerTile));
                    bool targetBackground = IsBackground(pixels[targetIndex]);

                    if (source.Kind == RoomMapPixelKind.Solid || source.Kind == RoomMapPixelKind.UnknownShortcut)
                    {
                        if (materialMode != 1 || targetBackground)
                            pixels[targetIndex] = SolidColor;
                        continue;
                    }

                    if (materialMode == 2 && !targetBackground)
                        continue;
                    pixels[targetIndex] = Encode(source);
                }
            }
        }
        return pixels;
    }

    private static int MaterialMode(List<PlayerMapDefMaterialState> defs, Vector2 canonPoint)
    {
        int mode = 1;
        for (int i = 0; i < defs.Count; i++)
        {
            PlayerMapDefMaterialState item = defs[i];
            float left = Mathf.Min(item.A.x, item.B.x);
            float right = Mathf.Max(item.A.x, item.B.x);
            float bottom = Mathf.Min(item.A.y, item.B.y);
            float top = Mathf.Max(item.A.y, item.B.y);
            if (canonPoint.x >= left && canonPoint.x <= right && canonPoint.y >= bottom && canonPoint.y <= top)
                mode = item.Air ? 2 : 0;
        }
        return mode;
    }

    private static Color32 Encode(RoomMapPixel pixel)
    {
        if (pixel.Kind == RoomMapPixelKind.Structure)
            return pixel.Water ? StructureWet : StructureDry;
        return pixel.Water ? BrightWet : BrightDry;
    }

    private static bool IsBackground(Color32 value) => value.r == 0 && value.g == 255 && value.b == 0;

    private static PlayerMapRenderPreview BuildPreview(int width, int height, Color32[] pixels)
    {
        List<PlayerMapPreviewRun> runs = new();
        for (int y = 0; y < height; y++)
        {
            int x = 0;
            while (x < width)
            {
                Color32 color = pixels[y * width + x];
                int end = x + 1;
                while (end < width && SameColor(color, pixels[y * width + end])) end++;
                if (!IsBackground(color)) runs.Add(new PlayerMapPreviewRun(x, y, end - x, color));
                x = end;
            }
        }
        return new PlayerMapRenderPreview { Available = true, Width = width, Height = height, Runs = runs.ToArray() };
    }

    private static bool SameColor(Color32 a, Color32 b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;

    private static string Export(MapPage page, PlayerMapRenderPlan plan, Color32[] pixels)
    {
        string mapConfig = page.filePath;
        if (string.IsNullOrWhiteSpace(mapConfig)) throw new InvalidOperationException("Map config path is unavailable.");
        string directory = Path.GetDirectoryName(mapConfig);
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("Map output directory is unavailable.");
        Directory.CreateDirectory(directory);

        string pngPath = Path.ChangeExtension(mapConfig, ".png");
        string fileName = Path.GetFileNameWithoutExtension(pngPath);
        string metaName = fileName.StartsWith("map_", StringComparison.OrdinalIgnoreCase)
            ? "map_image_" + fileName.Substring(4) + ".txt"
            : fileName + "_image.txt";
        string metaPath = Path.Combine(directory, metaName);
        string tempPng = pngPath + ".drycycle.tmp";
        string tempMeta = metaPath + ".drycycle.tmp";

        Texture2D texture = null;
        try
        {
            texture = new Texture2D(plan.Width, plan.Height, TextureFormat.RGBA32, mipChain: false);
            texture.SetPixels32(pixels);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Point;
            texture.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            PNGSaver.SaveTextureToFile(texture, tempPng);

            List<string> meta = new(plan.Rooms.Count);
            for (int i = 0; i < plan.Rooms.Count; i++)
            {
                PlayerMapRenderRoom room = plan.Rooms[i];
                int y = room.Layer * plan.LayerHeight + room.PixelYInLayer;
                meta.Add(room.Name + ": " +
                         room.PixelX.ToString(CultureInfo.InvariantCulture) + "," +
                         y.ToString(CultureInfo.InvariantCulture) + "," +
                         room.Bake.Width.ToString(CultureInfo.InvariantCulture) + "," +
                         room.Bake.Height.ToString(CultureInfo.InvariantCulture));
            }
            File.WriteAllLines(tempMeta, meta.ToArray());
            CommitPair(tempPng, pngPath, tempMeta, metaPath);
            return pngPath;
        }
        finally
        {
            if (texture != null) UnityEngine.Object.Destroy(texture);
            TryDelete(tempPng);
            TryDelete(tempMeta);
        }
    }

    private static void CommitPair(string tempA, string targetA, string tempB, string targetB)
    {
        if (!File.Exists(tempA) || !File.Exists(tempB))
            throw new IOException("Render staging files are incomplete.");

        string backupA = targetA + ".drycycle.bak";
        string backupB = targetB + ".drycycle.bak";
        TryDelete(backupA);
        TryDelete(backupB);
        bool backedA = false;
        bool backedB = false;
        bool installedA = false;
        bool installedB = false;
        try
        {
            if (File.Exists(targetA)) { File.Move(targetA, backupA); backedA = true; }
            if (File.Exists(targetB)) { File.Move(targetB, backupB); backedB = true; }
            File.Move(tempA, targetA); installedA = true;
            File.Move(tempB, targetB); installedB = true;
            TryDelete(backupA);
            TryDelete(backupB);
        }
        catch
        {
            if (installedA) TryDelete(targetA);
            if (installedB) TryDelete(targetB);
            if (backedA && File.Exists(backupA)) File.Move(backupA, targetA);
            if (backedB && File.Exists(backupB)) File.Move(backupB, targetB);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
