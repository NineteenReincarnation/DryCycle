using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using DryCycle.Iterators;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;

namespace IteratorFramework.Tests;

internal static class RuntimeTests
{
    private static int _assertions;

    internal static int Run()
    {
        var log = new TextWriterTraceListener(Console.Error);
        Trace.Listeners.Add(log);
        var tests = new KeyValuePair<string, Action>[]
        {
            Test("transactional hook install, uninstall and re-enable", HookLifetime),
            Test("real Room.ReadyForAI and Oracle constructor integration", AutomaticSpawn),
            Test("deferred factory and exact lifecycle ordering", Lifecycle),
            Test("reentrant generation and update are bounded", Reentrancy),
            Test("destroy during initialization or update stops later callbacks", DestroyDuringCallbacks),
            Test("callback failures always release runtime and host", CallbackFailures),
            Test("missing, throwing and reused factories fail safely", FactoryFailures),
            Test("failed automatic spawn requires explicit retry", RetryPolicy),
            Test("unregister destroys instances before releasing ID", DefinitionRetirement),
            Test("room unload and re-realization create a fresh runtime", RoomLifetime),
            Test("same room name across sessions remains isolated", SessionLifetime),
            Test("room player context selection and reference release", PlayerContext),
            Test("Oracle destruction, removal and room mismatch", HostLifetime),
            Test("invalid or unready rooms cannot spawn", RoomValidation),
            Test("unowned Oracle identities are never adopted", OracleOwnership),
            Test("bad constructor IL layout is rejected", PatchLayoutGuard),
            Test("factory-time disable and unregistration cannot publish stale instances", InterruptedCreation),
            Test("repeated room realization leaves no live instances", RepeatedRooms)
        };
        int failures = 0;
        try
        {
            foreach (KeyValuePair<string, Action> test in tests)
            {
                try
                {
                    Check(IteratorHooks.Install(), "runtime hooks installed");
                    test.Value();
                    Console.WriteLine("PASS " + test.Key);
                }
                catch (Exception exception)
                {
                    failures++;
                    Console.Error.WriteLine("FAIL " + test.Key + Environment.NewLine + exception);
                }
                finally
                {
                    IteratorHooks.Uninstall();
                    foreach (IteratorDescriptor descriptor in IteratorRegistry.Registered)
                        IteratorRegistry.Unregister(descriptor);
                }
            }
        }
        finally
        {
            Trace.Listeners.Remove(log);
        }
        Console.WriteLine($"Iterator Framework phase 2: {tests.Length - failures}/{tests.Length} groups passed; {_assertions} assertions; {failures} failures.");
        Console.WriteLine("Managed room fixtures execute actual game methods and hooks; this does not replace a Unity in-game room test.");
        return failures == 0 ? 0 : 1;
    }

    private static KeyValuePair<string, Action> Test(string name, Action run) => new(name, run);

    private static ProbeRuntime Spawn(RuntimeScene scene, Action<ProbeRuntime, string> callback = null)
    {
        ProbeRuntime created = null;
        Iterator.Create("RT_" + scene.AbstractRoom.name).Room(scene.AbstractRoom.name)
            .Runtime(ctx => created = new ProbeRuntime(ctx, callback)).Register();
        Check(IteratorRuntimes.TrySpawn(scene.Room, out var runtime), "runtime spawned");
        Check(ReferenceEquals(runtime, created), "factory result returned");
        return created;
    }

    private static void HookLifetime()
    {
        RuntimeScene scene = RuntimeScene.Create("HOOKS_ROOM");
        ProbeRuntime runtime = Spawn(scene);
        Check(IteratorHooks.Install() && IteratorRuntimes.IsEnabled, "second install is idempotent");
        Check(IteratorRuntimes.Active.Count == 1, "install twice retains one runtime");
        Oracle host = runtime.Context.Oracle;
        IteratorHooks.Uninstall();
        IteratorHooks.Uninstall();
        Check(runtime.State == IteratorLifecycle.Destroyed && runtime.DestroyReason == IteratorDestroyReason.FrameworkDisabled, "disable releases active instance");
        Check(!IteratorRuntimes.IsEnabled && IteratorRuntimes.Active.Count == 0, "runtime service disabled");
        Check(!IteratorRuntimes.TryGet(host, out _) && !IteratorRuntimes.TrySpawn(scene.Room, out _), "disabled service cannot spawn");
        Check(IteratorID.IsRegistered(runtime.ID.Value), "definitions survive framework disable");
        Check(IteratorHooks.Install(), "reinstall succeeds");
        Check(IteratorRuntimes.TrySpawn(scene.Room, out var replacement) && !ReferenceEquals(runtime, replacement), "new runtime after reinstall");
    }

