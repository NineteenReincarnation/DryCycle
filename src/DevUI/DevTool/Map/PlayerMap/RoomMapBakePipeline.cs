using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

public enum RoomMapBakeStatus
{
    Missing = 0,
    Pending = 1,
    Ready = 2,
    Failed = 3
}

public enum RoomMapPixelKind : byte
{
    Air = 0,
    BackWall = 1,
    Solid = 2,
    Structure = 3,
    RoomExit = 4,
    CreatureHole = 5,
    NormalShortcut = 6,
    NpcTransport = 7,
    RegionTransport = 8,
    UnknownShortcut = 9
}

public readonly struct RoomMapPreviewRun
{
    public RoomMapPreviewRun(int x, int y, int length, RoomMapPixelKind kind, bool water)
    {
        X = x;
        Y = y;
        Length = length;
        Kind = kind;
        Water = water;
    }

    public int X { get; }
    public int Y { get; }
    public int Length { get; }
    public RoomMapPixelKind Kind { get; }
    public bool Water { get; }
}

public sealed class RoomMapBakeSnapshot
{
    public static readonly RoomMapBakeSnapshot Empty = new();

    public RoomMapBakeStatus Status { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string Error { get; init; } = string.Empty;
    public RoomMapPreviewRun[] Runs { get; init; } = Array.Empty<RoomMapPreviewRun>();
}

internal readonly struct RoomMapPixel
{
    internal RoomMapPixel(RoomMapPixelKind kind, bool water)
    {
        Kind = kind;
        Water = water;
    }

    internal RoomMapPixelKind Kind { get; }
    internal bool Water { get; }
}

internal sealed class RoomMapBake
{
    internal int RoomIndex;
    internal string RoomName = string.Empty;
    internal int Width;
    internal int Height;
    internal RoomMapPixel[] Pixels = Array.Empty<RoomMapPixel>();
    internal RoomMapPreviewRun[] Runs = Array.Empty<RoomMapPreviewRun>();
    internal string SourcePath = string.Empty;
    internal long SourceLength;
    internal DateTime SourceWriteTimeUtc;
}

internal sealed class RoomMapSource
{
    internal int Width;
    internal int Height;
    internal SourceTile[] Tiles = Array.Empty<SourceTile>();

    internal ref SourceTile Tile(int x, int y) => ref Tiles[y * Width + x];
    internal bool Inside(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;
}

internal struct SourceTile
{
    internal byte Terrain;
    internal bool VerticalBeam;
    internal bool HorizontalBeam;
    internal bool WallBehind;
    internal byte Shortcut;
    internal bool Water;
}

/// <summary>
/// Direct static-room loader used by Player Map baking. It deliberately does not construct Room,
/// RoomPreparer, ShortcutMapper, MiniMap, or call any of their Update methods.
/// </summary>
internal static class RoomMapSourceLoader
{
    internal static bool TryLoad(string roomName, out RoomMapSource source, out string sourcePath, out string error)
    {
        source = null;
        sourcePath = string.Empty;
        error = null;
        try
        {
            sourcePath = WorldLoader.FindRoomFile(roomName, includeRootDirectory: false, ".txt", showWarning: false);
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                error = "Room source file was not found.";
                return false;
            }

            string[] lines = File.ReadAllLines(sourcePath);
            if (lines == null || lines.Length < 12)
            {
                error = "Room source has fewer than 12 lines.";
                return false;
            }

            // VersionFix is a pure text-shape compatibility helper; it does not instantiate or
            // advance any Rain World room runtime state.
            RoomPreprocessor.VersionFix(ref lines);
            string[] header = lines[1].Split('|');
            if (header.Length < 2)
            {
                error = "Room size/water header is malformed.";
                return false;
            }

            string[] size = header[0].Split('*');
            if (size.Length < 2 ||
                !int.TryParse(size[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int width) ||
                !int.TryParse(size[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int height) ||
                width <= 0 || height <= 0)
            {
                error = "Room dimensions are invalid.";
                return false;
            }

            int waterLevel = -1;
            int.TryParse(header[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out waterLevel);
            SourceTile[] tiles = new SourceTile[checked(width * height)];
            string[] encoded = lines[11].Split('|');
            int encodedCount = Math.Min(width * height, Math.Max(0, encoded.Length - 1));

            for (int ordinal = 0; ordinal < width * height; ordinal++)
            {
                int x = ordinal / height;
                int y = height - 1 - ordinal % height;
                SourceTile tile = new()
                {
                    Terrain = 0,
                    Water = waterLevel >= 0 && y <= waterLevel
                };

                if (ordinal < encodedCount)
                    DecodeTile(encoded[ordinal], ref tile);
                tiles[y * width + x] = tile;
            }

            source = new RoomMapSource { Width = width, Height = height, Tiles = tiles };
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            source = null;
            return false;
        }
    }

    private static void DecodeTile(string encoded, ref SourceTile tile)
    {
        if (string.IsNullOrWhiteSpace(encoded)) return;
        string[] parts = encoded.Split(',');
        if (parts.Length > 0 && byte.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte terrain))
            tile.Terrain = terrain;

        for (int i = 1; i < parts.Length; i++)
        {
            switch (parts[i])
            {
                case "1": tile.VerticalBeam = true; break;
                case "2": tile.HorizontalBeam = true; break;
                case "3": if (tile.Shortcut < 1) tile.Shortcut = 1; break;
                case "4": tile.Shortcut = 2; break;
                case "5": tile.Shortcut = 3; break;
                case "9": tile.Shortcut = 4; break;
                case "12": tile.Shortcut = 5; break;
                case "6": tile.WallBehind = true; break;
            }
        }
    }
}

internal static class RoomMapSemanticCompiler
{
    private const byte Air = 0;
    private const byte Solid = 1;
    private const byte Slope = 2;
    private const byte Floor = 3;
    private const byte ShortcutEntrance = 4;

