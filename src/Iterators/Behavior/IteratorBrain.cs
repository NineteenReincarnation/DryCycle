using System;
using System.Collections.Generic;

namespace DryCycle.Iterators;

/// <summary>每 Runtime 独立的行为中心。默认基类只提供 Idle；StandardIteratorBrain 组合玩家观察。</summary>
public class IteratorBrain
{
    private readonly List<IteratorBehaviorModule> _modules = new();
    private bool _initializing, _updating;
    private readonly IteratorLogger _initializeLog, _senseLog, _eventLog, _updateLog, _destroyLog;
    internal bool Claimed;
    internal IteratorLogger Log { get; }

    public IteratorBrain(IteratorContext context, IteratorSensorProfile sensors = null)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Log = new IteratorLogger(context.ID).ForModule("Brain");
        _initializeLog = Log.ForPhase("Initialize"); _senseLog = Log.ForPhase("Sense");
        _eventLog = Log.ForPhase("PlayerEvent"); _updateLog = Log.ForPhase("Update"); _destroyLog = Log.ForPhase("Destroy");
        StateMachine = new IteratorStateMachine(this);
        Sensors = new PlayerSensor(this, sensors ?? IteratorSensorProfile.Default);
        Modules = _modules.AsReadOnly();
    }
    public IteratorContext Context { get; }
    public IteratorStateMachine StateMachine { get; }
    public PlayerSensor Sensors { get; }
    public IReadOnlyList<IteratorBehaviorModule> Modules { get; }
    public IteratorAction CurrentAction => StateMachine.Current;
    public bool IsInitialized { get; private set; }
    public bool IsEnabled { get; private set; } = true;
    public bool IsDestroyed { get; private set; }
    internal IteratorAction Idle { get; private set; }

    /// <summary>在工厂或 OnInitialize 中组合模块；初始化结束后冻结。模块实例不能跨 Brain 共享。</summary>
    public IteratorBrain Use(IteratorBehaviorModule module)
    {
        if (IsInitialized || IsDestroyed || (Claimed && !_initializing)) throw new InvalidOperationException("Configure modules before Brain initialization ends.");
        if (module == null) throw new ArgumentNullException(nameof(module));
        if (module.Brain != null || module.IsDestroyed) throw new InvalidOperationException("Behavior module is already owned or destroyed.");
        foreach (IteratorBehaviorModule existing in _modules) if (existing.Name == module.Name) throw new ArgumentException("Duplicate behavior module: " + module.Name);
        if (_modules.Count >= 64) throw new InvalidOperationException("A Brain supports at most 64 behavior modules.");
        module.Bind(this); _modules.Add(module); return this;
    }
    public IteratorBrain Use<T>() where T : IteratorBehaviorModule, new() => Use(new T());

    /// <summary>排队请求动作；优先级、条件和可打断性仍参与仲裁，不同步执行 Enter/Exit。</summary>
    public bool SetAction(IteratorAction action) => StateMachine.Request(action);
    public bool SetAction(string name)
    {
        if (!StateMachine.TryGet(name, out IteratorAction action)) throw new ArgumentException("Unknown action: " + name, nameof(name));
        return SetAction(action);
    }
    protected IteratorAction RegisterAction(IteratorAction action) => RegisterOwnedAction(action, null);
    internal IteratorAction RegisterOwnedAction(IteratorAction action, IteratorBehaviorModule owner)
    {
        if (!_initializing || IsDestroyed) throw new InvalidOperationException("Register actions only during Brain/module initialization.");
        if (owner != null && !ReferenceEquals(owner.Brain, this)) throw new ArgumentException("Module belongs to another Brain.");
        return StateMachine.Add(action, owner);
    }
    protected virtual void OnInitialize() { }
    protected virtual void OnSense() { }
    protected virtual void OnPlayerEvent(IteratorPlayerEvent change) { }
    protected virtual void OnUpdate() { }
    protected virtual void OnDestroy() { }

    internal void Initialize()
    {
        _initializing = true;
        try
        {
            Idle = RegisterAction(new IdleAction());
            Context.Logger = _initializeLog;
            OnInitialize();
            if (IsDestroyed) return;
            // OnInitialize may append more modules, bounded by Use's limit.
            for (int i = 0; i < _modules.Count; i++) { _modules[i].Initialize(); if (IsDestroyed) return; }
            IsInitialized = true;
            StateMachine.StartIdle();
        }
        finally { _initializing = false; }
    }
    internal void Update()
    {
        if (_updating || !IsInitialized || !IsEnabled || IsDestroyed || !Context.Runtime.IsActive) return;
        _updating = true;
        try
        {
            bool sampled = Sensors.Update();
            if (IsDestroyed || !IsEnabled) return;
            if (sampled)
            {
                for (int i = 0; i < Sensors.Events.Count; i++)
                {
                    IteratorPlayerEvent change = Sensors.Events[i];
                    Context.Logger = _eventLog;
                    OnPlayerEvent(change);
                    if (IsDestroyed || !IsEnabled) return;
                    foreach (IteratorBehaviorModule module in _modules)
                    {
                        module.Notify(change);
                        if (IsDestroyed || !IsEnabled) return;
                    }
                }
                Sensors.FinishEvents();
                Context.Logger = _senseLog;
                OnSense();
                if (IsDestroyed || !IsEnabled) return;
                foreach (IteratorBehaviorModule module in _modules)
                { module.Sense(); if (IsDestroyed || !IsEnabled) return; }
            }
            Context.Logger = _updateLog;
            OnUpdate();
            if (IsDestroyed || !IsEnabled) return;
            foreach (IteratorBehaviorModule module in _modules)
            { module.Update(); if (IsDestroyed || !IsEnabled) return; }
            StateMachine.Step();
        }
        catch (Exception exception) { Disable("Update", exception); }
        finally { Sensors.FinishEvents(); _updating = false; }
    }

    internal void Disable(string phase, Exception exception)
    {
        if (!IsEnabled || IsDestroyed) return;
        IsEnabled = false;
        Log.ForPhase(phase).Error("Brain disabled; Body, Graphics and Runtime remain available.", exception);
        StateMachine.Stop(IteratorActionExitReason.BrainDisabled);
        Sensors.Release();
    }
    internal void Release()
    {
        if (IsDestroyed) return;
        IsDestroyed = true; IsEnabled = false;
        StateMachine.Release();
        for (int i = _modules.Count - 1; i >= 0; i--) _modules[i].Release();
        try { Context.Logger = _destroyLog; OnDestroy(); }
        catch (Exception exception) { Log.ForPhase("Destroy").Error("Brain cleanup failed.", exception); }
        finally { Sensors.Release(); }
    }
}
