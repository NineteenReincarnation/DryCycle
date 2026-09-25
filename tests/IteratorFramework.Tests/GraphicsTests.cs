using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DryCycle.Iterators;
using UnityEngine;

namespace IteratorFramework.Tests;

// Necessary graphics integration checks. Futile containers are off-stage; no GPU,
// Unity camera, shader execution or texture upload is simulated as successful.
internal static class GraphicsTests
{
    private static int _assertions;
    private static string _gameDirectory;
    internal static int Run(string gameDirectory)
    {
        _gameDirectory = gameDirectory;
        int failed = 0;
        foreach (Action test in new Action[] { CompositionAndIsolation, FactoryAndLifetime, CameraViews, PwnSample })
        {
            try { Check(IteratorHooks.Install(), "hooks installed"); test(); Console.WriteLine("PASS graphics " + test.Method.Name); }
            catch (Exception exception) { failed++; Console.Error.WriteLine("FAIL graphics " + test.Method.Name + Environment.NewLine + exception); }
            finally
            {
                IteratorHooks.Uninstall(); PwnIteratorExample.Unregister();
                foreach (IteratorDescriptor descriptor in IteratorRegistry.Registered) IteratorRegistry.Unregister(descriptor);
            }
        }
        Console.WriteLine($"Iterator Framework phase 4: {4 - failed}/4 groups passed; {_assertions} assertions; {failed} failures.");
        return failed == 0 ? 0 : 1;
    }

    private static IteratorRuntime Spawn(string name, Func<IteratorContext, IteratorGraphics> factory = null)
    {
        RuntimeScene scene = RuntimeScene.Create(name);
        var builder = Iterator.Create(name).Room(name);
        if (factory != null) builder.Graphics(factory);
        builder.Register();
        Check(IteratorRuntimes.TrySpawn(scene.Room, out IteratorRuntime runtime), "runtime spawned");
        return runtime;
    }

    private static void CompositionAndIsolation()
    {
        var broken = new ProbePart("Broken", failUpdate: true);
        var drawing = new ProbePart("Drawing", failDraw: true);
        var good = new ProbePart("Healthy");
        IteratorRuntime runtime = Spawn("GFX_PARTS", ctx => new ProbeGraphics(ctx, broken, drawing, good));
        IteratorGraphics graphics = runtime.Graphics;
        Check(graphics.IsInitialized && graphics.Sprites.Entries.Count == 3, "parts register named meshes");
        Check(!drawing.IsEnabled && !graphics.Sprites["Drawing.Triangle"].Visible, "draw failure hides only its sprites");
        runtime.Context.Oracle.Update(true);
        Check(runtime.IsActive && graphics.IsEnabled && !broken.IsEnabled && good.IsEnabled, "update failure leaves body/runtime and healthy parts running");
        Check(!graphics.Sprites["Broken.Triangle"].Visible && graphics.Sprites["Healthy.Triangle"].Visible, "failed module is isolated");
        Throws(() => graphics.Sprites.Register("Late", Triangle()), "layout frozen");
        Vector2 first = graphics.Sprites["Healthy.Triangle"].Mesh.Vertices[0];
        graphics.Render(0.4f); graphics.Render(0.4f);
        Check(runtime.UpdateCount == 1, "camera render never advances runtime");
        Check(graphics.Sprites["Healthy.Triangle"].Mesh.Vertices[0] == first, "repeated draws are stable for stationary body");
        var registry = new SpriteRegistry(); IteratorMesh mesh = Triangle();
        registry.Register("ThirdArm", mesh);
        Throws(() => registry.Register("ThirdArm", Triangle()), "duplicate name rejected");
        Throws(() => registry.Register("FourthArm", mesh), "mesh aliases rejected within a registry");
        Throws(() => new SpriteRegistry().Register("Shared", mesh), "cross-instance mesh alias rejected");
        Throws(() => mesh.SetVertex(0, new Vector2(float.NaN, 0)), "nonfinite mesh rejected");
        Throws(() => new GraphicsProfile(haloCount: -1), "invalid profile rejected");
        runtime.Destroy();
        Check(good.Released == 1 && broken.Released == 1 && drawing.Released == 1, "all parts, including failed parts, release exactly once");
    }