    private static void AutomaticSpawn()
    {
        RuntimeScene scene = RuntimeScene.Create("AUTO_ROOM", true);
        scene.World.name = "HR";
        IteratorDescriptor descriptor = Iterator.Create("AUTO").Room("AUTO_ROOM").Register();
        scene.Room.ReadyForAI();
        Check(IteratorRuntimes.TryGet(scene.Room, out var runtime) && runtime.IsActive, "room hook automatically spawned");
        Oracle oracle = runtime.Context.Oracle;
        Check(oracle != null && oracle.ID.value == "AUTO" && ReferenceEquals(oracle.room, scene.Room), "Oracle identity and room binding");
        Check(ReferenceEquals(oracle.abstractPhysicalObject.realizedObject, oracle), "PhysicalObject base constructor executed");
        Check(oracle.bodyChunks.Length == 2 && oracle.bodyChunkConnections.Length == 1, "valid engine host structure");
        Check(ReferenceEquals(runtime.Descriptor, descriptor) && ReferenceEquals(runtime.Context.Runtime, runtime), "context and descriptor bound");
        Check(IteratorRuntimes.TryGet(oracle, out var found) && ReferenceEquals(found, runtime), "Oracle instance lookup");
        Check(scene.Room.updateList.Count == 1 && scene.Room.physicalObjects[1].Count == 1, "one actual room object");
        Check(scene.AbstractRoom.entities.Count == 0, "realization host never enters persistent abstract entities");
        Check(scene.Room.gravity == 0.73f && oracle.mySwarmers.Count == 0 && oracle.myScreen == null && oracle.arm == null, "no vanilla room, swarmer, projection or arm side effects");
        Check(oracle.oracleBehavior == null && oracle.graphicsModule == null, "no premature vanilla behavior or graphics");
        Check(ReferenceEquals(runtime.Context.StorySession, scene.Game.session), "story context is available");
        scene.Room.ReadyForAI();
        Check(scene.Room.updateList.Count == 1 && IteratorRuntimes.Active.Count == 1, "repeated readiness does not duplicate host");
        oracle.InitiateGraphicsModule();
        oracle.Update(true);
        Check(runtime.UpdateCount == 1 && oracle.graphicsModule == null, "host update routes to runtime without vanilla graphics");
    }

    private static void Lifecycle()
    {
        RuntimeScene scene = RuntimeScene.Create("LIFECYCLE");
        int factories = 0;
        ProbeRuntime captured = null;
        IteratorDescriptor definition = Iterator.Create("LIFECYCLE").Room("LIFECYCLE")
            .Runtime(ctx => { factories++; return captured = new ProbeRuntime(ctx); }).Build();
        Check(factories == 0, "Build does not run factory");
        IteratorRegistry.Register(definition);
        Check(factories == 0, "Register does not run factory");
        Check(IteratorRuntimes.TrySpawn(scene.Room, out var runtime) && factories == 1, "factory runs exactly once at spawn");
        Check(string.Join(",", captured.Calls) == "Create:RuntimeCreated,Initialize:Initializing,RoomReady:Initialized,Activate:Active", "ordered lifecycle states");
        Check(captured.Initialize() && captured.Calls.Count == 4, "repeated initialization is harmless");
        Check(IteratorRuntimes.TrySpawn(scene.Room, out var same) && ReferenceEquals(runtime, same) && factories == 1, "duplicate spawn returns active instance");
        var oldSnapshot = IteratorRuntimes.Active;
        runtime.Context.Oracle.Update(true);
        Check(captured.Calls[4] == "Update:Active" && captured.Calls[5] == "LateUpdate:Active" && runtime.UpdateCount == 1, "one update then one late update");
        runtime.Destroy();
        runtime.Destroy();
        captured.Tick();
        Check(captured.DestroyCalls == 1 && captured.State == IteratorLifecycle.Destroyed && captured.Calls.Count == 7, "destroy once and no updates afterwards");
        Check(oldSnapshot.Count == 1 && IteratorRuntimes.Active.Count == 0, "runtime snapshots stay stable");
        Check(runtime.Context.Room == null && runtime.Context.World == null && runtime.Context.Game == null && runtime.Context.Oracle == null, "destroy releases all context game references");
        Check(runtime.Context.PrimaryPlayer == null && runtime.Context.Players.Count == 0, "player references cleared");
        Check(ReferenceEquals(runtime.Descriptor, definition), "definition retained for diagnostics");
    }

