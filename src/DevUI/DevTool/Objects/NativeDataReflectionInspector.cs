using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Objects;

/// <summary>
/// Headless fallback inspector for PlacedObject.Data. It exposes authored public model members
/// directly instead of requiring a PlacedObjectRepresentation/ObjectsPage control tree.
///
/// Strongly typed adapters registered by DryCycle or third-party extensions still win because this
/// adapter is registered at a low priority. Unsupported member types remain visible through the
/// serialized-data row, while known scalar/vector/color/enum members are editable natively.
/// </summary>
internal sealed class NativeDataReflectionInspector : IObjectInspectorAdapter
{
    internal static readonly NativeDataReflectionInspector Instance = new();

    private sealed class Schema
    {
        internal readonly MemberBinding[] Members;
        internal readonly Dictionary<string, MemberBinding> ByKey;

        internal Schema(MemberBinding[] members)
        {
            Members = members ?? Array.Empty<MemberBinding>();
            ByKey = new Dictionary<string, MemberBinding>(StringComparer.Ordinal);
            for (int i = 0; i < Members.Length; i++)
                ByKey[Members[i].Key] = Members[i];
        }
    }

    private sealed class MemberBinding
    {
        internal string Key;
        internal string DisplayName;
        internal string Group;
        internal Type ValueType;
        internal FieldInfo Field;
        internal PropertyInfo Property;
        internal int ArrayIndex = -1;
        internal bool Writable;
        internal EditorPropertyKind Kind;
        internal string[] Options = Array.Empty<string>();

        internal object Read(object target)
        {
            if (ArrayIndex >= 0)
            {
                if (Field?.GetValue(target) is Vector2[] vectors &&
                    ArrayIndex < vectors.Length)
                    return vectors[ArrayIndex];
                return default(Vector2);
            }

            if (Field != null) return Field.GetValue(target);
            return Property?.GetValue(target, null);
        }

        internal void Write(object target, object value)
        {
            if (!Writable) return;

            if (ArrayIndex >= 0)
            {
                if (value is Vector2 vector &&
                    Field?.GetValue(target) is Vector2[] vectors &&
                    ArrayIndex < vectors.Length)
                    vectors[ArrayIndex] = vector;
                return;
            }

            if (Field != null) Field.SetValue(target, value);
            else Property?.SetValue(target, value, null);
        }
    }

    private static readonly ConcurrentDictionary<Type, Schema> Schemas = new();

    public bool CanInspect(PlacedObject target) => target?.data != null;

    public IReadOnlyList<EditorPropertySnapshot> Capture(PlacedObject target)
    {
        PlacedObject.Data data = target?.data;
        if (data == null) return Array.Empty<EditorPropertySnapshot>();

        Schema schema = Schemas.GetOrAdd(data.GetType(), BuildSchema);
        List<EditorPropertySnapshot> result = new(schema.Members.Length + 2)
        {
            new EditorPropertySnapshot
            {
                Key = "__nativeDataType",
                DisplayName = "Data Type",
                Group = "Native Data",
                Source = "Rain World model",
                Kind = EditorPropertyKind.ReadOnly,
                StringValue = data.GetType().FullName ?? data.GetType().Name
            }
        };

        for (int i = 0; i < schema.Members.Length; i++)
        {
            MemberBinding binding = schema.Members[i];
            try
            {
                result.Add(CaptureMember(binding, data));
            }
            catch (Exception error)
            {
                result.Add(new EditorPropertySnapshot
                {
                    Key = binding.Key,
                    DisplayName = binding.DisplayName,
                    Group = binding.Group,
                    Source = "Rain World model",
                    Kind = EditorPropertyKind.ReadOnly,
                    StringValue = "<read failed: " + error.Message + ">"
                });
            }
        }

        string serialized;
        try { serialized = data.ToString() ?? string.Empty; }
        catch { serialized = "<serialization failed>"; }
        result.Add(new EditorPropertySnapshot
        {
            Key = "__nativeSerialized",
            DisplayName = "Serialized Data",
            Group = "Compatibility",
            Source = "Rain World model",
            Kind = EditorPropertyKind.ReadOnly,
            StringValue = serialized
        });

        return result;
    }

    public bool TrySetValue(PlacedObject target, string key, EditorPropertyValue value)
    {
        PlacedObject.Data data = target?.data;
        if (data == null || string.IsNullOrEmpty(key)) return false;

        Schema schema = Schemas.GetOrAdd(data.GetType(), BuildSchema);
        if (!schema.ByKey.TryGetValue(key, out MemberBinding binding) || !binding.Writable)
            return false;

        try
        {
            object next = ConvertValue(binding, value);
            if (next == null && binding.ValueType.IsValueType)
                return false;
            binding.Write(data, next);
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning(
                "DevTool native reflected object mutation failed for " + binding.DisplayName + ": " + error.Message);
            return false;
        }
    }

    internal static bool HasWritableMembers(PlacedObject target)
    {
        PlacedObject.Data data = target?.data;
        if (data == null) return false;
        Schema schema = Schemas.GetOrAdd(data.GetType(), BuildSchema);
        for (int i = 0; i < schema.Members.Length; i++)
            if (schema.Members[i].Writable && schema.Members[i].Kind != EditorPropertyKind.ReadOnly)
                return true;
        return false;
    }

