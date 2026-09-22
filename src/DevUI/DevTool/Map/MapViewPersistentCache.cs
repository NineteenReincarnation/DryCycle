using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Map;

internal readonly struct MapViewFileStamp
{
    internal MapViewFileStamp(string path, long length, long writeTicks)
    {
        Path = path ?? string.Empty;
        Length = length;
        WriteTicks = writeTicks;
    }

    internal string Path { get; }
    internal long Length { get; }
    internal long WriteTicks { get; }

    internal bool Matches(MapViewFileStamp other) =>
        string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase) &&
        Length == other.Length &&
        WriteTicks == other.WriteTicks;

    internal static MapViewFileStamp Capture(string path)
    {
        string normalized = path ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
            return new MapViewFileStamp(string.Empty, 0L, 0L);

        try
        {
            if (!File.Exists(normalized))
                return new MapViewFileStamp(normalized, 0L, 0L);

            FileInfo info = new(normalized);
            return new MapViewFileStamp(
                normalized,
                info.Length,
                info.LastWriteTimeUtc.Ticks);
        }
        catch
        {
            return new MapViewFileStamp(normalized, 0L, 0L);
        }
    }
}

internal sealed class MapViewPersistentRoom
{
    internal string RoomName = string.Empty;
    internal MapViewFileStamp RoomSource;
    internal MapViewFileStamp SettingsSource;
    internal string RasterSignature = string.Empty;
    internal bool RasterInitialized;
    internal bool NodesInitialized;
    internal bool CurvesInitialized;
    internal int RasterWidth;
    internal int RasterHeight;
    internal int NodeFingerprint;
    internal int SettingsFingerprint;
    internal float WidthTiles = 12f;
    internal float HeightTiles = 6f;

    // V3 frontend hint. This never owns a Unity texture; it is only a stable Futile atlas-element
    // identity plus the descriptor observed when the cache was written.
    internal string ThumbnailElementName = string.Empty;
    internal float ThumbnailUvX;
    internal float ThumbnailUvY;
    internal float ThumbnailUvWidth;
    internal float ThumbnailUvHeight;
    internal float ThumbnailPixelWidth;
    internal float ThumbnailPixelHeight;

    internal EditorMapRectSnapshot[] BaseRasterRuns = Array.Empty<EditorMapRectSnapshot>();
    internal EditorMapRectSnapshot[] TerrainFillRuns = Array.Empty<EditorMapRectSnapshot>();
    internal EditorMapPolylineSnapshot[] Curves = Array.Empty<EditorMapPolylineSnapshot>();
    internal EditorMapNodeVisualSnapshot[] Nodes = Array.Empty<EditorMapNodeVisualSnapshot>();
}

internal sealed class MapViewPersistentRoute
{
    internal string ConnectionId = string.Empty;
    internal int FromRoomIndex;
    internal int FromNodeIndex;
    internal int ToRoomIndex;
    internal int ToNodeIndex;
    internal string FromRoomName = string.Empty;
    internal string ToRoomName = string.Empty;
    internal int Direction;
    internal bool Ambiguous;
    internal int PolicyVersion;
    internal int Kind;
    internal float FromRoomX;
    internal float FromRoomY;
    internal float ToRoomX;
    internal float ToRoomY;
    internal float StartDirectionX;
    internal float StartDirectionY;
    internal float EndDirectionX;
    internal float EndDirectionY;
    internal EditorMapPointSnapshot[] Points = Array.Empty<EditorMapPointSnapshot>();
}

internal sealed class MapViewPersistentSnapshot
{
    internal int FormatVersion;
    internal string ContextKey = string.Empty;
    internal long TemplateFingerprint;
    internal long FrontendTopologyFingerprint;
    internal readonly List<MapViewPersistentRoom> Rooms = new();
    internal readonly List<MapViewPersistentRoute> Routes = new();
}

/// <summary>
/// Binary persistent store for the heavy, immutable part of the World Map view.
///
/// The map runtime validates room geometry and RoomSettings independently before reusing a room,
/// so changing one settings file only invalidates that room's authored-curve layer. Writes are
/// serialized on a background worker and use an atomic temp-file replacement.
/// </summary>
internal static class MapViewPersistentCacheStore
{
    private sealed class WriteRequest
    {
        internal string Path = string.Empty;
        internal MapViewPersistentSnapshot Snapshot;
    }

