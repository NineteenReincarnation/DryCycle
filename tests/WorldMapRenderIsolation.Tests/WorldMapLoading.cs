using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Num = System.Numerics;

public static partial class MapRenderIsolationTests
{
    private static void ExerciseWorldMapLoading()
    {
        Type queueType = Core("Map.WorldMapRoomSourceQueue"), stampType = Core("Map.MapViewFileStamp");
        object queue = Activator.CreateInstance(queueType, true);
        object Stamp(string path) => stampType.GetMethod("Capture", Flags).Invoke(null, new object[] { path });
        object[] Args(int index, string path) => new object[] { index, Path.GetFileNameWithoutExtension(path),
            Stamp(path), Stamp(Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + "_settings.txt")), null, null };
        bool Poll(object[] args)
        {
            bool ready = (bool)queueType.GetMethod("TryGet", Flags).Invoke(queue, args);
            if (args[5] is Exception e) throw e;
            return ready;
        }
        string root = Path.Combine(game, "RainWorld_Data/StreamingAssets");
        string[] Rooms(string folder) => Directory.GetFiles(folder, "*.txt")
            .Where(p => !Path.GetFileName(p).Contains("settings") && !Path.GetFileName(p).Contains("_map") &&
                File.ReadLines(p).Take(2).LastOrDefault()?.Contains("*") == true).OrderBy(p => p).ToArray();
        string[] b5 = Rooms(Path.Combine(root, "mods/Ancient Site/world/b5-rooms"));
        string[] su = Rooms(Path.Combine(root, "world/su-rooms"));
        string[] sh = Rooms(Path.Combine(root, "world/sh-rooms"));
        Check(b5.Length >= 60 && su.Length >= 60 && sh.Length >= 60, "Three real region room sets are available.");

        void LoadRegion(string label, string[] paths, bool expectWarm)
        {
            Invoke(queue, "Reset");
            object[][] args = paths.Select((p, i) => Args(i, p)).ToArray();
            bool[] ready = new bool[paths.Length];
            int count = 0, frames = 0, maxWorkers = 0;
            long totalMainTicks = 0, peakMainTicks = 0;
            var timer = Stopwatch.StartNew();
            while (count < paths.Length && timer.ElapsedMilliseconds < 20000)
            {
                long started = Stopwatch.GetTimestamp();
                int uploaded = 0;
                for (int i = 0; i < paths.Length && uploaded < 2; i++)
                {
                    if (ready[i] || !Poll(args[i])) continue;
                    object data = args[i][4], bake = Get(data, "Bake");
                    int width = (int)Get(bake, "Width"), height = (int)Get(bake, "Height");
                    Color[] pixels = (Color[])Get(data, "Pixels");
                    if (width <= 0 || height <= 0 || pixels.Length != width * height) throw new Exception("Invalid room raster.");
                    var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                    texture.SetPixels(pixels); texture.Apply(false, false);
                    if (paths[i].EndsWith("b5_potdb01.txt", StringComparison.OrdinalIgnoreCase))
                    {
                        Check(((Array)Get(data, "Curves")).Length > 0, "Background B5 decode retains native/custom terrain.");
                        File.WriteAllBytes(Path.Combine(output, "B5_POTDB01-background-source.png"), texture.EncodeToPNG());
                    }
                    UnityEngine.Object.DestroyImmediate(texture);
                    ready[i] = true; count++; uploaded++;
                }
                maxWorkers = Math.Max(maxWorkers, (int)queueType.GetProperty("ActiveWorkers", Flags).GetValue(queue));
                long ticks = Stopwatch.GetTimestamp() - started;
                totalMainTicks += ticks; peakMainTicks = Math.Max(peakMainTicks, ticks); frames++;
                if (count < paths.Length) Thread.Sleep(16); // Host a 60 Hz caller; never busy-wait on a task.
            }
            int loads = (int)queueType.GetProperty("Loads", Flags).GetValue(queue);
            int hits = (int)queueType.GetProperty("Hits", Flags).GetValue(queue);
            Check(count == paths.Length, label + ": every drawable room finishes without clicking a room.");
            Check(maxWorkers <= 2, label + ": background concurrency remains bounded at two jobs.");
            if (expectWarm) Check(loads == 0 && hits == paths.Length, label + ": revisiting the region reuses decoded sources without new disk parses.");
            results.Add(label + ": rooms=" + count + ", elapsed=" + timer.ElapsedMilliseconds + " ms, frames=" + frames +
                ", main avg=" + (totalMainTicks * 1000d / Stopwatch.Frequency / frames).ToString("F2") +
                " ms, main peak=" + (peakMainTicks * 1000d / Stopwatch.Frequency).ToString("F2") +
                " ms, decoded=" + loads + ", warm hits=" + hits + ". Includes real Unity texture upload; excludes full game UI/render.");
        }
        LoadRegion("B5 cold", b5, false);
        LoadRegion("SU cold", su, false);
        LoadRegion("SH cold", sh, false);
        LoadRegion("B5 revisit", b5, true);

