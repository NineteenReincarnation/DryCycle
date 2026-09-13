using System;
using System.Collections.Generic;
using System.IO;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Map;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Persistent authoring cache for the retained GPU World Map.
///
/// Rain World's own MapObject already treats MapTex as the authoritative pre-baked room image.
/// This cache deliberately does not duplicate those atlas pixels. It stores only data that the
/// rebuilt editor used to rediscover every time the region was opened: detailed mod terrain,
/// node positions and exact shortcut mouths. The file is content/version checked per room, so a
/// single edited room invalidates only its own record rather than forcing a whole-region rebake.
///
/// All Unity/file mutation happens on the Unity main thread. Render-thread readers only observe an
/// immutable Snapshot reference and therefore never block the RWImGui Present callback.
/// </summary>
internal static class WorldMapGpuCache
{
    private const uint Magic = 0x4D574344; // DCWM, little-endian on disk.
    private const int FormatVersion = 3;
    private const int BakerVersion = 4;
    private const int ValidationRoomsPerFrame = 4;
    private const int CaptureRoomsPerFrame = 6;
    private const int SaveDelayFrames = 45;

    internal readonly struct ExitMarker
    {
        internal ExitMarker(int nodeIndex, float x, float y)
        {
            NodeIndex = nodeIndex;
            X = x;
            Y = y;
        }

        internal int NodeIndex { get; }
        internal float X { get; }
        internal float Y { get; }
    }

    internal sealed class RoomBake
    {
        internal int RoomIndex;
        internal string RoomName = string.Empty;
        internal ulong SourceSignature;
        internal bool GeometryReady;
        internal bool ShortcutsReady;
        internal EditorMapRoomVisualSnapshot Visual = EditorMapRoomVisualSnapshot.Empty;
        internal ExitMarker[] Exits = Array.Empty<ExitMarker>();
        internal WorldMapShortcutPresentation.ShortcutMarker[] CreatureHoles =
            Array.Empty<WorldMapShortcutPresentation.ShortcutMarker>();

        private Dictionary<int, WorldMapShortcutPresentation.ShortcutMarker> exitsByNode;

        internal bool TryGetExit(int nodeIndex, out WorldMapShortcutPresentation.ShortcutMarker marker)
        {
            Dictionary<int, WorldMapShortcutPresentation.ShortcutMarker> index = exitsByNode;
            if (index == null)
            {
                index = new Dictionary<int, WorldMapShortcutPresentation.ShortcutMarker>();
                ExitMarker[] source = Exits ?? Array.Empty<ExitMarker>();
                for (int i = 0; i < source.Length; i++)
                {
                    ExitMarker exit = source[i];
                    index[exit.NodeIndex] = new WorldMapShortcutPresentation.ShortcutMarker(
                        exit.X,
                        exit.Y,
                        exit.NodeIndex);
                }
                exitsByNode = index;
            }

            return index.TryGetValue(nodeIndex, out marker);
        }
    }

    private sealed class Snapshot
    {
        internal static readonly Snapshot Empty = new(string.Empty, new Dictionary<int, RoomBake>(), false);

        internal Snapshot(string region, Dictionary<int, RoomBake> rooms, bool diskLoaded)
        {
            Region = region ?? string.Empty;
            Rooms = rooms ?? new Dictionary<int, RoomBake>();
            DiskLoaded = diskLoaded;
        }

        internal string Region { get; }
        internal Dictionary<int, RoomBake> Rooms { get; }
        internal bool DiskLoaded { get; }
    }

    private static volatile Snapshot current = Snapshot.Empty;
    private static string activeRegion = string.Empty;
    private static string activePath = string.Empty;
    private static int validationCursor;
    private static int captureCursor;
    private static int dirtyFrame = -1;
    private static bool dirty;
    private static string lastError = string.Empty;
    private static int cacheHits;
    private static int cacheMisses;

    internal static string ActiveRegion => current.Region;
    internal static int CachedRoomCount => current.Rooms.Count;
    internal static int CacheHits => cacheHits;
    internal static int CacheMisses => cacheMisses;
    internal static string LastError => lastError;
    internal static bool Dirty => dirty;

    internal static bool TryGetRoom(int roomIndex, out RoomBake bake)
    {
        Snapshot snapshot = current;
        return snapshot.Rooms.TryGetValue(roomIndex, out bake);
    }

