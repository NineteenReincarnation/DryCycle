using System;
using UnityEngine;

namespace DryCycle.Iterators;

/// <summary>可替换的机械臂物理约束，不要求存在原版 Oracle.OracleArm 或任何图形。</summary>
public abstract class IteratorArm
{
    private readonly IteratorLogger _initializeLog;
    private readonly IteratorLogger _updateLog;
    private readonly IteratorLogger _destroyLog;

    protected IteratorArm(IteratorContext context)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        var logger = new IteratorLogger(context.ID).ForModule("Arm");
        _initializeLog = logger.ForPhase("Initialize");
        _updateLog = logger.ForPhase("Update");
        _destroyLog = logger.ForPhase("Destroy");
    }

    public IteratorContext Context { get; }
    public bool IsInitialized { get; private set; }
    public bool IsDestroyed { get; private set; }
    public virtual Vector2 Tip => Context.Body?.Position ?? Vector2.zero;

    /// <summary>返回满足本机械臂约束的目标坐标；自定义实现必须返回有限坐标。</summary>
    public Vector2 ConstrainTarget(Vector2 target)
    {
        if (IsDestroyed) throw new ObjectDisposedException(GetType().Name);
        if (!IsInitialized) throw new InvalidOperationException("IteratorFramework: Arm is not initialized.");
        BodyValidation.Vector(target, nameof(target));
        Context.Logger = _updateLog;
        return BodyValidation.Vector(OnConstrainTarget(target), "constrainedTarget");
    }

    protected virtual void OnInitialize() { }
    protected virtual Vector2 OnConstrainTarget(Vector2 target) => target;
    protected virtual void OnUpdate() { }
    protected virtual void OnAfterPhysics() { }
    protected virtual void OnDestroy() { }

    internal bool Claimed { get; private set; }
    internal void Claim() => Claimed = true;
    internal void Initialize()
    {
        Context.Logger = _initializeLog;
        OnInitialize();
        if (!IsDestroyed) IsInitialized = true;
    }

    internal void Update() { Context.Logger = _updateLog; OnUpdate(); }
    internal void AfterPhysics() { Context.Logger = _updateLog; OnAfterPhysics(); }
    internal void Release()
    {
        if (IsDestroyed) return;
        IsDestroyed = true;
        IsInitialized = false;
        Context.Logger = _destroyLog;
        try { OnDestroy(); }
        catch (Exception exception) { _destroyLog.Error("Arm cleanup failed; body and host cleanup will continue.", exception); }
    }
}
