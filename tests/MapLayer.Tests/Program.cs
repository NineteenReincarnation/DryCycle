using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.History;
using DryCycle.DevUI.DevTool.Map;
using DryCycle.DevUI.DevTool.Objects;
using UnityEngine;
using Num = System.Numerics;

// Actual deployed command, authoring, snapshot, history, retained scene, spatial-filter and
// serializer code. Only the game-owned object graph/clock is supplied; no game files are written.
internal static partial class Program
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static string gameDirectory, plugins;
    private static Assembly core, frontend;
    private static int assertions;

    private static int Main(string[] args)
    {
        gameDirectory = Path.GetFullPath(args.Length > 0 ? args[0] : "D:/Steam/steamapps/common/Rain World");
        plugins = Path.Combine(gameDirectory, "RainWorld_Data/StreamingAssets/mods/Ancient Site/newest/plugins");
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        try { Prepare(); Run(); Console.WriteLine("PASS: " + assertions + " assertions; deployed map commands, history, filtering, serialization and automatic Player Map thumbnails."); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Prepare() => ManagedMapEngine.Load(gameDirectory);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run()
    {
        core = typeof(EditorSession).Assembly;
        frontend = Assembly.LoadFrom(Path.Combine(plugins, "DryCycle.DevTool.RWImGui.dll"));
        string mapFile = Path.Combine(gameDirectory, "RainWorld_Data/StreamingAssets/mods/Ancient Site/world/b5/map_b5.txt");
        string line = File.ReadLines(mapFile).Single(value => value.StartsWith("B5_BDF01: ", StringComparison.Ordinal));
        string[] fields = line.Substring(line.IndexOf(": ", StringComparison.Ordinal) + 2).Split(new[] { "><" }, StringSplitOptions.None);
        int originalLayer = int.Parse(fields[4], CultureInfo.InvariantCulture);
        Console.WriteLine("Actual B5_BDF01 source layer: " + originalLayer + " -> " + MapRoomLayer.Label(originalLayer));

        World world = Raw<World>(); world.name = "B5"; world.DisabledMapRooms = new List<string>();
        AbstractRoom room = Raw<AbstractRoom>(); room.world = world; room.index = 0; room.name = "B5_BDF01";
        room.subregionName = fields[5]; room.nodes = Array.Empty<AbstractRoomNode>(); room.connections = Array.Empty<int>();
        room.size = new RWCustom.IntVector2(96, 105); world.abstractRooms = new[] { room };
        RainWorldGame game = Raw<RainWorldGame>(); game.overWorld = Raw<OverWorld>(); game.overWorld.activeWorld = world;
        game.session = Raw<SandboxGameSession>(); game.session.Players = new List<AbstractCreature>();
        RoomCamera camera = Raw<RoomCamera>(); camera.room = Raw<Room>(); camera.room.world = world;
        Set(camera.room, "<abstractRoom>k__BackingField", room); Set(game, "<cameras>k__BackingField", new[] { camera });
        DevUI owner = Raw<DevUI>(); owner.game = game;
        MapPage page = Raw<MapPage>(); page.world = world; page.owner = owner; page.subNodes = new List<DevUINode>(); owner.activePage = page;
        RoomPanel Panel(int layer)
        {
            RoomPanel panel = Raw<RoomPanel>(); panel.world = world; panel.layer = layer;
            panel.roomRep = Raw<MapObject.RoomRepresentation>(); panel.roomRep.room = room;
            panel.pos = new Vector2(float.Parse(fields[0], CultureInfo.InvariantCulture), float.Parse(fields[1], CultureInfo.InvariantCulture));
            panel.devPos = new Vector2(float.Parse(fields[2], CultureInfo.InvariantCulture), float.Parse(fields[3], CultureInfo.InvariantCulture));
            return panel;
        }
        RoomPanel panel = Panel(originalLayer); page.subNodes.Add(panel);
        EditorSession session = Raw<EditorSession>(); Set(session, "<Owner>k__BackingField", owner); Set(session, "<ToolMode>k__BackingField", EditorToolMode.Map);
        Set(session, "<History>k__BackingField", new EditorHistoryService(64)); Set(session, "<LegacyTransactions>k__BackingField", new LegacyTransactionRecorder());
        var key = new EditorDocumentKey(EditorDocumentKind.RegionMap, "B5-layer-test"); Set(session, "documentKey", key); session.History.ActivateDocument(key);
        typeof(DevToolSessionHub).GetField("current", Flags).SetValue(null, new WeakReference<EditorSession>(session));
        Type native = Core("Map.NativeMapAuthoringStateHub"), actions = Core("Map.MapEditorActions");
        object authoring()
        {
            object[] args = { session, 0, null }; Check((bool)Call(native, "TryGet", args), "The authoritative room exists."); return args[2];
        }
        int layer() => (int)Get(authoring(), "Layer");
        object scene = New(Front("WorldMapScene")), synchronizer = New(Front("WorldMapSceneSynchronizer")), spatial = New(Front("WorldMapSpatialIndex"));
        object transform = New(Front("WorldMapViewTransform"));
        Type playerRuntime = Core("Map.PlayerMap.PlayerMapWorkspaceRuntime");
        object playerState = Call(playerRuntime, "GetOrCreateState", session, page);
        // Terrain/GPU loading is outside this regression. A stable missing-bake entry lets the
        // actual per-frame synchronization run without resolving Unity's installation directories.
        Type bakeCache = Core("Map.PlayerMap.RoomMapBakeCache");
        object bakeEntry = New(Core("Map.PlayerMap.RoomMapBakeCacheEntry"));
        Set(bakeEntry, "RoomIndex", 0); Set(bakeEntry, "RoomName", room.name);
        Set(bakeEntry, "Status", DryCycle.DevUI.DevTool.Map.PlayerMap.RoomMapBakeStatus.Missing);
        Set(bakeEntry, "NextSourceAuditFrame", int.MaxValue);
        ((IDictionary)bakeCache.GetField("Entries", Flags).GetValue(null))[0] = bakeEntry;
        long lastAuthoringRevision = -1, lastPlayerRevision = -1;
        void Verify(int expected)
        {
            Check(layer() == expected && panel.layer == expected, "Authoring state and compatibility panel agree.");
            Call(typeof(MapEditorPresentationHub), "Publish", session);
            EditorMapPresentationSnapshot snapshot = MapEditorPresentationHub.Current;
            Check(snapshot.Rooms.Single().Layer == expected, "Published World Map data uses the edited index.");
            object dirty = Invoke(synchronizer, "Synchronize", scene, snapshot, null, 0L, -1, transform);
            Invoke(spatial, "ApplyDirty", scene, null, dirty);
            object[] find = { 0, null }; Check((bool)Invoke(scene, "TryGetRoom", find) && (int)Get(find[1], "Layer") == expected, "Retained scene metadata follows the snapshot.");
            for (int candidate = 0; candidate < MapRoomLayer.Count; candidate++)
            {
                var visible = new List<int>();
                Invoke(spatial, "Query", new Num.Vector2(-2000, -2000), new Num.Vector2(2000, 2000), 1 << candidate, visible);
                Check(visible.Contains(0) == (candidate == expected), "Layer filtering uses the real layer, including after edits/Undo.");
            }
            long authoringRevision = (long)Call(native, "GetRevision", session);
            Call(playerRuntime, "Synchronize", session);
            object playerSnapshot = Get(playerState, "Presentation");
            object playerPublishedRoom = ((Array)Get(playerSnapshot, "Rooms")).GetValue(0);
            Check((int)Get(playerPublishedRoom, "Layer") == expected, "Player Map base publication shares the authoritative layer.");
            long playerRevision = (long)Get(playerSnapshot, "Revision");
            if (lastAuthoringRevision >= 0 && authoringRevision != lastAuthoringRevision)
                Check(playerRevision > lastPlayerRevision, "Layer-only changes must invalidate Player Map diagnostic caches.");
            lastAuthoringRevision = authoringRevision; lastPlayerRevision = playerRevision;
            Call(playerRuntime, "Synchronize", session);
            Check(ReferenceEquals(playerSnapshot, Get(playerState, "Presentation")), "Stable frames reuse the Player Map snapshot.");
            Type pipeline = Core("Map.PlayerMap.PlayerMapConfigBuildPipeline");
            object[] capture = { page, playerState, null, null }; object records = Call(pipeline, "CaptureRooms", capture);
            int before = panel.layer; panel.layer = (before + 1) % MapRoomLayer.Count;
            var lines = (IDictionary)Call(pipeline, "BuildRoomLines", records, capture[2], capture[3]);
            panel.layer = before;
            string saved = (string)lines[room.name];
            Check(int.Parse(saved.Split(new[] { "><" }, StringSplitOptions.None)[4], CultureInfo.InvariantCulture) == expected,
                "Serialization freezes the authoritative layer and does not reread a mutable panel.");
            Check(MapRoomLayer.Label(expected) == "L" + expected && DryCycle.DevUI.DevTool.Map.PlayerMap.PlayerMapCoordinateSystem.LayerCount == MapRoomLayer.Count,
                "All controls use the same raw layer numbering and count.");
        }
        Verify(originalLayer);
        foreach (int target in new[] { 0, 2, 1 })
        {
            int before = layer();
            MapEditorCommandQueue.Enqueue(new MapEditorCommand(MapEditorCommandKind.SetRoomLayer, 0,
                value: new EditorPropertyValue(EditorPropertyKind.Integer, integer: target)));
            Call(typeof(MapEditorCommandQueue), "Process", session); Verify(target);
            Check(session.History.Undo(session), "Actual map history undo succeeds."); Verify(before);
            Check(session.History.Redo(session), "Actual map history redo succeeds."); Verify(target);
        }
        int currentLayer = layer(); long historyRevision = session.History.Revision;
        Call(actions, "SetRoomLayer", session, 0, currentLayer);
        Check(session.History.Revision == historyRevision, "Selecting the existing layer creates no fake edit.");
        try { Call(actions, "SetRoomLayer", session, 0, 99); throw new Exception("Invalid layer was accepted."); }
        catch (TargetInvocationException error) when (error.InnerException is ArgumentOutOfRangeException) { Check(layer() == currentLayer, "Invalid commands cannot silently clamp author data."); }

        // Recreated legacy panels start from stale on-disk values. Retained author state wins.
        panel = Panel((currentLayer + 1) % MapRoomLayer.Count); page.subNodes.Clear(); page.subNodes.Add(panel);
        Call(native, "AuditLegacy", session); Verify(currentLayer);
        panel.layer = (currentLayer + 1) % MapRoomLayer.Count; // A real external edit on the same panel is imported.
        Call(native, "AuditLegacy", session); Verify(panel.layer);
        RunPlayerMapLoading(session, page, world);
    }

    private static Type Core(string name) => core.GetType("DryCycle.DevUI.DevTool." + name, true);
    private static Type Front(string name) => frontend.GetType("DryCycle.DevUI.DevTool.RWImGui." + name, true);
    private static T Raw<T>() where T : class => (T)FormatterServices.GetUninitializedObject(typeof(T));
    private static object New(Type type) => Activator.CreateInstance(type, true);
    private static object Get(object target, string name) => target.GetType().GetField(name, Flags)?.GetValue(target) ?? target.GetType().GetProperty(name, Flags).GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags).SetValue(target, value);
    private static object Call(Type type, string name, params object[] args) => type.GetMethod(name, Flags).Invoke(null, args);
    private static object Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Flags).Invoke(target, args);
    private static void Check(bool condition, string message) { assertions++; if (!condition) throw new InvalidOperationException(message); }
    private static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name;
        if (name == "Assembly-CSharp") return Assembly.LoadFrom(Path.Combine(gameDirectory, "BepInEx/utils/PUBLIC-Assembly-CSharp.dll"));
        foreach (string directory in new[] { plugins, Path.Combine(gameDirectory, "RainWorld_Data/Managed"), Path.Combine(gameDirectory, "BepInEx/core"),
            Path.Combine(gameDirectory, "BepInEx/plugins"), Path.GetFullPath(Path.Combine(gameDirectory, "../../workshop/content/312520/3417372413/plugins")) })
        {
            string path = Path.Combine(directory, name + ".dll"); if (File.Exists(path)) return Assembly.LoadFrom(path);
        }
        return null;
    }
}
