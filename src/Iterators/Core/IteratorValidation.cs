using System;

namespace DryCycle.Iterators;

/// <summary>只验证定义数据；不查询 Registry，也不初始化游戏类型。</summary>
internal static class IteratorValidation
{
    internal static void RequireToken(string value, string parameterName)
    {
        if (value == null)
            throw new ArgumentNullException(parameterName, "IteratorFramework: a non-null identifier is required.");

        if (!IsToken(value))
            throw new ArgumentException(
                $"IteratorFramework: '{value}' is not a valid {parameterName}. " +
                "Use 1-128 ASCII letters, digits, underscores, dots or hyphens; start with a letter, digit or underscore.",
                parameterName);
    }

    internal static bool IsToken(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128)
            return false;

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            bool letterOrDigit = c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9';
            if (!letterOrDigit && c != '_' && !(i > 0 && (c == '.' || c == '-')))
                return false;
        }

        return true;
    }

    internal static void RequireText(string value, string parameterName)
    {
        if (value == null)
            throw new ArgumentNullException(parameterName, "IteratorFramework: a non-null text value is required.");

        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
            throw new ArgumentException("IteratorFramework: text cannot be blank or have surrounding whitespace.", parameterName);

        foreach (char c in value)
        {
            if (char.IsControl(c))
                throw new ArgumentException("IteratorFramework: text cannot contain control characters.", parameterName);
        }
    }
}
