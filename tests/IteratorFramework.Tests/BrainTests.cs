using System;
using System.Collections.Generic;
using DryCycle.Iterators;
using UnityEngine;

namespace IteratorFramework.Tests;

// Focused phase-five integration: real Runtime, Room.VisualContact and body/face
// data, deterministic manual player positions; no Unity/gameplay acceptance claim.
internal static class BrainTests
{
    private static int _assertions;
    internal static int Run()
    {
        int failures = 0;
        Action[] groups = { ActionArbitration, PlayerSensing, FaultIsolation, BrainLifetime, PwnObservation };
        foreach (Action test in groups)
        {
            try { Check(IteratorHooks.Install(), "hooks installed"); test(); Console.WriteLine("PASS brain " + test.Method.Name); }
            catch (Exception exception) { failures++; Console.Error.WriteLine("FAIL brain " + test.Method.Name + Environment.NewLine + exception); }
            finally
            {
                IteratorHooks.Uninstall(); PwnIteratorExample.Unregister();
                foreach (IteratorDescriptor definition in IteratorRegistry.Registered) IteratorRegistry.Unregister(definition);
            }
        }
        Console.WriteLine($"Iterator Framework phase 5: {groups.Length - failures}/{groups.Length} groups passed; {_assertions} assertions; {failures} failures.");
        return failures == 0 ? 0 : 1;
    }
    private static IteratorRuntime Spawn(RuntimeScene scene, Func<IteratorContext, IteratorBrain> factory = null)
    {
        IteratorBuilder builder = Iterator.Create(scene.AbstractRoom.name).Room(scene.AbstractRoom.name);
        if (factory != null) builder.Brain(factory);
        IteratorDescriptor definition = builder.Register();
        Check(IteratorRuntimes.TrySpawn(scene.Room, out IteratorRuntime runtime), "spawn " + scene.AbstractRoom.name);
        Check(ReferenceEquals(runtime.Context.Brain, runtime.Brain) && runtime.Brain.IsInitialized, "Brain initialized and exposed through Context");
        return runtime;
    }
    private static void Step(IteratorRuntime runtime, int frames = 1)
    {
        Oracle oracle = runtime.Context.Oracle;
        for (int i = 0; i < frames; i++) oracle.Update((i & 1) == 0);
    }

    private static void ActionArbitration()
    {
        var calls = new List<string>();
        var low = new ProbeAction("Low", calls, 10);
        var blocked = new ProbeAction("Blocked", calls, 100) { Allowed = false };
        var equalFirst = new ProbeAction("First", calls, 20);
        var equalSecond = new ProbeAction("Second", calls, 20);
        var locked = new ProbeAction("Locked", calls, 30, false);
        var urgent = new ProbeAction("Urgent", calls, 200);
        TestBrain brain = null;
        IteratorRuntime runtime = Spawn(RuntimeScene.Create("BRAIN_ACTIONS"), ctx => brain = new TestBrain(ctx, low, blocked, equalFirst, equalSecond, locked, urgent));
        Check(brain.CurrentAction.Name == "Idle", "brain starts in safe Idle before first update");
        brain.SetAction(blocked); brain.SetAction(low); Step(runtime);
        Check(ReferenceEquals(brain.CurrentAction, low) && blocked.Enters == 0, "false high-priority condition does not block lower eligible action");
        brain.SetAction(equalSecond); brain.SetAction(equalFirst); Step(runtime);
        Check(ReferenceEquals(brain.CurrentAction, equalFirst) && low.Exits == 1, "equal priority resolves by registration order");
        brain.SetAction(locked); Step(runtime);
        brain.SetAction(urgent); Step(runtime, 3);
        Check(ReferenceEquals(brain.CurrentAction, locked) && urgent.Enters == 0, "noninterruptible action retains pending higher-priority request");
        locked.FinishOnUpdate = true; Step(runtime);
        Check(ReferenceEquals(brain.CurrentAction, locked) && locked.IsCompleted, "completion does not recursively enter a new action in the same frame");
        Step(runtime);
        Check(ReferenceEquals(brain.CurrentAction, urgent) && locked.LastExit == IteratorActionExitReason.Completed, "completion releases lock and dispatches pending request");
        brain.SetAction(low); Step(runtime);
        Check(ReferenceEquals(brain.CurrentAction, urgent), "lower priority cannot interrupt valid action");
        urgent.Continue = false; Step(runtime);
        Check(ReferenceEquals(brain.CurrentAction, low) && urgent.LastExit == IteratorActionExitReason.ConditionLost, "condition loss releases current action");
        equalFirst.Entered = () => brain.SetAction(equalSecond);
        brain.SetAction(equalFirst); Step(runtime);
        Check(ReferenceEquals(brain.CurrentAction, equalFirst), "enter callback request deferred");
        Step(runtime);
        Check(ReferenceEquals(brain.CurrentAction, equalSecond), "deferred request runs next frame");
        int entered = equalSecond.Enters; brain.SetAction(equalSecond); Step(runtime);
        Check(equalSecond.Enters == entered, "requesting current action never restarts it");
        Throws(() => brain.Use(new EventProbe()), "configuration freezes after initialize");
        Throws(() => brain.SetAction("Missing"), "unknown action rejected");
        Check(runtime.IsActive && runtime.Graphics.IsEnabled, "behavior control leaves runtime and graphics healthy");
    }

