using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Objects;

/// <summary>
/// Native ImGui-facing inspector adapter for POM ManagedData. The adapter consumes POM's own
/// ManagedField definitions rather than its generated legacy DevInterface controls. POM remains
/// an optional runtime dependency: all interaction is reflection based.
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
    private const string ExtEnumFieldDefinitionName = "Pom.Pom+ExtEnumField`1";

    public static readonly PomManagedDataInspectorAdapter Instance = new();
    private static bool registered;

    private PomManagedDataInspectorAdapter() { }

    public static void EnsureRegistered()
    {
        if (registered) return;
        registered = true;
        ObjectInspectorRegistry.Register(Instance, 1000);
    }

    public static bool IsManagedData(object data) => data != null && IsTypeOrBase(data.GetType(), ManagedDataTypeName);
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
                Key = "pom.__panelPos", DisplayName = "Legacy Panel Position", Group = "POM Layout",
                Source = "POM ManagedData · preserved for serialization compatibility",
                Kind = EditorPropertyKind.Vector2, X = panelPos.x, Y = panelPos.y
            });
        }

        for (int i = 0; i < fields.Length; i++)
        {
            object field = fields.GetValue(i);
            if (field == null || !IsTypeOrBase(field.GetType(), ManagedFieldTypeName)) continue;
            string key = ReadMember(field, "key") as string;
            if (string.IsNullOrEmpty(key)) continue;
            result.Add(CaptureField(field, key, GetManagedValue(data, key)));
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

        Type fieldType = field.GetType();
        object next;
        if (IsTypeOrBase(fieldType, FloatFieldTypeName))
        {
            if (value.Kind != EditorPropertyKind.Float) return false;
            next = Mathf.Clamp(value.X, ReadFloat(field, "min", float.NegativeInfinity), ReadFloat(field, "max", float.PositiveInfinity));
        }
        else if (IsTypeOrBase(fieldType, IntegerFieldTypeName))
        {
            if (value.Kind != EditorPropertyKind.Integer) return false;
            next = Mathf.Clamp(value.Integer, ReadInt(field, "min", int.MinValue), ReadInt(field, "max", int.MaxValue));
        }
        else if (IsTypeOrBase(fieldType, BooleanFieldTypeName))
        {
            if (value.Kind != EditorPropertyKind.Boolean) return false;
            next = value.Boolean;
        }
        else if (IsTypeOrBase(fieldType, StringFieldTypeName))
        {
            if (value.Kind != EditorPropertyKind.String) return false;
            next = ParseFieldText(field, value.Text ?? string.Empty, out bool parsedString);
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
        else if (TryGetChoiceOptions(fieldType, field, out object[] choiceValues, out _))
        {
            if (value.Kind != EditorPropertyKind.Enum || value.Integer < 0 || value.Integer >= choiceValues.Length) return false;
            next = choiceValues[value.Integer];
        }
        else
        {
            if (value.Kind != EditorPropertyKind.String) return false;
            next = ParseFieldText(field, value.Text ?? string.Empty, out bool parsed);
            if (!parsed) return false;
        }
        return next != null && SetManagedValue(data, managedKey, next);
    }

    private static EditorPropertySnapshot CaptureField(object field, string key, object value)
    {
        Type type = field.GetType();
        string display = ReadDisplayName(field, key);
        string source = "POM ManagedField · " + (type.Name ?? "ManagedField");
        string propertyKey = "pom.field." + EncodeKey(key);

        if (IsTypeOrBase(type, FloatFieldTypeName))
        {
            float current = ConvertFloat(value, 0f), min = ReadFloat(field, "min", current - 1f), max = ReadFloat(field, "max", current + 1f);
            float increment = Math.Abs(ReadFloat(field, "increment", 0.1f));
            return new EditorPropertySnapshot { Key = propertyKey, DisplayName = display, Group = "POM Managed Fields", Source = source,
                Kind = EditorPropertyKind.Float, X = current, Min = min, Max = max, Step = increment <= 0f ? 0.1f : increment,
                HasRange = IsFinite(min) && IsFinite(max) && max >= min };
        }
        if (IsTypeOrBase(type, IntegerFieldTypeName))
        {
            int current = ConvertInt(value, 0), min = ReadInt(field, "min", current - 100), max = ReadInt(field, "max", current + 100);
            return new EditorPropertySnapshot { Key = propertyKey, DisplayName = display, Group = "POM Managed Fields", Source = source,
                Kind = EditorPropertyKind.Integer, IntegerValue = current, Min = min, Max = max, Step = 1f, HasRange = max >= min };
        }
        if (IsTypeOrBase(type, BooleanFieldTypeName))
            return new EditorPropertySnapshot { Key = propertyKey, DisplayName = display, Group = "POM Managed Fields", Source = source,
                Kind = EditorPropertyKind.Boolean, BooleanValue = value is bool boolean && boolean };
        if (IsTypeOrBase(type, StringFieldTypeName))
            return new EditorPropertySnapshot { Key = propertyKey, DisplayName = display, Group = "POM Managed Fields", Source = source,
                Kind = EditorPropertyKind.String, StringValue = value?.ToString() ?? string.Empty };
        if (IsTypeOrBase(type, Vector2FieldTypeName) && value is Vector2 vector)
            return new EditorPropertySnapshot { Key = propertyKey, DisplayName = display, Group = "POM Managed Fields", Source = source,
                Kind = EditorPropertyKind.Vector2, X = vector.x, Y = vector.y };
        if (IsTypeOrBase(type, ColorFieldTypeName) && value is Color color)
            return new EditorPropertySnapshot { Key = propertyKey, DisplayName = display, Group = "POM Managed Fields", Source = source,
                Kind = EditorPropertyKind.Color, X = color.r, Y = color.g, Z = color.b, W = color.a };

        if (TryGetChoiceOptions(type, field, out object[] values, out string[] names))
        {
            int selected = FindChoice(values, value);
            return new EditorPropertySnapshot { Key = propertyKey, DisplayName = display, Group = "POM Managed Fields", Source = source,
                Kind = EditorPropertyKind.Enum, IntegerValue = selected, StringValue = value?.ToString() ?? string.Empty, Options = names };
        }

        return new EditorPropertySnapshot
        {
            Key = propertyKey, DisplayName = display, Group = "POM Managed Fields · Serialized",
            Source = source + " · custom field uses its own POM ToString/FromString contract",
            Kind = EditorPropertyKind.String, StringValue = SerializeFieldValue(field, value)
        };
    }

    private static bool TryGetChoiceOptions(Type fieldType, object field, out object[] values, out string[] names)
    {
        if (TryGetEnumOptions(fieldType, field, out values, out names)) return true;
        return TryGetExtEnumOptions(fieldType, field, out values, out names);
    }

    private static bool TryGetEnumOptions(Type fieldType, object field, out object[] values, out string[] names)
    {
        values = Array.Empty<object>(); names = Array.Empty<string>();
        Type enumFieldType = FindGenericBase(fieldType, EnumFieldDefinitionName);
        if (enumFieldType == null) return false;
        Type enumType = enumFieldType.GetGenericArguments()[0];
        if (!enumType.IsEnum) return false;
        Array possible = ReadMember(field, "PossibleValues") as Array ?? ReadMember(field, "_possibleValues") as Array;
        if (possible == null || possible.Length == 0) possible = Enum.GetValues(enumType);
        CopyChoices(possible, out values, out names);
        return values.Length > 0;
    }

    private static bool TryGetExtEnumOptions(Type fieldType, object field, out object[] values, out string[] names)
    {
        values = Array.Empty<object>(); names = Array.Empty<string>();
        if (FindGenericBase(fieldType, ExtEnumFieldDefinitionName) == null) return false;
        // POM's protected PossibleValues performs its own valuesVersion refresh and honors any
        // explicit allowed-value subset. Reading it is safer than reconstructing ExtEnum globals.
        Array possible = ReadMember(field, "PossibleValues") as Array ?? ReadMember(field, "_possibleValues") as Array;
        if (possible == null || possible.Length == 0) return false;
        CopyChoices(possible, out values, out names);
        return values.Length > 0;
    }

    private static void CopyChoices(Array source, out object[] values, out string[] names)
    {
        values = new object[source?.Length ?? 0]; names = new string[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = source.GetValue(i);
            names[i] = values[i]?.ToString() ?? string.Empty;
        }
    }

    private static int FindChoice(object[] values, object current)
    {
        for (int i = 0; i < values.Length; i++)
            if (Equals(values[i], current) || string.Equals(values[i]?.ToString(), current?.ToString(), StringComparison.Ordinal)) return i;
        return -1;
    }

    private static object FindManagedField(object data, string key)
    {
        Array fields = ReadMember(data, "fields") as Array;
        if (fields == null) return null;
        for (int i = 0; i < fields.Length; i++)
        {
            object field = fields.GetValue(i);
            if (field != null && string.Equals(ReadMember(field, "key") as string, key, StringComparison.Ordinal)) return field;
        }
        return null;
    }

    private static object GetManagedValue(object data, string key)
    {
        try
        {
            MethodInfo method = FindGenericMethod(data?.GetType(), "GetValue", 1, 1);
            return method?.MakeGenericMethod(typeof(object)).Invoke(data, new object[] { key });
        }
        catch (Exception error) { Plugin.Logger?.LogWarning("DevTool POM GetValue failed for '" + key + "': " + error.Message); return null; }
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
        catch (Exception error) { Plugin.Logger?.LogWarning("DevTool POM SetValue failed for '" + key + "': " + error.Message); return false; }
    }

    private static MethodInfo FindGenericMethod(Type type, string name, int genericArguments, int parameters)
    {
        for (Type current = type; current != null; current = current.BaseType)
        {
            MethodInfo[] methods = current.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (string.Equals(method.Name, name, StringComparison.Ordinal) && method.IsGenericMethodDefinition &&
                    method.GetGenericArguments().Length == genericArguments && method.GetParameters().Length == parameters) return method;
            }
        }
        return null;
    }

    private static Type FindGenericBase(Type type, string genericDefinitionName)
    {
        for (Type current = type; current != null; current = current.BaseType)
            if (current.IsGenericType && string.Equals(current.GetGenericTypeDefinition().FullName, genericDefinitionName, StringComparison.Ordinal)) return current;
        return null;
    }

    private static string SerializeFieldValue(object field, object value)
    {
        try
        {
            MethodInfo method = field?.GetType().GetMethod("ToString", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(object) }, null);
            return method?.Invoke(field, new[] { value })?.ToString() ?? value?.ToString() ?? string.Empty;
        }
        catch { return value?.ToString() ?? string.Empty; }
    }

    private static object ParseFieldText(object field, string text, out bool parsed)
    {
        parsed = false;
        try
        {
            MethodInfo method = field?.GetType().GetMethod("FromString", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(string) }, null);
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
        catch (Exception error) { Plugin.Logger?.LogWarning("DevTool POM field parse failed: " + error.Message); return null; }
    }

    private static string ReadDisplayName(object field, string fallback)
    {
        string display = ReadMember(field, "displayName") as string;
        return !string.IsNullOrWhiteSpace(display) ? display.Trim().TrimEnd(':').Trim() : (string.IsNullOrWhiteSpace(fallback) ? "Field" : fallback);
    }

    private static object ReadMember(object instance, string name)
    {
        if (instance == null || string.IsNullOrEmpty(name)) return null;
        for (Type current = instance.GetType(); current != null; current = current.BaseType)
        {
            FieldInfo field = current.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null) return field.GetValue(instance);
            PropertyInfo property = current.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property != null && property.CanRead && property.GetIndexParameters().Length == 0) return property.GetValue(instance, null);
        }
        return null;
    }

    private static bool WriteMember(object instance, string name, object value)
    {
        if (instance == null || string.IsNullOrEmpty(name)) return false;
        for (Type current = instance.GetType(); current != null; current = current.BaseType)
        {
            FieldInfo field = current.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null && !field.IsInitOnly && (value == null || field.FieldType.IsInstanceOfType(value))) { field.SetValue(instance, value); return true; }
            PropertyInfo property = current.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property != null && property.CanWrite && property.GetIndexParameters().Length == 0 && (value == null || property.PropertyType.IsInstanceOfType(value)))
            { property.SetValue(instance, value, null); return true; }
        }
        return false;
    }

    private static bool IsTypeOrBase(Type type, string fullName)
    {
        for (Type current = type; current != null; current = current.BaseType)
            if (string.Equals(current.FullName, fullName, StringComparison.Ordinal)) return true;
        return false;
    }

    private static float ReadFloat(object instance, string name, float fallback)
    { object value = ReadMember(instance, name); return value == null ? fallback : ConvertFloat(value, fallback); }
    private static int ReadInt(object instance, string name, int fallback)
    { object value = ReadMember(instance, name); return value == null ? fallback : ConvertInt(value, fallback); }
    private static float ConvertFloat(object value, float fallback)
    { try { return Convert.ToSingle(value, CultureInfo.InvariantCulture); } catch { return fallback; } }
    private static int ConvertInt(object value, int fallback)
    { try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); } catch { return fallback; } }
    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static string EncodeKey(string key) => Uri.EscapeDataString(key ?? string.Empty);
    private static string DecodeKey(string key) => Uri.UnescapeDataString(key ?? string.Empty);
}