    private const int CacheMagic = 0x44434D56; // DCMV
    private const int LegacyCacheVersion = 2;
    internal const int CurrentCacheVersion = 3;
    private const long MaxCacheBytes = 256L * 1024L * 1024L;
    private const int MaxRooms = 4096;
    private const int MaxRectsPerRoom = 500000;
    private const int MaxCurvesPerRoom = 20000;
    private const int MaxPointsPerCurve = 200000;
    private const int MaxNodesPerRoom = 8192;
    private const int MaxRoutes = 16384;
    private const int MaxRoutePoints = 4096;
    private const int FrontendExtensionMagic = 0x33574656; // VFW3

    private static readonly object writeSync = new();
    private static readonly Queue<WriteRequest> writeQueue = new();
    private static bool writerRunning;

    internal static string ResolveCachePath(string contextKey) =>
        ResolveCachePath(contextKey, CurrentCacheVersion);

    private static string ResolveCachePath(string contextKey, int version)
    {
        string fileName =
            "map-view-v" + version + "-" +
            StableHash(contextKey).ToString("x16") +
            ".bin";
        try
        {
            return Path.Combine(
                Paths.CachePath,
                "DryCycle",
                "DevTool",
                "MapView",
                fileName);
        }
        catch
        {
            return Path.Combine(
                Application.persistentDataPath,
                "DryCycle",
                "MapView",
                fileName);
        }
    }

    internal static MapViewPersistentSnapshot Load(
        string path,
        string expectedContextKey)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string sourcePath = path;
        if (!File.Exists(sourcePath))
        {
            string legacyPath =
                ResolveCachePath(
                    expectedContextKey,
                    LegacyCacheVersion);
            if (!File.Exists(legacyPath))
                return null;
            sourcePath = legacyPath;
        }

        try
        {
            FileInfo info = new(sourcePath);
            if (info.Length <= 0L || info.Length > MaxCacheBytes)
                return null;

            using FileStream stream = new(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            using BinaryReader reader = new(stream);

            if (reader.ReadInt32() != CacheMagic)
                return null;

            int version = reader.ReadInt32();
            if (version < LegacyCacheVersion ||
                version > CurrentCacheVersion)
                return null;

            string contextKey = reader.ReadString();
            if (!string.Equals(
                    contextKey,
                    expectedContextKey ?? string.Empty,
                    StringComparison.Ordinal))
                return null;

            MapViewPersistentSnapshot snapshot = new()
            {
                FormatVersion = version,
                ContextKey = contextKey,
                TemplateFingerprint = reader.ReadInt64()
            };

            int roomCount =
                ReadSafeCount(reader, MaxRooms);
            for (int i = 0; i < roomCount; i++)
                snapshot.Rooms.Add(
                    ReadRoom(reader, version));

            if (version >= 3)
            {
                int routeCount =
                    ReadSafeCount(reader, MaxRoutes);
                for (int i = 0; i < routeCount; i++)
                    snapshot.Routes.Add(ReadRoute(reader));

                // Early development V3 files ended after the route list. Keep them readable, but
                // leave the fingerprint at zero so their routes are treated as untrusted and rebuilt.
                if (reader.BaseStream.Position + sizeof(int) + sizeof(long) <=
                    reader.BaseStream.Length)
                {
                    int extensionMagic = reader.ReadInt32();
                    if (extensionMagic == FrontendExtensionMagic)
                        snapshot.FrontendTopologyFingerprint = reader.ReadInt64();
                }
            }

            return snapshot;
        }
        catch (Exception error)
        {
            global::DryCycle.Plugin.Logger?.LogWarning(
                "WorldMap persistent cache ignored because it is invalid: " +
                error.Message);
            return null;
        }
    }

    internal static void QueueWrite(
        string path,
        MapViewPersistentSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(path) || snapshot == null) return;

        lock (writeSync)
        {
            writeQueue.Enqueue(new WriteRequest { Path = path, Snapshot = snapshot });
            if (writerRunning) return;
            writerRunning = true;
        }