    private static void PlayerSensing()
    {
        RuntimeScene scene = RuntimeScene.Create("BRAIN_SENSORS");
        var events = new EventProbe();
        var profile = new IteratorSensorProfile(sampleInterval: 3, observeRange: 80, retainRange: 100, nearRange: 20, nearExitRange: 30, targetSwitchMargin: 10);
        IteratorRuntime runtime = Spawn(scene, ctx => new StandardIteratorBrain(ctx, profile).Use(events));
        Player a = AddPlayer(scene, new Vector2(140, 90)), b = AddPlayer(scene, new Vector2(150, 90));
        Step(runtime);
        var observe = (ObservePlayerAction)runtime.Brain.CurrentAction;
        Check(ReferenceEquals(observe.Target.Player, a) && runtime.Body.LookPoint == a.bodyChunks[0].pos, "observes nearest visible living player");
        Check(events.Count(IteratorPlayerEventKind.Entered) == 2 && runtime.Brain.Sensors.SampleCount == 1, "presence notifications emitted once at centralized sample");
        a.bodyChunks[0].pos = new Vector2(145, 90); Step(runtime, 2);
        Check(runtime.Brain.Sensors.SampleCount == 1 && runtime.Body.LookPoint == new Vector2(140, 90), "sample interval caches position, distance and LOS");
        Step(runtime);
        Check(runtime.Brain.Sensors.SampleCount == 2 && runtime.Body.LookPoint == new Vector2(145, 90), "next sample updates observation");
        b.bodyChunks[0].pos = new Vector2(140, 90); Refresh(runtime);
        Check(ReferenceEquals(observe.Target.Player, a), "small distance difference does not churn target");
        b.bodyChunks[0].pos = new Vector2(110, 90); Refresh(runtime);
        Check(ReferenceEquals(observe.Target.Player, b) && events.Count(IteratorPlayerEventKind.Approached) == 1, "meaningfully closer player selected with approach event");
        b.bodyChunks[0].pos = new Vector2(115, 90); Refresh(runtime);
        Check(runtime.Brain.Sensors.TryGet(b, out PlayerObservation bRecord) && bRecord.IsNear, "near hysteresis retains inside exit range");
        b.bodyChunks[0].pos = new Vector2(125, 90); Refresh(runtime);
        Check(!bRecord.IsNear && events.Count(IteratorPlayerEventKind.MovedAway) == 1, "near exit emitted once beyond exit range");
        for (int y = 0; y < scene.Room.TileHeight; y++) scene.Room.Tiles[5, y].Terrain = Room.Tile.TerrainType.Solid;
        Refresh(runtime);
        Check(runtime.Brain.Sensors.IsEnabled && runtime.Brain.CurrentAction.Name == "Idle" && runtime.Body.LookPoint == null, "actual Room.VisualContact blocks observation through wall");
        for (int y = 0; y < scene.Room.TileHeight; y++) scene.Room.Tiles[5, y].Terrain = Room.Tile.TerrainType.Air;
        b.bodyChunks[0].pos = new Vector2(155, 90); a.bodyChunks[0].pos = new Vector2(170, 90); Refresh(runtime);
        a.inShortcut = true; RuntimeScene.Set(b, "<dead>k__BackingField", true); Refresh(runtime);
        Check(runtime.Brain.CurrentAction.Name == "Idle" && events.Count(IteratorPlayerEventKind.Died) == 1, "dead and shortcut players cannot be observed");
        RuntimeScene.Set(b, "<dead>k__BackingField", false); Refresh(runtime);
        Check(runtime.Brain.CurrentAction.Name == "Observe" && events.Count(IteratorPlayerEventKind.Revived) == 1, "revival restores observation");
        b.room = null; a.room = null; Refresh(runtime);
        Check(events.Count(IteratorPlayerEventKind.Left) == 2 && bRecord.Player == null && runtime.Brain.Sensors.Players.Count == 0, "leaving emits notifications then clears player records");
        Check(events.LeftPlayersAvailable == 2 && runtime.Brain.CurrentAction.Name == "Idle", "left event has valid payload during callback");
        Throws(() => new IteratorSensorProfile(observeRange: 500, retainRange: 100), "invalid hysteresis profile rejected");
    }

