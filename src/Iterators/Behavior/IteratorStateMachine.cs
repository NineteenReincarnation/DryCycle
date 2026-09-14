using System;
using System.Collections.Generic;

namespace DryCycle.Iterators;

/// <summary>确定性动作仲裁。请求在更新边界处理，每帧最多进入一个新动作，回调中的请求留到下一帧。</summary>
public sealed class IteratorStateMachine
{
    private readonly IteratorBrain _brain;
    private readonly List<IteratorAction> _actions = new();
    private readonly Dictionary<string, IteratorAction> _names = new(StringComparer.Ordinal);
    private long _requestVersion;
    private bool _stepping;
    internal IteratorStateMachine(IteratorBrain brain) { _brain = brain; Actions = _actions.AsReadOnly(); }
    public IReadOnlyList<IteratorAction> Actions { get; }
    public IteratorAction Current { get; private set; }
    public long TransitionCount { get; private set; }
    public bool TryGet(string name, out IteratorAction action)
    {
        action = null;
        return name != null && _names.TryGetValue(name, out action);
    }
    internal IteratorAction Add(IteratorAction action, IteratorBehaviorModule owner)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));
        if (action.Brain != null || action.IsDestroyed) throw new InvalidOperationException("Action is already owned or destroyed.");
        if (_names.ContainsKey(action.Name)) throw new ArgumentException("Duplicate action name: " + action.Name);
        if (_actions.Count >= 128) throw new InvalidOperationException("A Brain supports at most 128 actions.");
        _actions.Add(action); _names.Add(action.Name, action);
        action.Bind(_brain, owner);
        return action;
    }
    internal bool Request(IteratorAction action)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));
        if (!ReferenceEquals(action.Brain, _brain)) throw new ArgumentException("Action does not belong to this Brain.");
        if (!_brain.IsEnabled || _brain.IsDestroyed || !action.Available) return false;
        if (ReferenceEquals(Current, action) && !action.IsCompleted) return true;
        action.RequestVersion = ++_requestVersion;
        return true;
    }

    internal void StartIdle()
    {
        Current = _brain.Idle; TransitionCount++; Current.Enter();
    }

    internal void Step()
    {
        if (_stepping || !_brain.IsEnabled || _brain.IsDestroyed) return;
        _stepping = true;
        try
        {
            long cutoff = _requestVersion;
            IteratorAction previous = Current;
            bool continuing = previous != null && previous.CheckContinue();
            if (!_brain.IsEnabled || _brain.IsDestroyed) return;
            IteratorAction selected = null;
            if (continuing && !previous.Interruptible) selected = previous;
            else
            {
                // Scan registration order so equal-priority requests have a stable winner.
                foreach (IteratorAction candidate in _actions)
                {
                    if (candidate.RequestVersion == 0 || candidate.RequestVersion > cutoff) continue;
                    if (continuing && candidate.Priority < previous.Priority) continue;
                    if (selected != null && candidate.Priority <= selected.Priority) continue;
                    if (candidate.CheckEnter()) selected = candidate;
                    if (!_brain.IsEnabled || _brain.IsDestroyed) return;
                }
                if (selected != null || !continuing)
                    foreach (IteratorAction action in _actions) if (action.RequestVersion <= cutoff) action.RequestVersion = 0;
                selected ??= continuing ? previous : _brain.Idle;
            }
            if (!ReferenceEquals(selected, previous))
            {
                IteratorActionExitReason reason = previous == null ? IteratorActionExitReason.Replaced :
                    !previous.Available ? IteratorActionExitReason.Failed : previous.IsCompleted ? IteratorActionExitReason.Completed :
                    !continuing ? IteratorActionExitReason.ConditionLost : IteratorActionExitReason.Replaced;
                Current = null;
                previous?.Exit(reason);
                if (!_brain.IsEnabled || _brain.IsDestroyed) return;
                if (selected == null || !selected.Available) selected = _brain.Idle;
                Current = selected;
                TransitionCount++;
                selected.Enter();
            }
            if (!_brain.IsEnabled || _brain.IsDestroyed) return;
            Current?.Update();
            // Fault cleanup is immediate, while starting a fallback waits for the next
            // frame. This prevents recursive enter/exit loops from extension callbacks.
            if (Current != null && !Current.Available)
            {
                IteratorAction failed = Current; Current = null; failed.Exit(IteratorActionExitReason.Failed);
            }
        }
        finally { _stepping = false; }
    }

    internal void DisableOwned(IteratorBehaviorModule owner)
    {
        foreach (IteratorAction action in _actions) if (ReferenceEquals(action.Owner, owner)) { action.Disable(); action.RequestVersion = 0; }
    }
    internal void Stop(IteratorActionExitReason reason)
    {
        IteratorAction previous = Current; Current = null;
        foreach (IteratorAction action in _actions) action.RequestVersion = 0;
        previous?.Exit(reason);
    }
    internal void Release()
    {
        Stop(IteratorActionExitReason.Destroyed);
        for (int i = _actions.Count - 1; i >= 0; i--) _actions[i].Release();
    }
}