    internal static RoomMapBake Compile(int roomIndex, string roomName, RoomMapSource source, string sourcePath)
    {
        RoomMapPixel[] pixels = new RoomMapPixel[source.Width * source.Height];
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                ref SourceTile tile = ref source.Tile(x, y);
                RoomMapPixelKind kind;
                if (tile.Terrain == ShortcutEntrance)
                {
                    kind = ResolveShortcut(source, x, y);
                }
                else if (tile.Terrain == Solid)
                {
                    kind = RoomMapPixelKind.Solid;
                }
                else if (tile.Terrain == Floor || tile.Terrain == Slope || tile.VerticalBeam || tile.HorizontalBeam)
                {
                    kind = RoomMapPixelKind.Structure;
                }
                else if (tile.WallBehind)
                {
                    kind = RoomMapPixelKind.BackWall;
                }
                else
                {
                    kind = RoomMapPixelKind.Air;
                }
                pixels[y * source.Width + x] = new RoomMapPixel(kind, tile.Water);
            }
        }

        FileInfo info = new(sourcePath);
        RoomMapBake bake = new()
        {
            RoomIndex = roomIndex,
            RoomName = roomName ?? string.Empty,
            Width = source.Width,
            Height = source.Height,
            Pixels = pixels,
            SourcePath = sourcePath,
            SourceLength = info.Exists ? info.Length : 0L,
            SourceWriteTimeUtc = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue
        };
        bake.Runs = BuildRuns(bake);
        return bake;
    }

    private static RoomMapPixelKind ResolveShortcut(RoomMapSource source, int startX, int startY)
    {
        int x = startX;
        int y = startY;
        int lastX = startX;
        int lastY = startY;
        int maxSteps = Math.Max(16, source.Width * source.Height * 4);

        for (int step = 0; step < maxSteps; step++)
        {
            if (step > 0)
            {
                ref SourceTile current = ref source.Tile(x, y);
                if (current.Terrain == ShortcutEntrance)
                    return RoomMapPixelKind.NormalShortcut;
                switch (current.Shortcut)
                {
                    case 2: return RoomMapPixelKind.RoomExit;
                    case 3: return RoomMapPixelKind.CreatureHole;
                    case 4: return RoomMapPixelKind.NpcTransport;
                    case 5: return RoomMapPixelKind.RegionTransport;
                }
            }

            int previousX = x;
            int previousY = y;
            if (!TryNextShortcutPosition(source, ref x, ref y, lastX, lastY))
                break;
            lastX = previousX;
            lastY = previousY;
            if (x == startX && y == startY && step > 1) break;
        }
        return RoomMapPixelKind.UnknownShortcut;
    }