    private static void FaultIsolation()
    {
        var bad = new FailingModule(); var healthy = new EventProbe();
        IteratorRuntime runtime = Spawn(RuntimeScene.Create("BRAIN_MODULE_FAULT"), ctx => new IteratorBrain(ctx).Use(bad).Use(healthy));
        Step(runtime);
        Check(!bad.IsEnabled && healthy.IsEnabled && !bad.Action.IsEnabled && runtime.Brain.CurrentAction.Name == "Idle", "module fault disables its owned action only");
        Check(runtime.IsActive && runtime.Brain.IsEnabled, "optional module failure leaves brain/runtime active");

        var fail = new ProbeAction("Fail", new List<string>(), 100) { ThrowUpdate = true };
        IteratorRuntime actionRuntime = Spawn(RuntimeScene.Create("BRAIN_ACTION_FAULT"), ctx => new TestBrain(ctx, fail));
        actionRuntime.Brain.SetAction(fail); Step(actionRuntime);
        Check(!fail.IsEnabled && fail.Exits == 1 && fail.LastExit == IteratorActionExitReason.Failed, "action fault exits exactly once immediately");
        Step(actionRuntime);
        Check(actionRuntime.Brain.CurrentAction.Name == "Idle" && actionRuntime.IsActive, "faulted action falls back to Idle next frame");

        IteratorRuntime brainRuntime = Spawn(RuntimeScene.Create("BRAIN_GLOBAL_FAULT"), ctx => new ThrowingBrain(ctx));
        Step(brainRuntime);
        Check(!brainRuntime.Brain.IsEnabled && brainRuntime.IsActive && brainRuntime.Graphics.IsEnabled, "global brain error cannot kill body or graphics");
        var actor = new ProbeAction("EnterDestroys", new List<string>(), 1);
        IteratorRuntime interrupted = Spawn(RuntimeScene.Create("BRAIN_ENTER_DESTROY"), ctx => new TestBrain(ctx, actor));
        actor.Entered = interrupted.Destroy;
        interrupted.Brain.SetAction(actor); Step(interrupted);
        Check(interrupted.State == IteratorLifecycle.Destroyed && actor.Updates == 0 && actor.Exits == 1 && actor.Destroys == 1, "destroy during Enter prevents Update and cleans up once");
    }