    internal static void ClearSchemaCache() => Schemas.Clear();

    private static Schema BuildSchema(Type dataType)
    {
        List<MemberBinding> bindings = new();
        HashSet<string> names = new(StringComparer.Ordinal);

        Type current = dataType;
        while (current != null && typeof(PlacedObject.Data).IsAssignableFrom(current))
        {
            FieldInfo[] fields = current.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                if (field.IsStatic || ShouldIgnore(field.Name) || !names.Add(field.Name)) continue;

                // QuadObjectRepresentation exposes exactly three authored relative handles. Flatten
                // only this verified Rain World model contract; arbitrary arrays remain read-only.
                if (current == typeof(PlacedObject.QuadObjectData) &&
                    field.Name == "handles" &&
                    field.FieldType == typeof(Vector2[]))
                {
                    for (int handleIndex = 0; handleIndex < 3; handleIndex++)
                        bindings.Add(BuildVectorArrayBinding(current, field, handleIndex));
                    continue;
                }

                MemberBinding binding = BuildBinding(
                    current,
                    field.Name,
                    field.FieldType,
                    writable: !field.IsInitOnly && !field.IsLiteral);
                binding.Field = field;
                bindings.Add(binding);
            }

            PropertyInfo[] properties = current.GetProperties(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            for (int i = 0; i < properties.Length; i++)
            {
                PropertyInfo property = properties[i];
                if (!property.CanRead || property.GetIndexParameters().Length != 0 ||
                    ShouldIgnore(property.Name) || !names.Add(property.Name))
                    continue;

                MemberBinding binding = BuildBinding(
                    current,
                    property.Name,
                    property.PropertyType,
                    writable: property.CanWrite && property.SetMethod?.IsPublic == true);
                binding.Property = property;
                bindings.Add(binding);
            }

            if (current == typeof(PlacedObject.Data)) break;
            current = current.BaseType;
        }

        bindings.Sort((a, b) =>
        {
            int group = string.Compare(a.Group, b.Group, StringComparison.Ordinal);
            return group != 0 ? group : string.Compare(a.DisplayName, b.DisplayName, StringComparison.Ordinal);
        });
        return new Schema(bindings.ToArray());
    }

    private static MemberBinding BuildVectorArrayBinding(
        Type declaringType,
        FieldInfo field,
        int index)
    {
        return new MemberBinding
        {
            Key = "native." + (declaringType.FullName ?? declaringType.Name) + "." + field.Name + "[" + index + "]",
            DisplayName = Humanize(field.Name) + " " + (index + 1),
            Group = "Geometry",
            ValueType = typeof(Vector2),
            Field = field,
            ArrayIndex = index,
            Writable = !field.IsInitOnly && !field.IsLiteral,
            Kind = EditorPropertyKind.Vector2,
            Options = Array.Empty<string>()
        };
    }

    private static MemberBinding BuildBinding(
        Type declaringType,
        string name,
        Type valueType,
        bool writable)
    {
        EditorPropertyKind kind = ResolveKind(valueType, out string[] options);
        if (kind == EditorPropertyKind.ReadOnly)
            writable = false;

        return new MemberBinding
        {
            Key = "native." + (declaringType.FullName ?? declaringType.Name) + "." + name,
            DisplayName = Humanize(name),
            Group = ResolveGroup(name, kind),
            ValueType = valueType,
            Writable = writable,
            Kind = kind,
            Options = options ?? Array.Empty<string>()
        };
    }

    private static EditorPropertyKind ResolveKind(Type type, out string[] options)
    {
        options = Array.Empty<string>();
        if (type == typeof(float) || type == typeof(double)) return EditorPropertyKind.Float;
        if (type == typeof(int) || type == typeof(short) || type == typeof(byte)) return EditorPropertyKind.Integer;
        if (type == typeof(bool)) return EditorPropertyKind.Boolean;
        if (type == typeof(string)) return EditorPropertyKind.String;
        if (type == typeof(Vector2)) return EditorPropertyKind.Vector2;
        if (type == typeof(Color)) return EditorPropertyKind.Color;

        if (type.IsEnum)
        {
            options = Enum.GetNames(type);
            return EditorPropertyKind.Enum;
        }

        if (typeof(ExtEnumBase).IsAssignableFrom(type))
        {
            try { options = ExtEnumBase.GetNames(type); }
            catch { options = Array.Empty<string>(); }
            return options.Length > 0 ? EditorPropertyKind.Enum : EditorPropertyKind.ReadOnly;
        }

        return EditorPropertyKind.ReadOnly;
    }

