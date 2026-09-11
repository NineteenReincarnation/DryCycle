using System;
using System.Collections.Generic;
using UnityEngine;

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

/// <summary>
/// Public extension point for objects that want a first-class DevTool inspector. Third-party
/// mods may implement this interface or use <see cref="DevToolObjectApi.RegisterInspector{TData}"/>.
/// DryCycle never searches for or calls a third-party framework to obtain these adapters.
/// </summary>
public interface IObjectInspectorAdapter
{
    bool CanInspect(PlacedObject target);
    IReadOnlyList<EditorPropertySnapshot> Capture(PlacedObject target);
    bool TrySetValue(PlacedObject target, string key, EditorPropertyValue value);
}

public static class ObjectInspectorRegistry
{
    private sealed class Entry
    {
        internal IObjectInspectorAdapter Adapter;
        internal int Priority;
        internal long Order;
    }

    private static readonly List<Entry> adapters = new();
    private static long registrationOrder;

    public static void Register(IObjectInspectorAdapter adapter, int priority = 0)
    {
        if (adapter == null) throw new ArgumentNullException(nameof(adapter));

        for (int i = 0; i < adapters.Count; i++)
        {
            if (ReferenceEquals(adapters[i].Adapter, adapter)) return;
        }

        adapters.Add(new Entry
        {
            Adapter = adapter,
            Priority = priority,
            Order = registrationOrder++
        });
        adapters.Sort((a, b) =>
        {
            int priorityCompare = b.Priority.CompareTo(a.Priority);
            return priorityCompare != 0 ? priorityCompare : b.Order.CompareTo(a.Order);
        });
    }

