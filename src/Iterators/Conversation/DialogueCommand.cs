using System;
using UnityEngine;

namespace DryCycle.Iterators;

/// <summary>可共享的指令定义。CreateExecution 必须为每次执行返回使用所传 Context 的新状态对象。</summary>
public abstract class DialogueCommand
{
    protected DialogueCommand(string name) { IteratorValidation.RequireText(name, nameof(name)); Name = name; }
    public string Name { get; }
    public abstract DialogueCommandExecution CreateExecution(DialogueContext context);
}

/// <summary>一个步骤的独占状态。Update 返回 true 表示完成；暂停不会重新执行已完成步骤。</summary>
public abstract class DialogueCommandExecution : IDisposable
{
    protected DialogueCommandExecution(DialogueContext context) => Context = context ?? throw new ArgumentNullException(nameof(context));
    public DialogueContext Context { get; }
    public bool IsDisposed { get; private set; }
    internal bool Claimed;
    public abstract bool Update();
    public virtual void Pause() { }
    public virtual void Resume() { }
    protected virtual void OnDispose() { }
    public void Dispose() { if (IsDisposed) return; IsDisposed = true; OnDispose(); }
}

public static class DialogueCommands
{
    public static DialogueCommand Say(string text, int extraLinger = 40)
    {
        IteratorValidation.RequireText(text, nameof(text));
        if (text.Length > 4096) throw new ArgumentException("A dialogue line is limited to 4096 characters.", nameof(text));
        ValidateFrames(extraLinger, nameof(extraLinger));
        return new Factory("Say", ctx => new SayExecution(ctx, text, extraLinger));
    }
    public static DialogueCommand Wait(int frames)
    {
        ValidateFrames(frames, nameof(frames));
        return new Factory("Wait", ctx => new WaitExecution(ctx, frames));
    }
    public static DialogueCommand WaitUntil(DialogueCondition condition, int timeoutFrames = 0)
    {
        if (condition == null) throw new ArgumentNullException(nameof(condition));
        ValidateFrames(timeoutFrames, nameof(timeoutFrames));
        return new Factory("WaitUntil", ctx => new PredicateExecution(ctx, condition, timeoutFrames));
    }
    public static DialogueCommand Callback(Action<DialogueContext> callback)
    {
        if (callback == null) throw new ArgumentNullException(nameof(callback));
        return new Factory("Callback", ctx => new CallbackExecution(ctx, callback));
    }
    public static DialogueCommand MoveTo(Vector2 position, float tolerance = 3f, int timeoutFrames = 600)
    {
        BodyValidation.Vector(position, nameof(position));
        BodyValidation.Range(tolerance, 0.1f, 100f, nameof(tolerance));
        ValidateFrames(timeoutFrames, nameof(timeoutFrames));
        if (timeoutFrames == 0) throw new ArgumentOutOfRangeException(nameof(timeoutFrames));
        return new Factory("MoveTo", ctx => new MoveExecution(ctx, position, tolerance, timeoutFrames));
    }
    public static DialogueCommand Gesture(IteratorPose pose, int frames = 40)
    {
        if (pose == null) throw new ArgumentNullException(nameof(pose));
        ValidateFrames(frames, nameof(frames));
        return new Factory("Gesture", ctx => new GestureExecution(ctx, pose, frames));
    }
    internal static void ValidateFrames(int frames, string name)
    { if (frames < 0 || frames > 216000) throw new ArgumentOutOfRangeException(name, "Use 0-216000 game frames."); }

    private sealed class Factory : DialogueCommand
    {
        private readonly Func<DialogueContext, DialogueCommandExecution> _create;
        internal Factory(string name, Func<DialogueContext, DialogueCommandExecution> create) : base(name) => _create = create;
        public override DialogueCommandExecution CreateExecution(DialogueContext context) => _create(context);
    }
    private sealed class CallbackExecution : DialogueCommandExecution
    {
        private readonly Action<DialogueContext> _callback;
        internal CallbackExecution(DialogueContext ctx, Action<DialogueContext> callback) : base(ctx) => _callback = callback;
        public override bool Update() { _callback(Context); return true; }
    }
    private sealed class WaitExecution : DialogueCommandExecution
    {
        private int _remaining;
        internal WaitExecution(DialogueContext ctx, int frames) : base(ctx) => _remaining = frames;
        public override bool Update() => _remaining == 0 || --_remaining == 0;
    }
    private sealed class PredicateExecution : DialogueCommandExecution
    {
        private readonly DialogueCondition _condition;
        private readonly int _timeout;
        private int _elapsed;
        internal PredicateExecution(DialogueContext ctx, DialogueCondition condition, int timeout) : base(ctx)
        { _condition = condition; _timeout = timeout; }
        public override bool Update()
        {
            if (_condition(Context)) return true;
            if (_timeout > 0 && ++_elapsed >= _timeout) throw new TimeoutException("Dialogue WaitUntil timed out.");
            return false;
        }
    }
    private sealed class MoveExecution : DialogueCommandExecution
    {
        private readonly Vector2 _position;
        private readonly float _tolerance;
        private readonly int _timeout;
        private int _elapsed;
        internal MoveExecution(DialogueContext ctx, Vector2 position, float tolerance, int timeout) : base(ctx)
        { _position = position; _tolerance = tolerance; _timeout = timeout; }
        public override bool Update()
        {
            Context.MoveTo(_position);
            if ((Context.Iterator.Body.Position - _position).sqrMagnitude <= _tolerance * _tolerance) return true;
            if (++_elapsed >= _timeout) throw new TimeoutException("Dialogue MoveTo could not reach its target.");
            return false;
        }
    }
    private sealed class GestureExecution : DialogueCommandExecution
    {
        private readonly IteratorPose _pose;
        private int _remaining;
        internal GestureExecution(DialogueContext ctx, IteratorPose pose, int frames) : base(ctx) { _pose = pose; _remaining = frames; }
        public override bool Update() { Context.Gesture(_pose); return _remaining == 0 || --_remaining == 0; }
    }
    private sealed class SayExecution : DialogueCommandExecution
    {
        private readonly string _text;
        private readonly int _linger;
        private IDialogueLine _line;
        internal SayExecution(DialogueContext ctx, string text, int linger) : base(ctx) { _text = text; _linger = linger; }
        public override bool Update()
        {
            if (_line == null)
            {
                IDialogueLine line = Context.Controller.Output.TryShow(Context, _text, _linger);
                // An output callback can unload the room while creating its lease.
                if (IsDisposed) { line?.Dispose(); return true; }
                _line = line;
            }
            return _line != null && _line.IsComplete;
        }
        public override void Pause() => Clear();
        protected override void OnDispose() => Clear();
        private void Clear() { IDialogueLine line = _line; _line = null; line?.Dispose(); }
    }
}

/// <summary>返回 null 表示 HUD 尚未就绪或正忙；控制器将等待。输出不得清除其他系统的台词。</summary>
public interface IDialogueOutput
{
    IDialogueLine TryShow(DialogueContext context, string text, int extraLinger);
}
/// <summary>仅拥有本条台词。Dispose 取消该条；IsComplete 在自然结束或视图失效时为 true。</summary>
public interface IDialogueLine : IDisposable
{
    bool IsComplete { get; }
}