    private static void BrainLifetime()
    {
        IteratorRuntime fallback = Spawn(RuntimeScene.Create("BRAIN_FACTORY_FAIL"), _ => throw new InvalidOperationException("factory failure"));
        Check(fallback.Brain.GetType() == typeof(IteratorBrain) && fallback.Brain.CurrentAction.Name == "Idle", "factory failure uses passive fallback");
        IteratorRuntime foreign = Spawn(RuntimeScene.Create("BRAIN_FACTORY_FOREIGN"), _ => fallback.Brain);
        Check(!ReferenceEquals(foreign.Brain, fallback.Brain) && !fallback.Brain.IsDestroyed, "foreign factory result stays owned and intact");
        var partialAction = new ProbeAction("Partial", new List<string>(), 1);
        var partialModule = new EventProbe(); InitFailBrain partial = null;
        IteratorRuntime recovered = Spawn(RuntimeScene.Create("BRAIN_INIT_FAIL"), ctx => partial = new InitFailBrain(ctx, partialAction, partialModule));
        Check(recovered.Brain.GetType() == typeof(IteratorBrain) && partial.IsDestroyed && partialAction.Destroys == 1 && partialModule.Destroys == 1,
            "initialization failure releases partial action/module ownership before passive fallback");
        RuntimeScene scene = RuntimeScene.Create("BRAIN_FACTORY_DESTROY");
        IteratorBrain late = null;
        Iterator.Create(scene.AbstractRoom.name).Room(scene.AbstractRoom.name).Brain(ctx =>
        { ctx.Runtime.Destroy(); return late = new StandardIteratorBrain(ctx); }).Register();
        Check(!IteratorRuntimes.TrySpawn(scene.Room, out _) && late.IsDestroyed, "brain returned after factory-time destruction is released");

        var actions = new List<string>(); var action = new ProbeAction("Owned", actions, 1);
        var module = new EventProbe(); TestBrain brain = null;
        RuntimeScene owner = RuntimeScene.Create("BRAIN_RELEASE");
        IteratorRuntime runtime = Spawn(owner, ctx => (brain = new TestBrain(ctx, action)).Use(module));
        Player player = AddPlayer(owner, new Vector2(120, 90)); Step(runtime);
        brain.Sensors.TryGet(player, out PlayerObservation retained);
        brain.SetAction(action); Step(runtime);
        runtime.Destroy(); runtime.Destroy();
        Check(brain.IsDestroyed && action.Destroys == 1 && action.Exits == 1 && module.Destroys == 1, "owned action and module cleanup is idempotent");
        Check(retained.Player == null && brain.Sensors.Players.Count == 0 && brain.Context.Room == null, "retained sensor views and Context release game references");
        Check(owner.Room.updateList.Count == 0 && owner.Room.drawableObjects.Count == 0, "brain cleanup integrates with existing room/graphics cleanup");
    }

    private static void PwnObservation()
    {
        RuntimeScene scene = RuntimeScene.Create("PWN_AI"); PwnIteratorExample.Register();
        Check(IteratorRuntimes.TrySpawn(scene.Room, out IteratorRuntime runtime), "PWN sample spawned");
        IteratorPose pose = runtime.Body.Pose; Vector2 position = runtime.Body.Position;
        Player player = AddPlayer(scene, position + new Vector2(60, 0)); Step(runtime); runtime.Graphics.Render();
        float rightEyeX = runtime.Graphics.Sprites["Face.Eye0"].Mesh.Vertices[0].x;
        player.bodyChunks[0].pos = position + new Vector2(-60, 0); Refresh(runtime); runtime.Graphics.Render();
        float leftEyeX = runtime.Graphics.Sprites["Face.Eye0"].Mesh.Vertices[0].x;
        Check(runtime.Brain.CurrentAction.Name == "Observe" && leftEyeX < rightEyeX, "PWN brain drives Body look intent and compiled face geometry");
        Check(ReferenceEquals(pose, runtime.Body.Pose) && Vector2.Distance(position, runtime.Body.Position) < 0.01f, "reference pose and hovering position preserved");
        player.room = null; Refresh(runtime);
        Check(runtime.Brain.CurrentAction.Name == "Idle" && runtime.Body.LookPoint == null, "PWN returns to Idle after player departure");
    }

