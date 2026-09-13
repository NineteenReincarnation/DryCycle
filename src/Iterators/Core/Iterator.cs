namespace DryCycle.Iterators;

/// <summary>自定义迭代器定义入口；创建 Builder 不会登记或生成游戏实体。</summary>
public static class Iterator
{
    /// <summary>从区分大小写的 ID 创建独立的 Builder。</summary>
    public static IteratorBuilder Create(string id) => new(new IteratorID(id));

    /// <summary>从已有的值对象创建独立的 Builder。</summary>
    public static IteratorBuilder Create(IteratorID id) => new(id);
}