    private static void Reentrancy()
    {
        RuntimeScene scene = RuntimeScene.Create("REENTRANT");
        int factoryCalls = 0;
        Iterator.Create("REENTRANT").Room("REENTRANT").Runtime(ctx =>
        {
            factoryCalls++;
            Check(!IteratorRuntimes.TrySpawn(scene.Room, out _), "same room cannot spawn inside factory");
            return new ProbeRuntime(ctx, (runtime, stage) =>
            {
                if (stage == "Initialize") Check(!IteratorRuntimes.TrySpawn(scene.Room, out _), "initialization cannot duplicate spawn");
                if (stage == "Update") runtime.Context.Oracle.Update(false);
                if (stage == "Destroy") runtime.Destroy();
            });
        }).Register();
        Check(IteratorRuntimes.TrySpawn(scene.Room, out var result), "outer spawn succeeds");
        result.Context.Oracle.Update(true);
        Check(result.UpdateCount == 1 && factoryCalls == 1, "recursive update and factory are bounded");
        result.Destroy();
        Check(((ProbeRuntime)result).DestroyCalls == 1, "recursive destroy is bounded");
    }

    private static void DestroyDuringCallbacks()
    {
        foreach (string stop in new[] { "Create", "Initialize", "RoomReady", "Activate" })
        {
            RuntimeScene scene = RuntimeScene.Create("STOP_" + stop);
            ProbeRuntime probe = null;
            Iterator.Create("STOP_" + stop).Room(scene.AbstractRoom.name).Runtime(ctx => probe = new ProbeRuntime(ctx,
                (runtime, stage) => { if (stage == stop) runtime.Destroy(); })).Register();
            Check(!IteratorRuntimes.TrySpawn(scene.Room, out _), "stopped initialization does not return an active runtime");
            Check(probe.State == IteratorLifecycle.Destroyed && probe.DestroyCalls == 1, "partial initialization released");
            Check(scene.Room.updateList.Count == 0 && !IteratorRuntimes.TryGet(scene.Room, out _), "no host from stopped initialization");
            Check(probe.Calls[probe.Calls.Count - 2].StartsWith(stop + ":"), "no callbacks beyond cancellation point");
        }
        RuntimeScene updating = RuntimeScene.Create("STOP_UPDATE");
        ProbeRuntime live = Spawn(updating, (runtime, stage) => { if (stage == "Update") runtime.Destroy(); });
        live.Context.Oracle.Update(true);
        Check(live.UpdateCount == 0 && !live.Calls.Contains("LateUpdate:Active"), "destroy in update prevents late update");
    }

