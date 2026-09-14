using System;
using System.Collections.Generic;

namespace DryCycle.DevUI.DevTool.Objects;

/// <summary>
/// Builds the native multi-selection inspector without changing the public property
/// registration API. Only properties shared by every selected object are exposed.
/// Mixed values are reported separately so the frontend can present them without
/// inventing sentinel values for bools/enums/numbers.
/// </summary>
internal static class MultiSelectionInspector
{
    internal static EditorPropertySnapshot[] Capture(
        IReadOnlyList<PlacedObject> selected,
        out string[] mixedPropertyKeys)
    {
        mixedPropertyKeys = Array.Empty<string>();
        if (selected == null || selected.Count == 0)
            return Array.Empty<EditorPropertySnapshot>();

        // Capturing inspector properties can involve adapter/reflection work. The previous path
        // recaptured every peer once per property from the first object, making a P-property,
        // S-object selection pay roughly P*S full captures. Snapshot each selected object exactly
        // once, then use a key index for the intersection/comparison pass.
        EditorPropertySnapshot[][] captured = new EditorPropertySnapshot[selected.Count][];
        Dictionary<string, EditorPropertySnapshot>[] byKey =
            new Dictionary<string, EditorPropertySnapshot>[selected.Count];

        for (int s = 0; s < selected.Count; s++)
        {
            EditorPropertySnapshot[] properties = ObjectInspectorRegistry.Capture(selected[s]) ??
                                                  Array.Empty<EditorPropertySnapshot>();
            captured[s] = properties;

            Dictionary<string, EditorPropertySnapshot> index = new(StringComparer.Ordinal);
            for (int i = 0; i < properties.Length; i++)
            {
                EditorPropertySnapshot property = properties[i];
                if (property == null || string.IsNullOrEmpty(property.Key)) continue;
                index[property.Key] = property;
            }
            byKey[s] = index;
        }

        EditorPropertySnapshot[] first = captured[0];
        if (first.Length == 0)
            return first;

        List<EditorPropertySnapshot> common = new(first.Length);
        List<string> mixed = new();

        for (int i = 0; i < first.Length; i++)
        {
            EditorPropertySnapshot candidate = first[i];
            if (candidate == null || candidate.Kind == EditorPropertyKind.ReadOnly || string.IsNullOrEmpty(candidate.Key))
                continue;

            bool existsEverywhere = true;
            bool sameValue = true;

            for (int s = 1; s < selected.Count; s++)
            {
                byKey[s].TryGetValue(candidate.Key, out EditorPropertySnapshot peer);
                if (!Compatible(candidate, peer))
                {
                    existsEverywhere = false;
                    break;
                }

                if (!SameValue(candidate, peer))
                    sameValue = false;
            }

            if (!existsEverywhere)
                continue;

            common.Add(candidate);
            if (!sameValue)
                mixed.Add(candidate.Key);
        }

        mixedPropertyKeys = mixed.ToArray();
        return common.ToArray();
    }

    private static bool Compatible(EditorPropertySnapshot a, EditorPropertySnapshot b)
    {
        if (a == null || b == null || a.Kind != b.Kind)
            return false;

        if (a.Kind == EditorPropertyKind.Enum)
        {
            string[] left = a.Options ?? Array.Empty<string>();
            string[] right = b.Options ?? Array.Empty<string>();
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
                if (!string.Equals(left[i], right[i], StringComparison.Ordinal)) return false;
        }

        return true;
    }

    private static bool SameValue(EditorPropertySnapshot a, EditorPropertySnapshot b)
    {
        if (a == null || b == null || a.Kind != b.Kind) return false;

        return a.Kind switch
        {
            EditorPropertyKind.Float => Nearly(a.X, b.X),
            EditorPropertyKind.Integer => a.IntegerValue == b.IntegerValue,
            EditorPropertyKind.Boolean => a.BooleanValue == b.BooleanValue,
            EditorPropertyKind.String => string.Equals(a.StringValue, b.StringValue, StringComparison.Ordinal),
            EditorPropertyKind.Vector2 => Nearly(a.X, b.X) && Nearly(a.Y, b.Y),
            EditorPropertyKind.Color => Nearly(a.X, b.X) && Nearly(a.Y, b.Y) && Nearly(a.Z, b.Z) && Nearly(a.W, b.W),
            EditorPropertyKind.Enum => a.IntegerValue == b.IntegerValue,
            _ => string.Equals(a.StringValue, b.StringValue, StringComparison.Ordinal)
        };
    }

    private static bool Nearly(float a, float b) => Math.Abs(a - b) <= 0.0001f;
}
