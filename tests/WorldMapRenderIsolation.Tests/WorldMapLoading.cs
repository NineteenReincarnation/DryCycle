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
