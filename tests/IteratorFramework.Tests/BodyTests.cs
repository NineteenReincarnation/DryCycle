using System;
using System.Collections.Generic;
using DryCycle.Iterators;
using UnityEngine;

namespace IteratorFramework.Tests;

// Focused integration checks for the new physics/control contract. Native Unity
// entry points throw in ManagedUnityFixture; this is not an in-game visual test.
internal static class BodyTests
{
    private static int _assertions;

    internal static int Run()
    {
        var tests = new Action[] { MovementAndPose, TerrainAndGravity, ArmConstraint, ComponentLifetime };
        int failed = 0;
        foreach (Action test in tests)
        {
            try
            {
                Check(IteratorHooks.Install(), "hooks installed");
                test();
                Console.WriteLine("PASS body " + test.Method.Name);
            }
            catch (Exception exception)
            {
                failed++;
                Console.Error.WriteLine("FAIL body " + test.Method.Name + Environment.NewLine + exception);
            }
            finally
            {
                IteratorHooks.Uninstall();
                foreach (IteratorDescriptor definition in IteratorRegistry.Registered) IteratorRegistry.Unregister(definition);
            }
        }
        Console.WriteLine($"Iterator Framework phase 3: {tests.Length - failed}/{tests.Length} groups passed; {_assertions} assertions; {failed} failures.");
        return failed == 0 ? 0 : 1;
    }

    private static IteratorRuntime Spawn(RuntimeScene scene, Action<IteratorBuilder> configure = null)
    {
        IteratorBuilder builder = Iterator.Create(scene.AbstractRoom.name).Room(scene.AbstractRoom.name);
        configure?.Invoke(builder);
        builder.Register();
        Check(IteratorRuntimes.TrySpawn(scene.Room, out IteratorRuntime runtime), "spawn " + scene.AbstractRoom.name);
        return runtime;
    }

    private static void Step(IteratorRuntime runtime, int frames)
    {
        Oracle oracle = runtime.Context.Oracle;
        for (int i = 0; i < frames; i++) oracle.Update((i & 1) == 0);
        Check(runtime.IsActive, "runtime remains active through physics");
    }

    private static void MovementAndPose()
    {
        RuntimeScene scene = RuntimeScene.Create("BODY_MOVE");
        IteratorRuntime runtime = Spawn(scene);
        IteratorBody body = runtime.Body;
        Check(body is StandardIteratorBody && runtime.Arm is NoArm, "safe default components");
        Check(ReferenceEquals(body, runtime.Context.Body) && body.IsInitialized && runtime.Arm.IsInitialized, "context components initialized");
        var target = new Vector2(148f, 144f);
        body.MoveTo(target);
        Step(runtime, 120);
        Check(Vector2.Distance(body.Position, target) < 2f && body.Velocity.magnitude < 0.1f, "accelerates then arrives without persistent oscillation");
        Check(runtime.Context.Oracle.abstractPhysicalObject.pos.Tile == scene.Room.GetTilePosition(body.Chunks[0].pos), "game updates abstract coordinate");
        body.MoveTo(new Vector2(50f, 50f));
        Step(runtime, 8);
        body.Stop();
        Vector2 stopped = body.Position;
        Step(runtime, 30);
        Check(Vector2.Distance(body.Position, stopped) < 0.01f && body.Velocity.magnitude < 0.01f, "Stop holds the current center");
        body.LookAt(new Vector2(30f, 80f));
        Check(body.LookPoint == new Vector2(30f, 80f), "look intent persists without a player reference");
        body.SetPose(new IteratorPose("Test.Sideways", Vector2.right, rightHandOffset: new Vector2(22f, 3f)));
        Step(runtime, 80);
        Check(Vector2.Dot(body.Direction, Vector2.right) > 0.98f, "pose drives body orientation");
        Check(Math.Abs(Vector2.Distance(body.Chunks[0].pos, body.Chunks[1].pos) - 9f) < 0.1f, "game connection preserves body length");
        Check(Vector2.Distance(body.Position, body.RightHandPosition) > 20f, "pose supplies world-space limb target");
        body.ClearLookTarget();
        Check(body.LookPoint == null, "clear look target");
        Throws<ArgumentOutOfRangeException>(() => body.MoveTo(new Vector2(float.NaN, 0f)));
        Throws<ArgumentNullException>(() => body.SetPose(null));
        Throws<ArgumentOutOfRangeException>(() => new BodyProfile(maxSpeed: float.PositiveInfinity));
        Throws<ArgumentException>(() => new IteratorPose("Bad", Vector2.zero));
        runtime.Destroy();
        Check(body.IsDestroyed && body.Chunks.Count == 0 && body.Context.Oracle == null, "body clears host references");
        Throws<ObjectDisposedException>(body.Stop);
    }