    internal static EditorMapRoomVisualSnapshot GetVisualOrEmpty(int roomIndex)
    {
        return TryGetRoom(roomIndex, out RoomBake bake) && bake.Visual != null
            ? bake.Visual
            : EditorMapRoomVisualSnapshot.Empty;
    }

    internal static bool TryGetExit(
        int roomIndex,
        int nodeIndex,
        out WorldMapShortcutPresentation.ShortcutMarker marker)
    {
        marker = default;
        return TryGetRoom(roomIndex, out RoomBake bake) &&
               bake.ShortcutsReady &&
               bake.TryGetExit(nodeIndex, out marker);
    }

    internal static WorldMapShortcutPresentation.ShortcutMarker[] GetCreatureHoles(int roomIndex)
    {
        return TryGetRoom(roomIndex, out RoomBake bake) && bake.ShortcutsReady
            ? bake.CreatureHoles ?? Array.Empty<WorldMapShortcutPresentation.ShortcutMarker>()
            : Array.Empty<WorldMapShortcutPresentation.ShortcutMarker>();
    }

    /// <summary>
    /// Main-thread maintenance. Disk records are usable immediately when a region is entered and
    /// are then verified a few rooms at a time. Missing/stale rooms are populated from the existing
    /// high-fidelity parser while the GPU renderer can already show Rain World's original MapTex.
    /// </summary>
    internal static void Update(EditorSession session, EditorMapPresentationSnapshot snapshot)
    {
        if (session?.ToolMode != EditorToolMode.Map ||
            session.Owner?.activePage is not MapPage page ||
            page.world == null || snapshot?.Available != true)
        {
            FlushIfNeeded(force: true);
            return;
        }

        string region = NormalizeRegion(snapshot.RegionName ?? page.world.name);
        EnsureRegion(region);
        if (region.Length == 0) return;

        ValidateSomeRooms(page, snapshot);
        CaptureSomeRooms(page, snapshot);
        FlushIfNeeded(force: false);
    }

    internal static bool HasCompleteCachedData(EditorMapPresentationSnapshot snapshot)
    {
        if (snapshot?.Available != true) return false;
        Snapshot cache = current;
        EditorMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        if (rooms.Length == 0 || cache.Rooms.Count < rooms.Length) return false;

        for (int i = 0; i < rooms.Length; i++)
        {
            EditorMapRoomSnapshot room = rooms[i];
            if (room == null || !cache.Rooms.TryGetValue(room.RoomIndex, out RoomBake bake) ||
                !bake.GeometryReady || !bake.ShortcutsReady)
                return false;
        }

        return true;
    }

    internal static void InvalidateRoom(int roomIndex)
    {
        Snapshot before = current;
        if (!before.Rooms.ContainsKey(roomIndex)) return;
        Dictionary<int, RoomBake> next = new(before.Rooms);
        next.Remove(roomIndex);
        current = new Snapshot(before.Region, next, before.DiskLoaded);
        MarkDirty();
    }

    internal static void ClearDiskCache(string region)
    {
        string normalized = NormalizeRegion(region);
        string path = CachePath(normalized);
        try
        {
            if (File.Exists(path)) File.Delete(path);
            if (string.Equals(activeRegion, normalized, StringComparison.OrdinalIgnoreCase))
            {
                current = new Snapshot(normalized, new Dictionary<int, RoomBake>(), false);
                validationCursor = 0;
                captureCursor = 0;
                dirty = false;
                dirtyFrame = -1;
            }
            lastError = string.Empty;
        }
        catch (Exception error)
        {
            lastError = error.Message;
        }
    }

    internal static void FlushNow() => FlushIfNeeded(force: true);

    private static void EnsureRegion(string region)
    {
        if (string.Equals(activeRegion, region, StringComparison.OrdinalIgnoreCase)) return;
        FlushIfNeeded(force: true);

        activeRegion = region;
        activePath = CachePath(region);
        validationCursor = 0;
        captureCursor = 0;
        dirty = false;
        dirtyFrame = -1;
        lastError = string.Empty;
        cacheHits = 0;
        cacheMisses = 0;
        current = Load(region, activePath);
    }

