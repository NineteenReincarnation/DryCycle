using System;
using System.Collections.Generic;
using System.IO;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Map;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Persistent, per-room authoring cache for the retained GPU World Map.
///
/// Rain World's own MapTex remains the room image cache. This file stores only work the rebuilt
/// editor would otherwise repeat every visit: detailed mod-terrain geometry, node positions and
/// exact shortcut mouths. Disk validation is deliberately one-shot per room per region session;
/// stable viewing never polls file timestamps. Explicit editor invalidation re-opens only the room
/// that changed.
/// </summary>
internal static class WorldMapGpuCache
{
    private const uint Magic = 0x4D574344; // "DCWM"
    private const int FormatVersion = 4;
    // v6 invalidates bakes produced while the retained cache could feed its own geometry hook.
    // Those bakes could be internally self-consistent yet visually stale, so source signatures
    // alone cannot safely migrate them.
    private const int BakerVersion = 6;
    private const int ValidationRoomsPerFrame = 6;
    private const int CaptureRoomsPerFrame = 8;
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
                        exit.X, exit.Y, exit.NodeIndex);
                }
                exitsByNode = index;
            }
            return index.TryGetValue(nodeIndex, out marker);
        }
    }

    private sealed class Snapshot
    {
        internal static readonly Snapshot Empty = new(string.Empty, new Dictionary<int, RoomBake>());

        internal Snapshot(string region, Dictionary<int, RoomBake> rooms)
        {
            Region = region ?? string.Empty;
            Rooms = rooms ?? new Dictionary<int, RoomBake>();
        }

        internal string Region { get; }
        internal Dictionary<int, RoomBake> Rooms { get; }
    }

    private static volatile Snapshot current = Snapshot.Empty;
    private static readonly HashSet<int> validatedRooms = new();
    private static readonly Dictionary<int, ulong> liveSignatures = new();
    private static string activeRegion = string.Empty;
    private static string activePath = string.Empty;
    private static int validationCursor;
    private static int captureCursor;
    private static int dirtyFrame = -1;
    private static int generation;
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
    internal static int Generation => generation;

    internal static bool TryGetRoom(int roomIndex, out RoomBake bake) =>
        current.Rooms.TryGetValue(roomIndex, out bake);

    internal static EditorMapRoomVisualSnapshot GetVisualOrEmpty(int roomIndex) =>
        TryGetRoom(roomIndex, out RoomBake bake) && bake.Visual != null
            ? bake.Visual
            : EditorMapRoomVisualSnapshot.Empty;

    internal static bool TryGetExit(
        int roomIndex,
        int nodeIndex,
        out WorldMapShortcutPresentation.ShortcutMarker marker)
    {
        marker = default;
        return TryGetRoom(roomIndex, out RoomBake bake) && bake.ShortcutsReady &&
               bake.TryGetExit(nodeIndex, out marker);
    }

    internal static WorldMapShortcutPresentation.ShortcutMarker[] GetCreatureHoles(int roomIndex) =>
        TryGetRoom(roomIndex, out RoomBake bake) && bake.ShortcutsReady
            ? bake.CreatureHoles ?? Array.Empty<WorldMapShortcutPresentation.ShortcutMarker>()
            : Array.Empty<WorldMapShortcutPresentation.ShortcutMarker>();

    internal static void Update(EditorSession session, EditorMapPresentationSnapshot snapshot)
    {
        WorldMapGpuRegionPreload.BeforeCacheUpdate(session, snapshot);
        try
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
            CaptureSomeRooms(snapshot);
            FlushIfNeeded(force: false);
        }
        finally
        {
            WorldMapGpuRegionPreload.AfterCacheUpdate();
        }
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
                !bake.GeometryReady || !bake.ShortcutsReady ||
                bake.Visual?.Available != true || !bake.Visual.DetailedRasterAvailable ||
                bake.Visual.WidthTiles <= 0f || bake.Visual.HeightTiles <= 0f)
                return false;
        }
        return true;
    }

    internal static void InvalidateRoom(int roomIndex)
    {
        Snapshot before = current;
        validatedRooms.Remove(roomIndex);
        liveSignatures.Remove(roomIndex);
        if (!before.Rooms.ContainsKey(roomIndex)) return;
        Dictionary<int, RoomBake> next = new(before.Rooms);
        next.Remove(roomIndex);
        Publish(before.Region, next);
        MarkDirty();
    }

    internal static void ClearDiskCache(string region)
    {
        WorldMapGpuRegionPreload.OnCacheClearing(region);
        string normalized = NormalizeRegion(region);
        try
        {
            string path = CachePath(normalized);
            if (File.Exists(path)) File.Delete(path);
            if (string.Equals(activeRegion, normalized, StringComparison.OrdinalIgnoreCase))
            {
                validatedRooms.Clear();
                liveSignatures.Clear();
                validationCursor = 0;
                captureCursor = 0;
                dirty = false;
                dirtyFrame = -1;
                Publish(normalized, new Dictionary<int, RoomBake>());
            }
            lastError = string.Empty;
        }
        catch (Exception error)
        {
            lastError = error.Message;
        }
    }

    internal static object CaptureResidentSnapshot(out string region, out string path)
    {
        region = activeRegion;
        path = activePath;
        return current;
    }

    internal static bool RestoreResidentSnapshot(string region, string path, object snapshot)
    {
        if (snapshot is not Snapshot typed) return false;

        string normalized = NormalizeRegion(region);
        current = typed;
        activeRegion = normalized;
        activePath = string.IsNullOrWhiteSpace(path) ? CachePath(normalized) : path;
        validatedRooms.Clear();
        liveSignatures.Clear();
        validationCursor = 0;
        captureCursor = 0;
        dirty = false;
        dirtyFrame = -1;
        lastError = string.Empty;
        cacheHits = 0;
        cacheMisses = 0;
        unchecked { generation++; }
        return true;
    }

    internal static object LoadResidentSnapshot(string region, string path) =>
        Load(NormalizeRegion(region), path, reportError: false);

    internal static string GetCachePath(string region) =>
        CachePath(NormalizeRegion(region));

    internal static void FlushNow() => FlushIfNeeded(force: true);

    internal static void ReleaseWorkingSet()
    {
        current = Snapshot.Empty;
        activeRegion = string.Empty;
        activePath = string.Empty;
        validatedRooms.Clear();
        liveSignatures.Clear();
        validationCursor = 0;
        captureCursor = 0;
        dirtyFrame = -1;
        dirty = false;
        lastError = string.Empty;
        cacheHits = 0;
        cacheMisses = 0;
        unchecked { generation++; }
    }

    private static void EnsureRegion(string region)
    {
        if (string.Equals(activeRegion, region, StringComparison.OrdinalIgnoreCase)) return;
        FlushIfNeeded(force: true);
        activeRegion = region;
        activePath = CachePath(region);
        validatedRooms.Clear();
        liveSignatures.Clear();
        validationCursor = 0;
        captureCursor = 0;
        dirty = false;
        dirtyFrame = -1;
        lastError = string.Empty;
        cacheHits = 0;
        cacheMisses = 0;
        Snapshot loaded = Load(region, activePath, reportError: true);
        current = loaded;
        unchecked { generation++; }
    }

    private static Snapshot Load(string region, string path, bool reportError)
    {
        Dictionary<int, RoomBake> rooms = new();
        if (region.Length == 0 || string.IsNullOrEmpty(path) || !File.Exists(path))
            return new Snapshot(region, rooms);
        try
        {
            using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using BinaryReader reader = new(stream);
            if (reader.ReadUInt32() != Magic || reader.ReadInt32() != FormatVersion ||
                reader.ReadInt32() != BakerVersion)
                return new Snapshot(region, rooms);
            if (!string.Equals(reader.ReadString(), region, StringComparison.OrdinalIgnoreCase))
                return new Snapshot(region, rooms);

            int count = CheckedCount(reader.ReadInt32(), 100_000);
            for (int i = 0; i < count; i++)
            {
                RoomBake bake = ReadRoom(reader);
                if (bake != null && bake.RoomIndex >= 0) rooms[bake.RoomIndex] = bake;
            }
            return new Snapshot(region, rooms);
        }
        catch (Exception error)
        {
            // Background region preloading also calls Load(). It must not write main-cache status:
            // a failed speculative preload could otherwise overwrite LastError while the active
            // region is healthy. The preload owner logs its own failure; only the active-region
            // load publishes an error into WorldMapGpuCache state.
            if (reportError)
                lastError = error.Message;
            return new Snapshot(region, new Dictionary<int, RoomBake>());
        }
    }

    private static void ValidateSomeRooms(MapPage page, EditorMapPresentationSnapshot snapshot)
    {
        EditorMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        if (rooms.Length == 0 || validatedRooms.Count >= rooms.Length) return;

        int checkedRooms = 0;
        int attempts = 0;
        while (checkedRooms < ValidationRoomsPerFrame && attempts < rooms.Length)
        {
            if (validationCursor >= rooms.Length) validationCursor = 0;
            EditorMapRoomSnapshot room = rooms[validationCursor++];
            attempts++;
            if (room == null || validatedRooms.Contains(room.RoomIndex) ||
                !TryFindRoomPanel(page, room.RoomIndex, out RoomPanel panel))
                continue;

            ulong signature = ComputeSourceSignature(page, panel);
            liveSignatures[room.RoomIndex] = signature;
            validatedRooms.Add(room.RoomIndex);
            checkedRooms++;

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
            Publish(cache.Region, next);
            cacheMisses++;
            MarkDirty();
        }
    }

    private static void CaptureSomeRooms(EditorMapPresentationSnapshot snapshot)
    {
        EditorMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        if (rooms.Length == 0) return;

        int captured = 0;
        int attempts = 0;
        while (captured < CaptureRoomsPerFrame && attempts < rooms.Length)
        {
            if (captureCursor >= rooms.Length) captureCursor = 0;
            EditorMapRoomSnapshot room = rooms[captureCursor++];
            attempts++;
            if (room == null || !liveSignatures.TryGetValue(room.RoomIndex, out ulong signature)) continue;

            Snapshot cache = current;
            cache.Rooms.TryGetValue(room.RoomIndex, out RoomBake existing);
            if (existing != null && existing.SourceSignature == signature &&
                existing.GeometryReady && existing.ShortcutsReady)
                continue;

            EditorMapRoomVisualSnapshot visual = MapRoomGeometryPresentationHub.Get(room.RoomIndex);
            bool geometryReady = visual?.Available == true && visual.DetailedRasterAvailable;
            List<ExitMarker> exits = new();
            int exitCount = 0;
            int denCount = 0;
            EditorMapRoomNodeSnapshot[] nodes = room.Nodes ?? Array.Empty<EditorMapRoomNodeSnapshot>();
            for (int n = 0; n < nodes.Length; n++)
            {
                EditorMapRoomNodeSnapshot node = nodes[n];
                if (node.Exit)
                {
                    exitCount++;
                    if (WorldMapShortcutPresentation.TryGetExitMouth(
                            room.RoomIndex, node.NodeIndex,
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

            if (existing != null && existing.SourceSignature == signature)
            {
                if (!geometryReady && existing.GeometryReady)
                {
                    visual = existing.Visual;
                    geometryReady = true;
                }
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
            Dictionary<int, RoomBake> next = new(cache.Rooms) { [room.RoomIndex] = bake };
            Publish(cache.Region, next);
            MarkDirty();
            captured++;
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
                MixByte((byte)value); MixByte((byte)(value >> 8));
                MixByte((byte)(value >> 16)); MixByte((byte)(value >> 24));
            }
            void MixLong(long value) { MixInt((int)value); MixInt((int)(value >> 32)); }
            void MixString(string value)
            {
                string text = value ?? string.Empty;
                for (int i = 0; i < text.Length; i++)
                {
                    char ch = text[i];
                    MixByte((byte)ch); MixByte((byte)(ch >> 8));
                }
                MixByte(0xFF);
            }
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
                catch { }
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
                if (element.atlas?.texture is Texture2D atlas)
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
        }
    }

    private static void Publish(string region, Dictionary<int, RoomBake> rooms)
    {
        current = new Snapshot(region, rooms);
        unchecked { generation++; }
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
            writer.Write(runs[i].X); writer.Write(runs[i].Y);
            writer.Write(runs[i].Width); writer.Write(runs[i].Height); writer.Write((int)runs[i].Kind);
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
                writer.Write(points[p].X); writer.Write(points[p].Y);
            }
        }

        EditorMapNodeVisualSnapshot[] nodes = visual.Nodes ?? Array.Empty<EditorMapNodeVisualSnapshot>();
        writer.Write(nodes.Length);
        for (int i = 0; i < nodes.Length; i++)
        {
            writer.Write(nodes[i].NodeIndex); writer.Write(nodes[i].X); writer.Write(nodes[i].Y);
        }

        ExitMarker[] exits = bake.Exits ?? Array.Empty<ExitMarker>();
        writer.Write(exits.Length);
        for (int i = 0; i < exits.Length; i++)
        {
            writer.Write(exits[i].NodeIndex); writer.Write(exits[i].X); writer.Write(exits[i].Y);
        }

        WorldMapShortcutPresentation.ShortcutMarker[] holes =
            bake.CreatureHoles ?? Array.Empty<WorldMapShortcutPresentation.ShortcutMarker>();
        writer.Write(holes.Length);
        for (int i = 0; i < holes.Length; i++)
        {
            writer.Write(holes[i].NodeIndex); writer.Write(holes[i].X); writer.Write(holes[i].Y);
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

        int runCount = CheckedCount(reader.ReadInt32(), 2_000_000);
        EditorMapRectSnapshot[] runs = new EditorMapRectSnapshot[runCount];
        for (int i = 0; i < runCount; i++)
            runs[i] = new EditorMapRectSnapshot(
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                (EditorMapGeometryKind)reader.ReadInt32());

        int curveCount = CheckedCount(reader.ReadInt32(), 100_000);
        EditorMapPolylineSnapshot[] curves = new EditorMapPolylineSnapshot[curveCount];
        for (int i = 0; i < curveCount; i++)
        {
            EditorMapGeometryKind kind = (EditorMapGeometryKind)reader.ReadInt32();
            bool closed = reader.ReadBoolean();
            int pointCount = CheckedCount(reader.ReadInt32(), 1_000_000);
            EditorMapPointSnapshot[] points = new EditorMapPointSnapshot[pointCount];
            for (int p = 0; p < pointCount; p++)
                points[p] = new EditorMapPointSnapshot(reader.ReadSingle(), reader.ReadSingle());
            curves[i] = new EditorMapPolylineSnapshot { Kind = kind, Closed = closed, Points = points };
        }

        int nodeCount = CheckedCount(reader.ReadInt32(), 100_000);
        EditorMapNodeVisualSnapshot[] nodes = new EditorMapNodeVisualSnapshot[nodeCount];
        for (int i = 0; i < nodeCount; i++)
            nodes[i] = new EditorMapNodeVisualSnapshot(reader.ReadInt32(), reader.ReadSingle(), reader.ReadSingle());

        int exitCount = CheckedCount(reader.ReadInt32(), 100_000);
        ExitMarker[] exits = new ExitMarker[exitCount];
        for (int i = 0; i < exitCount; i++)
            exits[i] = new ExitMarker(reader.ReadInt32(), reader.ReadSingle(), reader.ReadSingle());

        int holeCount = CheckedCount(reader.ReadInt32(), 100_000);
        WorldMapShortcutPresentation.ShortcutMarker[] holes =
            new WorldMapShortcutPresentation.ShortcutMarker[holeCount];
        for (int i = 0; i < holeCount; i++)
        {
            int nodeIndex = reader.ReadInt32();
            float x = reader.ReadSingle();
            float y = reader.ReadSingle();
            holes[i] = new WorldMapShortcutPresentation.ShortcutMarker(x, y, nodeIndex);
        }

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

    private static int CheckedCount(int count, int maximum)
    {
        if (count < 0 || count > maximum)
            throw new InvalidDataException("World Map cache count is invalid.");
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
