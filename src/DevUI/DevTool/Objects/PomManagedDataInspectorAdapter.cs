using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Objects;

/// <summary>
/// Native ImGui-facing inspector adapter for POM ManagedData.
///
/// RegionKit uses POM as a hard dependency and a large share of its placed objects are backed by
/// ManagedData/ManagedField definitions. Instead of mirroring every generated DevInterface panel,
/// this adapter reads the same ManagedField metadata and drives ManagedData.GetValue/SetValue.
/// Unknown/custom ManagedField subclasses remain editable through their own ToString/FromString
/// serialization contract, so custom POM fields do not silently disappear from the rebuilt UI.
///
/// No compile-time POM reference is taken; POM remains an optional runtime dependency for DryCycle.
/// </summary>
public sealed class PomManagedDataInspectorAdapter : IObjectInspectorAdapter
{
    private const string ManagedDataTypeName = "Pom.Pom+ManagedData";
    private const string ManagedFieldTypeName = "Pom.Pom+ManagedField";
    private const string FloatFieldTypeName = "Pom.Pom+FloatField";
    private const string IntegerFieldTypeName = "Pom.Pom+IntegerField";
    private const string BooleanFieldTypeName = "Pom.Pom+BooleanField";
    private const string StringFieldTypeName = "Pom.Pom+StringField";
    private const string Vector2FieldTypeName = "Pom.Pom+Vector2Field";
    private const string ColorFieldTypeName = "Pom.Pom+ColorField";
    private const string EnumFieldDefinitionName = "Pom.Pom+EnumField`1";

    public static readonly PomManagedDataInspectorAdapter Instance = new();
    private static bool registered;

    private PomManagedDataInspectorAdapter() { }

    public static void EnsureRegistered()
    {
        if (registered) return;
        registered = true;
        ObjectInspectorRegistry.Register(Instance, 1000);
    }

    public static bool IsManagedData(object data) =>
        data != null && IsTypeOrBase(data.GetType(), ManagedDataTypeName);

    public bool CanInspect(PlacedObject target) => IsManagedData(target?.data);

    public IReadOnlyList<EditorPropertySnapshot> Capture(PlacedObject target)
    {
        object data = target?.data;
        if (!IsManagedData(data)) return Array.Empty<EditorPropertySnapshot>();

        Array fields = ReadMember(data, "fields") as Array;
        if (fields == null || fields.Length == 0) return Array.Empty<EditorPropertySnapshot>();

        List<EditorPropertySnapshot> result = new(fields.Length + 1);
        if (ReadMember(data, "panelPos") is Vector2 panelPos)
        {
            result.Add(new EditorPropertySnapshot
            {
                Key = "pom.__panelPos",
                DisplayName = "Legacy Panel Position",
                Group = "POM Layout",
                Source = "POM ManagedData · preserved for serialization compatibility",
                Kind = EditorPropertyKind.Vector2,
                X = panelPos.x,
                Y = panelPos.y
            });
        }

        for (int i = 0; i < fields.Length; i++)
        {
            object field = fields.GetValue(i);
            if (field == null || !IsTypeOrBase(field.GetType(), ManagedFieldTypeName)) continue;

            string key = ReadMember(field, "key") as string;
            if (string.IsNullOrEmpty(key)) continue;
            object value = GetManagedValue(data, key);
            result.Add(CaptureField(field, key, value, i));
        }

        return result;
    }