    private static void FactoryAndLifetime()
    {
        IteratorRuntime fallback = Spawn("GFX_FACTORY", _ => throw new InvalidOperationException("test graphics factory failure"));
        Check(fallback.IsActive && fallback.Graphics is StandardIteratorGraphics && fallback.Graphics.IsInitialized, "optional factory failure uses standard graphics");
        IteratorGraphics foreign = fallback.Graphics;
        IteratorRuntime other = Spawn("GFX_FOREIGN", _ => foreign);
        Check(other.IsActive && !ReferenceEquals(other.Graphics, foreign) && !foreign.IsDestroyed, "foreign graphics never adopted or released");
        IteratorRuntime interrupted = null; IteratorGraphics returned = null;
        RuntimeScene scene = RuntimeScene.Create("GFX_INTERRUPTED");
        Iterator.Create("GFX_INTERRUPTED").Room("GFX_INTERRUPTED").Graphics(ctx =>
        {
            interrupted = ctx.Runtime; ctx.Runtime.Destroy(); return returned = new StandardIteratorGraphics(ctx);
        }).Register();
        Check(!IteratorRuntimes.TrySpawn(scene.Room, out _), "factory-time destruction stops publication");
        Check(interrupted.State == IteratorLifecycle.Destroyed && returned.IsDestroyed, "late factory result is released");
        Oracle host = fallback.Context.Oracle; Room room = fallback.Context.Room;
        IteratorGraphics saved = fallback.Graphics; fallback.Destroy(); fallback.Destroy();
        Check(saved.IsDestroyed && saved.Context.Room == null && host.graphicsModule == null, "graphics released before context disappears");
        Check(room.drawableObjects.Count == 0, "destroy leaves no orphan drawable");
    }

    private static void CameraViews()
    {
        var oldAtlas = Futile.atlasManager; FShader oldShader = FShader.defaultShader;
        try
        {
            PrepareFutile();
            IteratorRuntime runtime = Spawn("GFX_CAMERAS", ctx => new BorrowedGraphics(ctx));
            Room room = runtime.Context.Room;
            room.game.rainWorld = RuntimeScene.Raw<RainWorld>();
            room.game.rainWorld.Shaders = new Dictionary<string, FShader> { ["Basic"] = FShader.defaultShader };
            RoomCamera a = Camera(room), b = Camera(room);
            var host = (IteratorGraphicsHost)runtime.Context.Oracle.graphicsModule;
            var left = new RoomCamera.SpriteLeaser(host, a); var right = new RoomCamera.SpriteLeaser(host, b);
            Check(left.sprites.Length == 1 && right.sprites.Length == 1 && !ReferenceEquals(left.sprites[0], right.sprites[0]), "two cameras have independent actual Futile meshes");
            host.DrawSprites(left, a, 1f, Vector2.zero);
            host.DrawSprites(right, b, 1f, new Vector2(20, 5));
            Check(((TriangleMesh)left.sprites[0]).vertices[0] - ((TriangleMesh)right.sprites[0]).vertices[0] == new Vector2(20, 5), "camera offset cannot contaminate another view");
            Check(host.ViewCount == 2 && left.sprites[0].element.name == "Futile_White", "missing custom element falls back to borrowed white");
            Color firstColor = ((TriangleMesh)left.sprites[0]).verticeColors[0];
            host.ApplyPalette(right, b, new RoomPalette { blackColor = Color.red });
            host.DrawSprites(right, b, 1f, Vector2.zero);
            Check(((TriangleMesh)left.sprites[0]).verticeColors[0] == firstColor && ((TriangleMesh)right.sprites[0]).verticeColors[0] != firstColor, "palette remains camera-specific");
            left.CleanSpritesAndRemove(); host.PruneViews();
            Check(host.ViewCount == 1, "engine-owned camera cleanup prunes framework view");
            room.NoLongerViewed();
            Check(right.deleteMeNextFrame && host.ViewCount == 0 && runtime.Context.Oracle.graphicsModule == null && !runtime.Graphics.IsDestroyed, "unview detaches sprites and retains logical graphics");
            ((IteratorHost)runtime.Context.Oracle).AttachGraphics();
            var replacement = (IteratorGraphicsHost)runtime.Context.Oracle.graphicsModule;
            Check(!ReferenceEquals(host, replacement) && room.drawableObjects.Count == 1, "viewing again recreates one adapter");
            var last = new RoomCamera.SpriteLeaser(replacement, a);
            runtime.Destroy();
            Check(last.deleteMeNextFrame && last.sprites.Length == 0 && replacement.ViewCount == 0, "destruction releases all cameras");
            Check(Futile.atlasManager.DoesContainAtlas("Futile_White"), "borrowed game atlas survives final lease release");
        }
        finally { Futile.atlasManager = oldAtlas; FShader.defaultShader = oldShader; }
    }