        // Use a new queue to guarantee in-flight work, and reuse index 0 in a different region.
        queue = Activator.CreateInstance(queueType, true);
        var old = Args(0, b5[0]); Poll(old); Invoke(queue, "Reset");
        var next = Args(0, su[0]); var wait = Stopwatch.StartNew();
        while (!Poll(next) && wait.ElapsedMilliseconds < 5000) Thread.Sleep(2);
        Check(next[4] != null && (string)Get(Get(next[4], "Bake"), "RoomName") == Path.GetFileNameWithoutExtension(su[0]),
            "An old region's completion cannot populate the new region's reused room index.");
        string badPath = Path.Combine(output, "invalid-room.txt"); File.WriteAllText(badPath, "invalid fixture");
        object[] bad = Args(99, badPath); wait.Restart();
        while (bad[5] == null && wait.ElapsedMilliseconds < 5000)
        { queueType.GetMethod("TryGet", Flags).Invoke(queue, bad); Thread.Sleep(2); }
        Check(bad[5] is Exception, "Malformed source preserves its original worker exception.");
        ExerciseAsyncRouteLoading();
        ExerciseIndustrialMapLoading();
    }

    // Exercise the installed, merged HI topology, including Watcher rooms and ambiguous links.
    // Use production source loading, routing and Unity renderers; fixtures only provide game files
    // and detached room positions normally supplied by MapPage.
    private static void ExerciseIndustrialMapLoading()
    {
        string root = Path.Combine(game, "RainWorld_Data/StreamingAssets");
        string ResolveFile(string relative)
        {
            foreach (string folder in new[] { "mergedmods", "mods/watcher", "mods/moreslugcats", "" })
            {
                string file = Path.Combine(root, folder, relative);
                if (File.Exists(file)) return file;
            }
            return Path.Combine(root, relative);
        }
        string Read(string path) => File.Exists(ResolveFile(path)) ? File.ReadAllText(ResolveFile(path)) : "";
        object Property(object target, string name) => target.GetType().GetProperty(name, Flags).GetValue(target);
        void PropertySet(object target, string name, object value) => target.GetType().GetProperty(name, Flags).SetValue(target, value);
        string mapText = Read("world/hi/map_hi.txt");
        Func<string, (string data, string settings)> noRooms = _ => (null, "");
        object imported = Core("Map.Cartography.CartographyRegionLoader").GetMethod("Parse", Flags).Invoke(null,
            new object[] { "HI", "White", Read("world/hi/world_hi.txt"), mapText, Read("world/hi/properties.txt"), "", noRooms });
        var names = ((IDictionary)Get(imported, "Rooms")).Keys.Cast<string>().ToArray();
        var indices = names.Select((name, index) => (name, index)).ToDictionary(p => p.name, p => p.index, StringComparer.OrdinalIgnoreCase);
        var positions = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in mapText.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon > 0 && line.Contains("><")) positions[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Split(new[] { "><" }, StringSplitOptions.None);
        }
        float Number(string text) => float.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        object scene = New("WorldMapScene"), rooms = New("WorldMapRoomResourceStore"), routes = New("WorldMapConnectionResourceStore");
        object roomRenderer = New("WorldMapRetainedRoomRenderer"), routeRenderer = New("WorldMapRetainedConnectionRenderer"), surface = New("WorldMapRenderTextureSurface");
        object queue = Activator.CreateInstance(Core("Map.WorldMapRoomSourceQueue"), true);
        var textures = new List<Texture2D>();
        var ids = Enumerable.Range(0, names.Length).ToArray();
        for (int i = 0; i < names.Length; i++)
        {
            object room = Invoke(scene, "GetOrCreateRoom", i); Set(room, "Name", names[i]);
            if (positions.TryGetValue(names[i], out string[] p))
                Set(room, "WorldPosition", new Num.Vector2(Number(p[2]), Number(p[3])));
        }
        foreach (object link in (IEnumerable)Get(imported, "Connections"))
        {
            string from = (string)Get(link, "From"), to = (string)Get(link, "To");
            int fromPort = (int)Get(link, "FromPort"), toPort = (int)Get(link, "ToPort");
            object edge = Invoke(scene, "GetOrCreateConnection", from + ":" + fromPort + "|" + to + ":" + toPort);
            Set(edge, "FromRoomIndex", indices[from]); Set(edge, "ToRoomIndex", indices[to]);
            Set(edge, "FromNodeIndex", fromPort); Set(edge, "ToNodeIndex", toPort); Set(edge, "Ambiguous", (bool)Get(link, "Ambiguous"));
        }
        var edgeIds = ((IDictionary)Get(scene, "connections")).Keys.Cast<string>().ToArray();
        Check(names.Length >= 70 && edgeIds.Length >= 80, "HI fixture includes the complete installed topology, not a two-room route sample.");
        object dirty = New("WorldMapDirtySet"); PropertySet(dirty, "FullRebuild", true);
        Invoke(routes, "ApplyDirty", scene, rooms, dirty);
        object[][] requests = names.Select((name, i) =>
        {
            string folder = name.StartsWith("GATE_") ? "world/gates/" : "world/hi-rooms/";
            object Stamp(string file) => Core("Map.MapViewFileStamp").GetMethod("Capture", Flags).Invoke(null, new object[] { ResolveFile(file) });
            return new object[] { i, name, Stamp(folder + name.ToLowerInvariant() + ".txt"), Stamp(folder + name.ToLowerInvariant() + "_settings.txt"), null, null };
        }).ToArray();
        var done = new bool[names.Length]; int ready = 0, frames = 0;
        var bounds = ((IDictionary)Get(scene, "rooms")).Values.Cast<object>().Select(r => (Num.Vector2)Get(r, "WorldPosition")).ToArray();
        Num.Vector2 low = new(bounds.Min(p => p.X) - 80, bounds.Min(p => p.Y) - 80);
        Num.Vector2 high = new(bounds.Max(p => p.X) + 450, bounds.Max(p => p.Y) + 450);
        float zoom = Math.Min(1000 / (high.X - low.X), 1000 / (high.Y - low.Y));
        object view = Activator.CreateInstance(Front("WorldMapViewTransform"), Flags, null,
            new object[] { Num.Vector2.Zero, new Num.Vector2(1024, 1024), -low * zoom, zoom }, null);
        long peakMain = 0, totalMain = 0;
        var timer = Stopwatch.StartNew();
        try
        {
            while (timer.ElapsedMilliseconds < 30000)
            {
                long started = Stopwatch.GetTimestamp(); int uploaded = 0;
                for (int i = 0; i < names.Length && uploaded < 2; i++)
                {
                    if (done[i]) continue;
                    bool complete = (bool)queue.GetType().GetMethod("TryGet", Flags).Invoke(queue, requests[i]);
                    if (requests[i][5] is Exception error) throw error;
                    if (!complete) continue;
                    object source = requests[i][4], bake = Get(source, "Bake");
                    int width = (int)Get(bake, "Width"), height = (int)Get(bake, "Height");
                    var texture = new Texture2D(width, height, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
                    textures.Add(texture); texture.SetPixels((Color[])Get(source, "Pixels")); texture.Apply(false, false);
                    object visual = Activator.CreateInstance(Core("Map.EditorMapRoomVisualSnapshot"));
                    PropertySet(visual, "Available", true); PropertySet(visual, "DetailedRasterAvailable", true);
                    PropertySet(visual, "WidthTiles", (float)width); PropertySet(visual, "HeightTiles", (float)height);
                    PropertySet(visual, "RasterRuns", Get(source, "Raster")); PropertySet(visual, "TerrainRuns", Get(source, "Terrain")); PropertySet(visual, "Curves", Get(source, "Curves"));
                    var anchors = ((IEnumerable)Get(bake, "NodeAnchors")).Cast<object>().ToArray();
                    Array nodes = Array.CreateInstance(Core("Map.EditorMapNodeVisualSnapshot"), anchors.Length);
                    for (int n = 0; n < anchors.Length; n++) nodes.SetValue(Activator.CreateInstance(Core("Map.EditorMapNodeVisualSnapshot"),
                        new object[] { Property(anchors[n], "NodeIndex"), Property(anchors[n], "EntranceX"), Property(anchors[n], "EntranceY") }), n);
                    PropertySet(visual, "Nodes", nodes);
                    object geometry = Front("RoomGeometryBuilder").GetMethod("Build", Flags).Invoke(null, new object[] { i, visual, 1 });
                    object resource = New("WorldMapRoomResourceStore+RoomResource"); Set(resource, "Geometry", geometry); Set(resource, "GeometryGeneration", 1L);
                    object descriptor = Activator.CreateInstance(Front("WorldMapLegacyRoomSourceService+RoomTextureSource"), Flags, null,
                        new object[] { texture, new Rect(0, 0, 1, 1), (float)width, (float)height, 1, "" }, null);
                    object thumbnail = Get(resource, "Thumbnail"); Invoke(thumbnail, "Stage", descriptor); Invoke(thumbnail, "CommitPending");
                    ((IDictionary)Get(rooms, "rooms"))[i] = resource;
                    Invoke(routes, "InvalidateRooms", scene, rooms, new[] { i }, true);
                    ready++; done[i] = true; uploaded++;
                }
                Invoke(routes, "Update", scene, rooms);
                if (Get(routes, "routeWork") is Task work && work.IsFaulted) throw work.Exception;
                Action<Transform> prepare = parent =>
                {
                    Invoke(roomRenderer, "SynchronizeVisible", scene, rooms, ids, parent);
                    Invoke(routeRenderer, "SynchronizeVisible", routes, edgeIds, true, parent);
                };
                if (!(bool)Invoke(surface, "Render", view, prepare)) throw new Exception("HI surface failed: " + Get(surface, "error"));
                long elapsed = Stopwatch.GetTimestamp() - started;
                peakMain = Math.Max(peakMain, elapsed); totalMain += elapsed; frames++;
                if (ready == names.Length && (int)Property(routes, "PendingCount") == 0 &&
                    (bool)Property(routes, "CrossingsCurrent") && !(bool)Property(roomRenderer, "HasPendingUploads")) break;
                Thread.Sleep(16);
            }
            Check(ready == names.Length, "All HI thumbnails load automatically from real installed room files.");
            results.Add("HI route state: committed=" + Property(routes, "Count") + "/" + edgeIds.Length + ", pending=" + Property(routes, "PendingCount") +
                ", worker=" + (Get(routes, "routeWork") as Task)?.Status + ", built=" + Property(routes, "RouteBuildCount") +
                ", route peak=" + Property(routes, "RouteBuildPeakMilliseconds") + ", layouts=" + Property(routes, "CorridorLayoutCount") +
                ", crossings=" + Property(routes, "CrossingsCurrent") + ", main peak=" + (peakMain * 1000d / Stopwatch.Frequency).ToString("F2") + " ms.");
            int blocked = ((IDictionary)Get(routes, "routes")).Values.Cast<object>().Count(route => ((Num.Vector2[])Get(route, "Points")).Length < 2);
            results.Add("HI unroutable overlapping/blocked connections: " + blocked + ". No fabricated line is treated as a successful route.");
            Check((int)Property(routes, "Count") == edgeIds.Length && (int)Property(routes, "PendingCount") == 0 && (bool)Property(routes, "CrossingsCurrent"),
                "Every HI routing job and corridor/crossing pass settles within 30 seconds, including blocked routes.");
            Check((int)Property(roomRenderer, "RetainedRoomCount") == names.Length && !(bool)Property(roomRenderer, "HasPendingUploads"), "Every HI room reaches its retained GPU mesh.");
            Check(!((GameObject)Get(surface, "sceneObject")).activeSelf, "HI meshes remain isolated from gameplay after rendering.");
            Check(OpaquePixels((RenderTexture)Get(surface, "presented"), "HI-complete-map.png") > 1000, "The complete HI GPU surface contains the loaded map.");
            results.Add("HI integrated: rooms=" + ready + ", links=" + edgeIds.Length + ", elapsed=" + timer.ElapsedMilliseconds + " ms, frames=" + frames +
                ", main avg=" + (totalMain * 1000d / Stopwatch.Frequency / frames).ToString("F2") + " ms, main peak=" + (peakMain * 1000d / Stopwatch.Frequency).ToString("F2") + " ms; includes source decode, routes, mesh upload and Camera.Render, excludes gameplay/ImGui.");
        }
        finally
        {
            Invoke(roomRenderer, "Reset"); Invoke(routeRenderer, "Reset"); Invoke(surface, "Reset"); Invoke(routes, "Reset"); Invoke(queue, "Reset");
            foreach (var texture in textures) UnityEngine.Object.Destroy(texture);
        }
    }

    private static void ExerciseAsyncRouteLoading()
    {
        object scene = New("WorldMapScene"), rooms = New("WorldMapRoomResourceStore"), store = New("WorldMapConnectionResourceStore");
        object a = Invoke(scene, "GetOrCreateRoom", 0), b = Invoke(scene, "GetOrCreateRoom", 1);
        Set(a, "WorldPosition", Num.Vector2.Zero); Set(b, "WorldPosition", new Num.Vector2(200, 50));
        object connection = Invoke(scene, "GetOrCreateConnection", "async-route");
        Set(connection, "FromRoomIndex", 0); Set(connection, "ToRoomIndex", 1); Set(connection, "ToNodeIndex", -1);
        Array connections = Array.CreateInstance(connection.GetType(), 1); connections.SetValue(connection, 0);
        object Capture() => Front("WorldMapWorldSpaceRouter").GetMethod("Capture", Flags).Invoke(null,
            new object[] { scene, rooms, connections, null, null, null, null, null });
        Invoke(store, "StartRouteWork", Capture());
        var timer = Stopwatch.StartNew();
        while (Get(store, "routeWork") != null && timer.ElapsedMilliseconds < 5000)
        { Invoke(store, "DrainRouteWork", scene); Thread.Sleep(2); }
        Check(((IDictionary)Get(store, "routes")).Count == 1, "Background route solve commits a complete route.");
        Invoke(store, "StartLayoutWork"); timer.Restart();
        while (Get(store, "routeWork") != null && timer.ElapsedMilliseconds < 5000)
        { Invoke(store, "DrainRouteWork", scene); Thread.Sleep(2); }
        Check(!(bool)Get(store, "corridorLayoutDirty") && !(bool)Get(store, "crossingLayoutDirty"),
            "Corridor and crossing work completes off the calling thread and publishes together.");
        Invoke(store, "StartRouteWork", Capture()); Invoke(store, "Reset"); timer.Restart();
        while (Get(store, "routeWork") != null && timer.ElapsedMilliseconds < 5000)
        { Invoke(store, "DrainRouteWork", scene); Thread.Sleep(2); }
        Check(((IDictionary)Get(store, "routes")).Count == 0, "A route completion after region reset is discarded.");
        Invoke(store, "StartRouteWork", Capture()); Invoke(store, "Enqueue", "async-route"); timer.Restart();
        while (Get(store, "routeWork") != null && timer.ElapsedMilliseconds < 5000)
        { Invoke(store, "DrainRouteWork", scene); Thread.Sleep(2); }
        Check(((IDictionary)Get(store, "routes")).Count == 0 && (int)Front("WorldMapConnectionResourceStore").GetProperty("PendingCount", Flags).GetValue(store) > 0,
            "An edit during routing rejects stale coordinates and keeps the connection queued.");
    }
}