    public bool TrySetValue(PlacedObject target, string key, EditorPropertyValue value)
    {
        object data = target?.data;
        if (!IsManagedData(data) || string.IsNullOrEmpty(key)) return false;

        if (key == "pom.__panelPos")
        {
            if (value.Kind != EditorPropertyKind.Vector2) return false;
            return WriteMember(data, "panelPos", new Vector2(value.X, value.Y));
        }

        const string prefix = "pom.field.";
        if (!key.StartsWith(prefix, StringComparison.Ordinal)) return false;
        string managedKey = DecodeKey(key.Substring(prefix.Length));
        object field = FindManagedField(data, managedKey);
        if (field == null) return false;

        object current = GetManagedValue(data, managedKey);
        object next;
        Type fieldType = field.GetType();

        if (IsTypeOrBase(fieldType, FloatFieldTypeName))
        {
            if (value.Kind != EditorPropertyKind.Float) return false;
            float min = ReadFloat(field, "min", float.NegativeInfinity);
            float max = ReadFloat(field, "max", float.PositiveInfinity);
            next = Mathf.Clamp(value.X, min, max);
        }
        else if (IsTypeOrBase(fieldType, IntegerFieldTypeName))
        {
            if (value.Kind != EditorPropertyKind.Integer) return false;
            int min = ReadInt(field, "min", int.MinValue);
            int max = ReadInt(field, "max", int.MaxValue);
            next = Mathf.Clamp(value.Integer, min, max);
        }
        else if (IsTypeOrBase(fieldType, BooleanFieldTypeName))
        {
            if (value.Kind != EditorPropertyKind.Boolean) return false;
            next = value.Boolean;
        }
        else if (IsTypeOrBase(fieldType, StringFieldTypeName))
        {
            if (value.Kind != EditorPropertyKind.String) return false;
            next = ParseFieldText(field, value.Text ?? string.Empty, out bool parsedString)
                ?? value.Text ?? string.Empty;
            if (!parsedString) next = value.Text ?? string.Empty;
        }
        else if (IsTypeOrBase(fieldType, Vector2FieldTypeName))
        {
            if (value.Kind != EditorPropertyKind.Vector2) return false;
            next = new Vector2(value.X, value.Y);
        }
        else if (IsTypeOrBase(fieldType, ColorFieldTypeName))
        {
            if (value.Kind != EditorPropertyKind.Color) return false;
            next = new Color(value.X, value.Y, value.Z, value.W);
        }
        else if (TryGetEnumOptions(fieldType, field, out object[] enumValues, out _))
        {
            if (value.Kind != EditorPropertyKind.Enum || value.Integer < 0 || value.Integer >= enumValues.Length)
                return false;
            next = enumValues[value.Integer];
        }
        else
        {
            if (value.Kind != EditorPropertyKind.String) return false;
            next = ParseFieldText(field, value.Text ?? string.Empty, out bool parsed);
            if (!parsed) return false;
        }

        if (next == null && current != null) return false;
        return SetManagedValue(data, managedKey, next);
    }

    private static EditorPropertySnapshot CaptureField(object field, string key, object value, int index)
    {
        Type type = field.GetType();
        string display = ReadDisplayName(field, key);
        string source = "POM ManagedField · " + (type.Name ?? "ManagedField");
        string propertyKey = "pom.field." + EncodeKey(key);

        if (IsTypeOrBase(type, FloatFieldTypeName))
        {
            float current = ConvertFloat(value, 0f);
            float min = ReadFloat(field, "min", current - 1f);
            float max = ReadFloat(field, "max", current + 1f);
            float increment = Math.Abs(ReadFloat(field, "increment", 0.1f));
            return new EditorPropertySnapshot
            {
                Key = propertyKey,
                DisplayName = display,
                Group = "POM Managed Fields",
                Source = source,
                Kind = EditorPropertyKind.Float,
                X = current,
                Min = min,
                Max = max,
                Step = increment <= 0f ? 0.1f : increment,
                HasRange = IsFinite(min) && IsFinite(max) && max >= min
            };
        }

        if (IsTypeOrBase(type, IntegerFieldTypeName))
        {
            int current = ConvertInt(value, 0);
            int min = ReadInt(field, "min", current - 100);
            int max = ReadInt(field, "max", current + 100);
            int increment = Math.Abs(ReadInt(field, "increment", 1));
            return new EditorPropertySnapshot
            {
                Key = propertyKey,
                DisplayName = display,
                Group = "POM Managed Fields",
                Source = source,
                Kind = EditorPropertyKind.Integer,
                IntegerValue = current,
                Min = min,
                Max = max,
                Step = Math.Max(1, increment),
                HasRange = max >= min
            };
        }

        if (IsTypeOrBase(type, BooleanFieldTypeName))
        {
            return new EditorPropertySnapshot
            {
                Key = propertyKey,
                DisplayName = display,
                Group = "POM Managed Fields",
                Source = source,
                Kind = EditorPropertyKind.Boolean,
                BooleanValue = value is bool boolean && boolean
            };
        }

        if (IsTypeOrBase(type, StringFieldTypeName))
        {
            return new EditorPropertySnapshot
            {
                Key = propertyKey,
                DisplayName = display,
                Group = "POM Managed Fields",
                Source = source,
                Kind = EditorPropertyKind.String,
                StringValue = value?.ToString() ?? string.Empty
            };
        }

        if (IsTypeOrBase(type, Vector2FieldTypeName) && value is Vector2 vector)
        {
            return new EditorPropertySnapshot
            {
                Key = propertyKey,
                DisplayName = display,
                Group = "POM Managed Fields",
                Source = source,
                Kind = EditorPropertyKind.Vector2,
                X = vector.x,
                Y = vector.y
            };
        }

        if (IsTypeOrBase(type, ColorFieldTypeName) && value is Color color)
        {
            return new EditorPropertySnapshot
            {
                Key = propertyKey,
                DisplayName = display,
                Group = "POM Managed Fields",
                Source = source,
                Kind = EditorPropertyKind.Color,
                X = color.r,
                Y = color.g,
                Z = color.b,
                W = color.a
            };
        }

        if (TryGetEnumOptions(type, field, out object[] values, out string[] names))
        {
            int selected = -1;
            for (int i = 0; i < values.Length; i++)
            {
                if (Equals(values[i], value) || string.Equals(values[i]?.ToString(), value?.ToString(), StringComparison.Ordinal))
                {
                    selected = i;
                    break;
                }
            }
            return new EditorPropertySnapshot
            {
                Key = propertyKey,
                DisplayName = display,
                Group = "POM Managed Fields",
                Source = source,
                Kind = EditorPropertyKind.Enum,
                IntegerValue = selected,
                StringValue = value?.ToString() ?? string.Empty,
                Options = names
            };
        }

        return new EditorPropertySnapshot
        {
            Key = propertyKey,
            DisplayName = display,
            Group = "POM Managed Fields · Serialized",
            Source = source + " · custom field uses its own POM ToString/FromString contract",
            Kind = EditorPropertyKind.String,
            StringValue = SerializeFieldValue(field, value)
        };
    }

