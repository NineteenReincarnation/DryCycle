using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Objects;

public enum EditorPropertyKind
{
    ReadOnly,
    Float,
    Integer,
    Boolean,
    String,
    Vector2,
    Color,
    Enum
}

/// <summary>
/// Detached property description consumed by the optional ImGui frontend. It contains
/// only scalar/string data so the render callback never dereferences Rain World objects.
/// </summary>
public sealed class EditorPropertySnapshot
{
    public string Key { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Group { get; init; } = "Properties";
    public string Source { get; init; } = string.Empty;
    public EditorPropertyKind Kind { get; init; }
    public string SerializedValue { get; init; } = string.Empty;
    public string StringValue { get; init; } = string.Empty;
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
    public float W { get; init; }
    public int IntegerValue { get; init; }
    public bool BooleanValue { get; init; }
    public float Min { get; init; }
    public float Max { get; init; }
    public float Step { get; init; }
    public bool HasRange { get; init; }
    public string[] Options { get; init; } = Array.Empty<string>();
}

public readonly struct EditorPropertyValue
{
    public EditorPropertyValue(
        EditorPropertyKind kind,
        string text = null,
        float x = 0f,
        float y = 0f,
        float z = 0f,
        float w = 0f,
        int integer = 0,
        bool boolean = false)
    {
        Kind = kind;
        Text = text ?? string.Empty;
        X = x;
        Y = y;
        Z = z;
        W = w;
        Integer = integer;
        Boolean = boolean;
    }

    public EditorPropertyKind Kind { get; }
    public string Text { get; }
    public float X { get; }
    public float Y { get; }
    public float Z { get; }
    public float W { get; }
    public int Integer { get; }
    public bool Boolean { get; }
}

internal interface IObjectInspectorAdapter
{
    bool CanInspect(PlacedObject target);
    IReadOnlyList<EditorPropertySnapshot> Capture(PlacedObject target);
    bool TrySetValue(PlacedObject target, string key, EditorPropertyValue value);
}

internal static class ObjectInspectorRegistry
{
    private static readonly List<IObjectInspectorAdapter> adapters = new();

    internal static void Register(IObjectInspectorAdapter adapter)
    {
        if (adapter == null || adapters.Contains(adapter)) return;
        adapters.Add(adapter);
    }

    internal static EditorPropertySnapshot[] Capture(PlacedObject target)
    {
        if (target == null) return Array.Empty<EditorPropertySnapshot>();

        for (int i = 0; i < adapters.Count; i++)
        {
            IObjectInspectorAdapter adapter = adapters[i];
            try
            {
                if (!adapter.CanInspect(target)) continue;
                IReadOnlyList<EditorPropertySnapshot> properties = adapter.Capture(target);
                if (properties == null || properties.Count == 0) return Array.Empty<EditorPropertySnapshot>();

                EditorPropertySnapshot[] detached = new EditorPropertySnapshot[properties.Count];
                for (int p = 0; p < properties.Count; p++) detached[p] = properties[p];
                return detached;
            }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("DevTool inspector capture failed: " + error.Message);
                return Array.Empty<EditorPropertySnapshot>();
            }
        }

        return Array.Empty<EditorPropertySnapshot>();
    }

    internal static bool TrySetValue(PlacedObject target, string key, EditorPropertyValue value)
    {
        if (target == null || string.IsNullOrEmpty(key)) return false;

        for (int i = 0; i < adapters.Count; i++)
        {
            IObjectInspectorAdapter adapter = adapters[i];
            try
            {
                if (adapter.CanInspect(target))
                    return adapter.TrySetValue(target, key, value);
            }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("DevTool inspector mutation failed: " + error.Message);
                return false;
            }
        }

        return false;
    }
}
