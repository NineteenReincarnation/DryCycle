using System;
using System.Collections.Generic;
using UnityEngine;

namespace DryCycle.Iterators;

/// <summary>在步骤进入时求值；异常只终止本次对话。可读取玩家、StorySession 或扩展者自己的状态。</summary>
public delegate bool DialogueCondition(DialogueContext context);

public static class Dialogue
{
    public static DialogueBuilder Sequence(string name = "Dialogue") => new(name);
}

/// <summary>不可变脚本快照，可在多个实例中播放；执行位置、计时器和玩家引用属于 DialogueRun。</summary>
public sealed class DialogueSequence
{
    internal readonly DialogueStep[] Steps;
    internal DialogueSequence(string name, DialogueStep[] steps) { Name = name; Steps = steps; }
    public string Name { get; }
    public int Count => Steps.Length;
}

/// <summary>构建脚本。When 只修饰紧邻的前一步；Build 后继续编辑不改变已有快照。</summary>
public sealed class DialogueBuilder
{
    private readonly string _name;
    private readonly List<DialogueStep> _steps = new();
    internal DialogueBuilder(string name) { IteratorValidation.RequireText(name, nameof(name)); _name = name; }
    public DialogueSequence Build() => new(_name, _steps.ToArray());
    public DialogueBuilder Command(DialogueCommand command) => Add(new DialogueStep(command ?? throw new ArgumentNullException(nameof(command))));
    public DialogueBuilder Say(string text, int extraLinger = 40) => Command(DialogueCommands.Say(text, extraLinger));
    public DialogueBuilder Wait(int frames) => Command(DialogueCommands.Wait(frames));
    public DialogueBuilder WaitUntil(DialogueCondition condition, int timeoutFrames = 0) => Command(DialogueCommands.WaitUntil(condition, timeoutFrames));
    public DialogueBuilder Pause() => Callback(ctx => ctx.Controller.Pause());
    public DialogueBuilder Callback(Action<DialogueContext> callback) => Command(DialogueCommands.Callback(callback));
    public DialogueBuilder LookAtPlayer() => Callback(ctx => ctx.LookAtPlayer());
    public DialogueBuilder LookAt(Vector2 position)
    {
        BodyValidation.Vector(position, nameof(position));
        return Callback(ctx => ctx.LookAt(position));
    }
    public DialogueBuilder MoveTo(Vector2 position, float tolerance = 3f, int timeoutFrames = 600)
        => Command(DialogueCommands.MoveTo(position, tolerance, timeoutFrames));
    public DialogueBuilder Gesture(IteratorPose pose, int frames = 40) => Command(DialogueCommands.Gesture(pose, frames));
    /// <summary>请求 Brain 的具名动作，仍遵守动作优先级；不等待动作完成。</summary>
    public DialogueBuilder Action(string name)
    {
        IteratorValidation.RequireText(name, nameof(name));
        return Callback(ctx => ctx.Iterator.Brain.SetAction(name));
    }
    public DialogueBuilder Sound(SoundID sound, float volume = 1f, float pitch = 1f)
    {
        if (sound == null) throw new ArgumentNullException(nameof(sound));
        BodyValidation.Range(volume, 0f, 4f, nameof(volume));
        BodyValidation.Range(pitch, 0.1f, 4f, nameof(pitch));
        return Callback(ctx => ctx.Iterator.Room.PlaySound(sound, ctx.Iterator.Body.Position, volume, pitch));
    }
    public DialogueBuilder Then(DialogueSequence sequence) => Add(new DialogueStep(null, branches: new[] { Require(sequence) }));
    public DialogueBuilder Branch(DialogueCondition condition, DialogueSequence whenTrue, DialogueSequence whenFalse = null)
        => Add(new DialogueStep(null, choose: condition ?? throw new ArgumentNullException(nameof(condition)),
            branches: new[] { Require(whenTrue), whenFalse }));
    public DialogueBuilder Conditional(DialogueCondition condition, DialogueSequence sequence) => Branch(condition, sequence);
    /// <summary>等概率选择分支，使用 Controller 私有随机源，不改变 UnityEngine.Random。</summary>
    public DialogueBuilder Random(params DialogueSequence[] choices)
    {
        if (choices == null) throw new ArgumentNullException(nameof(choices));
        if (choices.Length == 0 || choices.Length > 128) throw new ArgumentException("Supply 1-128 dialogue alternatives.", nameof(choices));
        var copy = (DialogueSequence[])choices.Clone();
        foreach (DialogueSequence choice in copy) Require(choice);
        return Add(new DialogueStep(null, branches: copy, random: true));
    }
    public DialogueBuilder When(DialogueCondition condition)
    {
        if (condition == null) throw new ArgumentNullException(nameof(condition));
        if (_steps.Count == 0) throw new InvalidOperationException("When requires a preceding step.");
        DialogueStep previous = _steps[_steps.Count - 1];
        DialogueCondition gate = previous.When;
        _steps[_steps.Count - 1] = new DialogueStep(previous.Command,
            gate == null ? condition : ctx => gate(ctx) && condition(ctx), previous.Choose, previous.Branches, previous.Random);
        return this;
    }
    private DialogueBuilder Add(DialogueStep step)
    {
        if (_steps.Count >= 1024) throw new InvalidOperationException("A dialogue sequence supports at most 1024 steps.");
        _steps.Add(step); return this;
    }
    private static DialogueSequence Require(DialogueSequence sequence) => sequence ?? throw new ArgumentNullException(nameof(sequence));
}

internal sealed class DialogueStep
{
    internal readonly DialogueCommand Command;
    internal readonly DialogueCondition When, Choose;
    internal readonly DialogueSequence[] Branches;
    internal readonly bool Random;
    internal DialogueStep(DialogueCommand command, DialogueCondition when = null, DialogueCondition choose = null,
        DialogueSequence[] branches = null, bool random = false)
    { Command = command; When = when; Choose = choose; Branches = branches; Random = random; }
}
