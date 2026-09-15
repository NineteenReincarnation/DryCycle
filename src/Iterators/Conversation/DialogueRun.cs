using System;
using System.Collections.Generic;

namespace DryCycle.Iterators;

/// <summary>一次播放的状态句柄。控制请求通过所属 Controller 发出；结束后可保留此句柄查询结果。</summary>
public sealed class DialogueRun
{
    private readonly Stack<Cursor> _stack = new();
    private DialogueCommandExecution _execution;
    internal bool ResumePaused;
    internal DialogueRun(ConversationController controller, DialogueSequence sequence, Player player)
    { Sequence = sequence; Context = new DialogueContext(controller, player); _stack.Push(new Cursor(sequence)); }
    public DialogueSequence Sequence { get; }
    public DialogueContext Context { get; }
    public DialogueStatus Status { get; internal set; } = DialogueStatus.Queued;
    public DialogueEndReason? EndReason { get; internal set; }
    public string FailureMessage { get; internal set; }
    public string CurrentCommand { get; private set; }
    public long ElapsedFrames { get; private set; }
    public bool IsEnded => EndReason.HasValue;

    // At most one command Update per game frame. Tree traversal is separately
    // bounded so deeply nested/empty/conditional scripts cannot stall the game.
    internal bool Tick()
    {
        ElapsedFrames++;
        for (int budget = 0; budget < 64 && !IsEnded; budget++)
        {
            if (_execution != null)
            {
                DialogueCommandExecution current = _execution;
                if (current.IsDisposed) throw new InvalidOperationException("Active dialogue execution was disposed externally.");
                bool complete = current.Update();
                if (IsEnded) return false;
                if (complete) { _execution = null; current.Dispose(); }
                return false;
            }
            if (_stack.Count == 0) return true;
            Cursor cursor = _stack.Peek();
            if (cursor.Index >= cursor.Sequence.Count) { _stack.Pop(); continue; }
            DialogueStep step = cursor.Sequence.Steps[cursor.Index++];
            CurrentCommand = step.Command?.Name ?? "Branch";
            long revision = Context.Controller.RequestRevision;
            bool allowed = step.When == null || step.When(Context);
            if (IsEnded) return false;
            if (allowed)
            {
                if (step.Branches != null)
                {
                    int index = step.Random ? Context.Controller.NextRandom(step.Branches.Length) :
                        step.Choose == null || step.Choose(Context) ? 0 : 1;
                    if (IsEnded) return false;
                    DialogueSequence branch = step.Branches[index];
                    if (branch != null)
                    {
                        if (_stack.Count >= 32) throw new InvalidOperationException("Dialogue nesting exceeds 32 sequences.");
                        _stack.Push(new Cursor(branch));
                    }
                }
                else
                {
                    DialogueCommandExecution execution = step.Command.CreateExecution(Context);
                    if (execution == null || !ReferenceEquals(execution.Context, Context) || execution.Claimed || execution.IsDisposed)
                        throw new InvalidOperationException("Dialogue command must return a new execution with the supplied Context.");
                    execution.Claimed = true;
                    if (IsEnded) { execution.Dispose(); return false; }
                    _execution = execution;
                }
            }
            if (Context.Controller.RequestRevision != revision) return false;
        }
        return false;
    }
    internal void Suspend()
    {
        // Always relinquish body input, including when a custom Pause callback fails.
        try { _execution?.Pause(); }
        finally { Context.ReleaseInputs(); }
    }
    internal void ResumeExecution() => _execution?.Resume();
    internal void DisposeExecution()
    {
        DialogueCommandExecution execution = _execution; _execution = null;
        _stack.Clear(); CurrentCommand = null;
        try { execution?.Dispose(); }
        finally { Context.ReleaseInputs(); }
    }
    private sealed class Cursor
    {
        internal readonly DialogueSequence Sequence;
        internal int Index;
        internal Cursor(DialogueSequence sequence) => Sequence = sequence;
    }
}