    private static void TerrainAndGravity()
    {
        RuntimeScene wall = RuntimeScene.Create("BODY_WALL");
        for (int y = 0; y < wall.Room.TileHeight; y++) wall.Room.Tiles[6, y].Terrain = Room.Tile.TerrainType.Solid;
        IteratorRuntime runtime = Spawn(wall);
        runtime.Body.MoveTo(new Vector2(165f, 90f));
        Step(runtime, 100);
        Check(runtime.Body.Position.x <= 114.1f && runtime.Body.Position.x > 105f, "solid wall blocks MoveTo");
        Check(runtime.Body.Chunks[0].ContactPoint.x == 1 || runtime.Body.Chunks[1].ContactPoint.x == 1, "actual BodyChunk collision contact");

        RuntimeScene floor = RuntimeScene.Create("BODY_FLOOR");
        for (int x = 0; x < floor.Room.TileWidth; x++) floor.Room.Tiles[x, 1].Terrain = Room.Tile.TerrainType.Solid;
        IteratorRuntime falling = Spawn(floor, builder => builder.Body(ctx => new StandardIteratorBody(ctx, new BodyProfile(gravity: 1f, maxSpeed: 2f))));
        float start = falling.Body.Position.y;
        falling.Body.ReleaseMovement();
        Step(falling, 80);
        Check(falling.Body.Position.y < start - 20f, "released movement respects per-body gravity");
        // PhysicalObject solves chunk connections after terrain contact; its resting
        // connection correction can penetrate the contact surface by about one pixel.
        Check(falling.Body.Chunks[1].pos.y >= 45f && falling.Body.Chunks[1].ContactPoint.y == -1,
            "floor supports body through game physics; y=" + falling.Body.Chunks[1].pos.y + ", contact=" + falling.Body.Chunks[1].ContactPoint.y);
        Check(floor.Room.gravity == 0.73f, "body never rewrites room gravity");
    }

    private static void ArmConstraint()
    {
        RuntimeScene scene = RuntimeScene.Create("BODY_ARM");
        var anchor = new Vector2(90f, 90f);
        IteratorRuntime runtime = Spawn(scene, builder => builder.Arm(ctx => new FixedArm(ctx, anchor, 30f)));
        runtime.Body.MoveTo(new Vector2(175f, 105f));
        Step(runtime, 120);
        Check(Vector2.Distance(runtime.Body.Position, anchor) <= 30.01f && runtime.Body.Position.x > 110f, "fixed arm constrains reachable motion");
        Check(Vector2.Distance(runtime.Arm.ConstrainTarget(new Vector2(1000f, 90f)), anchor) <= 30.01f, "arm target projection");
        Check(runtime.Context.Oracle.arm == null, "framework arm needs no vanilla OracleArm");
        runtime.Body.ReleaseMovement();
        for (int i = 0; i < runtime.Body.Chunks.Count; i++) runtime.Body.Chunks[i].vel = new Vector2(4f, 0f);
        Step(runtime, 20);
        Check(Vector2.Distance(runtime.Body.Position, anchor) <= 30.01f, "constraint also limits inertial drift");
        RuntimeScene invalid = RuntimeScene.Create("BODY_BAD_ARM");
        Iterator.Create("BODY_BAD_ARM").Room("BODY_BAD_ARM").Arm(ctx => new FixedArm(ctx, Vector2.zero, 1f)).Register();
        Check(!IteratorRuntimes.TrySpawn(invalid.Room, out _) && invalid.Room.updateList.Count == 0, "unreachable initial arm rejects and cleans up");
    }

