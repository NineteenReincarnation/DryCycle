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
            target?.data is RainbowNoFade.RainbowNoFadeData ||
            target?.data is PlacedObject.RippleStalkData ||
            target?.data is PlacedObject.FilterData ||
            target?.data is ReliableIggyDirection.ReliableIggyDirectionData ||
            target?.data is CollectToken.CollectTokenData;

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
                case PlacedObject.RippleStalkData ripple:
                    AppendRippleCosmetics(result, ripple);
                    break;
                case PlacedObject.FilterData filter:
                    AppendFilterPlayers(result, filter);
                    break;
                case ReliableIggyDirection.ReliableIggyDirectionData direction:
                    AppendReliableDirectionPlayers(result, direction);
                    break;
                case CollectToken.CollectTokenData token:
                    AppendCollectTokenPlayers(result, token);
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

            if (target.data is PlacedObject.RippleStalkData ripple &&
                TrySetRippleCosmetic(ripple, key, value))
                return true;

            if (target.data is PlacedObject.FilterData filter &&
                TrySetFilterPlayer(filter, key, value))
                return true;

            if (target.data is ReliableIggyDirection.ReliableIggyDirectionData direction &&
                TrySetReliableDirectionPlayer(direction, key, value))
                return true;

            if (target.data is CollectToken.CollectTokenData token &&
                TrySetCollectTokenPlayer(token, key, value))
                return true;

            return NativeDataReflectionInspector.Instance.TrySetValue(target, key, value);
        }

        private static bool IsReplacedStructuredProperty(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            return key.EndsWith(".vertices", StringComparison.Ordinal) ||
                   key.EndsWith(".fades", StringComparison.Ordinal) ||
                   key.EndsWith(".availableToPlayers", StringComparison.Ordinal) ||
                   key.EndsWith(".availableOnTimelines", StringComparison.Ordinal) ||
                   key.EndsWith(".testRippleAmount", StringComparison.Ordinal) ||
                   key.EndsWith(".spiralCoils", StringComparison.Ordinal) ||
                   key.EndsWith(".spiralAmount", StringComparison.Ordinal) ||
                   key.EndsWith(".droopy", StringComparison.Ordinal) ||
                   key.EndsWith(".depth", StringComparison.Ordinal) ||
                   key.EndsWith(".sinWidth", StringComparison.Ordinal) ||
                   key.EndsWith(".sinDist", StringComparison.Ordinal) ||
                   key.EndsWith(".sinOffset", StringComparison.Ordinal) ||
                   key.EndsWith(".update", StringComparison.Ordinal);
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

        private static void AppendRippleCosmetics(
            List<EditorPropertySnapshot> result,
            PlacedObject.RippleStalkData data)
        {
            if (data == null) return;

            AppendNullableFloat(result, "testRippleAmount", "Test Ripple Amount", data.testRippleAmount, 1f);
            AppendNullableFloat(result, "spiralCoils", "Spiral Coils", data.spiralCoils, 10f);
            AppendNullableFloat(result, "spiralAmount", "Spiral Amount", data.spiralAmount, 1f);
            AppendNullableFloat(result, "droopy", "Droopy", data.droopy, 1f);
            AppendNullableFloat(result, "depth", "Depth", data.depth, 30f);
            AppendNullableFloat(result, "sinWidth", "Wave Width", data.sinWidth, 20f);
            AppendNullableFloat(result, "sinDist", "Wave Length", data.sinDist, 100f);
            AppendNullableFloat(result, "sinOffset", "Wave Offset", data.sinOffset, 100f);
        }

        private static void AppendNullableFloat(
            List<EditorPropertySnapshot> result,
            string name,
            string displayName,
            float? current,
            float max)
        {
            const string group = "Ripple Cosmetics";
            string prefix = "builtin.ripple." + name;

            result.Add(Boolean(
                prefix + ".override",
                displayName + " · Override",
                current.HasValue,
                group));

            result.Add(new EditorPropertySnapshot
            {
                Key = prefix + ".value",
                DisplayName = displayName,
                Group = group,
                Source = current.HasValue
                    ? "Rain World model"
                    : "Rain World model · inherited until edited",
                Kind = EditorPropertyKind.Float,
                HasRange = true,
                Min = 0f,
                Max = max,
                Step = max <= 1f ? 0.01f : 0.1f,
                X = current.GetValueOrDefault()
            });
        }

        private static bool TrySetRippleCosmetic(
            PlacedObject.RippleStalkData data,
            string key,
            EditorPropertyValue value)
        {
            if (data == null || string.IsNullOrEmpty(key))
                return false;

            bool changed =
                TrySetNullableFloat(ref data.testRippleAmount, key, "testRippleAmount", value, 1f) ||
                TrySetNullableFloat(ref data.spiralCoils, key, "spiralCoils", value, 10f) ||
                TrySetNullableFloat(ref data.spiralAmount, key, "spiralAmount", value, 1f) ||
                TrySetNullableFloat(ref data.droopy, key, "droopy", value, 1f) ||
                TrySetNullableFloat(ref data.depth, key, "depth", value, 30f) ||
                TrySetNullableFloat(ref data.sinWidth, key, "sinWidth", value, 20f) ||
                TrySetNullableFloat(ref data.sinDist, key, "sinDist", value, 100f) ||
                TrySetNullableFloat(ref data.sinOffset, key, "sinOffset", value, 100f);

            if (changed)
                data.update = true;
            return changed;
        }

        private static bool TrySetNullableFloat(
            ref float? field,
            string key,
            string name,
            EditorPropertyValue value,
            float max)
        {
            string prefix = "builtin.ripple." + name;
            if (key == prefix + ".override")
            {
                if (value.Kind != EditorPropertyKind.Boolean)
                    return false;
                if (value.Boolean)
                    field ??= 0f;
                else
                    field = null;
                return true;
            }

            if (key != prefix + ".value" || value.Kind != EditorPropertyKind.Float)
                return false;

            field = Mathf.Clamp(value.X, 0f, max);
            return true;
        }

        private static void AppendFilterPlayers(
            List<EditorPropertySnapshot> result,
            PlacedObject.FilterData data)
        {
            if (data == null) return;

            for (int i = 0; i < ExtEnum<SlugcatStats.Name>.values.Count; i++)
            {
                string entry = ExtEnum<SlugcatStats.Name>.values.GetEntry(i);
                SlugcatStats.Name name = new(entry);
                if (SlugcatStats.HiddenOrUnplayableSlugcat(name))
                    continue;

                result.Add(Boolean(
                    "builtin.filter.player." + entry,
                    entry,
                    data.availableToPlayers?.Contains(name) == true,
                    "Player Availability"));
            }

            string[] timelines = data.availableOnTimelines == null
                ? Array.Empty<string>()
                : data.availableOnTimelines.ConvertAll(x => x?.value ?? string.Empty).ToArray();
            result.Add(ReadOnly(
                "builtin.filter.timelines",
                "Derived Timelines",
                string.Join(", ", timelines),
                "Player Availability"));
        }

        private static bool TrySetFilterPlayer(
            PlacedObject.FilterData data,
            string key,
            EditorPropertyValue value)
        {
            const string prefix = "builtin.filter.player.";
            if (data == null ||
                value.Kind != EditorPropertyKind.Boolean ||
                string.IsNullOrEmpty(key) ||
                !key.StartsWith(prefix, StringComparison.Ordinal))
                return false;

            string entry = key.Substring(prefix.Length);
            if (string.IsNullOrEmpty(entry))
                return false;

            SlugcatStats.Name name = new(entry);
            if (SlugcatStats.HiddenOrUnplayableSlugcat(name))
                return false;

            data.availableToPlayers ??= new List<SlugcatStats.Name>();
            SetMembership(data.availableToPlayers, name, value.Boolean);
            data.RefreshTimelineList();
            return true;
        }

        private static void AppendReliableDirectionPlayers(
            List<EditorPropertySnapshot> result,
            ReliableIggyDirection.ReliableIggyDirectionData data)
        {
            if (data == null) return;

            for (int i = 0; i < ExtEnum<SlugcatStats.Name>.values.Count; i++)
            {
                string entry = ExtEnum<SlugcatStats.Name>.values.GetEntry(i);
                SlugcatStats.Name name = new(entry);
                result.Add(Boolean(
                    "builtin.reliableIggy.player." + entry,
                    entry,
                    data.availableToPlayers?.Contains(name) == true,
                    "Player Availability"));
            }
        }

        private static bool TrySetReliableDirectionPlayer(
            ReliableIggyDirection.ReliableIggyDirectionData data,
            string key,
            EditorPropertyValue value)
        {
            const string prefix = "builtin.reliableIggy.player.";
            if (data == null ||
                value.Kind != EditorPropertyKind.Boolean ||
                string.IsNullOrEmpty(key) ||
                !key.StartsWith(prefix, StringComparison.Ordinal))
                return false;

            string entry = key.Substring(prefix.Length);
            if (string.IsNullOrEmpty(entry))
                return false;

            SlugcatStats.Name name = new(entry);
            data.availableToPlayers ??= new List<SlugcatStats.Name>();
            SetMembership(data.availableToPlayers, name, value.Boolean);
            return true;
        }

        private static void AppendCollectTokenPlayers(
            List<EditorPropertySnapshot> result,
            CollectToken.CollectTokenData data)
        {
            if (data == null) return;

            for (int i = 0; i < ExtEnum<SlugcatStats.Name>.values.Count; i++)
            {
                string entry = ExtEnum<SlugcatStats.Name>.values.GetEntry(i);
                SlugcatStats.Name name = new(entry);
                if (SlugcatStats.HiddenOrUnplayableSlugcat(name))
                    continue;

                result.Add(Boolean(
                    "builtin.collectToken.player." + entry,
                    entry,
                    data.availableToPlayers?.Contains(name) == true,
                    "Player Availability"));
            }
        }

        private static bool TrySetCollectTokenPlayer(
            CollectToken.CollectTokenData data,
            string key,
            EditorPropertyValue value)
        {
            const string prefix = "builtin.collectToken.player.";
            if (data == null ||
                value.Kind != EditorPropertyKind.Boolean ||
                string.IsNullOrEmpty(key) ||
                !key.StartsWith(prefix, StringComparison.Ordinal))
                return false;

            string entry = key.Substring(prefix.Length);
            if (string.IsNullOrEmpty(entry))
                return false;

            SlugcatStats.Name name = new(entry);
            if (SlugcatStats.HiddenOrUnplayableSlugcat(name))
                return false;

            data.availableToPlayers ??= new List<SlugcatStats.Name>();
            SetMembership(data.availableToPlayers, name, value.Boolean);
            return true;
        }

        private static void SetMembership(
            List<SlugcatStats.Name> values,
            SlugcatStats.Name name,
            bool enabled)
        {
            bool present = values.Contains(name);
            if (enabled && !present)
                values.Add(name);
            else if (!enabled && present)
                values.Remove(name);
        }

        private static EditorPropertySnapshot Boolean(
            string key,
            string displayName,
            bool value,
            string group) =>
            new()
            {
                Key = key,
                DisplayName = displayName,
                Group = group,
                Source = "Rain World model",
                Kind = EditorPropertyKind.Boolean,
                BooleanValue = value
            };

        private static EditorPropertySnapshot ReadOnly(
            string key,
            string displayName,
            string value,
            string group) =>
            new()
            {
                Key = key,
                DisplayName = displayName,
                Group = group,
                Source = "Rain World model",
                Kind = EditorPropertyKind.ReadOnly,
                StringValue = value ?? string.Empty
            };

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
