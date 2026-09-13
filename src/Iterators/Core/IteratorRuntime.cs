using System;

namespace DryCycle.Iterators;

/// <summary>
/// 实例逻辑中心。框架独占状态转换、重复调用保护与异常处理；派生类型只实现生命周期回调。
/// 一份 Descriptor 可以在不同 Room / Session 中拥有不同 Runtime。
/// </summary>
public class IteratorRuntime
{
    private bool _initializing;
    private bool _updating;
    private Action<IteratorRuntime> _release;
    private readonly IteratorLogger _createLog;
    private readonly IteratorLogger _initializeLog;
    private readonly IteratorLogger _roomReadyLog;
    private readonly IteratorLogger _activateLog;
    private readonly IteratorLogger _updateLog;
    private readonly IteratorLogger _lateUpdateLog;
    private readonly IteratorLogger _destroyLog;

    /// <summary>仅从 Descriptor 的 RuntimeFactory 创建；context 必须原样传入，不能跨实例复用 Runtime。</summary>
    public IteratorRuntime(IteratorContext context)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        var logger = new IteratorLogger(context.ID).ForModule("Runtime");
        _createLog = logger.ForPhase("OnCreate");
        _initializeLog = logger.ForPhase("OnInitialize");
        _roomReadyLog = logger.ForPhase("OnRoomReady");
        _activateLog = logger.ForPhase("OnActivate");
        _updateLog = logger.ForPhase("OnUpdate");
        _lateUpdateLog = logger.ForPhase("OnLateUpdate");
        _destroyLog = logger.ForPhase("OnDestroy");
    }

    public IteratorContext Context { get; }
    public IteratorDescriptor Descriptor => Context.Descriptor;
    public IteratorID ID => Descriptor.ID;
    public IteratorLifecycle State { get; private set; } = IteratorLifecycle.RuntimeCreated;
    public bool IsInitialized => State == IteratorLifecycle.Initialized || State == IteratorLifecycle.Active;
    public bool IsActive => State == IteratorLifecycle.Active;
    public IteratorDestroyReason? DestroyReason { get; private set; }

    /// <summary>完成 OnUpdate 与 OnLateUpdate 的游戏帧数；不包括被暂停、重入或失败的更新。</summary>
    public long UpdateCount { get; private set; }

    /// <summary>立即结束本实例，幂等。不会注销 Descriptor；需要重新生成时显式调用 IteratorRuntimes.TrySpawn。</summary>
    public void Destroy() => Destroy(IteratorDestroyReason.Requested);

    protected virtual void OnCreate() { }
    protected virtual void OnInitialize() { }
    protected virtual void OnRoomReady() { }
    protected virtual void OnActivate() { }
    protected virtual void OnUpdate() { }
    protected virtual void OnLateUpdate() { }
    protected virtual void OnDestroy() { }

    internal void SetRelease(Action<IteratorRuntime> release)
    {
        if (_release != null || State != IteratorLifecycle.RuntimeCreated)
            throw new InvalidOperationException($"IteratorFramework: Runtime '{ID}' was already bound or has ended.");
        _release = release ?? throw new ArgumentNullException(nameof(release));
    }

    internal bool Initialize()
    {
        if (_initializing || State != IteratorLifecycle.RuntimeCreated)
            return IsActive;
        _initializing = true;
        try
        {
            Context.RefreshPlayers();
            Context.Logger = _createLog;
            OnCreate();
            if (State != IteratorLifecycle.RuntimeCreated) return false;
            State = IteratorLifecycle.Initializing;
            Context.Logger = _initializeLog;
            OnInitialize();
            if (State != IteratorLifecycle.Initializing) return false;
            State = IteratorLifecycle.Initialized;
            Context.Logger = _roomReadyLog;
            OnRoomReady();
            if (State != IteratorLifecycle.Initialized) return false;
            State = IteratorLifecycle.Active;
            Context.Logger = _activateLog;
            OnActivate();
            if (IsActive)
                _activateLog.Info($"Active in room '{Context.Room.abstractRoom.name}'.");
            return IsActive;
        }
        catch (Exception exception)
        {
            Context.Logger.Error("Runtime initialization failed; releasing this instance.", exception);
            Destroy(IteratorDestroyReason.InitializationFailed);
            return false;
        }
        finally
        {
            _initializing = false;
        }
    }

    internal void Tick()
    {
        if (!IsActive || _initializing || _updating)
            return;
        if (Context.Oracle == null || Context.Oracle.slatedForDeletetion ||
            Context.Room == null || !ReferenceEquals(Context.Oracle.room, Context.Room) ||
            !ReferenceEquals(Context.Room.abstractRoom?.realizedRoom, Context.Room) ||
            !ReferenceEquals(Context.Room.game, Context.Game) || !ReferenceEquals(Context.Room.world, Context.World))
        {
            Destroy(IteratorDestroyReason.HostUnavailable);
            return;
        }

        _updating = true;
        try
        {
            Context.Logger = _updateLog;
            Context.RefreshPlayers();
            OnUpdate();
            if (!IsActive) return;
            Context.Logger = _lateUpdateLog;
            OnLateUpdate();
            if (IsActive) UpdateCount++;
        }
        catch (Exception exception)
        {
            Context.Logger.Error("Runtime update failed; releasing this instance.", exception);
            Destroy(IteratorDestroyReason.UpdateFailed);
        }
        finally
        {
            _updating = false;
        }
    }

    internal void Destroy(IteratorDestroyReason reason)
    {
        if (State == IteratorLifecycle.Destroying || State == IteratorLifecycle.Destroyed)
            return;
        State = IteratorLifecycle.Destroying;
        DestroyReason = reason;
        Context.Logger = _destroyLog;
        try
        {
            OnDestroy();
        }
        catch (Exception exception)
        {
            _destroyLog.Error("OnDestroy failed; framework cleanup will still run.", exception);
        }
        finally
        {
            try
            {
                _release?.Invoke(this);
            }
            catch (Exception exception)
            {
                _destroyLog.Error("Host cleanup failed.", exception);
            }
            finally
            {
                _release = null;
                Context.ReleaseGameReferences();
                State = IteratorLifecycle.Destroyed;
                _destroyLog.Info($"Destroyed ({reason}).");
            }
        }
    }
}