    private static Snapshot Load(string region, string path)
    {
        Dictionary<int, RoomBake> rooms = new();
        if (region.Length == 0 || string.IsNullOrEmpty(path) || !File.Exists(path))
            return new Snapshot(region, rooms, false);

        try
        {
            using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using BinaryReader reader = new(stream);
            if (reader.ReadUInt32() != Magic || reader.ReadInt32() != FormatVersion ||
                reader.ReadInt32() != BakerVersion)
                return new Snapshot(region, rooms, false);

            string storedRegion = reader.ReadString();
            if (!string.Equals(storedRegion, region, StringComparison.OrdinalIgnoreCase))
                return new Snapshot(region, rooms, false);

            int count = Math.Max(0, reader.ReadInt32());
            for (int i = 0; i < count; i++)
            {
                RoomBake bake = ReadRoom(reader);
                if (bake != null && bake.RoomIndex >= 0) rooms[bake.RoomIndex] = bake;
            }

            return new Snapshot(region, rooms, true);
        }
        catch (Exception error)
        {
            lastError = error.Message;
            return new Snapshot(region, new Dictionary<int, RoomBake>(), false);
        }
    }

    private static void ValidateSomeRooms(MapPage page, EditorMapPresentationSnapshot snapshot)
    {
        EditorMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        if (rooms.Length == 0) return;

        int checks = Math.Min(ValidationRoomsPerFrame, rooms.Length);
        for (int i = 0; i < checks; i++)
        {
            if (validationCursor >= rooms.Length) validationCursor = 0;
            EditorMapRoomSnapshot room = rooms[validationCursor++];
            if (room == null) continue;

            if (!TryFindRoomPanel(page, room.RoomIndex, out RoomPanel panel)) continue;
            ulong signature = ComputeSourceSignature(page, panel);
            Snapshot cache = current;
            if (!cache.Rooms.TryGetValue(room.RoomIndex, out RoomBake bake))
            {
                cacheMisses++;
                continue;
            }

            if (bake.SourceSignature == signature)
            {
                cacheHits++;
                continue;
            }

            Dictionary<int, RoomBake> next = new(cache.Rooms);
            next.Remove(room.RoomIndex);
            current = new Snapshot(cache.Region, next, cache.DiskLoaded);
            cacheMisses++;
            MarkDirty();
        }
    }

    private static void CaptureSomeRooms(MapPage page, EditorMapPresentationSnapshot snapshot)
    {
        EditorMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        if (rooms.Length == 0) return;

        int captures = Math.Min(CaptureRoomsPerFrame, rooms.Length);
        for (int i = 0; i < captures; i++)
        {
            if (captureCursor >= rooms.Length) captureCursor = 0;
            EditorMapRoomSnapshot room = rooms[captureCursor++];
            if (room == null || !TryFindRoomPanel(page, room.RoomIndex, out RoomPanel panel)) continue;

            ulong signature = ComputeSourceSignature(page, panel);
            Snapshot cache = current;
            if (cache.Rooms.TryGetValue(room.RoomIndex, out RoomBake existing) &&
                existing.SourceSignature == signature &&
                existing.GeometryReady && existing.ShortcutsReady)
                continue;

            EditorMapRoomVisualSnapshot visual = MapRoomGeometryPresentationHub.Get(room.RoomIndex);
            bool geometryReady = visual?.Available == true && visual.DetailedRasterAvailable;

            int exitCount = 0;
            int denCount = 0;
            EditorMapRoomNodeSnapshot[] nodes = room.Nodes ?? Array.Empty<EditorMapRoomNodeSnapshot>();
            List<ExitMarker> exits = new();
            for (int n = 0; n < nodes.Length; n++)
            {
                EditorMapRoomNodeSnapshot node = nodes[n];
                if (node.Exit)
                {
                    exitCount++;
                    if (WorldMapShortcutPresentation.TryGetExitMouth(
                            room.RoomIndex,
                            node.NodeIndex,
                            out WorldMapShortcutPresentation.ShortcutMarker marker))
                        exits.Add(new ExitMarker(node.NodeIndex, marker.X, marker.Y));
                }
                else if (string.Equals(node.Type, "Den", StringComparison.OrdinalIgnoreCase))
                {
                    denCount++;
                }
            }

            WorldMapShortcutPresentation.ShortcutMarker[] holes =
                WorldMapShortcutPresentation.GetCreatureHoles(room.RoomIndex) ??
                Array.Empty<WorldMapShortcutPresentation.ShortcutMarker>();
            bool shortcutsReady = exits.Count >= exitCount && holes.Length >= denCount;

            // Keep an already verified disk half while the other half is still being rebaked.
            if (existing != null && existing.SourceSignature == signature)
            {
                if (!geometryReady && existing.GeometryReady) visual = existing.Visual;
                geometryReady |= existing.GeometryReady;
                if (!shortcutsReady && existing.ShortcutsReady)
                {
                    exits.Clear();
                    exits.AddRange(existing.Exits ?? Array.Empty<ExitMarker>());
                    holes = existing.CreatureHoles ?? Array.Empty<WorldMapShortcutPresentation.ShortcutMarker>();
                    shortcutsReady = true;
                }
            }

            if (!geometryReady && !shortcutsReady) continue;

            RoomBake bake = new()
            {
                RoomIndex = room.RoomIndex,
                RoomName = room.Name ?? string.Empty,
                SourceSignature = signature,
                GeometryReady = geometryReady,
                ShortcutsReady = shortcutsReady,
                Visual = visual ?? EditorMapRoomVisualSnapshot.Empty,
                Exits = exits.ToArray(),
                CreatureHoles = holes
            };

            Dictionary<int, RoomBake> next = new(cache.Rooms)
            {
                [room.RoomIndex] = bake
            };
            current = new Snapshot(cache.Region, next, cache.DiskLoaded);
            MarkDirty();
        }
    }

