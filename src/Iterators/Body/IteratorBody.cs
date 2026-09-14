using System;
using System.Collections.Generic;
using UnityEngine;

namespace DryCycle.Iterators;

/// <summary>可替换的身体组件。框架负责调度/销毁，派生类型实现初始化、物理前后的控制逻辑。</summary>
public abstract class IteratorBody
{
    private IReadOnlyList<BodyChunk> _chunks = Array.Empty<BodyChunk>();
    private bool _initializing;
    private readonly IteratorLogger _initializeLog;
    private readonly IteratorLogger _updateLog;
    private readonly IteratorLogger _destroyLog;

    protected IteratorBody(IteratorContext context, BodyProfile profile = null)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Profile = profile ?? BodyProfile.Default;
        var logger = new IteratorLogger(context.ID).ForModule("Body");
        _initializeLog = logger.ForPhase("Initialize");
        _updateLog = logger.ForPhase("Update");
        _destroyLog = logger.ForPhase("Destroy");
    }

    public IteratorContext Context { get; }
    public BodyProfile Profile { get; }
    public bool IsInitialized { get; private set; }
    public bool IsDestroyed { get; private set; }
    public IReadOnlyList<BodyChunk> Chunks => _chunks;
    public IteratorPose Pose { get; private set; } = IteratorPose.Idle;
    public Vector2? MovementTarget { get; private set; }
    public Vector2? LookPoint { get; private set; }
    public virtual Vector2 Position => Average(false);
    public virtual Vector2 Velocity => Average(true);
    public virtual Vector2 Direction => _chunks.Count < 2 || (_chunks[0].pos - _chunks[1].pos).sqrMagnitude < 0.000001f
        ? Pose.BodyDirection : (_chunks[0].pos - _chunks[1].pos).normalized;
    public virtual Vector2 LeftHandPosition => PosePoint(Pose.LeftHandOffset);
    public virtual Vector2 RightHandPosition => PosePoint(Pose.RightHandOffset);
    public virtual Vector2 LeftFootPosition => PosePoint(Pose.LeftFootOffset);
    public virtual Vector2 RightFootPosition => PosePoint(Pose.RightFootOffset);

    /// <summary>请求移动身体中心，目标为房间像素坐标；默认控制器受碰撞与 Arm 约束，不执行寻路。</summary>
    public void MoveTo(Vector2 target) { EnsureUsable(); MovementTarget = BodyValidation.Vector(target, nameof(target)); }

    /// <summary>停止当前速度并保持当前位置。ReleaseMovement 则只取消主动移动，不清除惯性。</summary>
    public void Stop()
    {
        EnsureUsable();
        MovementTarget = Position;
        for (int i = 0; i < _chunks.Count; i++) _chunks[i].vel = Vector2.zero;
    }

    public void ReleaseMovement() { EnsureUsable(); MovementTarget = null; }
    public void LookAt(Vector2 point) { EnsureUsable(); LookPoint = BodyValidation.Vector(point, nameof(point)); }
    public void ClearLookTarget() { EnsureUsable(); LookPoint = null; }
    public void SetPose(IteratorPose pose) { EnsureUsable(); Pose = pose ?? throw new ArgumentNullException(nameof(pose)); }

    protected abstract void OnInitialize();
    protected abstract void OnUpdate();
    protected virtual void OnAfterPhysics() { }
    protected virtual void OnDestroy() { }

    /// <summary>自定义身体可禁用原版 PhysicalObject 物理，并在 OnUpdate 中自行更新已配置的 BodyChunk。</summary>
    protected virtual bool UseGamePhysics => true;

    /// <summary>仅在 OnInitialize 内设置身体结构。数组复制，BodyChunk 必须属于当前 Oracle，连接只能引用这些 Chunk。</summary>
    protected void ConfigurePhysics(BodyChunk[] chunks, PhysicalObject.BodyChunkConnection[] connections)
    {
        if (!_initializing || IsDestroyed || Context.Oracle is not IteratorHost host)
            throw new InvalidOperationException("IteratorFramework: configure body physics only during OnInitialize.");
        if (chunks == null) throw new ArgumentNullException(nameof(chunks));
        if (connections == null) throw new ArgumentNullException(nameof(connections));
        if (chunks.Length == 0 || chunks.Length > 64)
            throw new ArgumentException("IteratorFramework: a body needs 1–64 chunks.", nameof(chunks));
        var owned = new HashSet<BodyChunk>();
        for (int i = 0; i < chunks.Length; i++)
        {
            BodyChunk chunk = chunks[i];
            if (chunk == null || !ReferenceEquals(chunk.owner, host) || chunk.index != i || !owned.Add(chunk))
                throw new ArgumentException("IteratorFramework: each body chunk needs its own index and the current Oracle as owner.", nameof(chunks));
            BodyValidation.Range(chunk.rad, 1f, 100f, "chunk.rad");
            BodyValidation.Range(chunk.mass, 0.01f, 100f, "chunk.mass");
            BodyValidation.Vector(chunk.pos, "chunk.pos");
            if (chunk.collideWithTerrain && !BodyPhysics.CanOccupy(Context.Room, chunk.pos, chunk.rad))
                throw new ArgumentException("IteratorFramework: initial body chunks must fit in clear room space.", nameof(chunks));
        }
        foreach (PhysicalObject.BodyChunkConnection connection in connections)
        {
            if (connection == null || !owned.Contains(connection.chunk1) || !owned.Contains(connection.chunk2) || ReferenceEquals(connection.chunk1, connection.chunk2))
                throw new ArgumentException("IteratorFramework: connections must join two different chunks of this body.", nameof(connections));
            BodyValidation.Range(connection.distance, 0.01f, 1000f, "connection.distance");
            BodyValidation.Range(connection.elasticity, 0f, 1f, "connection.elasticity");
            BodyValidation.Range(connection.weightSymmetry, 0f, 1f, "connection.weightSymmetry");
        }
        var chunkCopy = (BodyChunk[])chunks.Clone();
        host.ConfigurePhysics(chunkCopy, (PhysicalObject.BodyChunkConnection[])connections.Clone(), Profile);
        _chunks = Array.AsReadOnly(chunkCopy);
    }

    protected void EnsureUsable()
    {
        if (IsDestroyed || Context.Runtime?.State == IteratorLifecycle.Destroying || Context.Runtime?.State == IteratorLifecycle.Destroyed)
            throw new ObjectDisposedException(GetType().Name);
        if (!IsInitialized && !_initializing)
            throw new InvalidOperationException("IteratorFramework: body commands require an initialized body.");
    }

    internal bool Claimed { get; private set; }
    internal void Claim() => Claimed = true;
    internal void Initialize()
    {
        if (IsInitialized || _initializing || IsDestroyed) return;
        Context.Logger = _initializeLog;
        _initializing = true;
        try
        {
            OnInitialize();
            if (IsDestroyed) return;
            if (_chunks.Count == 0) throw new InvalidOperationException("IteratorFramework: body initialization must ConfigurePhysics.");
            ValidatePhysics();
            IsInitialized = true;
        }
        finally { _initializing = false; }
    }

    internal void Update()
    {
        Context.Logger = _updateLog;
        OnUpdate();
    }

    internal void AdvancePhysics()
    {
        Context.Logger = _updateLog;
        ValidatePhysics();
        if (UseGamePhysics && Context.Oracle is IteratorHost host) host.UpdatePhysics();
    }

    internal void AfterPhysics()
    {
        Context.Logger = _updateLog;
        OnAfterPhysics();
        if (!IsDestroyed) ValidatePhysics();
    }

    internal void Release()
    {
        if (IsDestroyed) return;
        IsDestroyed = true;
        IsInitialized = false;
        Context.Logger = _destroyLog;
        try { OnDestroy(); }
        catch (Exception exception) { _destroyLog.Error("Body cleanup failed; host cleanup will continue.", exception); }
        finally
        {
            _chunks = Array.Empty<BodyChunk>();
            MovementTarget = null;
            LookPoint = null;
        }
    }

    // Correct an Arm's small positional error only when every chunk can occupy the
    // destination. Terrain wins if a rigid constraint and a wall cannot both hold.
    internal void CorrectPosition(Vector2 offset)
    {
        if (!BodyPhysics.CanTranslate(Context.Room, _chunks, offset)) return;
        for (int i = 0; i < _chunks.Count; i++) _chunks[i].pos += offset;
    }

    private void ValidatePhysics()
    {
        for (int i = 0; i < _chunks.Count; i++)
        {
            BodyChunk chunk = _chunks[i];
            BodyValidation.Vector(chunk.pos, "chunk.pos");
            BodyValidation.Vector(chunk.vel, "chunk.vel", 1000f);
        }
    }

    private Vector2 Average(bool velocity)
    {
        Vector2 sum = Vector2.zero;
        float mass = 0f;
        for (int i = 0; i < _chunks.Count; i++)
        {
            BodyChunk chunk = _chunks[i];
            sum += (velocity ? chunk.vel : chunk.pos) * chunk.mass;
            mass += chunk.mass;
        }
        return mass > 0f ? sum / mass : Vector2.zero;
    }

    private Vector2 PosePoint(Vector2 offset)
    {
        Vector2 up = Direction;
        return Position + new Vector2(up.y, -up.x) * offset.x + up * offset.y;
    }
}