    private static void CallbackFailures()
    {
        foreach (string fail in new[] { "Create", "Initialize", "RoomReady", "Activate", "Update", "LateUpdate", "Destroy" })
        {
            RuntimeScene scene = RuntimeScene.Create("FAIL_" + fail);
            ProbeRuntime probe = null;
            Iterator.Create("FAIL_" + fail).Room(scene.AbstractRoom.name).Runtime(ctx => probe = new ProbeRuntime(ctx,
                (_, stage) => { if (stage == fail) throw new IOException("injected " + stage); })).Register();
            bool spawned = IteratorRuntimes.TrySpawn(scene.Room, out _);
            if (fail == "Update" || fail == "LateUpdate")
            {
                Check(spawned, "update failure starts from active instance");
                probe.Context.Oracle.Update(true);
            }
            else if (fail == "Destroy")
            {
                Check(spawned, "destroy failure starts from active instance");
                probe.Destroy();
            }
            else Check(!spawned, "initialization exception does not escape or report success");
            Check(probe.State == IteratorLifecycle.Destroyed && probe.DestroyCalls == 1, "failed callback still destroys once");
            Check(probe.Context.Room == null && !IteratorRuntimes.TryGet(scene.Room, out _), "failed instance releases bindings");
            Check(scene.Room.updateList.Count == 0 && scene.Room.physicalObjects[1].Count == 0, "failed instance releases room collections");
            Check(IteratorID.IsRegistered(probe.ID.Value), "instance failure does not remove definition");
        }
    }

    private static void FactoryFailures()
    {
        foreach (bool throws in new[] { false, true })
        {
            RuntimeScene scene = RuntimeScene.Create(throws ? "FACTORY_THROW" : "FACTORY_NULL");
            IteratorContext captured = null;
            Iterator.Create(scene.AbstractRoom.name).Room(scene.AbstractRoom.name).Runtime(ctx =>
            {
                captured = ctx;
                if (throws) throw new IOException("factory failed");
                return null;
            }).Register();
            Check(!IteratorRuntimes.TrySpawn(scene.Room, out _), "invalid factory handled");
            Check(captured.Room == null && captured.Game == null, "failed factory context releases references");
            Check(scene.Room.updateList.Count == 0 && IteratorRuntimes.Active.Count == 0, "no partial host after factory failure");
        }
        RuntimeScene original = RuntimeScene.Create("ORIGINAL_FACTORY");
        ProbeRuntime originalRuntime = Spawn(original);
        RuntimeScene wrong = RuntimeScene.Create("WRONG_FACTORY");
        Iterator.Create("WRONG_FACTORY").Room("WRONG_FACTORY").Runtime(_ => originalRuntime).Register();
        Check(!IteratorRuntimes.TrySpawn(wrong.Room, out _), "foreign factory result refused");
        Check(originalRuntime.IsActive, "rejecting foreign result does not destroy its rightful instance");
    }

    private static void RetryPolicy()
    {
        RuntimeScene scene = RuntimeScene.Create("RETRY");
        int attempts = 0;
        Iterator.Create("RETRY").Room("RETRY").Runtime(ctx => ++attempts == 1 ? null : new IteratorRuntime(ctx)).Register();
        scene.Room.ReadyForAI();
        scene.Room.ReadyForAI();
        Check(attempts == 1 && IteratorRuntimes.Active.Count == 0, "failed automatic readiness does not retry repeatedly");
        Check(IteratorRuntimes.TrySpawn(scene.Room, out _) && attempts == 2, "explicit retry succeeds");
    }

    private static void DefinitionRetirement()
    {
        RuntimeScene scene = RuntimeScene.Create("RETIRING");
        IteratorDescriptor definition = null;
        IteratorDescriptor other = Iterator.Create("OTHER_OLD").Room("OTHER_OLD_ROOM").Register();
        ProbeRuntime runtime = null;
        definition = Iterator.Create("RETIRING").Room("RETIRING").Runtime(ctx => runtime = new ProbeRuntime(ctx, (destroying, stage) =>
        {
            if (stage != "Destroy") return;
            Check(IteratorID.IsRegistered("RETIRING") && new Oracle.OracleID("RETIRING", false).Index >= 0, "definition and game ID retained throughout destroy callback");
            Check(!IteratorRegistry.Unregister(definition), "recursive unregistration cannot remove twice");
            Check(!IteratorRuntimes.TrySpawn(scene.Room, out _), "retiring definition cannot respawn");
            Throws<InvalidOperationException>(() => IteratorRegistry.Register(definition));
            IteratorRegistry.Unregister(other);
            Iterator.Create("OTHER_NEW").Room("OTHER_NEW_ROOM").Register();
        })).Register();
        Check(IteratorRuntimes.TrySpawn(scene.Room, out _), "retirement target spawned");
        Check(IteratorRegistry.Unregister(definition), "unregister succeeds");
        Check(runtime.DestroyReason == IteratorDestroyReason.DefinitionUnregistered && runtime.DestroyCalls == 1, "unregister ends runtime once");
        Check(!IteratorID.IsRegistered("RETIRING") && new Oracle.OracleID("RETIRING", false).Index == -1, "ID released after runtime");
        Check(!IteratorID.IsRegistered("OTHER_OLD") && IteratorID.IsRegistered("OTHER_NEW"), "callback registry changes survive retirement commit");
    }