        Task.Run(WriterLoop);
    }

    internal static bool Flush(int timeoutMilliseconds = 5000)
    {
        DateTime deadline =
            DateTime.UtcNow.AddMilliseconds(
                Math.Max(1, timeoutMilliseconds));

        lock (writeSync)
        {
            while (writerRunning || writeQueue.Count > 0)
            {
                double remaining =
                    (deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remaining <= 0d)
                    return false;

                Monitor.Wait(
                    writeSync,
                    Math.Max(
                        1,
                        (int)Math.Min(
                            int.MaxValue,
                            remaining)));
            }
        }

        return true;
    }

    private static void WriterLoop()
    {
        while (true)
        {
            WriteRequest request;
            lock (writeSync)
            {
                if (writeQueue.Count == 0)
                {
                    writerRunning = false;
                    Monitor.PulseAll(writeSync);
                    return;
                }
                request = writeQueue.Dequeue();
            }

            Exception lastError = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    Write(request.Path, request.Snapshot);
                    lastError = null;
                    break;
                }
                catch (Exception error)
                {
                    lastError = error;
                }
            }

            if (lastError != null)
            {
                try
                {
                    global::DryCycle.Plugin.Logger?.LogWarning(
                        "WorldMap persistent cache save failed: " + lastError.Message);
                }
                catch
                {
                }
            }
        }
    }

    private static MapViewPersistentRoom ReadRoom(
        BinaryReader reader,
        int version)
    {
        MapViewPersistentRoom room = new()
        {
            RoomName = reader.ReadString(),
            RoomSource = ReadStamp(reader),
            SettingsSource = ReadStamp(reader),
            RasterSignature = reader.ReadString(),
            RasterInitialized = reader.ReadBoolean(),
            NodesInitialized = reader.ReadBoolean(),
            CurvesInitialized = reader.ReadBoolean(),
            RasterWidth = reader.ReadInt32(),
            RasterHeight = reader.ReadInt32(),
            NodeFingerprint = reader.ReadInt32(),
            SettingsFingerprint = reader.ReadInt32(),
            WidthTiles = reader.ReadSingle(),
            HeightTiles = reader.ReadSingle()
        };

        if (version >= 3)
        {
            room.ThumbnailElementName = reader.ReadString();
            room.ThumbnailUvX = reader.ReadSingle();
            room.ThumbnailUvY = reader.ReadSingle();
            room.ThumbnailUvWidth = reader.ReadSingle();
            room.ThumbnailUvHeight = reader.ReadSingle();
            room.ThumbnailPixelWidth = reader.ReadSingle();
            room.ThumbnailPixelHeight = reader.ReadSingle();
        }

        room.BaseRasterRuns = ReadRects(reader);
        room.TerrainFillRuns = ReadRects(reader);
        room.Curves = ReadCurves(reader);
        room.Nodes = ReadNodes(reader);
        return room;
    }

    private static MapViewPersistentRoute ReadRoute(
        BinaryReader reader)
    {
        MapViewPersistentRoute route = new()
        {
            ConnectionId = reader.ReadString(),
            FromRoomIndex = reader.ReadInt32(),
            FromNodeIndex = reader.ReadInt32(),
            ToRoomIndex = reader.ReadInt32(),
            ToNodeIndex = reader.ReadInt32(),
            FromRoomName = reader.ReadString(),
            ToRoomName = reader.ReadString(),
            Direction = reader.ReadInt32(),
            Ambiguous = reader.ReadBoolean(),
            PolicyVersion = reader.ReadInt32(),
            Kind = reader.ReadInt32(),
            FromRoomX = reader.ReadSingle(),
            FromRoomY = reader.ReadSingle(),
            ToRoomX = reader.ReadSingle(),
            ToRoomY = reader.ReadSingle(),
            StartDirectionX = reader.ReadSingle(),
            StartDirectionY = reader.ReadSingle(),
            EndDirectionX = reader.ReadSingle(),
            EndDirectionY = reader.ReadSingle()
        };

        int pointCount =
            ReadSafeCount(reader, MaxRoutePoints);
        route.Points =
            new EditorMapPointSnapshot[pointCount];
        for (int i = 0; i < pointCount; i++)
            route.Points[i] =
                new EditorMapPointSnapshot(
                    reader.ReadSingle(),
                    reader.ReadSingle());
        return route;
    }

    private static MapViewFileStamp ReadStamp(BinaryReader reader) =>
        new(reader.ReadString(), reader.ReadInt64(), reader.ReadInt64());

    private static EditorMapRectSnapshot[] ReadRects(BinaryReader reader)
    {
        int count = ReadSafeCount(reader, MaxRectsPerRoom);
        EditorMapRectSnapshot[] result = new EditorMapRectSnapshot[count];
        for (int i = 0; i < count; i++)
        {
            float x = reader.ReadSingle();
            float y = reader.ReadSingle();
            float width = reader.ReadSingle();
            float height = reader.ReadSingle();
            EditorMapGeometryKind kind = (EditorMapGeometryKind)reader.ReadByte();
            result[i] = new EditorMapRectSnapshot(x, y, width, height, kind);
        }
        return result;
    }

    private static EditorMapPolylineSnapshot[] ReadCurves(BinaryReader reader)
    {
        int count = ReadSafeCount(reader, MaxCurvesPerRoom);
        EditorMapPolylineSnapshot[] result = new EditorMapPolylineSnapshot[count];
        for (int i = 0; i < count; i++)
        {
            EditorMapGeometryKind kind = (EditorMapGeometryKind)reader.ReadByte();
            bool closed = reader.ReadBoolean();
            int pointCount = ReadSafeCount(reader, MaxPointsPerCurve);
            EditorMapPointSnapshot[] points = new EditorMapPointSnapshot[pointCount];
            for (int point = 0; point < pointCount; point++)
                points[point] = new EditorMapPointSnapshot(reader.ReadSingle(), reader.ReadSingle());
            result[i] = new EditorMapPolylineSnapshot { Kind = kind, Closed = closed, Points = points };
        }
        return result;
    }

    private static EditorMapNodeVisualSnapshot[] ReadNodes(BinaryReader reader)
    {
        int count = ReadSafeCount(reader, MaxNodesPerRoom);
        EditorMapNodeVisualSnapshot[] result = new EditorMapNodeVisualSnapshot[count];
        for (int i = 0; i < count; i++)
            result[i] = new EditorMapNodeVisualSnapshot(reader.ReadInt32(), reader.ReadSingle(), reader.ReadSingle());
        return result;
    }

    private static int ReadSafeCount(BinaryReader reader, int max)
    {
        int value = reader.ReadInt32();
        if (value < 0 || value > max)
            throw new InvalidDataException("Invalid WorldMap cache item count: " + value);
        return value;
    }

    private static void Write(string path, MapViewPersistentSnapshot snapshot)
    {
        string directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");

        try
        {
            using (FileStream stream = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (BinaryWriter writer = new(stream))
            {
                writer.Write(CacheMagic);
                writer.Write(CurrentCacheVersion);
                writer.Write(snapshot.ContextKey ?? string.Empty);
                writer.Write(snapshot.TemplateFingerprint);
                writer.Write(snapshot.Rooms.Count);
                for (int i = 0; i < snapshot.Rooms.Count; i++)
                    WriteRoom(writer, snapshot.Rooms[i]);

                writer.Write(snapshot.Routes.Count);
                for (int i = 0; i < snapshot.Routes.Count; i++)
                    WriteRoute(writer, snapshot.Routes[i]);

                writer.Write(FrontendExtensionMagic);
                writer.Write(snapshot.FrontendTopologyFingerprint);

                writer.Flush();
            }

            try
            {
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            catch
            {
                File.Copy(temp, path, true);
                File.Delete(temp);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch
            {
            }
        }
    }

    private static void WriteRoom(BinaryWriter writer, MapViewPersistentRoom room)
    {
        room ??= new MapViewPersistentRoom();
        writer.Write(room.RoomName ?? string.Empty);
        WriteStamp(writer, room.RoomSource);
        WriteStamp(writer, room.SettingsSource);
        writer.Write(room.RasterSignature ?? string.Empty);
        writer.Write(room.RasterInitialized);
        writer.Write(room.NodesInitialized);
        writer.Write(room.CurvesInitialized);
        writer.Write(room.RasterWidth);
        writer.Write(room.RasterHeight);
        writer.Write(room.NodeFingerprint);
        writer.Write(room.SettingsFingerprint);
        writer.Write(room.WidthTiles);
        writer.Write(room.HeightTiles);

        writer.Write(room.ThumbnailElementName ?? string.Empty);
        writer.Write(room.ThumbnailUvX);
        writer.Write(room.ThumbnailUvY);
        writer.Write(room.ThumbnailUvWidth);
        writer.Write(room.ThumbnailUvHeight);
        writer.Write(room.ThumbnailPixelWidth);
        writer.Write(room.ThumbnailPixelHeight);

        WriteRects(writer, room.BaseRasterRuns);
        WriteRects(writer, room.TerrainFillRuns);
        WriteCurves(writer, room.Curves);
        WriteNodes(writer, room.Nodes);
    }

    private static void WriteRoute(
        BinaryWriter writer,
        MapViewPersistentRoute route)
    {
        route ??= new MapViewPersistentRoute();

        writer.Write(route.ConnectionId ?? string.Empty);
        writer.Write(route.FromRoomIndex);
        writer.Write(route.FromNodeIndex);
        writer.Write(route.ToRoomIndex);
        writer.Write(route.ToNodeIndex);
        writer.Write(route.FromRoomName ?? string.Empty);
        writer.Write(route.ToRoomName ?? string.Empty);
        writer.Write(route.Direction);
        writer.Write(route.Ambiguous);
        writer.Write(route.PolicyVersion);
        writer.Write(route.Kind);
        writer.Write(route.FromRoomX);
        writer.Write(route.FromRoomY);
        writer.Write(route.ToRoomX);
        writer.Write(route.ToRoomY);
        writer.Write(route.StartDirectionX);
        writer.Write(route.StartDirectionY);
        writer.Write(route.EndDirectionX);
        writer.Write(route.EndDirectionY);

        EditorMapPointSnapshot[] points =
            route.Points ?? Array.Empty<EditorMapPointSnapshot>();
        if (points.Length > MaxRoutePoints)
            throw new InvalidDataException(
                "WorldMap route cache point count exceeds limit: " +
                points.Length);

        writer.Write(points.Length);
        for (int i = 0; i < points.Length; i++)
        {
            writer.Write(points[i].X);
            writer.Write(points[i].Y);
        }
    }

    private static void WriteStamp(BinaryWriter writer, MapViewFileStamp stamp)
    {
        writer.Write(stamp.Path ?? string.Empty);
        writer.Write(stamp.Length);
        writer.Write(stamp.WriteTicks);
    }

    private static void WriteRects(BinaryWriter writer, EditorMapRectSnapshot[] values)
    {
        values ??= Array.Empty<EditorMapRectSnapshot>();
        writer.Write(values.Length);
        for (int i = 0; i < values.Length; i++)
        {
            EditorMapRectSnapshot value = values[i];
            writer.Write(value.X);
            writer.Write(value.Y);
            writer.Write(value.Width);
            writer.Write(value.Height);
            writer.Write((byte)value.Kind);
        }
    }

    private static void WriteCurves(BinaryWriter writer, EditorMapPolylineSnapshot[] values)
    {
        values ??= Array.Empty<EditorMapPolylineSnapshot>();
        writer.Write(values.Length);
        for (int i = 0; i < values.Length; i++)
        {
            EditorMapPolylineSnapshot curve = values[i] ?? new EditorMapPolylineSnapshot();
            writer.Write((byte)curve.Kind);
            writer.Write(curve.Closed);
            EditorMapPointSnapshot[] points = curve.Points ?? Array.Empty<EditorMapPointSnapshot>();
            writer.Write(points.Length);
            for (int point = 0; point < points.Length; point++)
            {
                writer.Write(points[point].X);
                writer.Write(points[point].Y);
            }
        }
    }

    private static void WriteNodes(BinaryWriter writer, EditorMapNodeVisualSnapshot[] values)
    {
        values ??= Array.Empty<EditorMapNodeVisualSnapshot>();
        writer.Write(values.Length);
        for (int i = 0; i < values.Length; i++)
        {
            writer.Write(values[i].NodeIndex);
            writer.Write(values[i].X);
            writer.Write(values[i].Y);
        }
    }

    private static ulong StableHash(string value)
    {
        unchecked
        {
            ulong hash = 1469598103934665603UL;
            if (!string.IsNullOrEmpty(value))
            {
                for (int i = 0; i < value.Length; i++)
                {
                    char c = value[i];
                    hash = (hash ^ (byte)c) * 1099511628211UL;
                    hash = (hash ^ (byte)(c >> 8)) * 1099511628211UL;
                }
            }
            return hash;
        }
    }
}
