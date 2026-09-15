using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DryCycle.Iterators;

/// <summary>
/// 一份创建后即不可变的定义，包含注册数据与可替换的 Runtime 工厂。
/// 输入集合会复制，Metadata 使用不可变字符串，避免外部引用改变已注册定义。
/// </summary>
public sealed class IteratorDescriptor
{
    /// <summary>直接构造定义，无须使用 Fluent API。displayName 为 null 时使用 ID，metadata 可省略。</summary>
    public IteratorDescriptor(
        IteratorID id,
        IEnumerable<string> rooms,
        string displayName = null,
        IReadOnlyDictionary<string, string> metadata = null)
        : this(id, rooms, displayName, metadata, null)
    {
    }

    /// <summary>指定 Runtime 工厂的完整构造入口；null 使用安全默认 Runtime，保留原四参数构造的兼容性。</summary>
    public IteratorDescriptor(
        IteratorID id,
        IEnumerable<string> rooms,
        string displayName,
        IReadOnlyDictionary<string, string> metadata,
        Func<IteratorContext, IteratorRuntime> runtimeFactory)
        : this(id, rooms, displayName, metadata, runtimeFactory, null, null)
    {
    }

    /// <summary>指定 Runtime、Body 和 Arm 工厂；null 使用各自默认实现，所有工厂仅在实例生成时执行。</summary>
    public IteratorDescriptor(
        IteratorID id,
        IEnumerable<string> rooms,
        string displayName,
        IReadOnlyDictionary<string, string> metadata,
        Func<IteratorContext, IteratorRuntime> runtimeFactory,
        Func<IteratorContext, IteratorBody> bodyFactory,
        Func<IteratorContext, IteratorArm> armFactory)
        : this(id, rooms, displayName, metadata, runtimeFactory, bodyFactory, armFactory, null)
    {
    }

    /// <summary>完整组件工厂入口；保留已有四、五、七参数构造签名。</summary>
    public IteratorDescriptor(IteratorID id, IEnumerable<string> rooms, string displayName,
        IReadOnlyDictionary<string, string> metadata, Func<IteratorContext, IteratorRuntime> runtimeFactory,
        Func<IteratorContext, IteratorBody> bodyFactory, Func<IteratorContext, IteratorArm> armFactory,
        Func<IteratorContext, IteratorGraphics> graphicsFactory)
        : this(id, rooms, displayName, metadata, runtimeFactory, bodyFactory, armFactory, graphicsFactory, null)
    {
    }

    /// <summary>包含 Brain 工厂的完整入口，保留四、五、七、八参数构造兼容性。</summary>
    public IteratorDescriptor(IteratorID id, IEnumerable<string> rooms, string displayName,
        IReadOnlyDictionary<string, string> metadata, Func<IteratorContext, IteratorRuntime> runtimeFactory,
        Func<IteratorContext, IteratorBody> bodyFactory, Func<IteratorContext, IteratorArm> armFactory,
        Func<IteratorContext, IteratorGraphics> graphicsFactory, Func<IteratorContext, IteratorBrain> brainFactory)
        : this(id, rooms, displayName, metadata, runtimeFactory, bodyFactory, armFactory, graphicsFactory, brainFactory, null)
    {
    }

    /// <summary>包含 Conversation 工厂的完整入口；保留前五阶段所有构造签名。</summary>
    public IteratorDescriptor(IteratorID id, IEnumerable<string> rooms, string displayName,
        IReadOnlyDictionary<string, string> metadata, Func<IteratorContext, IteratorRuntime> runtimeFactory,
        Func<IteratorContext, IteratorBody> bodyFactory, Func<IteratorContext, IteratorArm> armFactory,
        Func<IteratorContext, IteratorGraphics> graphicsFactory, Func<IteratorContext, IteratorBrain> brainFactory,
        Func<IteratorContext, ConversationController> conversationFactory)
    {
        ID = id ?? throw new ArgumentNullException(nameof(id));
        if (rooms == null)
            throw new ArgumentNullException(nameof(rooms));

        DisplayName = displayName ?? id.Value;
        RuntimeFactory = runtimeFactory ?? CreateDefaultRuntime;
        BodyFactory = bodyFactory ?? CreateDefaultBody;
        ArmFactory = armFactory ?? CreateDefaultArm;
        GraphicsFactory = graphicsFactory ?? CreateDefaultGraphics;
        BrainFactory = brainFactory ?? CreateDefaultBrain;
        ConversationFactory = conversationFactory ?? CreateDefaultConversation;
        Rooms = new ReadOnlyCollection<string>(new List<string>(rooms));
        var metadataCopy = new Dictionary<string, string>(StringComparer.Ordinal);
        if (metadata != null)
        {
            foreach (KeyValuePair<string, string> pair in metadata)
            {
                IteratorValidation.RequireText(pair.Key, "metadataKey");
                if (pair.Value == null)
                    throw new ArgumentException($"IteratorFramework: metadata '{pair.Key}' for '{ID}' cannot be null.", nameof(metadata));
                metadataCopy.Add(pair.Key, pair.Value);
            }
        }

        Metadata = new ReadOnlyDictionary<string, string>(metadataCopy);
        Validate();
    }

    /// <summary>稳定的迭代器 ID。</summary>
    public IteratorID ID { get; }

    /// <summary>显示名称，缺省为 ID。</summary>
    public string DisplayName { get; }

    /// <summary>按声明顺序保存的精确房间名。房间查询和冲突检查忽略大小写，不要求 _AI 后缀。</summary>
    public IReadOnlyList<string> Rooms { get; }

    /// <summary>区分大小写的只读字符串元数据；建议扩展使用所属 Mod 的键前缀。</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }

    /// <summary>每次生成调用一次，必须返回使用所传 Context 创建的全新 Runtime。</summary>
    public Func<IteratorContext, IteratorRuntime> RuntimeFactory { get; }

    public Func<IteratorContext, IteratorBody> BodyFactory { get; }
    public Func<IteratorContext, IteratorArm> ArmFactory { get; }
    public Func<IteratorContext, IteratorGraphics> GraphicsFactory { get; }
    public Func<IteratorContext, IteratorBrain> BrainFactory { get; }
    public Func<IteratorContext, ConversationController> ConversationFactory { get; }

    private static IteratorRuntime CreateDefaultRuntime(IteratorContext context) => new(context);
    private static IteratorBody CreateDefaultBody(IteratorContext context) => new StandardIteratorBody(context);
    private static IteratorArm CreateDefaultArm(IteratorContext context) => new NoArm(context);
    private static IteratorGraphics CreateDefaultGraphics(IteratorContext context) => new StandardIteratorGraphics(context);
    private static IteratorBrain CreateDefaultBrain(IteratorContext context) => new StandardIteratorBrain(context);
    private static ConversationController CreateDefaultConversation(IteratorContext context) => new EmptyConversation(context);

    /// <summary>验证定义本身。不会注册或检查全局冲突；全局冲突由 Registry.Register 检查。</summary>
    public void Validate()
    {
        IteratorValidation.RequireText(DisplayName, nameof(DisplayName));
        if (Rooms.Count == 0)
            throw new ArgumentException($"IteratorFramework: iterator '{ID}' must declare at least one room.", nameof(Rooms));

        var seenRooms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string room in Rooms)
        {
            IteratorValidation.RequireToken(room, "roomName");
            if (!seenRooms.Add(room))
                throw new ArgumentException($"IteratorFramework: iterator '{ID}' declares room '{room}' more than once.", nameof(Rooms));
        }
    }
}