    private static bool TryNextShortcutPosition(RoomMapSource source, ref int x, ref int y, int lastX, int lastY)
    {
        int dx = x - lastX;
        int dy = y - lastY;
        int nx = x + dx;
        int ny = y + dy;
        if ((dx != 0 || dy != 0) && HasShortcut(source, nx, ny))
        {
            x = nx;
            y = ny;
            return true;
        }

        // Fixed ordering keeps malformed branch handling deterministic.
        ReadOnlySpan<int> xs = stackalloc int[4] { 0, 1, 0, -1 };
        ReadOnlySpan<int> ys = stackalloc int[4] { 1, 0, -1, 0 };
        for (int i = 0; i < 4; i++)
        {
            if (xs[i] == -dx && ys[i] == -dy) continue;
            nx = x + xs[i];
            ny = y + ys[i];
            if (!HasShortcut(source, nx, ny)) continue;
            x = nx;
            y = ny;
            return true;
        }

        if (dx == 0 && dy == 0) return false;
        nx = x - dx;
        ny = y - dy;
        if (!source.Inside(nx, ny)) return false;
        x = nx;
        y = ny;
        return true;
    }

    private static bool HasShortcut(RoomMapSource source, int x, int y) =>
        source.Inside(x, y) && source.Tile(x, y).Shortcut != 0;

    private static RoomMapPreviewRun[] BuildRuns(RoomMapBake bake)
    {
        List<RoomMapPreviewRun> runs = new();
        for (int y = 0; y < bake.Height; y++)
        {
            int x = 0;
            while (x < bake.Width)
            {
                RoomMapPixel pixel = bake.Pixels[y * bake.Width + x];
                int end = x + 1;
                while (end < bake.Width)
                {
                    RoomMapPixel next = bake.Pixels[y * bake.Width + end];
                    if (next.Kind != pixel.Kind || next.Water != pixel.Water) break;
                    end++;
                }
                runs.Add(new RoomMapPreviewRun(x, y, end - x, pixel.Kind, pixel.Water));
                x = end;
            }
        }
        return runs.ToArray();
    }
}

internal sealed class RoomMapBakeCacheEntry
{
    internal int RoomIndex;
    internal string RoomName = string.Empty;
    internal RoomMapBakeStatus Status;
    internal RoomMapBake Bake;
    internal string Error = string.Empty;
    internal bool Queued;
    internal string LastSourcePath = string.Empty;
    internal long LastSourceLength;
    internal DateTime LastSourceWriteTimeUtc;
    internal int NextSourceAuditFrame;
}

internal static class RoomMapBakeCache
{
    private static readonly Dictionary<int, RoomMapBakeCacheEntry> Entries = new();
    private static readonly Queue<int> Pending = new();
    private static int revision = 1;

    internal static int Revision => revision;

