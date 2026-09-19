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
        internal string MemberName;
        internal string Group;
        internal Type ValueType;
        internal FieldInfo Field;
        internal PropertyInfo Property;
        internal int ArrayIndex = -1;
        internal bool Writable;
        internal EditorPropertyKind Kind;
        internal EditorPropertyGizmoHint GizmoHint;
        internal bool HasRange;
        internal float Min;
        internal float Max;
        internal float Step;
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
            next = ConstrainModelValue(data, binding, next);
            if (next == null && binding.ValueType.IsValueType)
                return false;
            binding.Write(data, next);
            ApplyPostWriteSemantics(data, binding);
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

    internal static bool TryBuildNativeGizmoValue(
        PlacedObject target,
        string key,
        float x,
        float y,
        out EditorPropertyValue value)
    {
        value = default;
        PlacedObject.Data data = target?.data;
        if (data == null || string.IsNullOrEmpty(key)) return false;

        Schema schema = Schemas.GetOrAdd(data.GetType(), BuildSchema);
        if (!schema.ByKey.TryGetValue(key, out MemberBinding binding) ||
            !binding.Writable ||
            binding.GizmoHint == EditorPropertyGizmoHint.None)
            return false;

        switch (binding.GizmoHint)
        {
            case EditorPropertyGizmoHint.RelativePoint:
                if (data is PlacedObject.TerrainHandleData)
                {
                    if (key.EndsWith(".leftOffset", StringComparison.Ordinal))
                        x = Math.Min(0f, x);
                    else if (key.EndsWith(".rightOffset", StringComparison.Ordinal))
                        x = Math.Max(0f, x);
                }

                value = new EditorPropertyValue(EditorPropertyKind.Vector2, x: x, y: y);
                return true;

            case EditorPropertyGizmoHint.VerticalDistance:
                value = new EditorPropertyValue(EditorPropertyKind.Float, x: y);
                return true;

            default:
                return false;
        }
    }

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
                if (field.IsStatic || ShouldIgnore(current, field.Name) || !names.Add(field.Name)) continue;

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
                    ShouldIgnore(current, property.Name) || !names.Add(property.Name))
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
            MemberName = field.Name,
            Group = "Geometry",
            ValueType = typeof(Vector2),
            Field = field,
            ArrayIndex = index,
            Writable = !field.IsInitOnly && !field.IsLiteral,
            Kind = EditorPropertyKind.Vector2,
            GizmoHint = EditorPropertyGizmoHint.RelativePoint,
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

        ResolveRange(declaringType, name, kind, out bool hasRange, out float min, out float max, out float step);

        return new MemberBinding
        {
            Key = "native." + (declaringType.FullName ?? declaringType.Name) + "." + name,
            DisplayName = Humanize(name),
            MemberName = name,
            Group = ResolveGroup(name, kind),
            ValueType = valueType,
            Writable = writable,
            Kind = kind,
            GizmoHint = ResolveGizmoHint(declaringType, name, valueType),
            HasRange = hasRange,
            Min = min,
            Max = max,
            Step = step,
            Options = options ?? Array.Empty<string>()
        };
    }

    private static void ResolveRange(
        Type declaringType,
        string name,
        EditorPropertyKind kind,
        out bool hasRange,
        out float min,
        out float max,
        out float step)
    {
        hasRange = false;
        min = 0f;
        max = 0f;
        step = kind == EditorPropertyKind.Integer ? 1f : 0.01f;

        if (declaringType == typeof(PlacedObject.LightFixtureData) &&
            string.Equals(name, "randomSeed", StringComparison.Ordinal))
        {
            hasRange = true;
            min = 0f;
            max = 100f;
            step = 1f;
            return;
        }

        if (declaringType == typeof(PlacedObject.LightningMachineData) &&
            string.Equals(name, "impact", StringComparison.Ordinal))
        {
            hasRange = true;
            min = 0f;
            max = 3f;
            step = 1f;
            return;
        }

        if (declaringType == typeof(PlacedObject.LightningMachineData) &&
            string.Equals(name, "soundType", StringComparison.Ordinal))
        {
            hasRange = true;
            min = 0f;
            max = 1f;
            step = 1f;
            return;
        }

        if (declaringType == typeof(PlacedObject.LightSourceData) &&
            (string.Equals(name, "strength", StringComparison.Ordinal) ||
             string.Equals(name, "blinkRate", StringComparison.Ordinal)))
        {
            hasRange = true;
            min = 0f;
            max = 1f;
            step = 0.01f;
            return;
        }

        if (declaringType == typeof(Watcher.KarmaFlowerPatch.KarmaFlowerPatchData) &&
            (string.Equals(name, "tilt", StringComparison.Ordinal) ||
             string.Equals(name, "glowStrength", StringComparison.Ordinal) ||
             string.Equals(name, "glowRadius", StringComparison.Ordinal)))
        {
            hasRange = true;
            min = 0f;
            max = 1f;
            step = 0.01f;
            return;
        }

        if (declaringType == typeof(Watcher.FlameJet.FlameJetData))
        {
            if (string.Equals(name, "lethality", StringComparison.Ordinal))
            {
                hasRange = true;
                min = 0f;
                max = 2f;
                step = 1f;
                return;
            }

            if (string.Equals(name, "intensity", StringComparison.Ordinal) ||
                string.Equals(name, "temperature", StringComparison.Ordinal) ||
                string.Equals(name, "intensityAnimSpeed", StringComparison.Ordinal) ||
                string.Equals(name, "intensityAnimOffset", StringComparison.Ordinal) ||
                string.Equals(name, "temperatureAnimSpeed", StringComparison.Ordinal) ||
                string.Equals(name, "temperatureAnimOffset", StringComparison.Ordinal) ||
                string.Equals(name, "intensityMin", StringComparison.Ordinal) ||
                string.Equals(name, "intensityMax", StringComparison.Ordinal) ||
                string.Equals(name, "temperatureMin", StringComparison.Ordinal) ||
                string.Equals(name, "temperatureMax", StringComparison.Ordinal) ||
                string.Equals(name, "width", StringComparison.Ordinal) ||
                string.Equals(name, "fireVolumeMax", StringComparison.Ordinal) ||
                string.Equals(name, "smokeVolumeMax", StringComparison.Ordinal))
            {
                hasRange = true;
                min = 0f;
                max = 1f;
                step = 0.01f;
                return;
            }
        }

        if (declaringType == typeof(LightBeam.LightBeamData) &&
            (string.Equals(name, "alpha", StringComparison.Ordinal) ||
             string.Equals(name, "colorA", StringComparison.Ordinal) ||
             string.Equals(name, "colorB", StringComparison.Ordinal) ||
             string.Equals(name, "blinkRate", StringComparison.Ordinal)))
        {
            hasRange = true;
            min = 0f;
            max = 1f;
            step = 0.01f;
            return;
        }

        if ((declaringType == typeof(PlacedObject.CustomDecalData) &&
             (string.Equals(name, "fromDepth", StringComparison.Ordinal) ||
              string.Equals(name, "toDepth", StringComparison.Ordinal) ||
              string.Equals(name, "noise", StringComparison.Ordinal))) ||
            (declaringType == typeof(PlacedObject.DeepProcessingData) &&
             (string.Equals(name, "fromDepth", StringComparison.Ordinal) ||
              string.Equals(name, "toDepth", StringComparison.Ordinal) ||
              string.Equals(name, "intensity", StringComparison.Ordinal))) ||
            (declaringType == typeof(PlacedObject.SSLightRodData) &&
             (string.Equals(name, "depth", StringComparison.Ordinal) ||
              string.Equals(name, "brightness", StringComparison.Ordinal))) ||
            (declaringType == typeof(GeyserData) &&
             string.Equals(name, "timing", StringComparison.Ordinal)) ||
            (declaringType == typeof(PlacedObject.ScavengerOutpostData) &&
             string.Equals(name, "direction", StringComparison.Ordinal)) ||
            (declaringType == typeof(Watcher.TowerCrabSpawner.Data) &&
             (string.Equals(name, "frequency", StringComparison.Ordinal) ||
              string.Equals(name, "minLayer", StringComparison.Ordinal) ||
              string.Equals(name, "maxLayer", StringComparison.Ordinal))))
        {
            hasRange = true;
            min = 0f;
            max = 1f;
            step = 0.01f;
            return;
        }

        if (declaringType == typeof(PlacedObject.SSLightRodData) &&
            string.Equals(name, "rotation", StringComparison.Ordinal))
        {
            hasRange = true;
            min = 0f;
            max = 315f;
            step = 45f;
            return;
        }

        if (declaringType == typeof(PlacedObject.SSLightRodData) &&
            string.Equals(name, "length", StringComparison.Ordinal))
        {
            hasRange = true;
            min = 40f;
            max = 800f;
            step = 1f;
            return;
        }

        if (declaringType == typeof(PlacedObject.PrinceFilterData) &&
            string.Equals(name, "stage", StringComparison.Ordinal))
        {
            hasRange = true;
            min = 0f;
            max = 3f;
            step = 1f;
            return;
        }

        if (declaringType == typeof(PlacedObject.RippleLevelFilterData) &&
            (string.Equals(name, "minimumRippleLevel", StringComparison.Ordinal) ||
             string.Equals(name, "maximumRippleLevel", StringComparison.Ordinal)))
        {
            hasRange = true;
            min = 0f;
            max = 5f;
            step = 0.1f;
            return;
        }

        if (declaringType == typeof(PlacedObject.RippleEggFilterData) &&
            string.Equals(name, "threshold", StringComparison.Ordinal))
        {
            hasRange = true;
            min = 0f;
            max = 1f;
            step = 0.01f;
            return;
        }

        if ((declaringType == typeof(GooDripSource.GooDripsData) &&
             string.Equals(name, "frequency", StringComparison.Ordinal)) ||
            (declaringType == typeof(PlacedObject.InsectGroupData) &&
             string.Equals(name, "density", StringComparison.Ordinal)) ||
            (declaringType == typeof(PlacedObject.MultiplayerItemData) &&
             string.Equals(name, "chance", StringComparison.Ordinal)) ||
            (declaringType == typeof(PlacedObject.FanData) &&
             (string.Equals(name, "speed", StringComparison.Ordinal) ||
              string.Equals(name, "depth", StringComparison.Ordinal))))
        {
            hasRange = true;
            min = 0f;
            max = 1f;
            step = 0.01f;
            return;
        }

        if (declaringType == typeof(PlacedObject.SkyWhalePathfindingData) &&
            string.Equals(name, "index", StringComparison.Ordinal))
        {
            hasRange = true;
            min = 0f;
            max = 5f;
            step = 1f;
            return;
        }

        if (declaringType == typeof(PlacedObject.ConsumableObjectData) &&
            (string.Equals(name, "minRegen", StringComparison.Ordinal) ||
             string.Equals(name, "maxRegen", StringComparison.Ordinal)))
        {
            hasRange = true;
            min = 0f;
            max = 50f;
            step = 1f;
            return;
        }

        if (declaringType == typeof(Watcher.UrbanLife.UrbanLifeData) &&
            (string.Equals(name, "intensity", StringComparison.Ordinal) ||
             string.Equals(name, "nLayers", StringComparison.Ordinal)))
        {
            hasRange = true;
            min = 0f;
            max = 1f;
            step = 0.01f;
            return;
        }

        if (declaringType == typeof(Watcher.UrbanLifePath.UrbanLifePathData) &&
            string.Equals(name, "density", StringComparison.Ordinal))
        {
            hasRange = true;
            min = 0f;
            max = 1f;
            step = 0.01f;
            return;
        }

        if (declaringType == typeof(Watcher.UrbanCandleHolder.UrbanCandleHolderData))
        {
            if (string.Equals(name, "height", StringComparison.Ordinal) ||
                string.Equals(name, "scale", StringComparison.Ordinal))
            {
                hasRange = true;
                min = 0f;
                max = 8f;
                step = 0.01f;
                return;
            }

            if (string.Equals(name, "depth", StringComparison.Ordinal))
            {
                hasRange = true;
                min = 0f;
                max = 1f;
                step = 0.01f;
                return;
            }

            if (string.Equals(name, "tilt", StringComparison.Ordinal))
            {
                hasRange = true;
                min = 0f;
                max = 0.8f;
                step = 0.01f;
                return;
            }

            if (string.Equals(name, "candleWidth", StringComparison.Ordinal))
            {
                hasRange = true;
                min = 0f;
                max = 2f;
                step = 0.01f;
                return;
            }
        }

        if (declaringType == typeof(Watcher.BigSkyWhaleSpawner.Data))
        {
            if (string.Equals(name, "minDelay", StringComparison.Ordinal) ||
                string.Equals(name, "maxDelay", StringComparison.Ordinal))
            {
                hasRange = true;
                min = 0f;
                max = 2400f;
                step = 1f;
                return;
            }
            if (string.Equals(name, "direction", StringComparison.Ordinal))
            {
                hasRange = true;
                min = -1f;
                max = 1f;
                step = 1f;
                return;
            }
        }

        if (declaringType == typeof(Watcher.SandGrubNetwork.NetworkData) &&
            (string.Equals(name, "density", StringComparison.Ordinal) ||
             string.Equals(name, "chance", StringComparison.Ordinal) ||
             string.Equals(name, "adultChance", StringComparison.Ordinal)))
        {
            hasRange = true;
            min = 0f;
            max = 1f;
            step = 0.01f;
            return;
        }

        if (declaringType == typeof(DaddyCorruption.CustomRotData))
        {
            if (string.Equals(name, "density", StringComparison.Ordinal))
            {
                hasRange = true;
                min = 0f;
                max = 3f;
                step = 0.01f;
                return;
            }
            if (string.Equals(name, "minSize", StringComparison.Ordinal) ||
                string.Equals(name, "maxSize", StringComparison.Ordinal))
            {
                hasRange = true;
                min = 0f;
                max = 30f;
                step = 0.1f;
                return;
            }
            if (string.Equals(name, "darknessScale", StringComparison.Ordinal))
            {
                hasRange = true;
                min = 0f;
                max = 2f;
                step = 0.01f;
                return;
            }
            if (string.Equals(name, "eyeChance", StringComparison.Ordinal) ||
                string.Equals(name, "minEyeSize", StringComparison.Ordinal) ||
                string.Equals(name, "maxEyeSize", StringComparison.Ordinal) ||
                string.Equals(name, "minColor", StringComparison.Ordinal) ||
                string.Equals(name, "maxColor", StringComparison.Ordinal) ||
                string.Equals(name, "slowdown", StringComparison.Ordinal) ||
                string.Equals(name, "legChance", StringComparison.Ordinal) ||
                string.Equals(name, "darknessChance", StringComparison.Ordinal))
            {
                hasRange = true;
                min = 0f;
                max = 1f;
                step = 0.01f;
                return;
            }
        }

        if (declaringType == typeof(PlacedObject.RippleTreeData) &&
            (string.Equals(name, "sproutThreshold", StringComparison.Ordinal) ||
             string.Equals(name, "sproutEnd", StringComparison.Ordinal)))
        {
            hasRange = true;
            min = 0f;
            max = 1f;
            step = 0.01f;
            return;
        }

        if (declaringType == typeof(ReliableIggyDirection.ReliableIggyDirectionData) &&
            string.Equals(name, "cyclesToShow", StringComparison.Ordinal))
        {
            hasRange = true;
            min = 0f;
            max = 9f;
            step = 1f;
            return;
        }

        if (declaringType == typeof(PlacedObject.SpawnMigrationStreamData))
        {
            if (string.Equals(name, "width", StringComparison.Ordinal))
            {
                hasRange = true;
                min = 1f;
                max = 200f;
                step = 1f;
                return;
            }
            if (string.Equals(name, "maxCapacity", StringComparison.Ordinal))
            {
                hasRange = true;
                min = 1f;
                max = 400f;
                step = 1f;
                return;
            }
            if (string.Equals(name, "rate", StringComparison.Ordinal))
            {
                hasRange = true;
                min = 5f;
                max = 400f;
                step = 1f;
                return;
            }
        }

        if (declaringType == typeof(PlacedObject.ScavengerOutpostData) &&
            (string.Equals(name, "skullSeed", StringComparison.Ordinal) ||
             string.Equals(name, "pearlsSeed", StringComparison.Ordinal)))
        {
            hasRange = true;
            min = 0f;
            max = 100f;
            step = 1f;
            return;
        }

        if (declaringType == typeof(Watcher.BigSkyWhaleTrigger.Data))
        {
            if (string.Equals(name, "waitCycles", StringComparison.Ordinal))
            {
                hasRange = true;
                min = -1f;
                max = 10f;
                step = 1f;
                return;
            }
            if (string.Equals(name, "direction", StringComparison.Ordinal))
            {
                hasRange = true;
                min = -1f;
                max = 1f;
                step = 1f;
                return;
            }
        }

        if ((declaringType == typeof(PlacedObject.EnergySwirlData) &&
             string.Equals(name, "depth", StringComparison.Ordinal)) ||
            (declaringType == typeof(PlacedObject.SnowSourceData) &&
             (string.Equals(name, "intensity", StringComparison.Ordinal) ||
              string.Equals(name, "noisiness", StringComparison.Ordinal))) ||
            (declaringType == typeof(PlacedObject.LocalBlizzardData) &&
             (string.Equals(name, "intensity", StringComparison.Ordinal) ||
              string.Equals(name, "scale", StringComparison.Ordinal) ||
              string.Equals(name, "angle", StringComparison.Ordinal))) ||
            (declaringType == typeof(PlacedObject.CellDistortionData) &&
             (string.Equals(name, "intensity", StringComparison.Ordinal) ||
              string.Equals(name, "scale", StringComparison.Ordinal) ||
              string.Equals(name, "chromaticIntensity", StringComparison.Ordinal) ||
              string.Equals(name, "timeMult", StringComparison.Ordinal))) ||
            (declaringType == typeof(PlacedObject.LightningMachineData) &&
             (string.Equals(name, "chance", StringComparison.Ordinal) ||
              string.Equals(name, "width", StringComparison.Ordinal) ||
              string.Equals(name, "intensity", StringComparison.Ordinal) ||
              string.Equals(name, "lifeTime", StringComparison.Ordinal) ||
              string.Equals(name, "lightningParam", StringComparison.Ordinal) ||
              string.Equals(name, "lightningType", StringComparison.Ordinal) ||
              string.Equals(name, "volume", StringComparison.Ordinal))) ||
            (declaringType == typeof(PlacedObject.AdjustableFanData) &&
             (string.Equals(name, "speed", StringComparison.Ordinal) ||
              string.Equals(name, "scale", StringComparison.Ordinal) ||
              string.Equals(name, "depth", StringComparison.Ordinal))) ||
            (declaringType == typeof(PlacedObject.HarmfulSteamData) &&
             (string.Equals(name, "duration", StringComparison.Ordinal) ||
              string.Equals(name, "frequency", StringComparison.Ordinal) ||
              string.Equals(name, "lifetime", StringComparison.Ordinal))))
        {
            hasRange = true;
            min = 0f;
            max = 1f;
            step = 0.01f;
        }
    }

    private static EditorPropertyGizmoHint ResolveGizmoHint(
        Type declaringType,
        string name,
        Type valueType)
    {
        if (valueType == typeof(Vector2))
        {
            if (string.Equals(name, "handlePos", StringComparison.Ordinal) &&
                (declaringType == typeof(PlacedObject.ResizableObjectData) ||
                 declaringType == typeof(PlacedObject.GridRectObjectData) ||
                 declaringType == typeof(PlacedObject.LightSourceData) ||
                 declaringType == typeof(PlacedObject.HarmfulSteamData) ||
                 declaringType == typeof(PlacedObject.PomegranateData) ||
                 declaringType == typeof(PlacedObject.SkyWhalePathfindingData) ||
                 declaringType == typeof(PlacedObject.EnergySwirlData) ||
                 declaringType == typeof(PlacedObject.SteamPipeData) ||
                 declaringType == typeof(PlacedObject.SnowSourceData) ||
                 declaringType == typeof(PlacedObject.LocalBlizzardData) ||
                 declaringType == typeof(PlacedObject.CellDistortionData)))
                return EditorPropertyGizmoHint.RelativePoint;

            if (declaringType == typeof(PlacedObject.TerrainHandleData) &&
                (string.Equals(name, "leftOffset", StringComparison.Ordinal) ||
                 string.Equals(name, "rightOffset", StringComparison.Ordinal)))
                return EditorPropertyGizmoHint.RelativePoint;
        }

        if (declaringType == typeof(PlacedObject.TerrainHandleData) &&
            valueType == typeof(float) &&
            string.Equals(name, "backHeight", StringComparison.Ordinal))
            return EditorPropertyGizmoHint.VerticalDistance;

        return EditorPropertyGizmoHint.None;
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
            GizmoHint = binding.GizmoHint,
            HasRange = binding.HasRange,
            Min = binding.Min,
            Max = binding.Max,
            Step = binding.Step,
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

    private static object ConstrainModelValue(
        PlacedObject.Data data,
        MemberBinding binding,
        object value)
    {
        string name = binding.MemberName ?? string.Empty;

        if (data is PlacedObject.ConsumableObjectData consumable && value is int regen)
        {
            if (string.Equals(name, "minRegen", StringComparison.Ordinal))
                return Math.Min(regen, consumable.maxRegen);
            if (string.Equals(name, "maxRegen", StringComparison.Ordinal))
                return Math.Max(regen, consumable.minRegen);
        }

        if (data is PlacedObject.CustomDecalData decal)
        {
            if (string.Equals(name, "fromDepth", StringComparison.Ordinal) && value is float fromDepth)
                return Mathf.Min(fromDepth, decal.toDepth);
            if (string.Equals(name, "toDepth", StringComparison.Ordinal) && value is float toDepth)
                return Mathf.Max(toDepth, decal.fromDepth);
        }

        if (data is PlacedObject.DeepProcessingData processing)
        {
            if (string.Equals(name, "fromDepth", StringComparison.Ordinal) && value is float fromDepth)
                return Mathf.Min(fromDepth, processing.toDepth);
            if (string.Equals(name, "toDepth", StringComparison.Ordinal) && value is float toDepth)
                return Mathf.Max(toDepth, processing.fromDepth);
        }

        return value;
    }

    private static void ApplyPostWriteSemantics(
        PlacedObject.Data data,
        MemberBinding binding)
    {
        string name = binding.MemberName ?? string.Empty;

        if (data is Watcher.TowerCrabSpawner.Data tower)
        {
            if (string.Equals(name, "minLayer", StringComparison.Ordinal))
                tower.maxLayer = Mathf.Max(tower.minLayer, tower.maxLayer);
            else if (string.Equals(name, "maxLayer", StringComparison.Ordinal))
                tower.minLayer = Mathf.Min(tower.maxLayer, tower.minLayer);
        }
    }

    private static object ConvertValue(MemberBinding binding, EditorPropertyValue value)
    {
        Type type = binding.ValueType;
        switch (binding.Kind)
        {
            case EditorPropertyKind.Float:
                if (value.Kind != EditorPropertyKind.Float) return null;
                float floatValue = binding.HasRange
                    ? Mathf.Clamp(value.X, binding.Min, binding.Max)
                    : value.X;
                if (type == typeof(double)) return (double)floatValue;
                return floatValue;
            case EditorPropertyKind.Integer:
                if (value.Kind != EditorPropertyKind.Integer) return null;
                int integerValue = binding.HasRange
                    ? Mathf.Clamp(value.Integer, Mathf.RoundToInt(binding.Min), Mathf.RoundToInt(binding.Max))
                    : value.Integer;
                if (type == typeof(short)) return (short)integerValue;
                if (type == typeof(byte)) return (byte)Mathf.Clamp(integerValue, byte.MinValue, byte.MaxValue);
                return integerValue;
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
            GizmoHint = source.GizmoHint,
            HasRange = source.HasRange,
            Min = source.Min,
            Max = source.Max,
            Step = source.Step,
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

    private static bool ShouldIgnore(Type declaringType, string name)
    {
        if (declaringType == typeof(Watcher.FlameJet.FlameJetData) &&
            (string.Equals(name, "obj", StringComparison.Ordinal) ||
             string.Equals(name, "pos", StringComparison.Ordinal)))
            return true;

        return string.Equals(name, "owner", StringComparison.Ordinal) ||
               string.Equals(name, "unrecognizedAttributes", StringComparison.Ordinal) ||
               string.Equals(name, "panelPos", StringComparison.Ordinal);
    }

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