    private static bool TryFindRoomPanel(MapPage page, int roomIndex, out RoomPanel panel)
    {
        panel = null;
        if (page?.subNodes == null) return false;
        for (int i = 0; i < page.subNodes.Count; i++)
        {
            if (page.subNodes[i] is not RoomPanel candidate || candidate.roomRep?.room == null ||
                candidate.roomRep.room.index != roomIndex)
                continue;
            panel = candidate;
            return true;
        }
        return false;
    }

    internal static ulong ComputeSourceSignature(MapPage page, RoomPanel panel)
    {
        unchecked
        {
            ulong hash = 1469598103934665603UL;
            void MixByte(byte value) { hash ^= value; hash *= 1099511628211UL; }
            void MixInt(int value)
            {
                MixByte((byte)value);
                MixByte((byte)(value >> 8));
                MixByte((byte)(value >> 16));
                MixByte((byte)(value >> 24));
            }
            void MixLong(long value)
            {
                MixInt((int)value);
                MixInt((int)(value >> 32));
            }
            void MixString(string value)
            {
                string text = value ?? string.Empty;
                for (int i = 0; i < text.Length; i++)
                {
                    char ch = text[i];
                    MixByte((byte)ch);
                    MixByte((byte)(ch >> 8));
                }
                MixByte(0xFF);
            }

            AbstractRoom room = panel?.roomRep?.room;
            MapObject.RoomRepresentation rep = panel?.roomRep;
            MixInt(BakerVersion);
            MixString(page?.world?.name);
            MixString(room?.name);

            AbstractRoomNode[] nodes = room?.nodes ?? Array.Empty<AbstractRoomNode>();
            MixInt(nodes.Length);
            for (int i = 0; i < nodes.Length; i++) MixString(nodes[i].type?.value);

            if (rep?.mapTex != null)
            {
                FAtlasElement element = rep.mapTex;
                MixString(element.name);
                MixInt(Mathf.RoundToInt(element.sourcePixelSize.x * 1000f));
                MixInt(Mathf.RoundToInt(element.sourcePixelSize.y * 1000f));
                Rect uv = element.uvRect;
                MixInt(Mathf.RoundToInt(uv.x * 1000000f));
                MixInt(Mathf.RoundToInt(uv.y * 1000000f));
                MixInt(Mathf.RoundToInt(uv.width * 1000000f));
                MixInt(Mathf.RoundToInt(uv.height * 1000000f));
                Texture2D atlas = element.atlas?.texture as Texture2D;
                if (atlas != null)
                {
                    MixInt(atlas.width);
                    MixInt(atlas.height);
                }
            }
            else if (rep?.texture != null)
            {
                MixInt(rep.texture.width);
                MixInt(rep.texture.height);
            }

            string region = NormalizeRegion(page?.world?.name);
            string roomName = room?.name ?? string.Empty;
            AddFileStamp(Path.Combine("World", region + "-rooms", roomName + ".txt"));
            AddFileStamp(Path.Combine("World", region + "-rooms", roomName + "_settings.txt"));

            return hash;

            void AddFileStamp(string relative)
            {
                try
                {
                    string resolved = AssetManager.ResolveFilePath(relative);
                    if (string.IsNullOrWhiteSpace(resolved) || !File.Exists(resolved)) return;
                    FileInfo info = new(resolved);
                    MixLong(info.Length);
                    MixLong(info.LastWriteTimeUtc.Ticks);
                }
                catch
                {
                    // Atlas metadata above remains a stable fallback when a mod source is virtual.
                }
            }
        }
    }

