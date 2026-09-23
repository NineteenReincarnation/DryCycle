using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DryCycle.DevUI.DevTool.Map.Cartography;

internal static partial class Program
{
    private static void RegionLoading()
    {
        string game = Path.Combine(output, "installation-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(game, "RainWorld_Data", "StreamingAssets");
        string folder = Path.Combine(root, "world", "b5");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(root, "world", "b5-rooms"));
        Directory.CreateDirectory(Path.Combine(root, "world", "not-a-region"));
        File.WriteAllText(Path.Combine(folder, "world_b5.txt"), "ROOMS\nB5_A01 : DISCONNECTED\nEND ROOMS\n");
        string map = Path.Combine(folder, "map_b5.txt");
        File.WriteAllText(map, "B5_A01: 0><0><12><24><1><\n");
        File.WriteAllText(Path.Combine(folder, "displayname.txt"), "远古遗迹\n");
        string[] room = Enumerable.Repeat("", 12).ToArray();
        room[0] = "B5_A01"; room[1] = "2*2|-1|0"; room[11] = "0|0|1|1|";
        File.WriteAllLines(Path.Combine(root, "world", "b5-rooms", "b5_a01.txt"), room);

        Check(CartographyRegionFiles.StreamingAssetsRoot(root) == root, "An existing StreamingAssets path must not be appended a second time.");
        Check(CartographyRegionFiles.StreamingAssetsRoot(game) == root, "A game-install path resolves to StreamingAssets.");
        Check(CartographyRegionFiles.StreamingAssetsRoot(Path.Combine(game, "RainWorld_Data")) == root, "A data-folder path resolves to StreamingAssets.");
        Throws(() => CartographyRegionFiles.StreamingAssetsRoot(Path.Combine(game, "missing")), "A missing installation is an observable error.");

        string Resolve(string relative) => Path.Combine(root, relative.ToLowerInvariant().Replace('/', Path.DirectorySeparatorChar));
        string[] List(string relative) => Directory.Exists(Resolve(relative)) ? Directory.GetFiles(Resolve(relative)) : Array.Empty<string>();
        var catalog = CartographyRegionFiles.Catalog(Directory.GetDirectories(Path.Combine(root, "world")), Resolve,
            code => File.ReadAllText(Resolve("world/" + code + "/displayname.txt")), name => name);
        Check(catalog.Length == 1 && catalog[0].Label(true) == "B5 · 远古遗迹", "The region picker lists real regions and preserves Chinese full names.");

        var cache = new CartographySourceCache(2);
        var files = CartographyRegionFiles.Capture("B5", List);
        CartographySource first = cache.Load("B5|White", files, "White", false, out bool hit);
        Check(!hit && first.Rooms["B5_A01"].Ready && first.Rooms["B5_A01"].Width == 2, "Cold loading compiles production room geometry.");
        var warm = cache.Load("B5|White", CartographyRegionFiles.Capture("B5", List), "White", false, out hit);
        Check(hit && ReferenceEquals(first, warm), "A warm revisit reuses decoded terrain.");
        var author = first.CreateDocument("B5|White", "B5"); author.Title = "未保存的制图";
        string authored = CartographyStorage.Serialize(author);
        File.AppendAllText(map, "\n// edited map file\n");
        var changed = cache.Load("B5|White", files, "White", false, out hit);
        Check(!hit && !ReferenceEquals(first, changed), "Changed input files invalidate geometry without touching author data.");
        Check(CartographyStorage.Serialize(author) == authored, "Cache invalidation retains unsaved author content.");
        var refreshed = cache.Load("B5|White", files, "White", true, out hit);
        Check(!hit && !ReferenceEquals(changed, refreshed), "Explicit refresh bypasses metadata caches.");

        string newest = Path.Combine(game, "mod", "newest", "world", "b5");
        Directory.CreateDirectory(newest);
        string overrideMap = Path.Combine(newest, "map_b5.txt");
        File.WriteAllText(overrideMap, "B5_A01: 0><0><120><24><1><\n");
        string[] WithOverride(string relative) => List(relative).Select(path => path == map ? overrideMap : path).ToArray();
        var resolved = cache.Load("B5|White", CartographyRegionFiles.Capture("B5", WithOverride), "White", false, out hit);
        Check(!hit && resolved.Rooms["B5_A01"].X == 180, "A game-resolved newest/mod override replaces base data and invalidates cache.");
        cache.Load("B5|Yellow", files, "Yellow", false, out _);
        cache.Load("B5|Red", files, "Red", false, out _);
        Check(cache.Count == 2, "Derived source caching is bounded across campaigns/regions.");
        cache.Load("B5|White", files, "White", false, out hit);
        Check(!hit && CartographyStorage.Serialize(author) == authored, "Eviction rebuilds derived source without deleting author documents.");
        File.Delete(map);
        Throws(() => cache.Load("B5|White", files, "White", false, out _), "A deleted source file cannot silently reuse stale geometry.");
    }

    private static void GameRegions(string game)
    {
        string root = CartographyRegionFiles.StreamingAssetsRoot(game);
        string mod = Path.Combine(root, "mods", "Ancient Site");
        // A real-file fixture of the enabled game's resolved precedence. The production adapter
        // obtains these file lists from AssetManager, not from this fixture's directory scan.
        string[] roots = { Path.Combine(root, "mergedmods"), Path.Combine(mod, "newest"), mod,
            Path.Combine(root, "mods", "moreslugcats"), Path.Combine(root, "mods", "watcher"),
            Path.Combine(root, "consolefiles", "moreslugcatswatcher"), root };
        string[] List(string relative) => roots.Select(r => Path.Combine(r, relative)).Where(Directory.Exists)
            .SelectMany(Directory.GetFiles).GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();
        var cache = new CartographySourceCache();
        foreach (string region in new[] { "B5", "CC", "SU" })
        {
            var files = CartographyRegionFiles.Capture(region, List);
            Stopwatch timer = Stopwatch.StartNew();
            var source = cache.Load(region + "|White", files, "White", false, out _);
            long cold = timer.ElapsedMilliseconds;
            timer.Restart();
            var warm = cache.Load(region + "|White", CartographyRegionFiles.Capture(region, List), "White", false, out bool hit);
            long warmTime = timer.ElapsedMilliseconds;
            Check(hit && ReferenceEquals(source, warm), region + " actual game files reuse their source cache.");
            Check(source.Rooms.Count > 20 && source.Rooms.Values.All(room => room.Ready), region + " actual room files all decode: " +
                string.Join("; ", source.Rooms.Values.Where(room => !room.Ready).Select(room => room.Error)));
            var document = source.CreateDocument("real|" + region, region);
            document.ExportScale = .25f;
            timer.Restart();
            var scene = CartographySceneBuilder.Build(document, source, new CartographySceneCache());
            long sceneTime = timer.ElapsedMilliseconds;
            Check(scene.Errors.Length == 0, region + " creates a complete scene from the actual installation.");
            string image = Path.Combine(output, region + "-game.png");
            var result = CartographyExporter.Export(document, scene, image, CartographyExportFormat.Png, CartographyStorage.HashFile(image));
            Console.WriteLine(region + ": " + source.Rooms.Count + " rooms; cold=" + cold + " ms, warm=" + warmTime + " ms, scene=" + sceneTime + " ms; " + result.Width + "x" + result.Height);
        }
    }
}