    private static EditorPropertySnapshot CaptureMember(MemberBinding binding, object data)
    {
        object raw = binding.Read(data);
        EditorPropertySnapshot common = new()
        {
            Key = binding.Key,
            DisplayName = binding.DisplayName,
            Group = binding.Group,
            Source = "Rain World model",
            Kind = binding.Kind,
            Options = binding.Options
        };

        switch (binding.Kind)
        {
            case EditorPropertyKind.Float:
                return Copy(common, x: raw == null ? 0f : Convert.ToSingle(raw));
            case EditorPropertyKind.Integer:
                return Copy(common, integer: raw == null ? 0 : Convert.ToInt32(raw));
            case EditorPropertyKind.Boolean:
                return Copy(common, boolean: raw is bool flag && flag);
            case EditorPropertyKind.String:
                return Copy(common, text: raw as string ?? string.Empty);
            case EditorPropertyKind.Vector2:
            {
                Vector2 vector = raw is Vector2 value ? value : default;
                return Copy(common, x: vector.x, y: vector.y);
            }
            case EditorPropertyKind.Color:
            {
                Color color = raw is Color value ? value : default;
                return Copy(common, x: color.r, y: color.g, z: color.b, w: color.a);
            }
            case EditorPropertyKind.Enum:
            {
                string current = raw?.ToString() ?? string.Empty;
                int index = Array.IndexOf(binding.Options, current);
                return Copy(common, integer: Math.Max(0, index), text: current);
            }
            default:
                return Copy(common, text: FormatReadOnly(raw));
        }
    }

    private static object ConvertValue(MemberBinding binding, EditorPropertyValue value)
    {
        Type type = binding.ValueType;
        switch (binding.Kind)
        {
            case EditorPropertyKind.Float:
                if (value.Kind != EditorPropertyKind.Float) return null;
                if (type == typeof(double)) return (double)value.X;
                return value.X;
            case EditorPropertyKind.Integer:
                if (value.Kind != EditorPropertyKind.Integer) return null;
                if (type == typeof(short)) return (short)value.Integer;
                if (type == typeof(byte)) return (byte)Mathf.Clamp(value.Integer, byte.MinValue, byte.MaxValue);
                return value.Integer;
            case EditorPropertyKind.Boolean:
                return value.Kind == EditorPropertyKind.Boolean ? value.Boolean : null;
            case EditorPropertyKind.String:
                return value.Kind == EditorPropertyKind.String ? value.Text ?? string.Empty : null;
            case EditorPropertyKind.Vector2:
                return value.Kind == EditorPropertyKind.Vector2 ? new Vector2(value.X, value.Y) : null;
            case EditorPropertyKind.Color:
                return value.Kind == EditorPropertyKind.Color ? new Color(value.X, value.Y, value.Z, value.W) : null;
            case EditorPropertyKind.Enum:
                if (value.Kind != EditorPropertyKind.Enum ||
                    value.Integer < 0 || value.Integer >= binding.Options.Length)
                    return null;
                string selected = binding.Options[value.Integer];
                if (type.IsEnum) return Enum.Parse(type, selected);
                if (typeof(ExtEnumBase).IsAssignableFrom(type))
                    return ExtEnumBase.Parse(type, selected, ignoreCase: false);
                return null;
            default:
                return null;
        }
    }

    private static EditorPropertySnapshot Copy(
        EditorPropertySnapshot source,
        float x = 0f,
        float y = 0f,
        float z = 0f,
        float w = 0f,
        int integer = 0,
        bool boolean = false,
        string text = null)
    {
        return new EditorPropertySnapshot
        {
            Key = source.Key,
            DisplayName = source.DisplayName,
            Group = source.Group,
            Source = source.Source,
            Kind = source.Kind,
            X = x,
            Y = y,
            Z = z,
            W = w,
            IntegerValue = integer,
            BooleanValue = boolean,
            StringValue = text ?? string.Empty,
            Options = source.Options ?? Array.Empty<string>()
        };
    }

    private static bool ShouldIgnore(string name) =>
        string.Equals(name, "owner", StringComparison.Ordinal) ||
        string.Equals(name, "unrecognizedAttributes", StringComparison.Ordinal) ||
        string.Equals(name, "panelPos", StringComparison.Ordinal);

    private static string ResolveGroup(string name, EditorPropertyKind kind)
    {
        if (name.IndexOf("handle", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("offset", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("point", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("radius", StringComparison.OrdinalIgnoreCase) >= 0 ||
            string.Equals(name, "Rad", StringComparison.OrdinalIgnoreCase) ||
            name.IndexOf("angle", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("rotation", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("length", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("width", StringComparison.OrdinalIgnoreCase) >= 0)
            return "Geometry";
        if (kind == EditorPropertyKind.Boolean || kind == EditorPropertyKind.Enum)
            return "Behavior";
        return "Native Data";
    }

    private static string Humanize(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        System.Text.StringBuilder builder = new(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char current = name[i];
            if (i > 0 && char.IsUpper(current) && !char.IsUpper(name[i - 1])) builder.Append(' ');
            builder.Append(i == 0 ? char.ToUpperInvariant(current) : current);
        }
        return builder.ToString();
    }

    private static string FormatReadOnly(object value)
    {
        if (value == null) return "<null>";
        if (value is System.Collections.ICollection collection)
            return value.GetType().Name + " (" + collection.Count + ")";
        return value.ToString() ?? string.Empty;
    }
}