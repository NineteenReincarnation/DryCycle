using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Map;
using DryCycle.DevUI.DevTool.Map.PlayerMap;
using UnityEngine;

internal static partial class Program
{
    private static void RunPlayerMapLoading(EditorSession session, MapPage page, World world)
    {
        Type geometry = Core("Map.MapRoomGeometryPresentationHub");
        Type bakeCache = Core("Map.PlayerMap.RoomMapBakeCache");
        Type runtime = Core("Map.PlayerMap.PlayerMapWorkspaceRuntime");
        Type activity = Core("Map.PlayerMap.PlayerMapActivityGate");
        Type terrainBridge = Core("Map.PlayerMap.PlayerMapTerrainBakeBridge");
        Type budget = Front("WorldMapBackgroundBudget");
        Type retained = Front("WorldMapRetainedV2Runtime");
        Call(bakeCache, "Clear");
        Call(Core("Map.NativeMapAuthoringStateHub"), "Reset");
        Call(typeof(MapEditorPresentationHub), "Clear");
        var entries = (IDictionary)geometry.GetField("cache", Flags).GetValue(null);
        var order = (IList)geometry.GetField("roomOrder", Flags).GetValue(null);
        var bakes = (IDictionary)bakeCache.GetField("Entries", Flags).GetValue(null);
        entries.Clear(); order.Clear(); page.subNodes.Clear();

        string mod = Path.GetFullPath(Path.Combine(plugins, "../.."));
        string roomDirectory = Path.Combine(mod, "world/b5-rooms");
        string[] names = File.ReadLines(Path.Combine(mod, "world/b5/map_b5.txt"))
            .Where(line => line.StartsWith("B5_", StringComparison.Ordinal) && line.Contains(": "))
            .Select(line => line.Substring(0, line.IndexOf(": ", StringComparison.Ordinal)))
            .Where(name => File.Exists(Path.Combine(roomDirectory, name + ".txt"))).ToArray();
        Check(names.Length > 24, "Use a real B5 room set larger than one background sweep.");
        var rooms = new List<AbstractRoom>();
        for (int index = 0; index < names.Length; index++)
        {
            string name = names[index], path = Path.Combine(roomDirectory, name + ".txt");
            string[] lines = File.ReadAllLines(path); RoomPreprocessor.VersionFix(ref lines);
            object source = Call(Core("Map.PlayerMap.RoomMapTextDecoder"), "Parse", (object)lines);
            object bake = Call(Core("Map.PlayerMap.RoomMapSemanticCompiler"), "Compile", index, name, source, path);
            int width = (int)Get(bake, "Width"), height = (int)Get(bake, "Height");
            AbstractRoom room = Raw<AbstractRoom>(); room.world = world; room.index = index; room.name = name;
            room.size = new RWCustom.IntVector2(width, height); room.nodes = Array.Empty<AbstractRoomNode>(); room.connections = Array.Empty<int>();
            RoomSettings settings = Raw<RoomSettings>(); settings.placedObjects = new List<PlacedObject>();
            settings.filePath = Path.Combine(roomDirectory, name + "_settings.txt");
            // One authored curve fixture exercises actual terrain compilation/overlay. Other rooms
            // have no custom terrain: a completed empty scan must also release their ready barrier.
            if (index == 0)
                foreach (float x in new[] { 0f, width * 20f })
                {
                    PlacedObject handle = Raw<PlacedObject>(); handle.type = PlacedObject.Type.TerrainHandle;
                    handle.active = true; handle.pos = new Vector2(x, 80f); handle.data = new PlacedObject.TerrainHandleData(handle);
                    settings.placedObjects.Add(handle);
                }
            room.realizedRoom = Raw<Room>(); room.realizedRoom.roomSettings = settings; rooms.Add(room);
            RoomPanel panel = Raw<RoomPanel>(); panel.world = world; panel.layer = 1;
            panel.roomRep = Raw<MapObject.RoomRepresentation>(); panel.roomRep.room = room; page.subNodes.Add(panel);
            object entry = New(geometry.GetNestedType("CacheEntry", Flags));
            Set(entry, "RoomIndex", index); Set(entry, "RoomName", name); Set(entry, "Room", room);
            Set(entry, "WidthTiles", (float)width); Set(entry, "HeightTiles", (float)height);
            entries[index] = entry; order.Add(index);
            object cached = New(Core("Map.PlayerMap.RoomMapBakeCacheEntry"));
            Set(cached, "RoomIndex", index); Set(cached, "RoomName", name); Set(cached, "Bake", bake);
            Set(cached, "Status", RoomMapBakeStatus.Ready); Set(cached, "NextSourceAuditFrame", int.MaxValue);
            bakes[index] = cached;
        }
        world.abstractRooms = rooms.ToArray();
        object state = Call(runtime, "GetOrCreateState", session, page);
        Set(state, "Initialized", false);
        Call(terrainBridge, "Enable", (object)null);
        // Use the actual frontend zoom budget. No World Map texture or raster readback is needed.
        retained.GetField("enabled", Flags).SetValue(null, true);
        retained.GetField("activeZoom", Flags).SetValue(null, 0.12f);
        Call(budget, "Enable", (object)null);
        Call(typeof(MapEditorPresentationHub), "Publish", session);
        Call(runtime, "Synchronize", session);
        var initial = (PlayerMapPresentationSnapshot)Get(state, "Presentation");
        Check(initial.SelectedRoomIndex == -1, "No room is selected during thumbnail loading.");
        Check(initial.Rooms.All(room => room.Bake.Status == RoomMapBakeStatus.Pending), "Base room pixels wait for the real terrain scan.");

        int ready = 0, steps = 0;
        while (ready < names.Length && steps++ < names.Length * 2)
        {
            ManagedMapEngine.AdvanceFrame();
            Call(activity, "MarkVisible");
            Check(!(bool)Call(budget, "ShouldProcessGeometry", world), "World Map visual background remains throttled at low zoom.");
            geometry.GetField("curveLoadsRemaining", Flags).SetValue(null, 3);
            geometry.GetField("rasterLoadsRemaining", Flags).SetValue(null, 2);
            Call(geometry, "ProcessBackground", world, -1, -1);
            int after = entries.Values.Cast<object>().Count(entry => (bool)Get(entry, "CurvesInitialized"));
            Check(after > ready && after - ready <= 3, "Unselected terrain scans advance within the three-room budget.");
            ready = after;
            Call(runtime, "Synchronize", session);
        }
        // A bounded revision audit may need one full sweep after the final geometry batch.
        for (int i = 0; i <= names.Length / 12; i++) Call(runtime, "Synchronize", session);
        var complete = (PlayerMapPresentationSnapshot)Get(state, "Presentation");
        Check(complete.Rooms.All(room => room.Bake.Status == RoomMapBakeStatus.Ready && room.Bake.Runs.Length > 0),
            "All thumbnails publish without any click or optional derived-layout projection.");
        Check(complete.Revision > initial.Revision && !complete.Dirty, "Readiness invalidates presentation caches without creating author edits.");
        object[] enhanced = { 0, null };
        Check((bool)Call(bakeCache, "TryGetReady", enhanced), "Terrain overlay becomes renderable after scanning.");
        object firstBake = enhanced[1];
        Check(!ReferenceEquals(firstBake, Get(bakes[0], "Bake")), "Authored curve data is merged before Ready is published.");
        Call(bakeCache, "TryGetReady", enhanced);
        Check(ReferenceEquals(firstBake, enhanced[1]), "An unchanged authored-terrain bake reuses its cached overlay.");
        Call(runtime, "Synchronize", session);
        Check(ReferenceEquals(complete, Get(state, "Presentation")), "Stable ready thumbnails reuse the published snapshot.");

        Call(activity, "Reset");
        Set(entries[0], "CurvesInitialized", false);
        geometry.GetField("curveLoadsRemaining", Flags).SetValue(null, 3);
        Call(geometry, "ProcessBackground", world, -1, -1);
        Check(!(bool)Get(entries[0], "CurvesInitialized"), "A hidden idle Player Map does not override the World Map budget.");
        Call(budget, "Disable"); retained.GetField("enabled", Flags).SetValue(null, false);
        Call(terrainBridge, "Disable");
        Console.WriteLine("B5 automatic thumbnails: " + ready + "/" + names.Length + " ready in " + steps + " bounded batches, with no selection and World Map zoom 0.12.");
    }
}