    public static bool Unregister(IObjectInspectorAdapter adapter)
    {
        if (adapter == null) return false;
        for (int i = adapters.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(adapters[i].Adapter, adapter)) continue;
            adapters.RemoveAt(i);
            return true;
        }
        return false;
    }

    internal static EditorPropertySnapshot[] Capture(PlacedObject target)
    {
        if (target == null) return Array.Empty<EditorPropertySnapshot>();

        for (int i = 0; i < adapters.Count; i++)
        {
            IObjectInspectorAdapter adapter = adapters[i].Adapter;
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
            IObjectInspectorAdapter adapter = adapters[i].Adapter;
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

/// <summary>
/// Strongly typed, framework-owned inspector definition. A mod can opt into the native
/// DevTool experience by registering getters/setters for its own PlacedObject.Data type.
/// This creates a dependency from that mod to DevTool, never the reverse.
/// </summary>
public sealed class ObjectInspectorDefinition<TData> : IObjectInspectorAdapter
    where TData : PlacedObject.Data
{
    private abstract class Binding
    {
        protected Binding(string key, string displayName, string group, string source)
        {
            Key = key;
            DisplayName = string.IsNullOrEmpty(displayName) ? key : displayName;
            Group = string.IsNullOrEmpty(group) ? "Properties" : group;
            Source = source ?? string.Empty;
        }

        internal string Key { get; }
        internal string DisplayName { get; }
        internal string Group { get; }
        internal string Source { get; }
        internal abstract EditorPropertySnapshot Capture(TData data);
        internal abstract bool Set(TData data, EditorPropertyValue value);
    }

    private sealed class ReadOnlyBinding : Binding
    {
        private readonly Func<TData, string> getter;

        internal ReadOnlyBinding(string key, string displayName, string group, string source, Func<TData, string> getter)
            : base(key, displayName, group, source) => this.getter = getter;

        internal override EditorPropertySnapshot Capture(TData data) => new()
        {
            Key = Key,
            DisplayName = DisplayName,
            Group = Group,
            Source = Source,
            Kind = EditorPropertyKind.ReadOnly,
            StringValue = getter(data) ?? string.Empty
        };

        internal override bool Set(TData data, EditorPropertyValue value) => false;
    }

    private sealed class FloatBinding : Binding
    {
        private readonly Func<TData, float> getter;
        private readonly Action<TData, float> setter;
        private readonly float min;
        private readonly float max;
        private readonly float step;
        private readonly bool hasRange;

        internal FloatBinding(string key, string displayName, string group, string source,
            Func<TData, float> getter, Action<TData, float> setter, float min, float max, float step, bool hasRange)
            : base(key, displayName, group, source)
        {
            this.getter = getter;
            this.setter = setter;
            this.min = min;
            this.max = max;
            this.step = step;
            this.hasRange = hasRange;
        }

        internal override EditorPropertySnapshot Capture(TData data) => new()
        {
            Key = Key,
            DisplayName = DisplayName,
            Group = Group,
            Source = Source,
            Kind = EditorPropertyKind.Float,
            X = getter(data),
            Min = min,
            Max = max,
            Step = step,
            HasRange = hasRange
        };

        internal override bool Set(TData data, EditorPropertyValue value)
        {
            if (setter == null || value.Kind != EditorPropertyKind.Float) return false;
            float next = hasRange ? Mathf.Clamp(value.X, min, max) : value.X;
            setter(data, next);
            return true;
        }
    }

    private sealed class IntegerBinding : Binding
    {
        private readonly Func<TData, int> getter;
        private readonly Action<TData, int> setter;
        private readonly int min;
        private readonly int max;
        private readonly int step;
        private readonly bool hasRange;

        internal IntegerBinding(string key, string displayName, string group, string source,
            Func<TData, int> getter, Action<TData, int> setter, int min, int max, int step, bool hasRange)
            : base(key, displayName, group, source)
        {
            this.getter = getter;
            this.setter = setter;
            this.min = min;
            this.max = max;
            this.step = Math.Max(1, step);
            this.hasRange = hasRange;
        }

        internal override EditorPropertySnapshot Capture(TData data) => new()
        {
            Key = Key,
            DisplayName = DisplayName,
            Group = Group,
            Source = Source,
            Kind = EditorPropertyKind.Integer,
            IntegerValue = getter(data),
            Min = min,
            Max = max,
            Step = step,
            HasRange = hasRange
        };

        internal override bool Set(TData data, EditorPropertyValue value)
        {
            if (setter == null || value.Kind != EditorPropertyKind.Integer) return false;
            int next = hasRange ? Mathf.Clamp(value.Integer, min, max) : value.Integer;
            setter(data, next);
            return true;
        }
    }

    private sealed class BooleanBinding : Binding
    {
        private readonly Func<TData, bool> getter;
        private readonly Action<TData, bool> setter;

        internal BooleanBinding(string key, string displayName, string group, string source,
            Func<TData, bool> getter, Action<TData, bool> setter)
            : base(key, displayName, group, source)
        {
            this.getter = getter;
            this.setter = setter;
        }

        internal override EditorPropertySnapshot Capture(TData data) => new()
        {
            Key = Key,
            DisplayName = DisplayName,
            Group = Group,
            Source = Source,
            Kind = EditorPropertyKind.Boolean,
            BooleanValue = getter(data)
        };

        internal override bool Set(TData data, EditorPropertyValue value)
        {
            if (setter == null || value.Kind != EditorPropertyKind.Boolean) return false;
            setter(data, value.Boolean);
            return true;
        }
    }

    private sealed class StringBinding : Binding
    {
        private readonly Func<TData, string> getter;
        private readonly Action<TData, string> setter;

        internal StringBinding(string key, string displayName, string group, string source,
            Func<TData, string> getter, Action<TData, string> setter)
            : base(key, displayName, group, source)
        {
            this.getter = getter;
            this.setter = setter;
        }

        internal override EditorPropertySnapshot Capture(TData data) => new()
        {
            Key = Key,
            DisplayName = DisplayName,
            Group = Group,
            Source = Source,
            Kind = EditorPropertyKind.String,
            StringValue = getter(data) ?? string.Empty
        };

        internal override bool Set(TData data, EditorPropertyValue value)
        {
            if (setter == null || value.Kind != EditorPropertyKind.String) return false;
            setter(data, value.Text ?? string.Empty);
            return true;
        }
    }

    private sealed class Vector2Binding : Binding
    {
        private readonly Func<TData, Vector2> getter;
        private readonly Action<TData, Vector2> setter;

        internal Vector2Binding(string key, string displayName, string group, string source,
            Func<TData, Vector2> getter, Action<TData, Vector2> setter)
            : base(key, displayName, group, source)
        {
            this.getter = getter;
            this.setter = setter;
        }

        internal override EditorPropertySnapshot Capture(TData data)
        {
            Vector2 value = getter(data);
            return new EditorPropertySnapshot
            {
                Key = Key,
                DisplayName = DisplayName,
                Group = Group,
                Source = Source,
                Kind = EditorPropertyKind.Vector2,
                X = value.x,
                Y = value.y
            };
        }

        internal override bool Set(TData data, EditorPropertyValue value)
        {
            if (setter == null || value.Kind != EditorPropertyKind.Vector2) return false;
            setter(data, new Vector2(value.X, value.Y));
            return true;
        }
    }

    private sealed class ColorBinding : Binding
    {
        private readonly Func<TData, Color> getter;
        private readonly Action<TData, Color> setter;

        internal ColorBinding(string key, string displayName, string group, string source,
            Func<TData, Color> getter, Action<TData, Color> setter)
            : base(key, displayName, group, source)
        {
            this.getter = getter;
            this.setter = setter;
        }

        internal override EditorPropertySnapshot Capture(TData data)
        {
            Color value = getter(data);
            return new EditorPropertySnapshot
            {
                Key = Key,
                DisplayName = DisplayName,
                Group = Group,
                Source = Source,
                Kind = EditorPropertyKind.Color,
                X = value.r,
                Y = value.g,
                Z = value.b,
                W = value.a
            };
        }

        internal override bool Set(TData data, EditorPropertyValue value)
        {
            if (setter == null || value.Kind != EditorPropertyKind.Color) return false;
            setter(data, new Color(value.X, value.Y, value.Z, value.W));
            return true;
        }
    }

    private sealed class EnumBinding<TEnum> : Binding where TEnum : struct, Enum
    {
        private readonly Func<TData, TEnum> getter;
        private readonly Action<TData, TEnum> setter;
        private readonly string[] names = global::System.Enum.GetNames(typeof(TEnum));

        internal EnumBinding(string key, string displayName, string group, string source,
            Func<TData, TEnum> getter, Action<TData, TEnum> setter)
            : base(key, displayName, group, source)
        {
            this.getter = getter;
            this.setter = setter;
        }

        internal override EditorPropertySnapshot Capture(TData data)
        {
            string current = getter(data).ToString();
            int index = Array.IndexOf(names, current);
            return new EditorPropertySnapshot
            {
                Key = Key,
                DisplayName = DisplayName,
                Group = Group,
                Source = Source,
                Kind = EditorPropertyKind.Enum,
                IntegerValue = Math.Max(0, index),
                StringValue = current,
                Options = (string[])names.Clone()
            };
        }

        internal override bool Set(TData data, EditorPropertyValue value)
        {
            if (setter == null || value.Kind != EditorPropertyKind.Enum) return false;
            if (value.Integer < 0 || value.Integer >= names.Length) return false;
            if (!global::System.Enum.TryParse(names[value.Integer], out TEnum parsed)) return false;
            setter(data, parsed);
            return true;
        }
    }

    private readonly List<Binding> bindings = new();
    private readonly Dictionary<string, Binding> byKey = new(StringComparer.Ordinal);
    private readonly string source;

    public ObjectInspectorDefinition(string source = null)
    {
        this.source = source ?? typeof(TData).Assembly.GetName().Name ?? string.Empty;
    }

    public ObjectInspectorDefinition<TData> ReadOnly(string key, string displayName, Func<TData, string> getter, string group = "Properties")
        => Add(new ReadOnlyBinding(key, displayName, group, source, getter ?? throw new ArgumentNullException(nameof(getter))));

    public ObjectInspectorDefinition<TData> Float(string key, string displayName, Func<TData, float> getter,
        Action<TData, float> setter, float? min = null, float? max = null, float step = 0.1f, string group = "Properties")
        => Add(new FloatBinding(key, displayName, group, source, getter ?? throw new ArgumentNullException(nameof(getter)), setter,
            min ?? 0f, max ?? 0f, step, min.HasValue && max.HasValue));

    public ObjectInspectorDefinition<TData> Integer(string key, string displayName, Func<TData, int> getter,
        Action<TData, int> setter, int? min = null, int? max = null, int step = 1, string group = "Properties")
        => Add(new IntegerBinding(key, displayName, group, source, getter ?? throw new ArgumentNullException(nameof(getter)), setter,
            min ?? 0, max ?? 0, step, min.HasValue && max.HasValue));

    public ObjectInspectorDefinition<TData> Boolean(string key, string displayName, Func<TData, bool> getter,
        Action<TData, bool> setter, string group = "Properties")
        => Add(new BooleanBinding(key, displayName, group, source, getter ?? throw new ArgumentNullException(nameof(getter)), setter));

    public ObjectInspectorDefinition<TData> String(string key, string displayName, Func<TData, string> getter,
        Action<TData, string> setter, string group = "Properties")
        => Add(new StringBinding(key, displayName, group, source, getter ?? throw new ArgumentNullException(nameof(getter)), setter));

    public ObjectInspectorDefinition<TData> Vector2(string key, string displayName, Func<TData, Vector2> getter,
        Action<TData, Vector2> setter, string group = "Properties")
        => Add(new Vector2Binding(key, displayName, group, source, getter ?? throw new ArgumentNullException(nameof(getter)), setter));

    public ObjectInspectorDefinition<TData> Color(string key, string displayName, Func<TData, Color> getter,
        Action<TData, Color> setter, string group = "Properties")
        => Add(new ColorBinding(key, displayName, group, source, getter ?? throw new ArgumentNullException(nameof(getter)), setter));

    public ObjectInspectorDefinition<TData> Enum<TEnum>(string key, string displayName, Func<TData, TEnum> getter,
        Action<TData, TEnum> setter, string group = "Properties") where TEnum : struct, Enum
        => Add(new EnumBinding<TEnum>(key, displayName, group, source, getter ?? throw new ArgumentNullException(nameof(getter)), setter));

    public bool CanInspect(PlacedObject target) => target?.data is TData;

    public IReadOnlyList<EditorPropertySnapshot> Capture(PlacedObject target)
    {
        if (target?.data is not TData data) return Array.Empty<EditorPropertySnapshot>();
        EditorPropertySnapshot[] result = new EditorPropertySnapshot[bindings.Count];
        for (int i = 0; i < bindings.Count; i++) result[i] = bindings[i].Capture(data);
        return result;
    }

    public bool TrySetValue(PlacedObject target, string key, EditorPropertyValue value)
    {
        if (target?.data is not TData data || string.IsNullOrEmpty(key) || !byKey.TryGetValue(key, out Binding binding))
            return false;
        return binding.Set(data, value);
    }

    private ObjectInspectorDefinition<TData> Add(Binding binding)
    {
        if (binding == null) throw new ArgumentNullException(nameof(binding));
        if (string.IsNullOrWhiteSpace(binding.Key)) throw new ArgumentException("Inspector property key cannot be empty.");
        if (byKey.ContainsKey(binding.Key)) throw new ArgumentException("Duplicate inspector property key: " + binding.Key);
        bindings.Add(binding);
        byKey[binding.Key] = binding;
        return this;
    }
}

/// <summary>
/// Stable public facade intended for optional integration by other mods. Registering through
/// this API is never required for basic PlacedObject compatibility; it only upgrades the
/// object to a native, strongly typed Inspector experience.
/// </summary>
public static class DevToolObjectApi
{
    public static IObjectInspectorAdapter RegisterInspector<TData>(
        Action<ObjectInspectorDefinition<TData>> configure,
        int priority = 100,
        string source = null)
        where TData : PlacedObject.Data
    {
        if (configure == null) throw new ArgumentNullException(nameof(configure));
        ObjectInspectorDefinition<TData> definition = new(source);
        configure(definition);
        ObjectInspectorRegistry.Register(definition, priority);
        return definition;
    }

    public static void RegisterObjectDescriptor(ObjectDescriptor descriptor)
    {
        ObjectCatalog.RegisterDescriptor(descriptor);
    }
}

internal sealed class SafeDataFallbackInspector : IObjectInspectorAdapter
{
    internal static readonly SafeDataFallbackInspector Instance = new();

    public bool CanInspect(PlacedObject target) => target != null;

    public IReadOnlyList<EditorPropertySnapshot> Capture(PlacedObject target)
    {
        string serialized;
        try { serialized = target?.data?.ToString() ?? string.Empty; }
        catch { serialized = "<serialization failed>"; }

        return new[]
        {
            new EditorPropertySnapshot
            {
                Key = "__dataType",
                DisplayName = "Data Type",
                Group = "Compatibility",
                Kind = EditorPropertyKind.ReadOnly,
                StringValue = target?.data?.GetType().FullName ?? "<none>"
            },
            new EditorPropertySnapshot
            {
                Key = "__serialized",
                DisplayName = "Serialized Data",
                Group = "Compatibility",
                Kind = EditorPropertyKind.ReadOnly,
                StringValue = serialized
            }
        };
    }

    public bool TrySetValue(PlacedObject target, string key, EditorPropertyValue value) => false;
}

internal static class BuiltinInspectorAdapters
{
    private static bool enabled;

    internal static void Enable()
    {
        if (enabled) return;
        ObjectInspectorRegistry.Register(SafeDataFallbackInspector.Instance, int.MinValue);
        enabled = true;
    }
}