    private static void RoomLifetime()
    {
        RuntimeScene scene = RuntimeScene.Create("ROOM_LIFETIME");
        ProbeRuntime runtime = Spawn(scene);
        Room old = scene.Room;
        int originalCalls = 0;
        IteratorHooks.RoomUnloaded(_ => { originalCalls++; Check(runtime.State == IteratorLifecycle.Destroyed, "framework cleans up before original room unload"); }, old);
        IteratorHooks.RoomUnloaded(_ => originalCalls++, old);
        Check(originalCalls == 2, "original room unload is called once per notification");
        Check(runtime.DestroyReason == IteratorDestroyReason.RoomUnloaded && runtime.DestroyCalls == 1, "room unload is idempotent");
        Check(!IteratorRuntimes.TrySpawn(old, out _), "closed realization cannot spawn again");
        scene.Realize();
        scene.Room.ReadyForAI();
        Check(IteratorRuntimes.TryGet(scene.Room, out var current) && !ReferenceEquals(runtime, current), "new realization gets new instance");
        Check(!IteratorRuntimes.TryGet(old, out _), "old room no longer resolves");
    }

    private static void SessionLifetime()
    {
        RuntimeScene first = RuntimeScene.Create("SAME_ROOM");
        RuntimeScene second = RuntimeScene.Create("SAME_ROOM");
        Iterator.Create("SAME_ID").Room("SAME_ROOM").Register();
        Check(IteratorRuntimes.TrySpawn(first.Room, out var a) && IteratorRuntimes.TrySpawn(second.Room, out _), "same name spawns per session");
        IteratorRuntimes.TryGet(second.Room, out var b);
        Check(!ReferenceEquals(a, b) && IteratorRuntimes.Active.Count == 2, "session instances remain distinct");
        IteratorHooks.SessionEnded(_ => Check(a.State == IteratorLifecycle.Destroyed && b.IsActive, "cleanup precedes original session shutdown"), first.Game);
        Throws<IOException>(() => IteratorHooks.SessionEnded(_ => throw new IOException("original shutdown failed"), first.Game));
        Check(a.DestroyReason == IteratorDestroyReason.SessionEnded && b.IsActive, "shutdown affects only its game");
        Check(!IteratorRuntimes.TrySpawn(first.Room, out _), "closed game cannot respawn");
        RuntimeScene third = RuntimeScene.Create("SAME_ROOM");
        Check(IteratorRuntimes.TrySpawn(third.Room, out var c) && !ReferenceEquals(a, c), "new session can spawn same definition");
    }

    private static void PlayerContext()
    {
        RuntimeScene scene = RuntimeScene.Create("PLAYER_CONTEXT", true);
        Player dead = scene.AddPlayer(true, false);
        Player shortcut = scene.AddPlayer(false, true);
        Player alive = scene.AddPlayer(false, false);
        ProbeRuntime runtime = Spawn(scene);
        Check(runtime.Context.Players.Count == 3 && ReferenceEquals(runtime.Context.PrimaryPlayer, alive), "prefer living player outside shortcut");
        var view = runtime.Context.Players;
        Throws<NotSupportedException>(() => ((IList<Player>)view).Clear());
        alive.room = null;
        runtime.Context.Oracle.Update(true);
        Check(view.Count == 2 && ReferenceEquals(runtime.Context.PrimaryPlayer, shortcut), "live view refreshes when player leaves");
        shortcut.room = null;
        runtime.Context.Oracle.Update(true);
        Check(ReferenceEquals(runtime.Context.PrimaryPlayer, dead), "fallback to remaining player");
        runtime.Destroy();
        Check(view.Count == 0 && runtime.Context.PrimaryPlayer == null && runtime.Context.StorySession == null, "destroy clears retained player view and story context");
    }