    private static void ComponentLifetime()
    {
        var calls = new List<string>();
        SingleChunkBody body = null;
        ProbeArm arm = null;
        RuntimeScene scene = RuntimeScene.Create("BODY_CUSTOM");
        IteratorRuntime runtime = Spawn(scene, builder => builder
            .Body(ctx => body = new SingleChunkBody(ctx, calls))
            .Arm(ctx => arm = new ProbeArm(ctx, calls)));
        Check(body.Chunks.Count == 1, "custom body can replace two-chunk anatomy");
        Check(string.Join(",", calls) == "Body.Initialize,Arm.Initialize", "body initializes before arm");
        float start = body.Position.x;
        Step(runtime, 3);
        Check(body.Position.x > start && runtime.UpdateCount == 3, "custom physics control is scheduled once per frame");

        RuntimeScene foreign = RuntimeScene.Create("BODY_FOREIGN");
        Iterator.Create("BODY_FOREIGN").Room("BODY_FOREIGN").Body(_ => body).Register();
        Check(!IteratorRuntimes.TrySpawn(foreign.Room, out _) && runtime.IsActive && !body.IsDestroyed, "foreign factory result is not destroyed");
        arm.FailAfterPhysics = true;
        runtime.Context.Oracle.Update(true);
        Check(runtime.DestroyReason == IteratorDestroyReason.UpdateFailed && body.IsDestroyed && arm.IsDestroyed, "critical constraint failure destroys only its instance");
        Check(calls[calls.Count - 2] == "Arm.Destroy" && calls[calls.Count - 1] == "Body.Destroy", "reverse component cleanup even when Body.OnDestroy throws");
        Check(scene.Room.updateList.Count == 0 && body.Chunks.Count == 0, "component failure still releases host");

        RuntimeScene interrupted = RuntimeScene.Create("BODY_INTERRUPTED");
        SingleChunkBody returned = null;
        Iterator.Create("BODY_INTERRUPTED").Room("BODY_INTERRUPTED").Body(ctx =>
        {
            ctx.Runtime.Destroy();
            return returned = new SingleChunkBody(ctx, new List<string>());
        }).Register();
        Check(!IteratorRuntimes.TrySpawn(interrupted.Room, out _) && returned.IsDestroyed && interrupted.Room.updateList.Count == 0,
            "factory-time destroy releases a subsequently returned component");
    }

    private sealed class SingleChunkBody : IteratorBody
    {
        private readonly List<string> _calls;
        internal SingleChunkBody(IteratorContext context, List<string> calls) : base(context) => _calls = calls;
        protected override void OnInitialize()
        {
            _calls.Add("Body.Initialize");
            ConfigurePhysics(new[] { new BodyChunk(Context.Oracle, 0, new Vector2(90f, 90f), 4f, 1f) }, Array.Empty<PhysicalObject.BodyChunkConnection>());
        }
        protected override void OnUpdate() => Chunks[0].vel = new Vector2(0.1f, 0f);
        protected override void OnDestroy() { _calls.Add("Body.Destroy"); throw new InvalidOperationException("injected cleanup failure"); }
    }

    private sealed class ProbeArm : IteratorArm
    {
        private readonly List<string> _calls;
        internal bool FailAfterPhysics;
        internal ProbeArm(IteratorContext context, List<string> calls) : base(context) => _calls = calls;
        protected override void OnInitialize() => _calls.Add("Arm.Initialize");
        protected override void OnAfterPhysics() { if (FailAfterPhysics) throw new InvalidOperationException("injected constraint failure"); }
        protected override void OnDestroy() => _calls.Add("Arm.Destroy");
    }

    private static void Check(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { _assertions++; return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
