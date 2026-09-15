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
    public IteratorBody Body { get; private set; }
    public IteratorArm Arm { get; private set; }
    public IteratorGraphics Graphics { get; private set; }
    public IteratorBrain Brain { get; private set; }
    public ConversationController Conversation { get; private set; }
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
            if (!InitializeBodyAndArm()) return false;
            InitializeGraphics();
            if (State != IteratorLifecycle.RuntimeCreated) return false;
            Context.RefreshPlayers();
            InitializeBrain();
            if (State != IteratorLifecycle.RuntimeCreated) return false;
            InitializeConversation();
            if (State != IteratorLifecycle.RuntimeCreated) return false;
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

    private bool InitializeBodyAndArm()
    {
        Context.Logger = _initializeLog.ForModule("Body").ForPhase("Factory");
        IteratorBody body = Descriptor.BodyFactory(Context);
        if (body == null || !ReferenceEquals(body.Context, Context) || body.Claimed || body.IsDestroyed)
            throw new InvalidOperationException("BodyFactory must return a new Body built with the supplied Context.");
        body.Claim();
        Body = body;
        // A user factory may unload the room or destroy the Runtime before returning.
        if (State != IteratorLifecycle.RuntimeCreated) { body.Release(); return false; }

        Context.Logger = _initializeLog.ForModule("Arm").ForPhase("Factory");
        IteratorArm arm = Descriptor.ArmFactory(Context);
        if (arm == null || !ReferenceEquals(arm.Context, Context) || arm.Claimed || arm.IsDestroyed)
            throw new InvalidOperationException("ArmFactory must return a new Arm built with the supplied Context.");
        arm.Claim();
        Arm = arm;
        if (State != IteratorLifecycle.RuntimeCreated) { arm.Release(); return false; }

        Body.Initialize();
        if (State != IteratorLifecycle.RuntimeCreated) return false;
        Arm.Initialize();
        return State == IteratorLifecycle.RuntimeCreated;
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
            Brain?.Update();
            if (!IsActive) return;
            Conversation?.Update();
            if (!IsActive) return;
            Context.Logger = _updateLog;
            OnUpdate();
            if (!IsActive) return;
            Body.Update();
            if (!IsActive) return;
            Arm.Update();
            if (!IsActive) return;
            Body.AdvancePhysics();
            if (!IsActive) return;
            Arm.AfterPhysics();
            if (!IsActive) return;
            Body.AfterPhysics();
            if (!IsActive) return;
            Graphics?.Update();
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
                Conversation?.Release();
                Brain?.Release();
                Graphics?.Release();
                Arm?.Release();
                Body?.Release();
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

    private void InitializeGraphics()
    {
        var logger = _initializeLog.ForModule("Graphics");
        try
        {
            IteratorGraphics graphics = Descriptor.GraphicsFactory(Context);
            if (graphics == null || !ReferenceEquals(graphics.Context, Context) || graphics.Claimed || graphics.IsDestroyed)
                throw new InvalidOperationException("GraphicsFactory must return a new Graphics built with the supplied Context.");
            graphics.Claimed = true; Graphics = graphics;
            if (State != IteratorLifecycle.RuntimeCreated) { graphics.Release(); return; }
            graphics.Initialize();
        }
        catch (Exception exception)
        {
            logger.Error("Graphics initialization failed; trying the standard appearance.", exception);
            Graphics?.Release();
            if (State != IteratorLifecycle.RuntimeCreated) return;
            Graphics = new StandardIteratorGraphics(Context) { Claimed = true };
            try { Graphics.Initialize(); }
            catch (Exception fallbackFailure) { Graphics.Disable("Fallback", fallbackFailure); }
        }
        if (State != IteratorLifecycle.RuntimeCreated) return;
        try { (Context.Oracle as IteratorHost)?.AttachGraphics(); }
        catch (Exception exception) { Graphics.Disable("Attach", exception); }
    }

    private void InitializeBrain()
    {
        try
        {
            Context.Logger = _initializeLog.ForModule("Brain").ForPhase("Factory");
            IteratorBrain brain = Descriptor.BrainFactory(Context);
            if (brain == null || !ReferenceEquals(brain.Context, Context) || brain.Claimed || brain.IsDestroyed)
                throw new InvalidOperationException("BrainFactory must return a new Brain built with the supplied Context.");
            brain.Claimed = true; Brain = brain;
            if (State != IteratorLifecycle.RuntimeCreated) { brain.Release(); return; }
            brain.Initialize();
        }
        catch (Exception exception)
        {
            _initializeLog.ForModule("Brain").Error("Brain initialization failed; using passive Idle behavior.", exception);
            Brain?.Release();
            if (State != IteratorLifecycle.RuntimeCreated) return;
            Brain = new IteratorBrain(Context) { Claimed = true };
            try { Brain.Initialize(); }
            catch (Exception fallbackFailure) { Brain.Disable("Fallback", fallbackFailure); }
        }
    }

    private void InitializeConversation()
    {
        try
        {
            Context.Logger = _initializeLog.ForModule("Conversation").ForPhase("Factory");
            ConversationController conversation = Descriptor.ConversationFactory(Context);
            if (conversation == null || !ReferenceEquals(conversation.Context, Context) || conversation.Claimed || conversation.IsDestroyed)
                throw new InvalidOperationException("ConversationFactory must return a new controller built with the supplied Context.");
            conversation.Claimed = true; Conversation = conversation;
            if (State != IteratorLifecycle.RuntimeCreated) { conversation.Release(); return; }
            conversation.Initialize();
        }
        catch (Exception exception)
        {
            _initializeLog.ForModule("Conversation").Error("Conversation initialization failed; using an empty controller.", exception);
            Conversation?.Release();
            if (State != IteratorLifecycle.RuntimeCreated) return;
            Conversation = new EmptyConversation(Context) { Claimed = true };
            try { Conversation.Initialize(); }
            catch (Exception fallbackFailure) { Conversation.Disable(fallbackFailure); }
        }
    }
}
