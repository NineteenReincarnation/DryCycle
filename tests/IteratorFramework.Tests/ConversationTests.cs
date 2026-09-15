using System;
using System.Collections.Generic;
using System.Reflection;
using DryCycle.Iterators;
using UnityEngine;

namespace IteratorFramework.Tests;

// Three focused groups for the scheduler and the cleanup boundary. No new PWN
// sample, no engine rendering fixture, and no dialogue/story-content tests.
internal static class ConversationTests
{
    private static int _assertions;
    internal static int Run()
    {
        int failures = 0;
        Action[] groups = { SequenceFlow, InterruptAndResume, LifetimeAndHudOwnership };
        foreach (Action test in groups)
        {
            try { Check(IteratorHooks.Install(), "hooks installed"); test(); Console.WriteLine("PASS conversation " + test.Method.Name); }
            catch (Exception exception) { failures++; Console.Error.WriteLine("FAIL conversation " + test.Method.Name + Environment.NewLine + exception); }
            finally
            {
                IteratorHooks.Uninstall();
                foreach (IteratorDescriptor descriptor in IteratorRegistry.Registered) IteratorRegistry.Unregister(descriptor);
            }
        }
        Console.WriteLine($"Iterator Framework phase 6: {groups.Length - failures}/{groups.Length} groups passed; {_assertions} assertions; {failures} failures.");
        return failures == 0 ? 0 : 1;
    }
    private static IteratorRuntime Spawn(string name, IDialogueOutput output = null, Func<IteratorContext, ConversationController> factory = null)
    {
        RuntimeScene scene = RuntimeScene.Create(name);
        Iterator.Create(name).Room(name).Conversation(factory ?? (ctx => new ConversationController(ctx, output, 9))).Register();
        Check(IteratorRuntimes.TrySpawn(scene.Room, out IteratorRuntime runtime), "spawn " + name);
        Check(runtime.Conversation.IsInitialized && ReferenceEquals(runtime.Conversation, runtime.Context.Conversation), "context exposes initialized conversation");
        return runtime;
    }
    private static void Step(IteratorRuntime runtime, int frames = 1)
    { for (int i = 0; i < frames && runtime.IsActive; i++) runtime.Context.Oracle.Update((i & 1) == 0); }
    private static void Until(IteratorRuntime runtime, Func<bool> condition)
    { for (int i = 0; i < 100 && !condition(); i++) Step(runtime); Check(condition(), "expected transition reached"); }

