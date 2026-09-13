namespace DryCycle.Iterators;

/// <summary>运行实例的生命周期。注册与生成请求发生在实例创建之前，不属于实例状态。</summary>
public enum IteratorLifecycle
{
    RuntimeCreated,
    Initializing,
    Initialized,
    Active,
    Destroying,
    Destroyed
}

/// <summary>本次实例结束的原因；与 Descriptor 是否继续注册相互独立。</summary>
public enum IteratorDestroyReason
{
    Requested,
    RoomUnloaded,
    OracleRemoved,
    SessionEnded,
    DefinitionUnregistered,
    FrameworkDisabled,
    InitializationFailed,
    UpdateFailed,
    HostUnavailable
}
