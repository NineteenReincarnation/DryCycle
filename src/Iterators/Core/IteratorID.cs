using System;

namespace DryCycle.Iterators;

/// <summary>不可变、按 Ordinal 比较的迭代器 ID；构造和解析均不登记 Oracle ExtEnum。</summary>
public sealed class IteratorID : IEquatable<IteratorID>
{
    /// <summary>创建已验证的 ID。允许字母、数字、下划线及非首位的点、连字符，最多 128 字符。</summary>
    public IteratorID(string value)
    {
        IteratorValidation.RequireToken(value, nameof(value));
        Value = value;
    }

    /// <summary>原始、区分大小写的稳定字符串；不会自动裁剪或转换大小写。</summary>
    public string Value { get; }

    /// <summary>解析 ID；非法格式抛出 FormatException，null 抛出 ArgumentNullException。</summary>
    public static IteratorID Parse(string value)
    {
        if (value == null)
            throw new ArgumentNullException(nameof(value));
        if (!TryParse(value, out IteratorID id))
            throw new FormatException($"IteratorFramework: '{value}' is not a valid iterator ID.");
        return id;
    }

    /// <summary>安全解析，不注册；null、空白及非法格式返回 false。</summary>
    public static bool TryParse(string value, out IteratorID id)
    {
        id = IteratorValidation.IsToken(value) ? new IteratorID(value) : null;
        return id != null;
    }

    /// <summary>查询已注册的 ID；与 Parse 不同，这不会返回尚未注册的值。</summary>
    public static bool TryGet(string value, out IteratorID id)
    {
        bool found = IteratorRegistry.TryGet(value, out IteratorDescriptor descriptor);
        id = found ? descriptor.ID : null;
        return found;
    }

    /// <summary>查询此字符串是否已由框架注册。</summary>
    public static bool IsRegistered(string value) => IteratorRegistry.TryGet(value, out _);

    /// <summary>按区分大小写的字符串值比较，不依赖对象引用或 ExtEnum 的可变 Index。</summary>
    public bool Equals(IteratorID other) => other is not null && StringComparer.Ordinal.Equals(Value, other.Value);

    /// <inheritdoc />
    public override bool Equals(object obj) => obj is IteratorID other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>按值比较，支持 null。</summary>
    public static bool operator ==(IteratorID left, IteratorID right) =>
        ReferenceEquals(left, right) || left is not null && left.Equals(right);

    /// <summary>按值比较，支持 null。</summary>
    public static bool operator !=(IteratorID left, IteratorID right) => !(left == right);
}
