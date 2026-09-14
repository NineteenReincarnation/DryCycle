using System;

namespace DryCycle.Iterators;

/// <summary>可组合的行为模块。感知通知与低频 OnSense 用于决策，OnUpdate 只用于必要的逐帧逻辑。</summary>
public abstract class IteratorBehaviorModule
{
    private IteratorLogger _initializeLog, _senseLog, _updateLog, _eventLog, _destroyLog;
    protected IteratorBehaviorModule(string name) { IteratorValidation.RequireText(name, nameof(name)); Name = name; }
    public string Name { get; }
    public IteratorBrain Brain { get; private set; }
    protected IteratorContext Context => Brain.Context;
    public bool IsEnabled { get; private set; } = true;
    public bool IsDestroyed { get; private set; }
    protected virtual void OnInitialize() { }
    protected virtual void OnSense() { }
    protected virtual void OnUpdate() { }
    protected virtual void OnPlayerEvent(IteratorPlayerEvent change) { }
    protected virtual void OnDestroy() { }
    protected IteratorAction RegisterAction(IteratorAction action) => Brain.RegisterOwnedAction(action, this);
    internal void Bind(IteratorBrain brain)
    {
        if (Brain != null || IsDestroyed) throw new InvalidOperationException("Behavior modules cannot be reused.");
        Brain = brain;
        IteratorLogger log = brain.Log.ForModule("Behavior." + Name);
        _initializeLog = log.ForPhase("Initialize"); _senseLog = log.ForPhase("Sense"); _updateLog = log.ForPhase("Update");
        _eventLog = log.ForPhase("PlayerEvent"); _destroyLog = log.ForPhase("Destroy");
    }
    internal void Initialize()
    {
        if (!IsEnabled || IsDestroyed) return;
        try { Context.Logger = _initializeLog; OnInitialize(); } catch (Exception exception) { Fail("Initialize", exception); }
    }
    internal void Sense()
    {
        if (!IsEnabled || IsDestroyed) return;
        try { Context.Logger = _senseLog; OnSense(); } catch (Exception exception) { Fail("Sense", exception); }
    }
    internal void Update()
    {
        if (!IsEnabled || IsDestroyed) return;
        try { Context.Logger = _updateLog; OnUpdate(); } catch (Exception exception) { Fail("Update", exception); }
    }
    internal void Notify(IteratorPlayerEvent change)
    {
        if (!IsEnabled || IsDestroyed) return;
        try { Context.Logger = _eventLog; OnPlayerEvent(change); } catch (Exception exception) { Fail("PlayerEvent", exception); }
    }
    private void Fail(string phase, Exception exception)
    {
        IsEnabled = false; Brain.StateMachine.DisableOwned(this);
        Brain.Log.ForModule("Behavior." + Name).ForPhase(phase).Error("Behavior module and its actions disabled; other modules continue.", exception);
    }
    internal void Release()
    {
        if (IsDestroyed) return;
        IsDestroyed = true; IsEnabled = false;
        try { Context.Logger = _destroyLog; OnDestroy(); }
        catch (Exception exception) { Brain.Log.ForModule("Behavior." + Name).ForPhase("Destroy").Error("Behavior cleanup failed.", exception); }
    }
}
