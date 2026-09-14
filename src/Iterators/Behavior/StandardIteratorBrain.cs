using UnityEngine;

namespace DryCycle.Iterators;

/// <summary>安全默认行为：沿用 Body 的悬停和姿势，观察可见的存活玩家，不主动追逐、攻击或触发对话。</summary>
public sealed class StandardIteratorBrain : IteratorBrain
{
    public StandardIteratorBrain(IteratorContext context, IteratorSensorProfile sensors = null) : base(context, sensors)
        => Use(new ObservePlayer());
}

/// <summary>最低优先级回退。保留已有 Body 移动/姿势输入，不覆盖 Runtime 或外部控制器。</summary>
public sealed class IdleAction : IteratorAction
{
    public IdleAction() : base("Idle", int.MinValue) { }
}

/// <summary>低频选择观察目标，以切换距离阈值避免两个相近玩家之间反复转移注视。</summary>
public sealed class ObservePlayer : IteratorBehaviorModule
{
    private ObservePlayerAction _observe;
    public ObservePlayer() : base("ObservePlayer") { }
    protected override void OnInitialize() => _observe = (ObservePlayerAction)RegisterAction(new ObservePlayerAction());
    protected override void OnSense()
    {
        _observe.RefreshTarget();
        if (_observe.Target != null) Brain.SetAction(_observe);
    }
}

public sealed class ObservePlayerAction : IteratorAction
{
    private Vector2? _lastLook;
    public ObservePlayerAction() : base("Observe", priority: 10) { }
    public PlayerObservation Target { get; private set; }
    internal void RefreshTarget()
    {
        PlayerSensor sensor = Brain.Sensors;
        PlayerObservation nearest = sensor.NearestVisiblePlayer;
        if (!sensor.CanObserve(Target, retaining: true)) Target = nearest;
        else if (nearest != null && !ReferenceEquals(nearest, Target) &&
            nearest.Distance + sensor.Profile.TargetSwitchMargin < Target.Distance) Target = nearest;
    }
    protected override bool CanEnter() { RefreshTarget(); return Brain.Sensors.CanObserve(Target, retaining: true); }
    protected override bool CanContinue() => Brain.Sensors.CanObserve(Target, retaining: true);
    protected override void OnUpdate()
    {
        if (Target == null || !Target.HasPosition) return;
        _lastLook = Target.Position;
        Context.Body.LookAt(Target.Position);
    }
    protected override void OnExit(IteratorActionExitReason reason)
    {
        if (_lastLook.HasValue && Context.Body is { IsDestroyed: false } body && body.LookPoint == _lastLook) body.ClearLookTarget();
        _lastLook = null; Target = null;
    }
    protected override void OnDestroy() { _lastLook = null; Target = null; }
}