    private static void PwnSample()
    {
        string file = Path.Combine(_gameDirectory, "RainWorld_Data/StreamingAssets/mods/ParchedWilderness/world/pwn-rooms/PWN_AI.txt");
        RuntimeScene scene = RuntimeScene.Create("PWN_AI");
        if (File.Exists(file))
        {
            string[] lines = File.ReadAllLines(file), size = lines[1].Split('|')[0].Split('*'), tiles = lines[11].Split('|');
            int width = int.Parse(size[0]), height = int.Parse(size[1]);
            RuntimeScene.Set(scene.Room, "Width", width); RuntimeScene.Set(scene.Room, "Height", height);
            scene.Room.Tiles = new Room.Tile[width, height];
            for (int x = 0; x < width; x++)
            for (int y = 0; y < height; y++) scene.Room.Tiles[x, y] = new Room.Tile(x, y, (Room.Tile.TerrainType)int.Parse(tiles[x * height + height - 1 - y].Split(',')[0]), false, false, false, 0, 0);
            Console.WriteLine($"PWN_AI authored geometry loaded: {width}x{height} tiles.");
        }
        else Console.WriteLine("PWN_AI author files unavailable; sample uses managed empty-room fixture.");
        IteratorDescriptor definition = PwnIteratorExample.Register();
        Check(ReferenceEquals(PwnIteratorExample.Register(), definition), "sample registration idempotent");
        Check(IteratorRuntimes.TrySpawn(scene.Room, out IteratorRuntime runtime), "sample bound to PWN_AI");
        runtime.Context.Oracle.Update(true); runtime.Graphics.Render();
        Check(runtime.Graphics is PwnIteratorGraphics && runtime.Graphics.Parts.All(p => p.IsEnabled), "reference sample components all initialized");
        Check(!runtime.Graphics.Sprites.Entries.Any(e => e.Name.StartsWith("Halo") || e.Name.StartsWith("Cable")), "sample omits halo/cable and blue orbs");
        bool finite = true, clear = true;
        foreach (SpriteHandle sprite in runtime.Graphics.Sprites.Entries)
        foreach (Vector2 vertex in sprite.Mesh.Vertices)
        {
            finite &= GraphicsProfile.Finite(vertex.x) && GraphicsProfile.Finite(vertex.y);
            if (File.Exists(file)) clear &= !scene.Room.GetTile(vertex).Solid;
        }
        Check(finite && clear, "sample geometry finite and fits authored room's clear space");
        GraphicsPreview.Export(runtime, Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../artifacts/iterator-framework")));
        Console.WriteLine($"PWN sample spawn: {runtime.Body.Position}; {runtime.Graphics.Sprites.Entries.Count} named sprites.");
        PwnIteratorExample.Unregister();
        Check(runtime.Graphics.IsDestroyed && scene.Room.drawableObjects.Count == 0, "sample unregistration cleans graphics and room");
    }

    private static RoomCamera Camera(Room room)
    {
        var camera = RuntimeScene.Raw<RoomCamera>(); camera.game = room.game;
        RuntimeScene.Set(camera, "<room>k__BackingField", room);
        RuntimeScene.Set(camera, "SpriteLayers", new[] { new FContainer() });
        RuntimeScene.Set(camera, "SpriteLayerIndex", new Dictionary<string, int> { ["Midground"] = 0 });
        camera.currentPalette = new RoomPalette { blackColor = Color.black };
        return camera;
    }
    private static void PrepareFutile()
    {
        Futile.atlasManager = new FAtlasManager(); FShader.defaultShader = RuntimeScene.Raw<FShader>();
        var atlas = RuntimeScene.Raw<FAtlas>(); RuntimeScene.Set(atlas, "_name", "Futile_White");
        var element = new FAtlasElement { name = "Futile_White", atlas = atlas, uvRect = new Rect(0, 0, 1, 1), sourceRect = new Rect(0, 0, 1, 1), sourceSize = Vector2.one,
            uvTopLeft = Vector2.up, uvTopRight = Vector2.one, uvBottomRight = Vector2.right, uvBottomLeft = Vector2.zero };
        RuntimeScene.Set(Futile.atlasManager, "_atlases", new List<FAtlas> { atlas });
        RuntimeScene.Set(Futile.atlasManager, "_allElementsByName", new Dictionary<string, FAtlasElement> { [element.name] = element });
    }
    private static IteratorMesh Triangle() => new(new[] { Vector2.zero, Vector2.right, Vector2.up }, new[] { 0, 1, 2 }, Color.white);
    private static void Check(bool value, string message) { _assertions++; if (!value) throw new InvalidOperationException("Assertion failed: " + message); }
    private static void Throws(Action action, string message)
    {
        try { action(); } catch (ArgumentException) { _assertions++; return; } catch (InvalidOperationException) { _assertions++; return; }
        throw new InvalidOperationException("Expected failure: " + message);
    }
    private sealed class ProbeGraphics : IteratorGraphics
    {
        private readonly IteratorGraphicsPart[] _parts;
        internal ProbeGraphics(IteratorContext context, params IteratorGraphicsPart[] parts) : base(context) => _parts = parts;
        protected override void OnInitialize() { foreach (IteratorGraphicsPart part in _parts) AddPart(part); }
    }
    private sealed class BorrowedGraphics : IteratorGraphics
    {
        internal BorrowedGraphics(IteratorContext context) : base(context) { }
        protected override void OnInitialize() { RequireAtlas("Futile_White"); Sprites.Register("ThirdArm", Triangle(), element: "missing-test-element", shader: "missing-test-shader"); }
        protected override void OnDraw(IteratorDrawState state)
        {
            IteratorMesh mesh = Sprites["ThirdArm"].Mesh;
            mesh.SetVertex(0, state.Position); mesh.SetVertex(1, state.Local(1, 0)); mesh.SetVertex(2, state.Local(0, 1));
        }
    }
    private sealed class ProbePart : IteratorMeshPart
    {
        private readonly bool _failUpdate, _failDraw;
        internal int Released;
        internal ProbePart(string name, bool failUpdate = false, bool failDraw = false) : base(name) { _failUpdate = failUpdate; _failDraw = failDraw; }
        protected override void OnInitialize() => Polygon("Triangle", new[] { Vector2.zero, Vector2.right, Vector2.up }, Color.white, 0);
        protected override void OnUpdate() { if (_failUpdate) throw new InvalidOperationException("test part update"); }
        protected override void OnDraw(IteratorDrawState state) { if (_failDraw) throw new InvalidOperationException("test part draw"); base.OnDraw(state); }
        protected override void OnDestroy() => Released++;
    }
}
