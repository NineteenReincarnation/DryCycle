using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DryCycle.DevUI.DevTool.Map.PlayerMap;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Map;

// Immutable, CPU-only data shared by the thumbnail, terrain and exact shortcut consumers.
// Paths are resolved on the game thread; no live Room/World or Unity objects reach a worker.
internal sealed class WorldMapRoomSource
{
    internal RoomMapBake Bake;
    internal Color[] Pixels;
    internal int WaterLevel;
    internal EditorMapRectSnapshot[] Raster;
    internal EditorMapRectSnapshot[] Terrain;
    internal EditorMapPolylineSnapshot[] Curves;
    internal MapViewFileStamp RoomStamp, SettingsStamp;
    internal long EstimatedBytes => (long)Pixels.Length * 24 + (Raster.Length + Terrain.Length) * 32L + Curves.Length * 160L;

    internal static WorldMapRoomSource Load(string name, MapViewFileStamp room, MapViewFileStamp settings)
    {
        string[] lines = File.ReadAllLines(room.Path);
        RoomPreprocessor.VersionFix(ref lines);
        RoomMapSource source = RoomMapTextDecoder.Parse(lines);
        RoomMapBake bake = RoomMapSemanticCompiler.Compile(0, name, source, room.Path);
        Color[] pixels = new Color[bake.Pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            RoomMapPixel pixel = bake.Pixels[i];
            Color color = pixel.Kind switch
            {
                RoomMapPixelKind.Solid or RoomMapPixelKind.UnknownShortcut => new Color(.3f, .3f, .3f),
                RoomMapPixelKind.BackWall => new Color(.5f, .5f, .5f),
                RoomMapPixelKind.Structure => new Color(.5f, .3f, .3f),
                RoomMapPixelKind.RoomExit => new Color(0f, 1f, .2f),
                RoomMapPixelKind.CreatureHole => new Color(1f, 0f, 1f),
                RoomMapPixelKind.NpcTransport => new Color(.7f, 0f, 0f),
                RoomMapPixelKind.RegionTransport => Color.black,
                RoomMapPixelKind.NormalShortcut => Color.white,
                _ => new Color(.6f, .6f, .6f)
            };
            pixels[i] = pixel.Water ? Color.Lerp(color, Color.blue, .3f) : color;
        }
        string terrainText = settings.Length > 0 ? File.ReadAllText(settings.Path) : string.Empty;
        MapRoomGeometryPresentationHub.BuildCurveGeometry(MapTerrainSourceDecoder.Parse(terrainText, name),
            bake.Width, out List<EditorMapPolylineSnapshot> curves, out List<EditorMapRectSnapshot> terrain);
        if (!room.Matches(MapViewFileStamp.Capture(room.Path)) ||
            !settings.Matches(MapViewFileStamp.Capture(settings.Path)))
            throw new IOException("Room sources changed during map loading: " + name);
        int.TryParse(lines[1].Split('|')[1], out int water);
        return new WorldMapRoomSource
        {
            Bake = bake, Pixels = pixels, WaterLevel = water,
            Raster = MapRoomGeometryPresentationHub.BuildRasterRuns(pixels, bake.Width, bake.Height),
            Terrain = terrain.ToArray(), Curves = curves.ToArray(), RoomStamp = room, SettingsStamp = settings
        };
    }
}

// At most two CPU jobs and a bounded warm cache. Reset never waits for an old region's job.
// Completion is accepted only for the caller's current generation and request identity.
internal sealed class WorldMapRoomSourceQueue
{
    private sealed class Work
    {
        internal int Generation, RoomIndex;
        internal string Name;
        internal MapViewFileStamp Room, Settings;
        internal Task<WorldMapRoomSource> Task;
    }
    private sealed class Warm { internal WorldMapRoomSource Source; internal long Used; }
    private readonly List<Work> workers = new();
    private readonly Dictionary<int, Work> pending = new();
    private readonly Dictionary<string, Warm> warm = new(StringComparer.OrdinalIgnoreCase);
    private int generation;
    private long clock, warmBytes;
    internal int ActiveWorkers => workers.Count;
    internal int Loads { get; private set; }
    internal int Hits { get; private set; }

    internal bool TryGet(int index, string name, MapViewFileStamp room, MapViewFileStamp settings,
        out WorldMapRoomSource source, out Exception error)
    {
        source = null; error = null;
        // Reap retired work without publishing it into another map's room indices.
        for (int i = workers.Count - 1; i >= 0; i--)
        {
            Work work = workers[i];
            if (work.Generation == generation || !work.Task.IsCompleted) continue;
            if (work.Task.IsFaulted)
                global::DryCycle.Plugin.Logger?.LogWarning("Retired WorldMap load failed for " + work.Name + ": " + work.Task.Exception);
            workers.RemoveAt(i);
        }
        if (pending.TryGetValue(index, out Work current))
        {
            if (!current.Task.IsCompleted) return false;
            pending.Remove(index); workers.Remove(current);
            if (current.Task.IsFaulted) error = current.Task.Exception.GetBaseException();
            else if (current.Room.Matches(room) && current.Settings.Matches(settings))
            {
                source = current.Task.Result;
                Remember(source);
                return true;
            }
            if (error != null) return false;
        }
        if (warm.TryGetValue(room.Path, out Warm cached) &&
            cached.Source.RoomStamp.Matches(room) && cached.Source.SettingsStamp.Matches(settings))
        {
            cached.Used = ++clock; Hits++; source = cached.Source; return true;
        }
        if (workers.Count >= 2) return false;
        Work request = new() { RoomIndex = index, Name = name, Generation = generation, Room = room, Settings = settings };
        request.Task = Task.Run(() => WorldMapRoomSource.Load(name, room, settings));
        pending[index] = request; workers.Add(request); Loads++;
        return false;
    }

    private void Remember(WorldMapRoomSource source)
    {
        if (warm.TryGetValue(source.RoomStamp.Path, out Warm old)) warmBytes -= old.Source.EstimatedBytes;
        warm[source.RoomStamp.Path] = new Warm { Source = source, Used = ++clock };
        warmBytes += source.EstimatedBytes;
        while (warmBytes > 64L * 1024 * 1024 && warm.Count > 0)
        {
            string oldest = null; long used = long.MaxValue;
            foreach (var item in warm) if (item.Value.Used < used) { oldest = item.Key; used = item.Value.Used; }
            warmBytes -= warm[oldest].Source.EstimatedBytes; warm.Remove(oldest);
        }
    }

    internal void Reset()
    {
        unchecked { generation++; }
        pending.Clear();
        Loads = Hits = 0;
    }
}