    private static Player AddPlayer(RuntimeScene scene, Vector2 position)
    {
        Player player = scene.AddPlayer(false, false);
        RuntimeScene.Set(player, "<bodyChunks>k__BackingField", new[] { new BodyChunk(player, 0, position, 4f, 1f) });
        return player;
    }
    private static void Refresh(IteratorRuntime runtime) { runtime.Brain.Sensors.RequestRefresh(); Step(runtime); }
    private static void Check(bool value, string message) { _assertions++; if (!value) throw new InvalidOperationException("Assertion failed: " + message); }
    private static void Throws(Action action, string message)
    {
        try { action(); } catch (ArgumentException) { _assertions++; return; } catch (InvalidOperationException) { _assertions++; return; }
        throw new InvalidOperationException("Expected exception: " + message);
    }
    private sealed class TestBrain : IteratorBrain
    {
        private readonly IteratorAction[] _actions;
        internal TestBrain(IteratorContext context, params IteratorAction[] actions) : base(context) => _actions = actions;
        protected override void OnInitialize() { foreach (IteratorAction action in _actions) RegisterAction(action); }
    }
    private sealed class ProbeAction : IteratorAction
    {
        private readonly List<string> _calls;
        internal bool Allowed = true, Continue = true, FinishOnUpdate, ThrowUpdate;
        internal int Enters, Updates, Exits, Destroys;
        internal Action Entered;
        internal IteratorActionExitReason LastExit;
        internal ProbeAction(string name, List<string> calls, int priority, bool interruptible = true) : base(name, priority, interruptible) => _calls = calls;
        protected override bool CanEnter() => Allowed;
        protected override bool CanContinue() => Continue;
        protected override void OnEnter() { Enters++; _calls.Add(Name + ".Enter"); Entered?.Invoke(); }
        protected override void OnUpdate()
        {
            Updates++; if (ThrowUpdate) throw new InvalidOperationException("action update failure");
            if (FinishOnUpdate) Complete();
        }
        protected override void OnExit(IteratorActionExitReason reason) { Exits++; LastExit = reason; _calls.Add(Name + ".Exit"); }
        protected override void OnDestroy() { Destroys++; _calls.Add(Name + ".Destroy"); }
    }
    private sealed class EventProbe : IteratorBehaviorModule
    {
        private readonly List<IteratorPlayerEventKind> _kinds = new();
        internal int LeftPlayersAvailable, Destroys;
        internal EventProbe() : base("Events") { }
        internal int Count(IteratorPlayerEventKind kind) => _kinds.FindAll(k => k == kind).Count;
        protected override void OnPlayerEvent(IteratorPlayerEvent change)
        { _kinds.Add(change.Kind); if (change.Kind == IteratorPlayerEventKind.Left && change.Player != null && !change.Observation.IsPresent) LeftPlayersAvailable++; }
        protected override void OnDestroy() => Destroys++;
    }
    private sealed class FailingModule : IteratorBehaviorModule
    {
        internal ProbeAction Action;
        internal FailingModule() : base("Fault") { }
        protected override void OnInitialize() => Action = (ProbeAction)RegisterAction(new ProbeAction("FaultOwned", new List<string>(), 100));
        protected override void OnSense() { Brain.SetAction(Action); throw new InvalidOperationException("module failure"); }
    }
    private sealed class ThrowingBrain : IteratorBrain
    {
        internal ThrowingBrain(IteratorContext context) : base(context) { }
        protected override void OnUpdate() => throw new InvalidOperationException("brain failure");
    }
    private sealed class InitFailBrain : IteratorBrain
    {
        private readonly IteratorAction _action;
        internal InitFailBrain(IteratorContext context, IteratorAction action, IteratorBehaviorModule module) : base(context)
        { _action = action; Use(module); }
        protected override void OnInitialize() { RegisterAction(_action); throw new InvalidOperationException("partial initialization failure"); }
    }
}