    private static void HostLifetime()
    {
        RuntimeScene first = RuntimeScene.Create("HOST_DESTROY");
        ProbeRuntime a = Spawn(first);
        Oracle host = a.Context.Oracle;
        host.Destroy();
        host.Destroy();
        host.Update(true);
        Check(a.DestroyReason == IteratorDestroyReason.OracleRemoved && a.DestroyCalls == 1 && a.UpdateCount == 0, "Oracle destroy controls lifecycle");
        Check(!IteratorRuntimes.TryGet(host, out _) && host.room == null && host.abstractPhysicalObject.realizedObject == null, "destroy detaches game and lookup references");

        RuntimeScene second = RuntimeScene.Create("HOST_REMOVE");
        ProbeRuntime b = Spawn(second);
        second.Room.RemoveObject(b.Context.Oracle);
        Check(b.DestroyReason == IteratorDestroyReason.OracleRemoved && second.Room.updateList.Count == 0, "real RemoveObject/RemoveFromRoom hook releases runtime");

        RuntimeScene moved = RuntimeScene.Create("HOST_MOVED");
        ProbeRuntime c = Spawn(moved);
        Oracle movedHost = c.Context.Oracle;
        RuntimeScene destination = RuntimeScene.Create("DESTINATION");
        destination.Room.AddObject(movedHost);
        movedHost.Update(true);
        Check(c.DestroyReason == IteratorDestroyReason.HostUnavailable, "unexpected room transfer retires old instance");
        Check(moved.Room.updateList.Count == 0 && destination.Room.updateList.Count == 0, "transferred host removed from both room lists");
    }

    private static void RoomValidation()
    {
        Check(!IteratorRuntimes.TrySpawn(null, out _) && !IteratorRuntimes.TryGet((Room)null, out _) && !IteratorRuntimes.TryGet((Oracle)null, out _), "null room/Oracle is safe");
        RuntimeScene scene = RuntimeScene.Create("UNREADY");
        Check(!IteratorRuntimes.TrySpawn(scene.Room, out _), "unregistered room is untouched");
        Iterator.Create("UNREADY").Room("UNREADY").Register();
        scene.Room.loadingProgress = 1;
        Check(!IteratorRuntimes.TrySpawn(scene.Room, out _), "unready room is refused");
        scene.Room.loadingProgress = 3;
        scene.AbstractRoom.realizedRoom = null;
        Check(!IteratorRuntimes.TrySpawn(scene.Room, out _), "stale realization is refused");
        scene.AbstractRoom.realizedRoom = scene.Room;
        for (int x = 0; x < 10; x++)
        for (int y = 0; y < 10; y++) scene.Room.Tiles[x, y].Terrain = Room.Tile.TerrainType.Solid;
        Check(!IteratorRuntimes.TrySpawn(scene.Room, out _) && scene.Room.updateList.Count == 0, "missing clear spawn location fails without a host");
    }

    private static void OracleOwnership()
    {
        RuntimeScene scene = RuntimeScene.Create("OWNERSHIP");
        Iterator.Create("OWNERSHIP").Room("OWNERSHIP").Register();
        Oracle unrelated = RuntimeScene.Raw<Oracle>();
        unrelated.ID = new Oracle.OracleID("OWNERSHIP", false);
        unrelated.room = scene.Room;
        Check(!IteratorHost.InitializeOracle(unrelated, null, scene.Room), "constructor adapter passes through every non-framework Oracle");
        Check(!IteratorRuntimes.TryGet(unrelated, out _), "registered ID alone does not grant runtime ownership");
        Check(unrelated.ID.value == "OWNERSHIP" && ReferenceEquals(unrelated.room, scene.Room), "pass-through does not mutate vanilla object");
    }

