using System;
using System.Collections.Generic;

namespace DryCycle.Iterators;

/// <summary>只负责收集配置；每次 Build 创建独立的不可变 Descriptor，不充当 Registry 数据源。</summary>
public class IteratorBuilder
{
    private readonly IteratorID _id;
    private readonly List<string> _rooms = new();
    private readonly Dictionary<string, string> _metadata = new(StringComparer.Ordinal);
    private string _displayName;
    private Func<IteratorContext, IteratorRuntime> _runtimeFactory;
    private Func<IteratorContext, IteratorBody> _bodyFactory;
    private Func<IteratorContext, IteratorArm> _armFactory;

    /// <summary>创建一个独立 Builder；扩展 Mod 可通过扩展方法组合公共配置方法。</summary>
    public IteratorBuilder(IteratorID id) => _id = id ?? throw new ArgumentNullException(nameof(id));

    /// <summary>设置显示名称，支持非 ASCII 文本。</summary>
    public IteratorBuilder Name(string displayName)
    {
        IteratorValidation.RequireText(displayName, nameof(displayName));
        _displayName = displayName;
        return this;
    }

    /// <summary>添加一个精确房间绑定；重复房间会报错。</summary>
    public IteratorBuilder Room(string roomName) => Rooms(roomName);

    /// <summary>一次添加多个房间；任一输入无效时整批不写入。</summary>
    public IteratorBuilder Rooms(params string[] roomNames)
    {
        if (roomNames == null)
            throw new ArgumentNullException(nameof(roomNames));
        if (roomNames.Length == 0)
            throw new ArgumentException("IteratorFramework: supply at least one room.", nameof(roomNames));

        var seenRooms = new HashSet<string>(_rooms, StringComparer.OrdinalIgnoreCase);
        foreach (string room in roomNames)
        {
            IteratorValidation.RequireToken(room, nameof(roomNames));
            if (!seenRooms.Add(room))
                throw new ArgumentException($"IteratorFramework: iterator '{_id}' already declares room '{room}'.", nameof(roomNames));
        }

        _rooms.AddRange(roomNames);
        return this;
    }

    /// <summary>设置扩展元数据；再次设置同一键会替换 Builder 中的值，不影响已 Build 的定义。</summary>
    public IteratorBuilder WithMetadata(string key, string value)
    {
        IteratorValidation.RequireText(key, nameof(key));
        if (value == null)
            throw new ArgumentNullException(nameof(value));
        _metadata[key] = value;
        return this;
    }

    /// <summary>验证并创建定义快照；没有 Registry 或 Oracle ExtEnum 副作用。</summary>
    public IteratorDescriptor Build() => new(_id, _rooms, _displayName, _metadata, _runtimeFactory, _bodyFactory, _armFactory);

    /// <summary>设置实例工厂，可注入参数或派生 Runtime；不在 Builder 阶段运行该工厂。</summary>
    public IteratorBuilder Runtime(Func<IteratorContext, IteratorRuntime> factory)
    {
        _runtimeFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    /// <summary>替换默认身体；每次生成必须使用传入的 Context 创建新组件。</summary>
    public IteratorBuilder Body(Func<IteratorContext, IteratorBody> factory)
    {
        _bodyFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    /// <summary>替换默认 NoArm 约束；每次生成必须返回新组件，不要求原版 OracleArm。</summary>
    public IteratorBuilder Arm(Func<IteratorContext, IteratorArm> factory)
    {
        _armFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    /// <summary>构造后交给 Registry。请保存返回值，以便幂等注册或安全注销同一份定义。</summary>
    public IteratorDescriptor Register() => IteratorRegistry.Register(Build());
}