    internal static void Request(int roomIndex, string roomName)
    {
        if (!Entries.TryGetValue(roomIndex, out RoomMapBakeCacheEntry entry))
        {
            entry = new RoomMapBakeCacheEntry
            {
                RoomIndex = roomIndex,
                RoomName = roomName ?? string.Empty,
                Status = RoomMapBakeStatus.Pending,
                Queued = true
            };
            Entries.Add(roomIndex, entry);
            Pending.Enqueue(roomIndex);
            revision++;
            return;
        }

        entry.RoomName = roomName ?? entry.RoomName;
        int frame = Time.frameCount;
        if (entry.Status == RoomMapBakeStatus.Ready && frame >= entry.NextSourceAuditFrame)
        {
            entry.NextSourceAuditFrame = frame + 120 + Math.Abs(roomIndex % 31);
            if (SourceChanged(entry))
            {
                entry.Status = RoomMapBakeStatus.Pending;
                entry.Queued = true;
                Pending.Enqueue(roomIndex);
                revision++;
            }
        }
        else if ((entry.Status == RoomMapBakeStatus.Missing || entry.Status == RoomMapBakeStatus.Failed) &&
                 !entry.Queued && frame >= entry.NextSourceAuditFrame)
        {
            // Missing/failed sources are compatibility-audited at low frequency instead of scanning
            // the filesystem for every room on every stable editor frame.
            entry.NextSourceAuditFrame = frame + 120 + Math.Abs(roomIndex % 31);
            string path = WorldLoader.FindRoomFile(entry.RoomName, false, ".txt", false);
            if (!string.Equals(path ?? string.Empty, entry.LastSourcePath ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
                SourceInfoChanged(path, entry.LastSourceLength, entry.LastSourceWriteTimeUtc))
            {
                entry.Status = RoomMapBakeStatus.Pending;
                entry.Queued = true;
                Pending.Enqueue(roomIndex);
                revision++;
            }
        }
    }

    internal static void ProcessPending(int budget)
    {
        budget = Math.Max(1, budget);
        while (budget-- > 0 && Pending.Count > 0)
        {
            int roomIndex = Pending.Dequeue();
            if (!Entries.TryGetValue(roomIndex, out RoomMapBakeCacheEntry entry) || !entry.Queued) continue;
            entry.Queued = false;
            Build(entry);
        }
    }

    internal static RoomMapBakeSnapshot GetSnapshot(int roomIndex)
    {
        if (!Entries.TryGetValue(roomIndex, out RoomMapBakeCacheEntry entry))
            return new RoomMapBakeSnapshot { Status = RoomMapBakeStatus.Missing };
        RoomMapBake bake = entry.Bake;
        return new RoomMapBakeSnapshot
        {
            Status = entry.Status,
            Width = bake?.Width ?? 0,
            Height = bake?.Height ?? 0,
            Error = entry.Error ?? string.Empty,
            Runs = bake?.Runs ?? Array.Empty<RoomMapPreviewRun>()
        };
    }

    internal static bool TryGetReady(int roomIndex, out RoomMapBake bake)
    {
        bake = null;
        return Entries.TryGetValue(roomIndex, out RoomMapBakeCacheEntry entry) &&
               entry.Status == RoomMapBakeStatus.Ready &&
               (bake = entry.Bake) != null;
    }

    internal static void Clear()
    {
        Entries.Clear();
        Pending.Clear();
        revision++;
    }

    private static void Build(RoomMapBakeCacheEntry entry)
    {
        if (!RoomMapSourceLoader.TryLoad(entry.RoomName, out RoomMapSource source, out string path, out string error))
        {
            entry.Bake = null;
            entry.Status = string.IsNullOrWhiteSpace(path) ? RoomMapBakeStatus.Missing : RoomMapBakeStatus.Failed;
            entry.Error = error ?? "Unknown room source error.";
            UpdateSourceEvidence(entry, path);
            entry.NextSourceAuditFrame = Time.frameCount + 120 + Math.Abs(entry.RoomIndex % 31);
            revision++;
            return;
        }

        try
        {
            entry.Bake = RoomMapSemanticCompiler.Compile(entry.RoomIndex, entry.RoomName, source, path);
            entry.Status = RoomMapBakeStatus.Ready;
            entry.Error = string.Empty;
            entry.LastSourcePath = path;
            entry.LastSourceLength = entry.Bake.SourceLength;
            entry.LastSourceWriteTimeUtc = entry.Bake.SourceWriteTimeUtc;
            entry.NextSourceAuditFrame = Time.frameCount + 120 + Math.Abs(entry.RoomIndex % 31);
        }
        catch (Exception exception)
        {
            entry.Bake = null;
            entry.Status = RoomMapBakeStatus.Failed;
            entry.Error = exception.Message;
            UpdateSourceEvidence(entry, path);
            entry.NextSourceAuditFrame = Time.frameCount + 120 + Math.Abs(entry.RoomIndex % 31);
        }
        revision++;
    }

    private static bool SourceChanged(RoomMapBakeCacheEntry entry)
    {
        string path = WorldLoader.FindRoomFile(entry.RoomName, false, ".txt", false);
        if (!string.Equals(path ?? string.Empty, entry.LastSourcePath ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            return true;
        return SourceInfoChanged(path, entry.LastSourceLength, entry.LastSourceWriteTimeUtc);
    }

    private static bool SourceInfoChanged(string path, long length, DateTime writeTimeUtc)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return length != 0L || writeTimeUtc != DateTime.MinValue;
        FileInfo info = new(path);
        return info.Length != length || info.LastWriteTimeUtc != writeTimeUtc;
    }

    private static void UpdateSourceEvidence(RoomMapBakeCacheEntry entry, string path)
    {
        entry.LastSourcePath = path ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            entry.LastSourceLength = 0L;
            entry.LastSourceWriteTimeUtc = DateTime.MinValue;
            return;
        }
        FileInfo info = new(path);
        entry.LastSourceLength = info.Length;
        entry.LastSourceWriteTimeUtc = info.LastWriteTimeUtc;
    }
}
