using System;
using System.IO;
using System.Linq;
using BepInEx;
using DryCycle.DevUI.DevTool.Map;
using UnityEngine;

internal static class Program
{
    private const int CacheMagic = 0x44434D56;
    private static int assertions;
    private static string root;

    private static int Main()
    {
        root = Path.Combine(
            Path.GetTempPath(),
            "drycycle-worldmap-cache-tests-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Paths.CachePath = root;
        Application.persistentDataPath = root;

        try
        {
            V3RoundTripAndFlush();
            V2FallbackMigrationRead();
            TruncatedCacheRejected();
            WrongContextRejected();
            FutureVersionRejected();

            Console.WriteLine(
                "PASS: " + assertions +
                " assertions; World Map V2/V3 cache compatibility, " +
                "route/thumbnail round-trip, corruption rejection and writer flush.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static void V3RoundTripAndFlush()
    {
        string path = Path.Combine(root, "roundtrip-v3.bin");
        MapViewPersistentSnapshot snapshot = SampleV3("world|Survivor");

        MapViewPersistentCacheStore.QueueWrite(path, snapshot);
        Check(
            MapViewPersistentCacheStore.Flush(5000),
            "Flush must wait for queued persistent writes.");
        Check(File.Exists(path), "V3 cache file must exist after flush.");
        Check(
            !Directory.GetFiles(
                root,
                "*.tmp-*",
                SearchOption.TopDirectoryOnly).Any(),
            "Atomic writer must not leave temp files after success.");

        MapViewPersistentSnapshot loaded =
            MapViewPersistentCacheStore.Load(
                path,
                snapshot.ContextKey);
        Check(loaded != null, "V3 cache must load.");
        Check(
            loaded.FormatVersion ==
            MapViewPersistentCacheStore.CurrentCacheVersion,
            "Loaded V3 format version must be reported.");
        Check(
            loaded.TemplateFingerprint == 123456789L &&
            loaded.FrontendTopologyFingerprint == 987654321L,
            "V3 fingerprints must round-trip.");
        Check(
            loaded.Rooms.Count == 1,
            "V3 room count must round-trip.");

        MapViewPersistentRoom room = loaded.Rooms[0];
        Check(
            room.RoomName == "SU_A01" &&
            room.ThumbnailElementName == "Map_SU_A01",
            "Room identity and persistent thumbnail element must round-trip.");
        Check(
            Math.Abs(room.ThumbnailUvX - 0.125f) < 0.0001f &&
            Math.Abs(room.ThumbnailUvHeight - 0.25f) < 0.0001f &&
            Math.Abs(room.ThumbnailPixelWidth - 320f) < 0.0001f,
            "Thumbnail UV/pixel descriptor must round-trip.");
        Check(
            room.BaseRasterRuns.Length == 2 &&
            room.Curves.Length == 1 &&
            room.Nodes.Length == 1,
            "Geometry payload must round-trip.");

        Check(
            loaded.Routes.Count == 1,
            "V3 route list must round-trip.");
        MapViewPersistentRoute route = loaded.Routes[0];
        Check(
            route.ConnectionId == "SU_A01->SU_A02:0:1" &&
            route.PolicyVersion == 3 &&
            route.Kind == 2,
            "Route metadata must round-trip.");
        Check(
            route.Points.Length == 3 &&
            Math.Abs(route.Points[1].X - 80f) < 0.0001f &&
            Math.Abs(route.Points[2].Y - 45f) < 0.0001f,
            "Route points must round-trip.");
    }

    private static void V2FallbackMigrationRead()
    {
        const string context = "legacy|Monk";
        string v3Path =
            MapViewPersistentCacheStore.ResolveCachePath(context);
        string v2Path = Path.Combine(
            root,
            "DryCycle",
            "DevTool",
            "MapView",
            "map-view-v2-" +
            StableHash(context).ToString("x16") +
            ".bin");

        Directory.CreateDirectory(Path.GetDirectoryName(v2Path));
        WriteV2(v2Path, context);

        MapViewPersistentSnapshot loaded =
            MapViewPersistentCacheStore.Load(
                v3Path,
                context);

        Check(
            loaded != null && loaded.FormatVersion == 2,
            "Missing V3 path must fall back to the matching V2 cache.");
        Check(
            loaded.Rooms.Count == 1 &&
            loaded.Rooms[0].RoomName == "SU_LEGACY",
            "V2 room payload must remain readable.");
        Check(
            string.IsNullOrEmpty(
                loaded.Rooms[0].ThumbnailElementName) &&
            loaded.Routes.Count == 0 &&
            loaded.FrontendTopologyFingerprint == 0L,
            "V2 load must default V3-only frontend metadata.");
    }

    private static void TruncatedCacheRejected()
    {
        string path = Path.Combine(root, "truncated.bin");
        MapViewPersistentCacheStore.QueueWrite(
            path,
            SampleV3("truncated|Survivor"));
        Check(
            MapViewPersistentCacheStore.Flush(5000),
            "Writer must flush before corruption test.");

        byte[] bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(
            path,
            bytes.Take(Math.Max(1, bytes.Length / 3)).ToArray());

        Check(
            MapViewPersistentCacheStore.Load(
                path,
                "truncated|Survivor") == null,
            "Truncated cache must be rejected instead of partially restored.");
    }

    private static void WrongContextRejected()
    {
        string path = Path.Combine(root, "wrong-context.bin");
        MapViewPersistentCacheStore.QueueWrite(
            path,
            SampleV3("source|Survivor"));
        Check(
            MapViewPersistentCacheStore.Flush(5000),
            "Writer must flush before context test.");

        Check(
            MapViewPersistentCacheStore.Load(
                path,
                "other|Survivor") == null,
            "Cache from another world/context must be rejected.");
    }

    private static void FutureVersionRejected()
    {
        string path = Path.Combine(root, "future.bin");
        using (FileStream stream = File.Create(path))
        using (BinaryWriter writer = new(stream))
        {
            writer.Write(CacheMagic);
            writer.Write(999);
        }

        Check(
            MapViewPersistentCacheStore.Load(
                path,
                "future|Survivor") == null,
            "Unknown future cache versions must be rejected.");
    }

    private static MapViewPersistentSnapshot SampleV3(
        string context)
    {
        MapViewPersistentSnapshot snapshot = new()
        {
            FormatVersion =
                MapViewPersistentCacheStore.CurrentCacheVersion,
            ContextKey = context,
            TemplateFingerprint = 123456789L,
            FrontendTopologyFingerprint = 987654321L
        };

        snapshot.Rooms.Add(
            new MapViewPersistentRoom
            {
                RoomName = "SU_A01",
                RoomSource =
                    new MapViewFileStamp(
                        "rooms/SU_A01.txt",
                        1234L,
                        5678L),
                SettingsSource =
                    new MapViewFileStamp(
                        "rooms/SU_A01_settings.txt",
                        2345L,
                        6789L),
                RasterSignature = "atlas:SU_A01",
                RasterInitialized = true,
                NodesInitialized = true,
                CurvesInitialized = true,
                RasterWidth = 40,
                RasterHeight = 20,
                NodeFingerprint = 99,
                SettingsFingerprint = 101,
                WidthTiles = 40f,
                HeightTiles = 20f,
                ThumbnailElementName = "Map_SU_A01",
                ThumbnailUvX = 0.125f,
                ThumbnailUvY = 0.5f,
                ThumbnailUvWidth = 0.5f,
                ThumbnailUvHeight = 0.25f,
                ThumbnailPixelWidth = 320f,
                ThumbnailPixelHeight = 160f,
                BaseRasterRuns = new[]
                {
                    new EditorMapRectSnapshot(
                        0f, 0f, 40f, 1f,
                        EditorMapGeometryKind.Solid),
                    new EditorMapRectSnapshot(
                        0f, 1f, 40f, 19f,
                        EditorMapGeometryKind.Air)
                },
                TerrainFillRuns = new[]
                {
                    new EditorMapRectSnapshot(
                        4f, 2f, 6f, 3f,
                        EditorMapGeometryKind.Structure)
                },
                Curves = new[]
                {
                    new EditorMapPolylineSnapshot
                    {
                        Kind = EditorMapGeometryKind.Solid,
                        Closed = false,
                        Points = new[]
                        {
                            new EditorMapPointSnapshot(0f, 3f),
                            new EditorMapPointSnapshot(8f, 7f)
                        }
                    }
                },
                Nodes = new[]
                {
                    new EditorMapNodeVisualSnapshot(
                        2,
                        39f,
                        10f)
                }
            });

        snapshot.Routes.Add(
            new MapViewPersistentRoute
            {
                ConnectionId = "SU_A01->SU_A02:0:1",
                FromRoomIndex = 1,
                FromNodeIndex = 0,
                ToRoomIndex = 2,
                ToNodeIndex = 1,
                FromRoomName = "SU_A01",
                ToRoomName = "SU_A02",
                Direction = 1,
                Ambiguous = false,
                PolicyVersion = 3,
                Kind = 2,
                FromRoomX = 10f,
                FromRoomY = 20f,
                ToRoomX = 140f,
                ToRoomY = 50f,
                StartDirectionX = 1f,
                StartDirectionY = 0f,
                EndDirectionX = -1f,
                EndDirectionY = 0f,
                Points = new[]
                {
                    new EditorMapPointSnapshot(30f, 25f),
                    new EditorMapPointSnapshot(80f, 25f),
                    new EditorMapPointSnapshot(80f, 45f)
                }
            });

        return snapshot;
    }

    private static void WriteV2(
        string path,
        string context)
    {
        using FileStream stream = File.Create(path);
        using BinaryWriter writer = new(stream);

        writer.Write(CacheMagic);
        writer.Write(2);
        writer.Write(context);
        writer.Write(4242L);
        writer.Write(1);

        writer.Write("SU_LEGACY");
        WriteStamp(
            writer,
            new MapViewFileStamp(
                "rooms/SU_LEGACY.txt",
                100L,
                200L));
        WriteStamp(
            writer,
            new MapViewFileStamp(
                "rooms/SU_LEGACY_settings.txt",
                50L,
                80L));
        writer.Write("legacy-raster");
        writer.Write(true);
        writer.Write(true);
        writer.Write(false);
        writer.Write(20);
        writer.Write(10);
        writer.Write(123);
        writer.Write(456);
        writer.Write(20f);
        writer.Write(10f);

        writer.Write(1);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(20f);
        writer.Write(10f);
        writer.Write((byte)EditorMapGeometryKind.Air);

        writer.Write(0); // terrain fill runs
        writer.Write(0); // curves
        writer.Write(1); // nodes
        writer.Write(0);
        writer.Write(19f);
        writer.Write(5f);
    }

    private static void WriteStamp(
        BinaryWriter writer,
        MapViewFileStamp stamp)
    {
        writer.Write(stamp.Path ?? string.Empty);
        writer.Write(stamp.Length);
        writer.Write(stamp.WriteTicks);
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
                    hash =
                        (hash ^ (byte)c) *
                        1099511628211UL;
                    hash =
                        (hash ^ (byte)(c >> 8)) *
                        1099511628211UL;
                }
            }
            return hash;
        }
    }

    private static void Check(
        bool value,
        string message)
    {
        assertions++;
        if (!value)
            throw new InvalidOperationException(message);
    }
}