    private static void SequenceFlow()
    {
        var output = new Output { Available = false };
        IteratorRuntime runtime = Spawn("DIALOGUE_FLOW", output);
        var calls = new List<string>(); int gates = 0;
        var choices = new[] { Dialogue.Sequence().Callback(_ => calls.Add("random0")).Build(), Dialogue.Sequence().Callback(_ => calls.Add("random1")).Build() };
        DialogueBuilder builder = Dialogue.Sequence("Flow").Callback(_ => calls.Add("start")).Wait(2)
            .Callback(_ => calls.Add("wrong")).When(_ => { gates++; return false; })
            .Branch(_ => true, Dialogue.Sequence().Callback(_ => calls.Add("yes")).Build(), Dialogue.Sequence().Callback(_ => calls.Add("no")).Build())
            .Random(choices).Say("Line", 3).Callback(_ => calls.Add("end"));
        DialogueSequence sequence = builder.Build();
        builder.Callback(_ => calls.Add("late mutation")); choices[0] = Dialogue.Sequence().Callback(_ => calls.Add("mutated choice")).Build();
        DialogueRun run = runtime.Conversation.Play(sequence);
        Check(run.Status == DialogueStatus.Queued && calls.Count == 0, "Play is deferred");
        Step(runtime); Check(calls.Count == 1, "first command executed once");
        Step(runtime); Check(calls.Count == 1, "Wait consumes first frame");
        Step(runtime); Check(calls.Count == 1, "Wait completes without executing a second command in its frame");
        Until(runtime, () => output.Attempts > 0);
        Check(gates == 1 && calls.Contains("yes") && !calls.Contains("wrong") && !calls.Contains("no"), "conditions evaluate once and select correct branch");
        Check(calls.Contains("random" + new System.Random(9).Next(2)) && !calls.Contains("mutated choice"), "private random source and defensive branch snapshot");
        Step(runtime, 3); Check(!run.IsEnded && output.Lines.Count == 0, "missing HUD waits without losing the line");
        output.Available = true; Step(runtime); Check(output.Lines.Count == 1 && output.Text[0] == "Line", "line emitted once when output becomes ready");
        Step(runtime, 3); Check(output.Lines.Count == 1 && !calls.Contains("end"), "Say waits for actual output completion");
        output.Lines[0].Complete = true; Until(runtime, () => run.IsEnded);
        Check(run.Status == DialogueStatus.Completed && output.Lines[0].Disposes == 1 && calls[calls.Count - 1] == "end", "completion releases output once");
        Check(!calls.Contains("late mutation") && run.Context.Iterator == null && run.Context.Controller == null, "snapshot and completed-reference cleanup");
        DialogueRun repeat = runtime.Conversation.Play(sequence); Until(runtime, () => output.Lines.Count == 2);
        Check(calls.FindAll(x => x == "start").Count == 2 && gates == 2, "shared sequence has fresh independent execution state");
        runtime.Conversation.Cancel(); Step(runtime); Check(repeat.EndReason == DialogueEndReason.Cancelled, "repeat can be cancelled");
    }
    private static void InterruptAndResume()
    {
        var output = new Output(); IteratorRuntime runtime = Spawn("DIALOGUE_INTERRUPT", output);
        int callbacks = 0, childCalls = 0;
        DialogueRun parent = runtime.Conversation.Play(Dialogue.Sequence("Parent").Callback(_ => callbacks++).Wait(3).Say("Resume me").Build());
        Step(runtime, 2); long elapsed = parent.ElapsedFrames;
        DialogueRun child = runtime.Conversation.Interrupt(Dialogue.Sequence("Child").Callback(_ => childCalls++).Wait(2).Build());
        Step(runtime); Check(parent.Status == DialogueStatus.Interrupted && runtime.Conversation.InterruptedCount == 1 && childCalls == 1, "interrupt suspends current step");
        Until(runtime, () => child.IsEnded);
        Check(parent.ElapsedFrames == elapsed, "suspended wait timer does not advance");
        Step(runtime); Check(output.Lines.Count == 0, "remaining parent wait retained after child ends");
        Until(runtime, () => output.Lines.Count == 1);
        runtime.Conversation.Pause(); Step(runtime);
        Check(parent.Status == DialogueStatus.Paused && output.Lines[0].Disposes == 1, "pause withdraws active line");
        elapsed = parent.ElapsedFrames; Step(runtime, 3); Check(parent.ElapsedFrames == elapsed, "paused sequence stops advancing");
        runtime.Conversation.Resume(); Step(runtime);
        Check(output.Lines.Count == 2 && output.Text[1] == "Resume me" && callbacks == 1, "resume replays only unfinished line");
        runtime.Conversation.Pause(); Step(runtime);
        DialogueRun overPause = runtime.Conversation.Interrupt(Dialogue.Sequence().Wait(1).Build());
        Until(runtime, () => overPause.IsEnded); Step(runtime);
        Check(parent.Status == DialogueStatus.Paused, "interrupt preserves an existing manual pause");
        runtime.Conversation.Resume(); Step(runtime);
        runtime.Conversation.Cancel(); Step(runtime);
        Check(parent.EndReason == DialogueEndReason.Cancelled && !runtime.Conversation.IsBusy && output.Lines[2].Disposes == 1, "cancel clears run and interruption stack");

        IteratorPose original = runtime.Body.Pose;
        DialogueRun pose = runtime.Conversation.Play(Dialogue.Sequence().LookAt(new Vector2(150, 120)).Gesture(IteratorPose.Talk, 20).Wait(40).Build());
        Step(runtime, 2); Check(runtime.Body.LookPoint == new Vector2(150, 120) && ReferenceEquals(runtime.Body.Pose, IteratorPose.Talk), "dialogue input applied after Brain");
        Vector2 overrideLook = new(170, 140); runtime.Body.LookAt(overrideLook);
        runtime.Conversation.Cancel(); Step(runtime);
        Check(runtime.Body.LookPoint == overrideLook && ReferenceEquals(runtime.Body.Pose, original), "cleanup preserves external body override");
        DialogueRun conditionalPose = runtime.Conversation.Play(Dialogue.Sequence().Gesture(IteratorPose.Talk, 30).When(_ => false).Wait(1).Build());
        Step(runtime); Check(ReferenceEquals(runtime.Body.Pose, original), "When gates the entire Gesture command, not just its timer");
        Until(runtime, () => conditionalPose.IsEnded);
        Vector2 destination = runtime.Body.Position + new Vector2(20, 0);
        Vector2? previousMovement = runtime.Body.MovementTarget;
        DialogueRun movement = runtime.Conversation.Play(Dialogue.Sequence().MoveTo(destination).Build());
        Until(runtime, () => movement.IsEnded);
        Check(movement.Status == DialogueStatus.Completed && (runtime.Body.Position - destination).sqrMagnitude < 25 && runtime.Body.MovementTarget == previousMovement,
            "MoveTo waits for actual body arrival and restores the pre-existing hover target");
        int afterCancel = 0;
        DialogueRun reentrant = runtime.Conversation.Play(Dialogue.Sequence().Callback(ctx => ctx.Controller.Cancel()).Callback(_ => afterCancel++).Build());
        Step(runtime, 2); Check(reentrant.EndReason == DialogueEndReason.Cancelled && afterCancel == 0, "callback cancellation is deferred without running subsequent commands");
    }
    private static void LifetimeAndHudOwnership()
    {
        IteratorRuntime runtime = Spawn("DIALOGUE_FAILURE", new Output());
        DialogueRun bad = runtime.Conversation.Play(Dialogue.Sequence().Wait(1).When(_ => throw new InvalidOperationException("condition")).Build());
        Step(runtime); Check(bad.Status == DialogueStatus.Failed && runtime.IsActive && runtime.Brain.IsEnabled && runtime.Graphics.IsEnabled, "condition failure only ends this dialogue");
        DialogueRun recovered = runtime.Conversation.Play(Dialogue.Sequence().Wait(1).Build()); Until(runtime, () => recovered.IsEnded);
        Check(recovered.Status == DialogueStatus.Completed, "controller remains usable after script failure");
        IteratorRuntime fallback = Spawn("DIALOGUE_FACTORY_FAIL", factory: _ => throw new InvalidOperationException("factory"));
        Check(fallback.Conversation is EmptyConversation, "factory error falls back to empty controller");
        IteratorRuntime foreign = Spawn("DIALOGUE_FACTORY_FOREIGN", factory: _ => runtime.Conversation);
        Check(foreign.Conversation is EmptyConversation && !runtime.Conversation.IsDestroyed, "foreign factory result is not disposed");
        InitFailure partial = null;
        IteratorRuntime partialRuntime = Spawn("DIALOGUE_PARTIAL", factory: ctx => partial = new InitFailure(ctx));
        Check(partial.IsDestroyed && partial.Queued.Context.Iterator == null && partialRuntime.Conversation is EmptyConversation, "partial initialization releases queued context");

        var output = new Output(); IteratorRuntime doomed = Spawn("DIALOGUE_OUTPUT_DESTROY", output);
        output.OnShow = doomed.Destroy;
        DialogueRun destroyed = doomed.Conversation.Play(Dialogue.Sequence().Say("late output").Build());
        Step(doomed);
        Check(doomed.State == IteratorLifecycle.Destroyed && output.Lines[0].Disposes == 1 && destroyed.Context.Iterator == null,
            "output returned after synchronous destruction is released");
        RuntimeScene scene = RuntimeScene.Create("DIALOGUE_TARGET");
        var targetOutput = new Output();
        Iterator.Create(scene.AbstractRoom.name).Room(scene.AbstractRoom.name).Conversation(ctx => new ConversationController(ctx, targetOutput)).Register();
        Check(IteratorRuntimes.TrySpawn(scene.Room, out IteratorRuntime targetRuntime), "target runtime spawned");
        Player player = scene.AddPlayer(false, false);
        DialogueRun targetRun = targetRuntime.Conversation.Play(Dialogue.Sequence().Say("target").Build(), player);
        Step(targetRuntime); player.room = null; Step(targetRuntime);
        Check(targetRun.EndReason == DialogueEndReason.TargetUnavailable && targetRun.Context.Player == null && targetOutput.Lines[0].Disposes == 1,
            "player departure cancels output and releases target reference");
        var rawBox = RuntimeScene.Raw<global::HUD.DialogBox>();
        var own = RuntimeScene.Raw<global::HUD.DialogBox.Message>(); own.text = "";
        var other = RuntimeScene.Raw<global::HUD.DialogBox.Message>(); other.text = "";
        var queued = RuntimeScene.Raw<global::HUD.DialogBox.Message>(); queued.text = "";
        rawBox.messages = new List<global::HUD.DialogBox.Message> { own, other, queued };
        rawBox.label = RuntimeScene.Raw<FLabel>(); RuntimeScene.Set(rawBox.label, "_text", "");
        RuntimeScene.Set(rawBox, "showCharacter", 9);
        RainWorldDialogueOutput.RemoveOwned(rawBox, own);
        Check(rawBox.messages.Count == 2 && ReferenceEquals(rawBox.CurrentMessage, other) && ReferenceEquals(rawBox.messages[1], queued), "HUD cleanup preserves foreign queue and order");
        Check((int)typeof(global::HUD.DialogBox).GetField("showCharacter", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(rawBox) == 0,
            "private game initializer invoked through adapter, not publicized-only access");
        RainWorldDialogueOutput.RemoveOwned(rawBox, own);
        Check(rawBox.messages.Count == 2, "owned message removal is idempotent");
        DialogueRun final = runtime.Conversation.Play(Dialogue.Sequence().Wait(100).Build()); Step(runtime);
        DialogueRun nested = runtime.Conversation.Interrupt(Dialogue.Sequence().Wait(100).Build()); Step(runtime);
        runtime.Destroy(); runtime.Destroy();
        Check(final.EndReason == DialogueEndReason.Destroyed && nested.EndReason == DialogueEndReason.Destroyed && final.Context.Iterator == null && nested.Context.Iterator == null,
            "runtime unload clears active and interrupted contexts once");
    }
    private sealed class InitFailure : ConversationController
    {
        internal DialogueRun Queued;
        internal InitFailure(IteratorContext ctx) : base(ctx) { }
        protected override void OnInitialize() { Queued = Play(Dialogue.Sequence().Wait(100).Build()); throw new InvalidOperationException("partial initialization"); }
    }
    private sealed class Output : IDialogueOutput
    {
        internal bool Available = true;
        internal int Attempts;
        internal Action OnShow;
        internal readonly List<Line> Lines = new();
        internal readonly List<string> Text = new();
        public IDialogueLine TryShow(DialogueContext context, string text, int extraLinger)
        {
            Attempts++; if (!Available) return null;
            var line = new Line(); Lines.Add(line); Text.Add(text); OnShow?.Invoke(); return line;
        }
    }
    private sealed class Line : IDialogueLine
    {
        internal bool Complete;
        internal int Disposes;
        public bool IsComplete => Complete;
        public void Dispose() => Disposes++;
    }
    private static void Check(bool condition, string message)
    { _assertions++; if (!condition) throw new InvalidOperationException(message); }
}
