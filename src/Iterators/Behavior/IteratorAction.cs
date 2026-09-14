using System;

namespace DryCycle.Iterators;

public enum IteratorActionExitReason { Replaced, Completed, ConditionLost, Failed, BrainDisabled, Destroyed }

/// <summary>每 Brain 独立的动作。条件、优先级和可打断性由 StateMachine 仲裁，回调不接触 Sprite。</summary>
public abstract class IteratorAction
{
    protected IteratorAction(string name, int priority = 0, bool interruptible = true)
    { IteratorValidation.RequireText(name, nameof(name)); Name = name; Priority = priority; Interruptible = interruptible; }
    public string Name { get; }
    public int Priority { get; }
    public bool Interruptible { get; }
    public IteratorBrain Brain { get; private set; }
    protected IteratorContext Context => Brain.Context;
    public bool IsEnabled { get; private set; } = true;
    public bool IsActive { get; private set; }
    public bool IsCompleted { get; private set; }
    public bool IsDestroyed { get; private set; }
    public long ElapsedFrames { get; private set; }
    internal IteratorBehaviorModule Owner;
    internal long RequestVersion;
    private IteratorLogger _initializeLog, _canEnterLog, _canContinueLog, _enterLog, _updateLog, _exitLog, _destroyLog;

    protected virtual void OnInitialize() { }
    protected virtual bool CanEnter() => true;
    protected virtual bool CanContinue() => true;
    protected virtual void OnEnter() { }
    protected virtual void OnUpdate() { }
    protected virtual void OnExit(IteratorActionExitReason reason) { }
    protected virtual void OnDestroy() { }
    protected void Complete()
    {
        if (!IsActive) throw new InvalidOperationException("Only an active action can complete.");
        IsCompleted = true;
    }

    internal bool Available => IsEnabled && !IsDestroyed && (Owner == null || Owner.IsEnabled);
    internal void Bind(IteratorBrain brain, IteratorBehaviorModule owner)
    {
        if (Brain != null || IsDestroyed) throw new InvalidOperationException("Actions cannot be reused across brains or registrations.");
        Brain = brain; Owner = owner;
        IteratorLogger log = brain.Log.ForModule("Action." + Name);
        _initializeLog = log.ForPhase("Initialize"); _canEnterLog = log.ForPhase("CanEnter"); _canContinueLog = log.ForPhase("CanContinue");
        _enterLog = log.ForPhase("Enter"); _updateLog = log.ForPhase("Update"); _exitLog = log.ForPhase("Exit"); _destroyLog = log.ForPhase("Destroy");
        try { Context.Logger = _initializeLog; OnInitialize(); }
        catch (Exception exception) { Fail("Initialize", exception); }
    }
    internal bool CheckEnter()
    {
        if (!Available) return false;
        try { Context.Logger = _canEnterLog; return CanEnter() && Available && !Brain.IsDestroyed; }
        catch (Exception exception) { Fail("CanEnter", exception); return false; }
    }
    internal bool CheckContinue()
    {
        if (!Available || IsCompleted) return false;
        try { Context.Logger = _canContinueLog; return CanContinue() && Available && !Brain.IsDestroyed; }
        catch (Exception exception) { Fail("CanContinue", exception); return false; }
    }
    internal void Enter()
    {
        IsActive = true; IsCompleted = false; ElapsedFrames = 0;
        try { Context.Logger = _enterLog; OnEnter(); }
        catch (Exception exception) { Fail("Enter", exception); }
    }
    internal void Update()
    {
        if (!Available || !IsActive || IsCompleted) return;
        try { Context.Logger = _updateLog; OnUpdate(); if (IsActive && !IsDestroyed) ElapsedFrames++; }
        catch (Exception exception) { Fail("Update", exception); }
    }
    internal void Exit(IteratorActionExitReason reason)
    {
        if (!IsActive) return;
        IsActive = false;
        try { Context.Logger = _exitLog; OnExit(reason); }
        catch (Exception exception) { Fail("Exit", exception); }
    }
    internal void Disable() => IsEnabled = false;
    private void Fail(string phase, Exception exception)
    {
        IsEnabled = false;
        Brain.Log.ForModule("Action." + Name).ForPhase(phase).Error("Action disabled; state machine will return to an eligible action or Idle.", exception);
    }
    internal void Release()
    {
        if (IsDestroyed) return;
        Exit(IteratorActionExitReason.Destroyed);
        IsDestroyed = true; IsEnabled = false; RequestVersion = 0;
        try { Context.Logger = _destroyLog; OnDestroy(); }
        catch (Exception exception) { Brain.Log.ForModule("Action." + Name).ForPhase("Destroy").Error("Action cleanup failed.", exception); }
    }
}