    private static void PatchLayoutGuard()
    {
        using (var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("LayoutProbe", new Version(1, 0)), "LayoutProbe", ModuleKind.Dll))
        {
            var method = new MethodDefinition("ChangedConstructor", MethodAttributes.Public, assembly.MainModule.TypeSystem.Void);
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            using (var context = new ILContext(method))
                Throws<InvalidOperationException>(() => IteratorHooks.PatchOracleConstructor(context));
        }
        Check(IteratorRuntimes.IsEnabled, "offline guard check leaves installed hooks untouched");
    }

    private static void InterruptedCreation()
    {
        RuntimeScene scene = RuntimeScene.Create("DISABLE_FACTORY");
        ProbeRuntime captured = null;
        Iterator.Create("DISABLE_FACTORY").Room("DISABLE_FACTORY").Runtime(ctx =>
        {
            IteratorHooks.Uninstall();
            Check(IteratorHooks.Install(), "re-enable during factory");
            return captured = new ProbeRuntime(ctx);
        }).Register();
        Check(!IteratorRuntimes.TrySpawn(scene.Room, out _), "generation change prevents publishing old creation");
        Check(captured.DestroyReason == IteratorDestroyReason.FrameworkDisabled && captured.Context.Room == null && scene.Room.updateList.Count == 0, "interrupted factory result released");

        RuntimeScene retired = RuntimeScene.Create("RETIRE_FACTORY");
        IteratorDescriptor definition = null;
        ProbeRuntime removed = null;
        definition = Iterator.Create("RETIRE_FACTORY").Room("RETIRE_FACTORY").Runtime(ctx =>
        {
            IteratorRegistry.Unregister(definition);
            return removed = new ProbeRuntime(ctx);
        }).Register();
        Check(!IteratorRuntimes.TrySpawn(retired.Room, out _), "factory cannot publish an unregistered definition");
        Check(removed.DestroyReason == IteratorDestroyReason.DefinitionUnregistered && !IteratorID.IsRegistered("RETIRE_FACTORY"), "retired factory result released");
    }

    private static void RepeatedRooms()
    {
        RuntimeScene scene = RuntimeScene.Create("LOOP_ROOM");
        Iterator.Create("LOOP_RUNTIME").Room("LOOP_ROOM").Register();
        for (int i = 0; i < 30; i++)
        {
            scene.Room.ReadyForAI();
            Check(IteratorRuntimes.TryGet(scene.Room, out var runtime) && runtime.IsActive, "room realization spawned");
            Oracle host = runtime.Context.Oracle;
            host.Update(true);
            Check(runtime.UpdateCount == 1, "runtime updated once");
            IteratorRuntimes.RoomUnloaded(scene.Room);
            Check(runtime.State == IteratorLifecycle.Destroyed && runtime.Context.Game == null, "unload releases runtime");
            Check(IteratorRuntimes.Active.Count == 0 && scene.Room.updateList.Count == 0, "no remaining live object");
            Check(!IteratorRuntimes.TryGet(host, out _), "no remaining Oracle lookup");
            scene.Realize();
        }
    }

    private static void Check(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }

    private static void Throws<T>(Action callback) where T : Exception
    {
        try { callback(); }
        catch (T) { Check(true, typeof(T).Name + " thrown"); return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private sealed class ProbeRuntime : IteratorRuntime
    {
        private readonly Action<ProbeRuntime, string> _callback;
        internal readonly List<string> Calls = new();
        internal int DestroyCalls;
        internal ProbeRuntime(IteratorContext context, Action<ProbeRuntime, string> callback = null) : base(context) => _callback = callback;
        private void Record(string stage)
        {
            Calls.Add(stage + ":" + State);
            _callback?.Invoke(this, stage);
        }
        protected override void OnCreate() => Record("Create");
        protected override void OnInitialize() => Record("Initialize");
        protected override void OnRoomReady() => Record("RoomReady");
        protected override void OnActivate() => Record("Activate");
        protected override void OnUpdate() => Record("Update");
        protected override void OnLateUpdate() => Record("LateUpdate");
        protected override void OnDestroy() { DestroyCalls++; Record("Destroy"); }
    }
}