    private static void MarkDirty()
    {
        dirty = true;
        dirtyFrame = Time.frameCount;
    }

    private static void FlushIfNeeded(bool force)
    {
        if (!dirty || string.IsNullOrEmpty(activePath) || activeRegion.Length == 0) return;
        if (!force && dirtyFrame >= 0 && Time.frameCount - dirtyFrame < SaveDelayFrames) return;

        try
        {
            string directory = Path.GetDirectoryName(activePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string temp = activePath + ".tmp";
            Snapshot snapshot = current;

            using (FileStream stream = File.Open(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (BinaryWriter writer = new(stream))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(BakerVersion);
                writer.Write(activeRegion);
                writer.Write(snapshot.Rooms.Count);
                foreach (RoomBake room in snapshot.Rooms.Values) WriteRoom(writer, room);
            }

            if (File.Exists(activePath)) File.Delete(activePath);
            File.Move(temp, activePath);
            dirty = false;
            dirtyFrame = -1;
            lastError = string.Empty;
        }
        catch (Exception error)
        {
            lastError = error.Message;
        }
    }

    private static void WriteRoom(BinaryWriter writer, RoomBake bake)
    {
        writer.Write(bake.RoomIndex);
        writer.Write(bake.RoomName ?? string.Empty);
        writer.Write(bake.SourceSignature);
        writer.Write(bake.GeometryReady);
        writer.Write(bake.ShortcutsReady);

        EditorMapRoomVisualSnapshot visual = bake.Visual ?? EditorMapRoomVisualSnapshot.Empty;
        writer.Write(visual.Available);
        writer.Write(visual.DetailedRasterAvailable);
        writer.Write(visual.WidthTiles);
        writer.Write(visual.HeightTiles);

        EditorMapRectSnapshot[] runs = visual.RasterRuns ?? Array.Empty<EditorMapRectSnapshot>();
        writer.Write(runs.Length);
        for (int i = 0; i < runs.Length; i++)
        {
            writer.Write(runs[i].X);
            writer.Write(runs[i].Y);
            writer.Write(runs[i].Width);
            writer.Write(runs[i].Height);
            writer.Write((int)runs[i].Kind);
        }

        EditorMapPolylineSnapshot[] curves = visual.Curves ?? Array.Empty<EditorMapPolylineSnapshot>();
        writer.Write(curves.Length);
        for (int i = 0; i < curves.Length; i++)
        {
            EditorMapPolylineSnapshot curve = curves[i] ?? new EditorMapPolylineSnapshot();
            writer.Write((int)curve.Kind);
            writer.Write(curve.Closed);
            EditorMapPointSnapshot[] points = curve.Points ?? Array.Empty<EditorMapPointSnapshot>();
            writer.Write(points.Length);
            for (int p = 0; p < points.Length; p++)
            {
                writer.Write(points[p].X);
                writer.Write(points[p].Y);
            }
        }

        EditorMapNodeVisualSnapshot[] nodes = visual.Nodes ?? Array.Empty<EditorMapNodeVisualSnapshot>();
        writer.Write(nodes.Length);
        for (int i = 0; i < nodes.Length; i++)
        {
            writer.Write(nodes[i].NodeIndex);
            writer.Write(nodes[i].X);
            writer.Write(nodes[i].Y);
        }

        ExitMarker[] exits = bake.Exits ?? Array.Empty<ExitMarker>();
        writer.Write(exits.Length);
        for (int i = 0; i < exits.Length; i++)
        {
            writer.Write(exits[i].NodeIndex);
            writer.Write(exits[i].X);
            writer.Write(exits[i].Y);
        }

        WorldMapShortcutPresentation.ShortcutMarker[] holes =
            bake.CreatureHoles ?? Array.Empty<WorldMapShortcutPresentation.ShortcutMarker>();
        writer.Write(holes.Length);
        for (int i = 0; i < holes.Length; i++)
        {
            writer.Write(holes[i].NodeIndex);
            writer.Write(holes[i].X);
            writer.Write(holes[i].Y);
        }
    }

    private static RoomBake ReadRoom(BinaryReader reader)
    {
        int roomIndex = reader.ReadInt32();
        string roomName = reader.ReadString();
        ulong signature = reader.ReadUInt64();
        bool geometryReady = reader.ReadBoolean();
        bool shortcutsReady = reader.ReadBoolean();
        bool available = reader.ReadBoolean();
        bool detailed = reader.ReadBoolean();
        float width = reader.ReadSingle();
        float height = reader.ReadSingle();

        int runCount = ClampCount(reader.ReadInt32(), 2_000_000);
        EditorMapRectSnapshot[] runs = new EditorMapRectSnapshot[runCount];
        for (int i = 0; i < runCount; i++)
        {
            runs[i] = new EditorMapRectSnapshot(
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                (EditorMapGeometryKind)reader.ReadInt32());
        }

        int curveCount = ClampCount(reader.ReadInt32(), 100_000);
        EditorMapPolylineSnapshot[] curves = new EditorMapPolylineSnapshot[curveCount];
        for (int i = 0; i < curveCount; i++)
        {
            EditorMapGeometryKind kind = (EditorMapGeometryKind)reader.ReadInt32();
            bool closed = reader.ReadBoolean();
            int pointCount = ClampCount(reader.ReadInt32(), 1_000_000);
            EditorMapPointSnapshot[] points = new EditorMapPointSnapshot[pointCount];
            for (int p = 0; p < pointCount; p++)
                points[p] = new EditorMapPointSnapshot(reader.ReadSingle(), reader.ReadSingle());
            curves[i] = new EditorMapPolylineSnapshot { Kind = kind, Closed = closed, Points = points };
        }

        int nodeCount = ClampCount(reader.ReadInt32(), 100_000);
        EditorMapNodeVisualSnapshot[] nodes = new EditorMapNodeVisualSnapshot[nodeCount];
        for (int i = 0; i < nodeCount; i++)
            nodes[i] = new EditorMapNodeVisualSnapshot(reader.ReadInt32(), reader.ReadSingle(), reader.ReadSingle());

        int exitCount = ClampCount(reader.ReadInt32(), 100_000);
        ExitMarker[] exits = new ExitMarker[exitCount];
        for (int i = 0; i < exitCount; i++)
            exits[i] = new ExitMarker(reader.ReadInt32(), reader.ReadSingle(), reader.ReadSingle());

        int holeCount = ClampCount(reader.ReadInt32(), 100_000);
        WorldMapShortcutPresentation.ShortcutMarker[] holes =
            new WorldMapShortcutPresentation.ShortcutMarker[holeCount];
        for (int i = 0; i < holeCount; i++)
            holes[i] = new WorldMapShortcutPresentation.ShortcutMarker(
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadInt32());

        return new RoomBake
        {
            RoomIndex = roomIndex,
            RoomName = roomName,
            SourceSignature = signature,
            GeometryReady = geometryReady,
            ShortcutsReady = shortcutsReady,
            Visual = new EditorMapRoomVisualSnapshot
            {
                Available = available,
                DetailedRasterAvailable = detailed,
                WidthTiles = width,
                HeightTiles = height,
                RasterRuns = runs,
                Curves = curves,
                Nodes = nodes
            },
            Exits = exits,
            CreatureHoles = holes
        };
    }

    private static int ClampCount(int count, int maximum)
    {
        if (count < 0 || count > maximum) throw new InvalidDataException("World Map cache count is invalid.");
        return count;
    }

    private static string CachePath(string region)
    {
        string root = Path.Combine(Application.persistentDataPath, "DryCycle", "WorldMapGpuCache");
        return Path.Combine(root, (region.Length == 0 ? "UNKNOWN" : region) + ".dcwm");
    }

    private static string NormalizeRegion(string region) =>
        string.IsNullOrWhiteSpace(region) ? string.Empty : region.Trim().ToUpperInvariant();
}