    private static object FindManagedField(object data, string key)
    {
        Array fields = ReadMember(data, "fields") as Array;
        if (fields == null) return null;
        for (int i = 0; i < fields.Length; i++)
        {
            object field = fields.GetValue(i);
            if (field == null) continue;
            if (string.Equals(ReadMember(field, "key") as string, key, StringComparison.Ordinal)) return field;
        }
        return null;
    }

    private static object GetManagedValue(object data, string key)
    {
        try
        {
            MethodInfo method = FindGenericMethod(data?.GetType(), "GetValue", 1, 1);
            if (method == null) return null;
            return method.MakeGenericMethod(typeof(object)).Invoke(data, new object[] { key });
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool POM GetValue failed for '" + key + "': " + error.Message);
            return null;
        }
    }

    private static bool SetManagedValue(object data, string key, object value)
    {
        if (data == null || value == null) return false;
        try
        {
            MethodInfo method = FindGenericMethod(data.GetType(), "SetValue", 1, 2);
            if (method == null) return false;
            method.MakeGenericMethod(value.GetType()).Invoke(data, new[] { (object)key, value });
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool POM SetValue failed for '" + key + "': " + error.Message);
            return false;
        }
    }

    private static MethodInfo FindGenericMethod(Type type, string name, int genericArguments, int parameters)
    {
        Type current = type;
        while (current != null)
        {
            MethodInfo[] methods = current.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (!string.Equals(method.Name, name, StringComparison.Ordinal) || !method.IsGenericMethodDefinition) continue;
                if (method.GetGenericArguments().Length != genericArguments || method.GetParameters().Length != parameters) continue;
                return method;
            }
            current = current.BaseType;
        }
        return null;
    }

    private static bool TryGetEnumOptions(Type fieldType, object field, out object[] values, out string[] names)
    {
        values = Array.Empty<object>();
        names = Array.Empty<string>();
        Type enumFieldType = FindGenericBase(fieldType, EnumFieldDefinitionName);
        if (enumFieldType == null) return false;
        Type enumType = enumFieldType.GetGenericArguments()[0];
        if (!enumType.IsEnum) return false;

        object possibleRaw = ReadMember(field, "_possibleValues");
        Array possible = possibleRaw as Array;
        if (possible == null || possible.Length == 0)
            possible = Enum.GetValues(enumType);

        values = new object[possible.Length];
        names = new string[possible.Length];
        for (int i = 0; i < possible.Length; i++)
        {
            object entry = possible.GetValue(i);
            values[i] = entry;
            names[i] = entry?.ToString() ?? string.Empty;
        }
        return true;
    }

    private static Type FindGenericBase(Type type, string genericDefinitionName)
    {
        Type current = type;
        while (current != null)
        {
            if (current.IsGenericType && string.Equals(current.GetGenericTypeDefinition().FullName, genericDefinitionName, StringComparison.Ordinal))
                return current;
            current = current.BaseType;
        }
        return null;
    }

    private static string SerializeFieldValue(object field, object value)
    {
        if (field == null) return value?.ToString() ?? string.Empty;
        try
        {
            MethodInfo method = field.GetType().GetMethod("ToString",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(object) },
                null);
            return method?.Invoke(field, new[] { value })?.ToString() ?? value?.ToString() ?? string.Empty;
        }
        catch
        {
            return value?.ToString() ?? string.Empty;
        }
    }

    private static object ParseFieldText(object field, string text, out bool parsed)
    {
        parsed = false;
        try
        {
            MethodInfo method = field?.GetType().GetMethod("FromString",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(string) },
                null);
            if (method == null) return null;
            object value = method.Invoke(field, new object[] { text ?? string.Empty });
            parsed = value != null;
            return value;
        }
        catch (TargetInvocationException error)
        {
            Plugin.Logger?.LogWarning("DevTool POM field parse rejected value: " + (error.InnerException?.Message ?? error.Message));
            return null;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool POM field parse failed: " + error.Message);
            return null;
        }
    }

    private static string ReadDisplayName(object field, string fallback)
    {
        string display = ReadMember(field, "displayName") as string;
        if (!string.IsNullOrWhiteSpace(display)) return display.Trim().TrimEnd(':').Trim();
        return string.IsNullOrWhiteSpace(fallback) ? "Field" : fallback;
    }

    private static object ReadMember(object instance, string name)
    {
        if (instance == null || string.IsNullOrEmpty(name)) return null;
        Type current = instance.GetType();
        while (current != null)
        {
            FieldInfo field = current.GetField(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null) return field.GetValue(instance);
            PropertyInfo property = current.GetProperty(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property != null && property.CanRead && property.GetIndexParameters().Length == 0)
                return property.GetValue(instance, null);
            current = current.BaseType;
        }
        return null;
    }

    private static bool WriteMember(object instance, string name, object value)
    {
        if (instance == null || string.IsNullOrEmpty(name)) return false;
        Type current = instance.GetType();
        while (current != null)
        {
            FieldInfo field = current.GetField(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null && !field.IsInitOnly && (value == null || field.FieldType.IsInstanceOfType(value)))
            {
                field.SetValue(instance, value);
                return true;
            }
            PropertyInfo property = current.GetProperty(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property != null && property.CanWrite && property.GetIndexParameters().Length == 0 &&
                (value == null || property.PropertyType.IsInstanceOfType(value)))
            {
                property.SetValue(instance, value, null);
                return true;
            }
            current = current.BaseType;
        }
        return false;
    }

    private static bool IsTypeOrBase(Type type, string fullName)
    {
        Type current = type;
        while (current != null)
        {
            if (string.Equals(current.FullName, fullName, StringComparison.Ordinal)) return true;
            current = current.BaseType;
        }
        return false;
    }

    private static float ReadFloat(object instance, string name, float fallback)
    {
        object value = ReadMember(instance, name);
        return value == null ? fallback : ConvertFloat(value, fallback);
    }

    private static int ReadInt(object instance, string name, int fallback)
    {
        object value = ReadMember(instance, name);
        return value == null ? fallback : ConvertInt(value, fallback);
    }

    private static float ConvertFloat(object value, float fallback)
    {
        try { return Convert.ToSingle(value, CultureInfo.InvariantCulture); }
        catch { return fallback; }
    }

    private static int ConvertInt(object value, int fallback)
    {
        try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
        catch { return fallback; }
    }

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private static string EncodeKey(string key) => Uri.EscapeDataString(key ?? string.Empty);
    private static string DecodeKey(string key) => Uri.UnescapeDataString(key ?? string.Empty);
}
