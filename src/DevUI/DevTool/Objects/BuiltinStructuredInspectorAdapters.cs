using System;
using System.Collections.Generic;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Objects;

/// <summary>
/// Strongly typed native inspector coverage for builtin authored arrays that the generic reflection
/// fallback intentionally keeps read-only. These adapters delegate ordinary/base members back to
/// NativeDataReflectionInspector so geometry metadata and common fields remain intact.
/// </summary>
internal static class BuiltinStructuredInspectorAdapters
{
    private static bool enabled;

    internal static void Enable()
    {
        if (enabled) return;
        ObjectInspectorRegistry.Register(StructuredArrayInspector.Instance, 500);
        enabled = true;
    }

    private sealed class StructuredArrayInspector : IObjectInspectorAdapter
    {
        internal static readonly StructuredArrayInspector Instance = new();

        public bool CanInspect(PlacedObject target) =>
            target?.data is PlacedObject.CustomDecalData ||
            target?.data is Rainbow.RainbowData ||
            target?.data is RainbowNoFade.RainbowNoFadeData;

        public IReadOnlyList<EditorPropertySnapshot> Capture(PlacedObject target)
        {
            if (!CanInspect(target))
                return Array.Empty<EditorPropertySnapshot>();

            List<EditorPropertySnapshot> result = new();
            IReadOnlyList<EditorPropertySnapshot> reflected =
                NativeDataReflectionInspector.Instance.Capture(target);
            for (int i = 0; i < reflected.Count; i++)
            {
                EditorPropertySnapshot property = reflected[i];
                if (property == null || IsReplacedStructuredProperty(property.Key))
                    continue;
                result.Add(property);
            }

            switch (target.data)
            {
                case PlacedObject.CustomDecalData decal:
                    AppendCustomDecal(result, decal);
                    break;
                case Rainbow.RainbowData rainbow:
                    AppendRainbow(result, rainbow.fades);
                    break;
                case RainbowNoFade.RainbowNoFadeData rainbowNoFade:
                    AppendRainbow(result, rainbowNoFade.fades);
                    break;
            }

            return result;
        }

        public bool TrySetValue(PlacedObject target, string key, EditorPropertyValue value)
        {
            if (!CanInspect(target) || string.IsNullOrEmpty(key))
                return false;

            if (target.data is PlacedObject.CustomDecalData decal &&
                TrySetCustomDecal(decal, key, value))
                return true;

            if (target.data is Rainbow.RainbowData rainbow &&
                TrySetRainbow(rainbow.fades, key, value))
                return true;

            if (target.data is RainbowNoFade.RainbowNoFadeData rainbowNoFade &&
                TrySetRainbow(rainbowNoFade.fades, key, value))
                return true;

            return NativeDataReflectionInspector.Instance.TrySetValue(target, key, value);
        }

        private static bool IsReplacedStructuredProperty(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            return key.EndsWith(".vertices", StringComparison.Ordinal) ||
                   key.EndsWith(".fades", StringComparison.Ordinal);
        }

        private static void AppendCustomDecal(
            List<EditorPropertySnapshot> result,
            PlacedObject.CustomDecalData data)
        {
            if (data?.vertices == null ||
                data.vertices.GetLength(0) < 4 ||
                data.vertices.GetLength(1) < 2)
                return;

            float alphaAverage = 0f;
            float erosionAverage = 0f;
            for (int i = 0; i < 4; i++)
            {
                alphaAverage += data.vertices[i, 0];
                erosionAverage += data.vertices[i, 1];

                result.Add(Float(
                    "builtin.customDecal.alpha." + i,
                    "Alpha " + i,
                    data.vertices[i, 0],
                    "Decal Vertices"));

                result.Add(Float(
                    "builtin.customDecal.erosion." + i,
                    "Erosion " + i,
                    data.vertices[i, 1],
                    "Decal Vertices"));
            }

            result.Add(Float(
                "builtin.customDecal.alpha.all",
                "Alpha · All Corners",
                alphaAverage * 0.25f,
                "Decal Vertices"));

            result.Add(Float(
                "builtin.customDecal.erosion.all",
                "Erosion · All Corners",
                erosionAverage * 0.25f,
                "Decal Vertices"));
        }

        private static bool TrySetCustomDecal(
            PlacedObject.CustomDecalData data,
            string key,
            EditorPropertyValue value)
        {
            if (data?.vertices == null ||
                data.vertices.GetLength(0) < 4 ||
                data.vertices.GetLength(1) < 2 ||
                value.Kind != EditorPropertyKind.Float)
                return false;

            float next = Mathf.Clamp01(value.X);
            if (key == "builtin.customDecal.alpha.all")
            {
                for (int i = 0; i < 4; i++) data.vertices[i, 0] = next;
                return true;
            }
            if (key == "builtin.customDecal.erosion.all")
            {
                for (int i = 0; i < 4; i++) data.vertices[i, 1] = next;
                return true;
            }

            if (TryParseIndexedKey(key, "builtin.customDecal.alpha.", out int alphaIndex))
            {
                data.vertices[alphaIndex, 0] = next;
                return true;
            }
            if (TryParseIndexedKey(key, "builtin.customDecal.erosion.", out int erosionIndex))
            {
                data.vertices[erosionIndex, 1] = next;
                return true;
            }

            return false;
        }

        private static void AppendRainbow(List<EditorPropertySnapshot> result, float[] fades)
        {
            if (fades == null || fades.Length < 6)
                return;

            for (int i = 0; i < 4; i++)
            {
                result.Add(Float(
                    "builtin.rainbow.fade." + i,
                    "Fade " + i,
                    fades[i],
                    "Rainbow"));
            }

            result.Add(Float(
                "builtin.rainbow.thickness",
                "Thickness",
                fades[4],
                "Rainbow"));

            result.Add(Float(
                "builtin.rainbow.chance",
                "Per Cycle Chance",
                fades[5],
                "Rainbow"));
        }

        private static bool TrySetRainbow(
            float[] fades,
            string key,
            EditorPropertyValue value)
        {
            if (fades == null || fades.Length < 6 || value.Kind != EditorPropertyKind.Float)
                return false;

            float next = Mathf.Clamp01(value.X);
            if (key == "builtin.rainbow.thickness")
            {
                fades[4] = next;
                return true;
            }
            if (key == "builtin.rainbow.chance")
            {
                fades[5] = next;
                return true;
            }
            if (TryParseIndexedKey(key, "builtin.rainbow.fade.", out int index))
            {
                fades[index] = next;
                return true;
            }

            return false;
        }

        private static bool TryParseIndexedKey(string key, string prefix, out int index)
        {
            index = -1;
            if (string.IsNullOrEmpty(key) ||
                string.IsNullOrEmpty(prefix) ||
                !key.StartsWith(prefix, StringComparison.Ordinal) ||
                !int.TryParse(key.Substring(prefix.Length), out index))
                return false;

            return index >= 0 && index < 4;
        }

        private static EditorPropertySnapshot Float(
            string key,
            string displayName,
            float value,
            string group) =>
            new()
            {
                Key = key,
                DisplayName = displayName,
                Group = group,
                Source = "Rain World model",
                Kind = EditorPropertyKind.Float,
                HasRange = true,
                Min = 0f,
                Max = 1f,
                Step = 0.01f,
                X = value
            };
    }
}
