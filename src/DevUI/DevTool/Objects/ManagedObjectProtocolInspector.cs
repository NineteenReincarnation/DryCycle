using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using DryCycle.DevUI.DevTool.Compatibility;
using RWCustom;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Objects;

/// <summary>
/// Framework-neutral adapter for the ManagedData/ManagedField protocol popularized by POM.
/// DryCycle does not reference Pom.dll: compatibility is structural and discovered from the live
/// Data object (fields + GetValue/SetValue). This keeps POM/RegionKit optional and also allows
/// compatible third-party implementations to participate without a hard dependency.
/// </summary>
internal sealed class ManagedObjectProtocolInspector :
    IObjectInspectorAdapter,
    IObjectInspectorCoverageProvider,
    IObjectInspectorGizmoAdapter
{
    internal static readonly ManagedObjectProtocolInspector Instance = new();

    private sealed class DataSchema
    {
        internal readonly MemberInfo FieldsMember;
        internal readonly MethodInfo GetValue;
        internal readonly MethodInfo SetValue;
        internal readonly bool Valid;
        internal readonly ConcurrentDictionary<Type, MethodInfo> Setters = new();

        internal DataSchema(
            MemberInfo fieldsMember,
            MethodInfo getValue,
            MethodInfo setValue)
        {
            FieldsMember = fieldsMember;
            GetValue = getValue?.MakeGenericMethod(typeof(object));
            SetValue = setValue;
            Valid = fieldsMember != null && getValue != null && setValue != null;
        }
    }

    private sealed class Binding
    {
        internal object Descriptor;
        internal string RawKey;
        internal string Key;
        internal string DisplayName;
        internal Type ValueType;
        internal object CurrentValue;
        internal EditorPropertyKind Kind;
        internal EditorPropertyGizmoShape GizmoShape;
        internal float GizmoScale = 1f;
        internal bool HasRange;
        internal float Min;
        internal float Max;
        internal float Step;
        internal string[] Options = Array.Empty<string>();
        internal bool InspectorSupported;
        internal bool GizmoSupported = true;
        internal bool IsIntVector;
        internal int ArrayIndex = -1;
        internal Type StorageType;

        internal Binding WithValue(object value)
        {
            Binding copy = (Binding)MemberwiseClone();
            copy.CurrentValue = value;
            return copy;
        }
    }

    private static readonly ConcurrentDictionary<Type, DataSchema> Schemas = new();
    // Many registrations share the same ManagedData CLR type but have different descriptors.
    // Cache immutable metadata by descriptor identity, never by object type or current values.
    private static ConditionalWeakTable<object, Binding> DescriptorBindings = new();
    private static ConditionalWeakTable<object, HashSet<string>> FailedFields = new();

    public bool CanInspect(PlacedObject target)
    {
        PlacedObject.Data data = target?.data;
        return data != null && Schemas.GetOrAdd(data.GetType(), BuildSchema).Valid;
    }

    public IReadOnlyList<EditorPropertySnapshot> Capture(PlacedObject target)
    {
        PlacedObject.Data data = target?.data;
        if (data == null) return Array.Empty<EditorPropertySnapshot>();

        DataSchema schema = Schemas.GetOrAdd(data.GetType(), BuildSchema);
        if (!schema.Valid || !TryBuildBindings(data, schema, out List<Binding> bindings))
            return Array.Empty<EditorPropertySnapshot>();

        List<EditorPropertySnapshot> result = new(bindings.Count + 2)
        {
            new()
            {
                Key = "__managedDataType",
                DisplayName = "Data Type",
                Group = "Native Data",
                Source = "Managed object protocol",
                Kind = EditorPropertyKind.ReadOnly,
                StringValue = data.GetType().FullName ?? data.GetType().Name
            }
        };

        for (int i = 0; i < bindings.Count; i++)
            result.Add(Capture(bindings[i]));

        HashSet<string> managedKeys = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < bindings.Count; i++) managedKeys.Add(bindings[i].RawKey);
        result.AddRange(NativeDataReflectionInspector.CaptureAdditionalFields(
            target, schema.FieldsMember.DeclaringType, managedKeys));

        // Serializing an entire third-party model is not an inspector render operation.
        if (DevUiDiagnosticsPolicy.Enabled)
        {
            string serialized;
            try { serialized = data.ToString() ?? string.Empty; }
            catch (Exception error)
            {
                ReportFieldFailure(data, "serialization", error);
                serialized = "<serialization failed: " + RootMessage(error) + ">";
            }
            result.Add(new EditorPropertySnapshot
            {
                Key = "__managedSerialized",
                DisplayName = "Serialized Data",
                Group = "Compatibility",
                Source = "Managed object protocol",
                Kind = EditorPropertyKind.ReadOnly,
                StringValue = serialized
            });
        }
        return result;
    }

    public bool TrySetValue(
        PlacedObject target,
        string key,
        EditorPropertyValue value)
    {
        PlacedObject.Data data = target?.data;
        if (data == null || string.IsNullOrEmpty(key))
            return false;

        if (key.StartsWith("native.", StringComparison.Ordinal))
            return NativeDataReflectionInspector.Instance.TrySetValue(target, key, value);

        DataSchema schema = Schemas.GetOrAdd(data.GetType(), BuildSchema);
        if (!schema.Valid ||
            !TryBuildBindings(data, schema, out List<Binding> bindings) ||
            !TryFindBinding(bindings, key, out Binding binding) ||
            !binding.InspectorSupported)
            return false;

        try
        {
            object next = ConvertEditorValue(binding, value);
            if (next == null)
                return false;

            Type storageType = binding.StorageType ?? binding.ValueType;
            if (binding.ArrayIndex >= 0)
            {
                if (ReadManagedValue(data, schema, binding.RawKey) is not Array current ||
                    binding.ArrayIndex >= current.Length)
                    return false;
                // Some frameworks reuse their default array across instances. Never mutate it,
                // another object's data or a history snapshot through a shared reference.
                Array copy = (Array)current.Clone();
                copy.SetValue(next, binding.ArrayIndex);
                next = copy;
            }
            MethodInfo setter = schema.Setters.GetOrAdd(storageType, type => schema.SetValue.MakeGenericMethod(type));
            setter.Invoke(data, new[] { binding.RawKey, next });
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning(
                "DevTool managed object mutation failed for " +
                binding.DisplayName + ": " + error);
            return false;
        }
    }

    public ObjectInspectorCoverage GetCoverage(PlacedObject target)
    {
        PlacedObject.Data data = target?.data;
        if (data == null)
            return ObjectInspectorCoverage.Conservative();

        DataSchema schema = Schemas.GetOrAdd(data.GetType(), BuildSchema);
        if (!schema.Valid ||
            !TryBuildBindings(data, schema, out List<Binding> bindings))
            return ObjectInspectorCoverage.Conservative();

        int supported = 0;
        int unsupported = 0;
        bool inspectorComplete = true;
        bool gizmoComplete = true;

        for (int i = 0; i < bindings.Count; i++)
        {
            Binding binding = bindings[i];
            if (binding.InspectorSupported)
                supported++;
            else
            {
                inspectorComplete = false;
                unsupported++;
            }

            if (!binding.GizmoSupported)
                gizmoComplete = false;
        }

        // Managed fields are not necessarily the whole story. A derived Data type can append its
        // own authored public members and a custom Representation can edit those. If we can see a
        // simple extra public member that is not represented by a managed key, stay conservative
        // and let the isolated representation fallback cover it.
        if (HasUncoveredAuthoredMembers(data.GetType(), bindings))
        {
            inspectorComplete = false;
            unsupported++;
        }

        return new ObjectInspectorCoverage(
            inspectorComplete,
            gizmoComplete,
            supported,
            unsupported);
    }

    public bool TryBuildGizmoValue(
        PlacedObject target,
        string key,
        float relativeX,
        float relativeY,
        bool snap,
        out EditorPropertyValue value)
    {
        value = default;
        PlacedObject.Data data = target?.data;
        if (data == null || string.IsNullOrEmpty(key))
            return false;

        DataSchema schema = Schemas.GetOrAdd(data.GetType(), BuildSchema);
        if (!schema.Valid ||
            !TryBuildBindings(data, schema, out List<Binding> bindings) ||
            !TryFindBinding(bindings, key, out Binding binding) ||
            !binding.InspectorSupported ||
            !binding.GizmoSupported ||
            binding.GizmoShape == EditorPropertyGizmoShape.None)
            return false;

        if (binding.IsIntVector)
        {
            Vector2 relative = new(relativeX, relativeY);
            if (binding.GizmoShape == EditorPropertyGizmoShape.Direction4)
            {
                float angle = Custom.VecToDeg(relative);
                angle -= ((angle + 45f + 360f) % 90f) - 45f;
                relative = Custom.DegToVec(angle) * 20f;
            }
            else if (binding.GizmoShape == EditorPropertyGizmoShape.Direction8)
            {
                float angle = Custom.VecToDeg(relative);
                angle -= ((angle + 22.5f + 360f) % 45f) - 22.5f;
                relative = Custom.DegToVec(angle);
                if (Mathf.Abs(relative.x) > 0.5f) relative.x = Mathf.Sign(relative.x);
                if (Mathf.Abs(relative.y) > 0.5f) relative.y = Mathf.Sign(relative.y);
                relative *= 20f;
            }

            Vector2 world = target.pos + relative;
            IntVector2 ownTile = new(
                Mathf.FloorToInt(world.x / 20f),
                Mathf.FloorToInt(world.y / 20f));
            IntVector2 parentTile = new(
                Mathf.FloorToInt(target.pos.x / 20f),
                Mathf.FloorToInt(target.pos.y / 20f));
            IntVector2 delta = ownTile - parentTile;
            value = new EditorPropertyValue(
                EditorPropertyKind.Vector2,
                x: delta.x,
                y: delta.y);
            return true;
        }

        if (binding.ValueType == typeof(Vector2))
        {
            value = new EditorPropertyValue(
                EditorPropertyKind.Vector2,
                x: relativeX,
                y: relativeY);
            return true;
        }

        return false;
    }

    internal static void ClearSchemaCache()
    {
        Schemas.Clear();
        DescriptorBindings = new ConditionalWeakTable<object, Binding>();
        FailedFields = new ConditionalWeakTable<object, HashSet<string>>();
    }

    private static DataSchema BuildSchema(Type dataType)
    {
        MemberInfo fieldsMember =
            (MemberInfo)FindField(dataType, "fields") ??
            FindProperty(dataType, "fields");

        MethodInfo getValue = FindGenericMethod(dataType, "GetValue", 1);
        MethodInfo setValue = FindGenericMethod(dataType, "SetValue", 2);

        if (fieldsMember == null || getValue == null || setValue == null)
            return new DataSchema(null, null, null);

        Type fieldsType = fieldsMember switch
        {
            FieldInfo field => field.FieldType,
            PropertyInfo property => property.PropertyType,
            _ => null
        };

        if (fieldsType == null ||
            (!fieldsType.IsArray && !typeof(IEnumerable).IsAssignableFrom(fieldsType)))
            return new DataSchema(null, null, null);

        return new DataSchema(fieldsMember, getValue, setValue);
    }

    private static bool TryBuildBindings(
        object data,
        DataSchema schema,
        out List<Binding> bindings)
    {
        bindings = new List<Binding>();
        try
        {
            object collection = ReadMember(schema.FieldsMember, data);
            if (collection is not IEnumerable enumerable)
                return false;

            foreach (object descriptor in enumerable)
            {
                if (descriptor == null)
                    continue;

                string rawKey = ReadString(descriptor, "key");
                if (string.IsNullOrWhiteSpace(rawKey))
                    continue;

                try
                {
                    object current = ReadManagedValue(data, schema, rawKey);
                    if (!DescriptorBindings.TryGetValue(descriptor, out Binding metadata))
                    {
                        object defaultValue = ReadNamedMember(descriptor, "DefaultValue");
                        metadata = BuildBinding(descriptor, rawKey, null,
                            current?.GetType() ?? defaultValue?.GetType());
                        DescriptorBindings.Add(descriptor, metadata);
                    }

                    if (current is Vector2[] vectors)
                    {
                        // The representation remains authoritative for chain, polygon, driven and
                        // custom curve geometry. The inspector can still edit every authored point.
                        bool includeParent = ReadNamedMember(descriptor, "IncludeParent") is bool include && include;
                        for (int index = includeParent ? 1 : 0; index < vectors.Length; index++)
                        {
                            Binding point = metadata.WithValue(vectors[index]);
                            point.Key += "[" + index + "]";
                            point.DisplayName += " " + (index + 1);
                            point.ArrayIndex = index;
                            point.StorageType = typeof(Vector2[]);
                            point.ValueType = typeof(Vector2);
                            point.Kind = EditorPropertyKind.Vector2;
                            point.InspectorSupported = true;
                            point.GizmoSupported = false;
                            bindings.Add(point);
                        }
                        if (vectors.Length == 0)
                            bindings.Add(metadata.WithValue(current));
                    }
                    else
                        bindings.Add(metadata.WithValue(current));
                }
                catch (Exception error)
                {
                    ReportFieldFailure(data, rawKey, error);
                    bindings.Add(new Binding
                    {
                        RawKey = rawKey, Key = "managed." + rawKey, DisplayName = Humanize(rawKey),
                        Kind = EditorPropertyKind.ReadOnly, GizmoSupported = false,
                        CurrentValue = "<read failed: " + RootMessage(error) + ">"
                    });
                }
            }

            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning(
                "DevTool managed object protocol capture failed: " + error);
            bindings.Clear();
            return false;
        }
    }

    private static void ReportFieldFailure(object data, string key, Exception error)
    {
        if (FailedFields.GetValue(data, _ => new HashSet<string>()).Add(key))
            Plugin.Logger?.LogWarning("DevTool managed object field '" + key + "' failed: " + error);
    }

    private static Binding BuildBinding(
        object descriptor,
        string rawKey,
        object current,
        Type valueType)
    {
        Binding binding = new()
        {
            Descriptor = descriptor,
            RawKey = rawKey,
            Key = "managed." + rawKey,
            DisplayName = ReadString(descriptor, "displayName") ?? Humanize(rawKey),
            ValueType = valueType,
            CurrentValue = current,
            Step = 0.01f
        };

        if (string.IsNullOrWhiteSpace(binding.DisplayName))
            binding.DisplayName = Humanize(rawKey);

        string descriptorName = descriptor.GetType().Name;
        object controlType = ReadNamedMember(descriptor, "controlType");
        string geometry = controlType?.ToString()?.ToLowerInvariant() ?? string.Empty;
        object drivenType = ReadNamedMember(descriptor, "drivenControlType");

        if (valueType == typeof(float))
        {
            binding.Kind = EditorPropertyKind.Float;
            binding.InspectorSupported = true;
            ResolveRange(descriptor, binding, integer: false);
        }
        else if (valueType == typeof(int))
        {
            binding.Kind = EditorPropertyKind.Integer;
            binding.InspectorSupported = true;
            ResolveRange(descriptor, binding, integer: true);
        }
        else if (valueType == typeof(bool))
        {
            binding.Kind = EditorPropertyKind.Boolean;
            binding.InspectorSupported = true;
        }
        else if (valueType == typeof(string))
        {
            binding.Kind = EditorPropertyKind.String;
            binding.InspectorSupported = true;
        }
        else if (valueType == typeof(Vector2))
        {
            binding.Kind = EditorPropertyKind.Vector2;
            binding.InspectorSupported = true;
            binding.GizmoShape = ResolveGeometry(geometry);
            binding.GizmoScale = 1f;

            // DrivenVector2 is a composite semantic control whose anchor can depend on another
            // managed field. Preserve field editing but require the headless representation for
            // the scene interaction until the composite protocol is implemented.
            if (drivenType != null)
            {
                binding.GizmoShape = EditorPropertyGizmoShape.None;
                binding.GizmoSupported = false;
            }
        }
        else if (valueType == typeof(IntVector2))
        {
            binding.Kind = EditorPropertyKind.Vector2;
            binding.InspectorSupported = true;
            binding.IsIntVector = true;
            binding.GizmoShape = ResolveGeometry(geometry);
            binding.GizmoScale = 20f;
        }
        else if (valueType == typeof(Color))
        {
            binding.Kind = EditorPropertyKind.Color;
            binding.InspectorSupported = true;
        }
        else if (valueType?.IsEnum == true)
        {
            binding.Kind = EditorPropertyKind.Enum;
            // Managed enum fields may intentionally expose only a subset of the CLR enum. Prefer
            // the descriptor's own list contract so DryCycle cannot create a value the original
            // control would reject.
            binding.Options = TryReadListOptions(descriptor, out string[] enumOptions)
                ? enumOptions
                : Enum.GetNames(valueType);
            binding.InspectorSupported = binding.Options.Length > 0;
        }
        else if (valueType != null &&
                 !valueType.IsArray &&
                 TryReadListOptions(descriptor, out string[] options))
        {
            // ExtEnum-style fields expose their legal values through the same list protocol used by
            // the original control. Setting is delegated back to descriptor.FromString().
            binding.Kind = EditorPropertyKind.Enum;
            binding.Options = options;
            binding.InspectorSupported = options.Length > 0;
        }
        else
        {
            binding.Kind = EditorPropertyKind.ReadOnly;
            binding.InspectorSupported = false;
            binding.GizmoSupported = false;
        }

        // A vector descriptor that explicitly says "none" intentionally has no scene gizmo.
        if ((valueType == typeof(Vector2) || valueType == typeof(IntVector2)) &&
            string.Equals(geometry, "none", StringComparison.OrdinalIgnoreCase))
            binding.GizmoShape = EditorPropertyGizmoShape.None;

        // Array/list fields commonly create multi-handle geometry. Do not lie about coverage until
        // the next phase's chain/polygon compiler is present.
        if (valueType?.IsArray == true ||
            descriptorName.IndexOf("Vector2List", StringComparison.OrdinalIgnoreCase) >= 0 ||
            descriptorName.IndexOf("Vector2Array", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            binding.InspectorSupported = false;
            binding.GizmoSupported = false;
            binding.Kind = EditorPropertyKind.ReadOnly;
        }

        return binding;
    }

    private static EditorPropertySnapshot Capture(Binding binding)
    {
        string group = binding.GizmoShape != EditorPropertyGizmoShape.None
            ? "Geometry"
            : "Fields";

        float x = 0f;
        float y = 0f;
        float z = 0f;
        float w = 0f;
        int integer = 0;
        bool boolean = false;
        string text = string.Empty;

        object current = binding.CurrentValue;
        switch (binding.Kind)
        {
            case EditorPropertyKind.Float:
                x = Convert.ToSingle(current, CultureInfo.InvariantCulture);
                break;

            case EditorPropertyKind.Integer:
                integer = Convert.ToInt32(current, CultureInfo.InvariantCulture);
                break;

            case EditorPropertyKind.Boolean:
                boolean = Convert.ToBoolean(current, CultureInfo.InvariantCulture);
                break;

            case EditorPropertyKind.String:
                text = current?.ToString() ?? string.Empty;
                break;

            case EditorPropertyKind.Vector2:
                if (current is Vector2 vector)
                {
                    x = vector.x;
                    y = vector.y;
                }
                else if (current is IntVector2 intVector)
                {
                    x = intVector.x;
                    y = intVector.y;
                }
                break;

            case EditorPropertyKind.Color:
                if (current is Color color)
                {
                    x = color.r;
                    y = color.g;
                    z = color.b;
                    w = color.a;
                }
                break;

            case EditorPropertyKind.Enum:
                text = current?.ToString() ?? string.Empty;
                integer = IndexOf(binding.Options, text);
                break;

            default:
                text = SerializeDescriptorValue(binding);
                break;
        }

        return new EditorPropertySnapshot
        {
            Key = binding.Key,
            DisplayName = binding.DisplayName,
            Group = group,
            Source = "Managed object protocol",
            Kind = binding.Kind,
            GizmoHint = binding.GizmoShape != EditorPropertyGizmoShape.None
                ? EditorPropertyGizmoHint.RelativePoint
                : EditorPropertyGizmoHint.None,
            GizmoShape = binding.GizmoShape,
            GizmoScale = binding.GizmoScale,
            HasRange = binding.HasRange,
            Min = binding.Min,
            Max = binding.Max,
            Step = binding.Step,
            Options = binding.Options ?? Array.Empty<string>(),
            X = x,
            Y = y,
            Z = z,
            W = w,
            IntegerValue = integer,
            BooleanValue = boolean,
            StringValue = text
        };
    }

    private static object ConvertEditorValue(
        Binding binding,
        EditorPropertyValue value)
    {
        switch (binding.Kind)
        {
            case EditorPropertyKind.Float:
                if (value.Kind != EditorPropertyKind.Float) return null;
                float number = value.X;
                if (binding.HasRange)
                    number = Mathf.Clamp(number, binding.Min, binding.Max);
                return number;

            case EditorPropertyKind.Integer:
                if (value.Kind != EditorPropertyKind.Integer) return null;
                int integer = value.Integer;
                if (binding.HasRange)
                    integer = Mathf.Clamp(
                        integer,
                        Mathf.RoundToInt(binding.Min),
                        Mathf.RoundToInt(binding.Max));
                return integer;

            case EditorPropertyKind.Boolean:
                return value.Kind == EditorPropertyKind.Boolean
                    ? value.Boolean
                    : null;

            case EditorPropertyKind.String:
                return value.Kind == EditorPropertyKind.String
                    ? value.Text ?? string.Empty
                    : null;

            case EditorPropertyKind.Vector2:
                if (value.Kind != EditorPropertyKind.Vector2) return null;
                if (binding.IsIntVector)
                    return new IntVector2(
                        Mathf.RoundToInt(value.X),
                        Mathf.RoundToInt(value.Y));
                return new Vector2(value.X, value.Y);

            case EditorPropertyKind.Color:
                return value.Kind == EditorPropertyKind.Color
                    ? new Color(value.X, value.Y, value.Z, value.W)
                    : null;

            case EditorPropertyKind.Enum:
                if (value.Kind != EditorPropertyKind.Enum ||
                    value.Integer < 0 ||
                    value.Integer >= binding.Options.Length)
                    return null;

                string selected = binding.Options[value.Integer];
                if (binding.ValueType?.IsEnum == true)
                    return Enum.Parse(binding.ValueType, selected);

                MethodInfo parser = FindMethod(
                    binding.Descriptor.GetType(),
                    "FromString",
                    typeof(string));
                return parser?.Invoke(binding.Descriptor, new object[] { selected });
        }

        return null;
    }

    private static void ResolveRange(
        object descriptor,
        Binding binding,
        bool integer)
    {
        if (!TryReadNumber(descriptor, "min", out float min) ||
            !TryReadNumber(descriptor, "max", out float max))
            return;

        binding.HasRange = true;
        binding.Min = Math.Min(min, max);
        binding.Max = Math.Max(min, max);
        binding.Step = integer
            ? 1f
            : TryReadNumber(descriptor, "increment", out float increment)
                ? Math.Max(0.000001f, Math.Abs(increment))
                : 0.01f;
    }

    private static EditorPropertyGizmoShape ResolveGeometry(string value)
    {
        switch (value)
        {
            case "line":
                return EditorPropertyGizmoShape.Line;
            case "circle":
                return EditorPropertyGizmoShape.Circle;
            case "rect":
                return EditorPropertyGizmoShape.Rectangle;
            case "tile":
                return EditorPropertyGizmoShape.Tile;
            case "fourdir":
                return EditorPropertyGizmoShape.Direction4;
            case "eightdir":
                return EditorPropertyGizmoShape.Direction8;
            default:
                return EditorPropertyGizmoShape.None;
        }
    }

    private static bool TryReadListOptions(
        object descriptor,
        out string[] options)
    {
        options = Array.Empty<string>();
        MethodInfo lowest = FindMethod(descriptor.GetType(), "LowestItem");
        MethodInfo highest = FindMethod(descriptor.GetType(), "HighestItem");
        MethodInfo values = FindMethod(
            descriptor.GetType(),
            "GetValues",
            typeof(int),
            typeof(int));
        if (lowest == null || highest == null || values == null)
            return false;

        try
        {
            int start = Convert.ToInt32(lowest.Invoke(descriptor, null), CultureInfo.InvariantCulture);
            int end = Convert.ToInt32(highest.Invoke(descriptor, null), CultureInfo.InvariantCulture);
            int count = end - start;
            if (count <= 0 || count > 512)
                return false;

            options = values.Invoke(descriptor, new object[] { start, end }) as string[]
                      ?? Array.Empty<string>();
            return options.Length > 0;
        }
        catch
        {
            options = Array.Empty<string>();
            return false;
        }
    }

    private static bool HasUncoveredAuthoredMembers(
        Type dataType,
        List<Binding> bindings)
    {
        HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < bindings.Count; i++)
            keys.Add(bindings[i].RawKey);

        Type current = dataType;
        while (current != null && current != typeof(PlacedObject.Data))
        {
            FieldInfo[] fields = current.GetFields(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.DeclaredOnly);
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                if (field.IsStatic ||
                    string.Equals(field.Name, "fields", StringComparison.Ordinal) ||
                    string.Equals(field.Name, "panelPos", StringComparison.Ordinal) ||
                    keys.Contains(field.Name))
                    continue;

                if (IsSimpleAuthoredType(field.FieldType))
                    return true;
            }

            current = current.BaseType;
        }

        return false;
    }

    private static bool IsSimpleAuthoredType(Type type) =>
        type == typeof(float) ||
        type == typeof(int) ||
        type == typeof(bool) ||
        type == typeof(string) ||
        type == typeof(Vector2) ||
        type == typeof(IntVector2) ||
        type == typeof(Color) ||
        type?.IsEnum == true;

    private static bool TryFindBinding(
        List<Binding> bindings,
        string key,
        out Binding binding)
    {
        for (int i = 0; i < bindings.Count; i++)
        {
            if (string.Equals(bindings[i].Key, key, StringComparison.Ordinal))
            {
                binding = bindings[i];
                return true;
            }
        }

        binding = null;
        return false;
    }

    private static object ReadManagedValue(
        object data,
        DataSchema schema,
        string key)
    {
        return schema.GetValue.Invoke(data, new object[] { key });
    }

    private static object ReadMember(MemberInfo member, object target) =>
        member switch
        {
            FieldInfo field => field.GetValue(target),
            PropertyInfo property => property.GetValue(target, null),
            _ => null
        };

    private static object ReadNamedMember(object target, string name)
    {
        if (target == null || string.IsNullOrEmpty(name))
            return null;

        FieldInfo field = FindField(target.GetType(), name);
        if (field != null)
            return field.GetValue(target);

        PropertyInfo property = FindProperty(target.GetType(), name);
        return property?.GetValue(target, null);
    }

    private static string ReadString(object target, string name) =>
        ReadNamedMember(target, name)?.ToString();

    private static bool TryReadNumber(
        object target,
        string name,
        out float value)
    {
        value = 0f;
        object raw = ReadNamedMember(target, name);
        if (raw == null)
            return false;

        try
        {
            value = Convert.ToSingle(raw, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static FieldInfo FindField(Type type, string name)
    {
        while (type != null)
        {
            FieldInfo field = type.GetField(
                name,
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly);
            if (field != null)
                return field;
            type = type.BaseType;
        }

        return null;
    }

    private static PropertyInfo FindProperty(Type type, string name)
    {
        while (type != null)
        {
            PropertyInfo property = type.GetProperty(
                name,
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly);
            if (property != null)
                return property;
            type = type.BaseType;
        }

        return null;
    }

    private static MethodInfo FindGenericMethod(
        Type type,
        string name,
        int parameterCount)
    {
        while (type != null)
        {
            MethodInfo[] methods = type.GetMethods(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                ParameterInfo[] parameters = method.GetParameters();
                if (method.Name == name &&
                    method.IsGenericMethodDefinition &&
                    method.GetGenericArguments().Length == 1 &&
                    parameters.Length == parameterCount &&
                    parameters.Length > 0 &&
                    parameters[0].ParameterType == typeof(string))
                    return method;
            }

            type = type.BaseType;
        }

        return null;
    }

    private static MethodInfo FindMethod(
        Type type,
        string name,
        params Type[] parameters)
    {
        while (type != null)
        {
            MethodInfo method = type.GetMethod(
                name,
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly,
                null,
                parameters ?? Type.EmptyTypes,
                null);
            if (method != null)
                return method;
            type = type.BaseType;
        }

        return null;
    }

    private static string SerializeDescriptorValue(Binding binding)
    {
        if (binding?.Descriptor == null)
            return binding?.CurrentValue?.ToString() ?? string.Empty;

        try
        {
            MethodInfo serializer = FindMethod(
                binding.Descriptor.GetType(),
                "ToString",
                typeof(object));
            return serializer?.Invoke(
                       binding.Descriptor,
                       new[] { binding.CurrentValue })?.ToString()
                   ?? binding.CurrentValue?.ToString()
                   ?? string.Empty;
        }
        catch
        {
            return binding.CurrentValue?.ToString() ?? string.Empty;
        }
    }

    private static int IndexOf(string[] values, string value)
    {
        if (values == null || values.Length == 0)
            return 0;

        for (int i = 0; i < values.Length; i++)
            if (string.Equals(values[i], value, StringComparison.Ordinal))
                return i;
        return 0;
    }

    private static string Humanize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        System.Text.StringBuilder text = new();
        for (int i = 0; i < value.Length; i++)
        {
            char current = value[i];
            if (i > 0 &&
                char.IsUpper(current) &&
                !char.IsUpper(value[i - 1]))
                text.Append(' ');
            text.Append(current);
        }

        return text.ToString();
    }

    private static string RootMessage(Exception error)
    {
        Exception current = error;
        while (current is TargetInvocationException invocation &&
               invocation.InnerException != null)
            current = invocation.InnerException;
        return current?.Message ?? "unknown error";
    }
}
