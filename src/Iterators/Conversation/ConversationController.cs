using System;
using System.Collections.Generic;

namespace DryCycle.Iterators;

/// <summary>每 Runtime 独立的对话控制器。默认不自动说话；指令、计时和 HUD 输出均可替换。</summary>
public class ConversationController
{
    private readonly Queue<Request> _requests = new();
    private readonly List<DialogueRun> _interrupted = new();
    private readonly Random _random;
    private readonly IteratorLogger _initializeLog, _updateLog, _commandLog, _endLog, _destroyLog;
    private bool _initializing, _updating;
    internal bool Claimed;
    internal long RequestRevision { get; private set; }
    public ConversationController(IteratorContext context, IDialogueOutput output = null, int randomSeed = 0)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Output = output ?? new RainWorldDialogueOutput();
        _random = new Random(randomSeed);
        var log = new IteratorLogger(context.ID).ForModule("Conversation");
        _initializeLog = log.ForPhase("Initialize"); _updateLog = log.ForPhase("Update");
        _commandLog = log.ForPhase("Command"); _endLog = log.ForPhase("Ended"); _destroyLog = log.ForPhase("Destroy");
    }
    public IteratorContext Context { get; }
    public IDialogueOutput Output { get; }
    public DialogueRun Current { get; private set; }
    public int InterruptedCount => _interrupted.Count;
    public bool IsBusy => Current != null || _interrupted.Count != 0 || _requests.Count != 0;
    public bool IsInitialized { get; private set; }
    public bool IsEnabled { get; private set; } = true;
    public bool IsDestroyed { get; private set; }

    /// <summary>排队播放，替换当前及被打断的对话。player 缺省绑定当前 PrimaryPlayer；没有玩家时允许无目标脚本。</summary>
    public DialogueRun Play(DialogueSequence sequence, Player player = null) => EnqueueRun(RequestKind.Play, sequence, player);
    /// <summary>暂存当前步骤并播放插入对话；插入结束后自动恢复，未结束的 Say 从该行开头重新显示。</summary>
    public DialogueRun Interrupt(DialogueSequence sequence, Player player = null) => EnqueueRun(RequestKind.Interrupt, sequence, player);
    public bool Pause() => Enqueue(new Request(RequestKind.Pause));
    public bool Resume() => Enqueue(new Request(RequestKind.Resume));
    /// <summary>取消当前和整个打断栈。控制请求按调用顺序，在下一次 Update 开始时处理。</summary>
    public bool Cancel() => Enqueue(new Request(RequestKind.CancelAll));
    /// <summary>只取消当前对话，下一帧恢复被打断的上层对话。</summary>
    public bool CancelCurrent() => Enqueue(new Request(RequestKind.CancelCurrent));
    protected virtual void OnInitialize() { }
    protected virtual void OnUpdate() { }
    protected virtual void OnStarted(DialogueRun run) { }
    protected virtual void OnEnded(DialogueRun run, DialogueEndReason reason) { }
    protected virtual void OnDestroy() { }
    internal int NextRandom(int count) => _random.Next(count);

    private bool CanControl => IsEnabled && !IsDestroyed && (IsInitialized || _initializing) &&
        Context.Runtime?.State != IteratorLifecycle.Destroying && Context.Runtime?.State != IteratorLifecycle.Destroyed;
    private DialogueRun EnqueueRun(RequestKind kind, DialogueSequence sequence, Player player)
    {
        if (sequence == null) throw new ArgumentNullException(nameof(sequence));
        if (!CanControl) return null;
        if (_requests.Count >= 64) throw new InvalidOperationException("Too many pending dialogue requests.");
        var run = new DialogueRun(this, sequence, player ?? Context.PrimaryPlayer);
        Enqueue(new Request(kind, run)); return run;
    }
    private bool Enqueue(Request request)
    {
        if (!CanControl) return false;
        if (_requests.Count >= 64) throw new InvalidOperationException("Too many pending dialogue requests.");
        _requests.Enqueue(request); RequestRevision++; return true;
    }
    internal void Initialize()
    {
        _initializing = true;
        try { Context.Logger = _initializeLog; OnInitialize(); if (!IsDestroyed) IsInitialized = true; }
        finally { _initializing = false; }
    }
    internal void Update()
    {
        if (_updating || !IsInitialized || !IsEnabled || IsDestroyed || !Context.Runtime.IsActive) return;
        _updating = true;
        try
        {
            int requests = _requests.Count;
            for (int i = 0; i < requests && IsEnabled && !IsDestroyed; i++) Apply(_requests.Dequeue());
            if (IsDestroyed || !IsEnabled) return;
            for (int i = _interrupted.Count - 1; i >= 0; i--)
            {
                DialogueRun run = _interrupted[i];
                if (!TargetAvailable(run)) { _interrupted.RemoveAt(i); End(run, DialogueEndReason.TargetUnavailable); }
                if (IsDestroyed || !IsEnabled) return;
            }
            if (Current != null && !TargetAvailable(Current)) FinishCurrent(DialogueEndReason.TargetUnavailable);
            if (IsDestroyed || !IsEnabled) return;
            RestoreInterrupted();
            if (IsDestroyed || !IsEnabled) return;
            Context.Logger = _updateLog; OnUpdate();
            if (IsDestroyed || !IsEnabled) return;
            DialogueRun active = Current;
            if (active?.Status != DialogueStatus.Running) return;
            try
            {
                Context.Logger = _commandLog;
                if (active.Tick()) FinishCurrent(DialogueEndReason.Completed);
                else if (!active.IsEnded && active.Status == DialogueStatus.Running) active.Context.ApplyInputs();
            }
            catch (Exception exception) { Fail(active, exception); }
        }
        catch (Exception exception) { Disable(exception); }
        finally { _updating = false; }
    }
    private static bool TargetAvailable(DialogueRun run) => !run.Context.HasTarget || run.Context.IsPlayerAvailable;
    private void Apply(Request request)
    {
        if (request.Run != null)
        {
            if (!TargetAvailable(request.Run)) { End(request.Run, DialogueEndReason.TargetUnavailable); return; }
            if (request.Kind == RequestKind.Play) StopAll(DialogueEndReason.Replaced);
            else if (Current != null)
            {
                if (_interrupted.Count >= 32) { End(request.Run, DialogueEndReason.Failed, new InvalidOperationException("Dialogue interruption depth exceeded.")); return; }
                DialogueRun previous = Current;
                try
                {
                    previous.ResumePaused = previous.Status == DialogueStatus.Paused;
                    if (!previous.ResumePaused) previous.Suspend();
                    if (!previous.IsEnded) { previous.Status = DialogueStatus.Interrupted; _interrupted.Add(previous); Current = null; }
                }
                catch (Exception exception) { Fail(previous, exception); }
            }
            if (IsDestroyed || !IsEnabled) { End(request.Run, DialogueEndReason.Destroyed); return; }
            Current = request.Run; Current.Status = DialogueStatus.Running;
            try { Context.Logger = _updateLog; OnStarted(request.Run); }
            catch (Exception exception) { Fail(request.Run, exception); }
            return;
        }
        switch (request.Kind)
        {
            case RequestKind.CancelAll: StopAll(DialogueEndReason.Cancelled); break;
            case RequestKind.CancelCurrent: FinishCurrent(DialogueEndReason.Cancelled); break;
            case RequestKind.Pause:
                if (Current?.Status == DialogueStatus.Running)
                {
                    DialogueRun run = Current;
                    try { run.Suspend(); if (!run.IsEnded) run.Status = DialogueStatus.Paused; }
                    catch (Exception exception) { Fail(run, exception); }
                }
                break;
            case RequestKind.Resume:
                if (Current?.Status == DialogueStatus.Paused) ResumeRun(Current, false);
                break;
        }
    }
    private void RestoreInterrupted()
    {
        if (Current != null || _interrupted.Count == 0) return;
        int last = _interrupted.Count - 1;
        Current = _interrupted[last]; _interrupted.RemoveAt(last);
        ResumeRun(Current, Current.ResumePaused);
    }
    private void ResumeRun(DialogueRun run, bool paused)
    {
        run.Status = paused ? DialogueStatus.Paused : DialogueStatus.Running;
        if (paused) return;
        try { run.ResumeExecution(); }
        catch (Exception exception) { Fail(run, exception); }
    }
    private void Fail(DialogueRun run, Exception exception)
    {
        if (run.IsEnded) return;
        if (ReferenceEquals(Current, run)) Current = null;
        _interrupted.Remove(run);
        End(run, DialogueEndReason.Failed, exception);
    }
    private void FinishCurrent(DialogueEndReason reason)
    { DialogueRun run = Current; Current = null; if (run != null) End(run, reason); }
    private void StopAll(DialogueEndReason reason)
    {
        DialogueRun current = Current; Current = null;
        // Snapshot before callbacks, which may synchronously destroy this Runtime.
        DialogueRun[] suspended = _interrupted.ToArray(); _interrupted.Clear();
        if (current != null) End(current, reason);
        for (int i = suspended.Length - 1; i >= 0; i--) End(suspended[i], reason);
    }
    private void End(DialogueRun run, DialogueEndReason reason, Exception exception = null)
    {
        if (run.IsEnded) return;
        run.EndReason = reason;
        run.Status = reason == DialogueEndReason.Completed ? DialogueStatus.Completed :
            reason == DialogueEndReason.Failed ? DialogueStatus.Failed : DialogueStatus.Cancelled;
        if (exception != null)
        {
            run.FailureMessage = exception.Message;
            _commandLog.Error($"Dialogue '{run.Sequence.Name}', command '{run.CurrentCommand}' failed; ending this run.", exception);
        }
        try { run.DisposeExecution(); }
        catch (Exception cleanupFailure) { _endLog.Error("Dialogue command cleanup failed.", cleanupFailure); }
        try { Context.Logger = _endLog; OnEnded(run, reason); }
        catch (Exception callbackFailure) { _endLog.Error("Dialogue end callback failed.", callbackFailure); }
        finally
        {
            try { run.Context.Detach(); }
            catch (Exception cleanupFailure) { _endLog.Error("Dialogue input cleanup failed.", cleanupFailure); }
        }
    }
    internal void Disable(Exception exception)
    {
        if (!IsEnabled || IsDestroyed) return;
        IsEnabled = false; _updateLog.Error("Conversation disabled; Runtime, Brain and Graphics remain active.", exception);
        StopAll(DialogueEndReason.ControllerDisabled); DrainRequests(DialogueEndReason.ControllerDisabled);
    }
    private void DrainRequests(DialogueEndReason reason)
    { while (_requests.Count > 0) { DialogueRun run = _requests.Dequeue().Run; if (run != null) End(run, reason); } }
    internal void Release()
    {
        if (IsDestroyed) return;
        IsDestroyed = true; IsEnabled = false;
        StopAll(DialogueEndReason.Destroyed); DrainRequests(DialogueEndReason.Destroyed);
        try { Context.Logger = _destroyLog; OnDestroy(); }
        catch (Exception exception) { _destroyLog.Error("Conversation cleanup failed.", exception); }
    }
    private enum RequestKind { Play, Interrupt, Pause, Resume, CancelAll, CancelCurrent }
    private readonly struct Request
    {
        internal readonly RequestKind Kind;
        internal readonly DialogueRun Run;
        internal Request(RequestKind kind, DialogueRun run = null) { Kind = kind; Run = run; }
    }
}

/// <summary>默认空对话组件；不主动触发台词，仍允许 Runtime 或行为模块显式 Play。</summary>
public sealed class EmptyConversation : ConversationController
{
    public EmptyConversation(IteratorContext context) : base(context) { }
}
