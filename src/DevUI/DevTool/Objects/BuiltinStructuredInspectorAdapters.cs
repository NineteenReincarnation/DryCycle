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
            target?.data is CollectToken.CollectTokenData ||
            ModManager.Watcher && target?.data is Watcher.WarpPoint.WarpPointData ||
            ModManager.Watcher && (
                target?.data is Watcher.UrbanCandlePlacer.UrbanCandlePlacerData ||
                target?.data is Watcher.FloatingDebrisData);

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
                if (property == null ||
                    IsReplacedStructuredProperty(property.Key) ||
                    target.data is Watcher.UrbanCandlePlacer.UrbanCandlePlacerData &&
                    IsUrbanCandlePlacerManagedProperty(property.Key) ||
                    target.data is Watcher.FloatingDebrisData &&
                    IsFloatingDebrisManagedProperty(property.Key) ||
                    target.data is Watcher.WarpPoint.WarpPointData &&
                    IsWarpPointManagedProperty(property.Key))
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
                case Watcher.UrbanCandlePlacer.UrbanCandlePlacerData:
                    AppendUrbanCandlePlacerActions(result);
                    break;
                case Watcher.FloatingDebrisData floatingDebris:
                    AppendFloatingDebris(result, floatingDebris);
                    break;
                case Watcher.WarpPoint.WarpPointData warpPoint:
                    AppendWarpPoint(result, warpPoint);
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

            if (target.data is Watcher.FloatingDebrisData floatingDebris &&
                TrySetFloatingDebris(floatingDebris, key, value))
                return true;

            if (target.data is Watcher.WarpPoint.WarpPointData warpPoint &&
                TrySetWarpPoint(warpPoint, key, value))
                return true;

            return NativeDataReflectionInspector.Instance.TrySetValue(target, key, value);
        }

        private static void AppendWarpPoint(
            List<EditorPropertySnapshot> result,
            Watcher.WarpPoint.WarpPointData data)
        {
            if (data == null) return;

            result.Add(ReadOnly(
                "builtin.warpPoint.uuid",
                "UUID Pair",
                data.uuidPair ?? string.Empty,
                "Warp Identity"));

            result.Add(new EditorPropertySnapshot
            {
                Key = "builtin.warpPoint.destRegion",
                DisplayName = "Destination Region",
                Group = "Destination",
                Source = "Rain World model",
                Kind = EditorPropertyKind.String,
                StringValue = data.destRegion ?? string.Empty
            });
            result.Add(new EditorPropertySnapshot
            {
                Key = "builtin.warpPoint.destRoom",
                DisplayName = "Destination Room",
                Group = "Destination",
                Source = "Rain World model",
                Kind = EditorPropertyKind.String,
                StringValue = data.destRoom ?? string.Empty
            });
            result.Add(Boolean(
                "builtin.warpPoint.hasDestPos",
                "Use Destination Position",
                data.destPos.HasValue,
                "Destination"));

            if (data.destPos.HasValue)
            {
                Vector2 position = data.destPos.Value;
                result.Add(new EditorPropertySnapshot
                {
                    Key = "builtin.warpPoint.destPos",
                    DisplayName = "Destination Position",
                    Group = "Destination",
                    Source = "Rain World model",
                    Kind = EditorPropertyKind.Vector2,
                    X = position.x,
                    Y = position.y
                });
            }

            result.Add(IntegerRange(
                "builtin.warpPoint.uses",
                "Uses · 0 = Unlimited",
                data.limitedUse ? Mathf.Clamp(data.uses, 1, 15) : 0,
                0,
                15,
                "Warp Modifiers"));

            Watcher.WarpPoint.WarpPointData.EffectSettings effect = data.effectSettings;
            AppendWarpEffect(result, "vignette", "Vignette", effect.vignette, 15);
            AppendWarpEffect(result, "darkness", "Darkness", effect.darkness, 15);
            AppendWarpEffect(result, "spiralGap", "Spiral Gap", effect.spiralGap, 15);
            AppendWarpEffect(result, "swirlIntensity", "Swirl Intensity", effect.swirlIntensity, 15);
            AppendWarpEffect(result, "lensingIntensity", "Lensing Intensity", effect.lensingIntensity, 15);
            AppendWarpEffect(result, "noiseIntensity", "Noise Intensity", effect.noiseIntensity, 15);
            AppendWarpEffect(result, "spiralTwist", "Spiral Twist", effect.spiralTwist, 15);
            AppendWarpEffect(result, "agitationSpeed", "Agitation Speed", effect.agitationSpeed, 15);
            AppendWarpEffect(result, "spaghettification", "Spaghettification", effect.spaghettification, 15);
            AppendWarpEffect(result, "activeDuration", "Active Duration", effect.activeDuration, 400);
            AppendWarpEffect(result, "triggerDuration", "Trigger Duration", effect.triggerDuration, 400);
            result.Add(Boolean(
                "builtin.warpPoint.effect.outerRimCosmetic",
                "Outer Rim Cosmetic",
                effect.outerRimCosmetic,
                "Warp Effects"));
            result.Add(Boolean(
                "builtin.warpPoint.effect.badWarpCosmetic",
                "Bad Warp Cosmetic",
                effect.badWarpCosmetic,
                "Warp Effects"));
            result.Add(Boolean(
                "builtin.warpPoint.effect.spawnBigRift",
                "Spawn Big Rift",
                effect.spawnBigRift,
                "Warp Effects"));

            result.Add(Action("builtin.warpPoint.preset.default", "Preset · Default", "Warp Effects"));
            result.Add(Action("builtin.warpPoint.preset.outerRim", "Preset · Outer Rim", "Warp Effects"));
            result.Add(Action("builtin.warpPoint.preset.badWarp", "Preset · Bad Warp", "Warp Effects"));
            result.Add(Action("builtin.warpPoint.preset.dynamic", "Preset · Dynamic", "Warp Effects"));
        }

        private static void AppendWarpEffect(
            List<EditorPropertySnapshot> result,
            string key,
            string displayName,
            int value,
            int max)
        {
            result.Add(IntegerRange(
                "builtin.warpPoint.effect." + key,
                displayName,
                value,
                0,
                max,
                "Warp Effects"));
        }

        private static bool TrySetWarpPoint(
            Watcher.WarpPoint.WarpPointData data,
            string key,
            EditorPropertyValue value)
        {
            if (data == null || string.IsNullOrEmpty(key))
                return false;

            switch (key)
            {
                case "builtin.warpPoint.destRegion":
                    if (value.Kind != EditorPropertyKind.String) return false;
                    data.destRegion = string.IsNullOrWhiteSpace(value.Text) ? null : value.Text.Trim();
                    data.destCam = -1;
                    return true;

                case "builtin.warpPoint.destRoom":
                    if (value.Kind != EditorPropertyKind.String) return false;
                    data.destRoom = string.IsNullOrWhiteSpace(value.Text) ? null : value.Text.Trim();
                    data.destCam = -1;
                    return true;

                case "builtin.warpPoint.hasDestPos":
                    if (value.Kind != EditorPropertyKind.Boolean) return false;
                    data.destPos = value.Boolean ? data.destPos ?? Vector2.zero : null;
                    data.destCam = -1;
                    return true;

                case "builtin.warpPoint.destPos":
                    if (value.Kind != EditorPropertyKind.Vector2) return false;
                    data.destPos = new Vector2(value.X, value.Y);
                    data.destCam = -1;
                    return true;

                case "builtin.warpPoint.uses":
                    if (value.Kind != EditorPropertyKind.Integer) return false;
                    data.uses = Mathf.Clamp(value.Integer, 0, 15);
                    data.limitedUse = data.uses > 0;
                    return true;

                case "builtin.warpPoint.preset.default":
                    if (value.Kind != EditorPropertyKind.Action) return false;
                    data.effectSettings = Watcher.WarpPoint.WarpPointData.EffectSettings.DefaultCosmetics();
                    return true;

                case "builtin.warpPoint.preset.outerRim":
                    if (value.Kind != EditorPropertyKind.Action) return false;
                    data.effectSettings = Watcher.WarpPoint.WarpPointData.EffectSettings.OuterRimCosmetics();
                    return true;

                case "builtin.warpPoint.preset.badWarp":
                    if (value.Kind != EditorPropertyKind.Action) return false;
                    data.effectSettings = Watcher.WarpPoint.WarpPointData.EffectSettings.BadWarpCosmetics();
                    return true;

                case "builtin.warpPoint.preset.dynamic":
                    if (value.Kind != EditorPropertyKind.Action) return false;
                    data.effectSettings = Watcher.WarpPoint.WarpPointData.EffectSettings.DynamicCosmetics();
                    return true;
            }

            const string effectPrefix = "builtin.warpPoint.effect.";
            if (!key.StartsWith(effectPrefix, StringComparison.Ordinal))
                return false;

            Watcher.WarpPoint.WarpPointData.EffectSettings effect = data.effectSettings;
            string field = key.Substring(effectPrefix.Length);

            if (value.Kind == EditorPropertyKind.Boolean)
            {
                switch (field)
                {
                    case "outerRimCosmetic":
                        effect.outerRimCosmetic = value.Boolean;
                        break;
                    case "badWarpCosmetic":
                        effect.badWarpCosmetic = value.Boolean;
                        break;
                    case "spawnBigRift":
                        effect.spawnBigRift = value.Boolean;
                        break;
                    default:
                        return false;
                }

                data.effectSettings = effect;
                return true;
            }

            if (value.Kind != EditorPropertyKind.Integer)
                return false;

            int scalar = value.Integer;
            switch (field)
            {
                case "vignette": effect.vignette = Mathf.Clamp(scalar, 0, 15); break;
                case "darkness": effect.darkness = Mathf.Clamp(scalar, 0, 15); break;
                case "spiralGap": effect.spiralGap = Mathf.Clamp(scalar, 0, 15); break;
                case "swirlIntensity": effect.swirlIntensity = Mathf.Clamp(scalar, 0, 15); break;
                case "lensingIntensity": effect.lensingIntensity = Mathf.Clamp(scalar, 0, 15); break;
                case "noiseIntensity": effect.noiseIntensity = Mathf.Clamp(scalar, 0, 15); break;
                case "spiralTwist": effect.spiralTwist = Mathf.Clamp(scalar, 0, 15); break;
                case "agitationSpeed": effect.agitationSpeed = Mathf.Clamp(scalar, 0, 15); break;
                case "spaghettification": effect.spaghettification = Mathf.Clamp(scalar, 0, 15); break;
                case "activeDuration": effect.activeDuration = Mathf.Clamp(scalar, 0, 400); break;
                case "triggerDuration": effect.triggerDuration = Mathf.Clamp(scalar, 0, 400); break;
                default: return false;
            }

            data.effectSettings = effect;
            return true;
        }

        private static bool IsWarpPointManagedProperty(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;

            return key.EndsWith(".effectSettings", StringComparison.Ordinal) ||
                   key.EndsWith(".destRegion", StringComparison.Ordinal) ||
                   key.EndsWith(".destRoom", StringComparison.Ordinal) ||
                   key.EndsWith(".destPos", StringComparison.Ordinal) ||
                   key.EndsWith(".uuidPair", StringComparison.Ordinal) ||
                   key.EndsWith(".limitedUse", StringComparison.Ordinal) ||
                   key.EndsWith(".uses", StringComparison.Ordinal);
        }

        private static void AppendFloatingDebris(
            List<EditorPropertySnapshot> result,
            Watcher.FloatingDebrisData data)
        {
            if (data == null) return;

            Watcher.FloatingDebris.UIText ui = null;
            if (!string.IsNullOrEmpty(data.type) &&
                Watcher.FloatingDebris.types.TryGetValue(data.type, out Watcher.FloatingDebris.Floater.IFloaterSpawner spawner))
            {
                try { ui = spawner.GetUIText(); }
                catch { }
            }

            result.Add(EnumProperty(
                "builtin.floatingDebris.type",
                "Type",
                FloatingDebrisTypeOptions(),
                data.type,
                "Floating Debris"));

            result.Add(ReadOnly(
                "builtin.floatingDebris.seed",
                "Seed",
                data.seed.ToString(),
                "Floating Debris"));

            if (ui?.amount?.hide != true)
                result.Add(IntegerRange(
                    "builtin.floatingDebris.amount",
                    ui?.amount?.title ?? "Amount",
                    data.numberOfFloaters,
                    0,
                    100,
                    "Floating Debris"));

            if (ui?.depthNear?.hide != true)
                result.Add(IntegerRange(
                    "builtin.floatingDebris.depthNear",
                    ui?.depthNear?.title ?? "Depth, Near",
                    data.depthNear,
                    0,
                    30,
                    "Floating Debris"));

            if (ui?.depthFar?.hide != true)
                result.Add(IntegerRange(
                    "builtin.floatingDebris.depthFar",
                    ui?.depthFar?.title ?? "Depth, Far",
                    data.depthFar,
                    0,
                    30,
                    "Floating Debris"));

            if (ui?.scaleMin?.hide != true)
                result.Add(FloatRange(
                    "builtin.floatingDebris.scaleMin",
                    ui?.scaleMin?.title ?? "Scale Min",
                    data.scaleMinimum,
                    0f,
                    3f,
                    "Floating Debris"));

            if (ui?.scaleMax?.hide != true)
                result.Add(FloatRange(
                    "builtin.floatingDebris.scaleMax",
                    ui?.scaleMax?.title ?? "Scale Max",
                    data.scaleMaximum,
                    0f,
                    3f,
                    "Floating Debris"));

            if (ui?.movementAmount?.hide != true)
                result.Add(FloatRange(
                    "builtin.floatingDebris.movement",
                    ui?.movementAmount?.title ?? "Movement Amount",
                    data.movement,
                    0f,
                    1f,
                    "Floating Debris"));

            result.Add(ReadOnly(
                "builtin.floatingDebris.controlPoints",
                "Control Points",
                (data.controlPointPosX?.Count ?? 0).ToString(),
                "Control Points"));

            result.Add(Action("builtin.floatingDebris.newSeed", "New Seed", "Actions"));
            result.Add(Action("builtin.floatingDebris.addLeft", "Add Control Point · Left", "Control Points"));
            result.Add(Action("builtin.floatingDebris.addRight", "Add Control Point · Right", "Control Points"));
            result.Add(Action("builtin.floatingDebris.removeLeft", "Remove Control Point · Left", "Control Points"));
            result.Add(Action("builtin.floatingDebris.removeRight", "Remove Control Point · Right", "Control Points"));
        }

        private static bool TrySetFloatingDebris(
            Watcher.FloatingDebrisData data,
            string key,
            EditorPropertyValue value)
        {
            if (data == null || string.IsNullOrEmpty(key))
                return false;

            switch (key)
            {
                case "builtin.floatingDebris.type":
                    if (value.Kind != EditorPropertyKind.Enum)
                        return false;
                    string[] options = FloatingDebrisTypeOptions();
                    if (value.Integer < 0 || value.Integer >= options.Length)
                        return false;
                    data.type = options[value.Integer];
                    return true;

                case "builtin.floatingDebris.amount":
                    if (value.Kind != EditorPropertyKind.Integer) return false;
                    data.numberOfFloaters = Mathf.Clamp(value.Integer, 0, 100);
                    return true;

                case "builtin.floatingDebris.depthNear":
                    if (value.Kind != EditorPropertyKind.Integer) return false;
                    data.depthNear = Mathf.Clamp(value.Integer, 0, 30);
                    return true;

                case "builtin.floatingDebris.depthFar":
                    if (value.Kind != EditorPropertyKind.Integer) return false;
                    data.depthFar = Mathf.Clamp(value.Integer, 0, 30);
                    return true;

                case "builtin.floatingDebris.scaleMin":
                    if (value.Kind != EditorPropertyKind.Float) return false;
                    data.scaleMinimum = Mathf.Clamp(value.X, 0f, 3f);
                    return true;

                case "builtin.floatingDebris.scaleMax":
                    if (value.Kind != EditorPropertyKind.Float) return false;
                    data.scaleMaximum = Mathf.Clamp(value.X, 0f, 3f);
                    return true;

                case "builtin.floatingDebris.movement":
                    if (value.Kind != EditorPropertyKind.Float) return false;
                    data.movement = Mathf.Clamp01(value.X);
                    return true;

                default:
                    return false;
            }
        }

        private static bool IsFloatingDebrisManagedProperty(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;

            return key.EndsWith(".obj", StringComparison.Ordinal) ||
                   key.EndsWith(".seed", StringComparison.Ordinal) ||
                   key.EndsWith("._depthNear", StringComparison.Ordinal) ||
                   key.EndsWith("._depthFar", StringComparison.Ordinal) ||
                   key.EndsWith(".depthNear", StringComparison.Ordinal) ||
                   key.EndsWith(".depthFar", StringComparison.Ordinal) ||
                   key.EndsWith(".numberOfFloaters", StringComparison.Ordinal) ||
                   key.EndsWith("._scaleMinimum", StringComparison.Ordinal) ||
                   key.EndsWith("._scaleMaximum", StringComparison.Ordinal) ||
                   key.EndsWith(".scaleMinimum", StringComparison.Ordinal) ||
                   key.EndsWith(".scaleMaximum", StringComparison.Ordinal) ||
                   key.EndsWith(".movement", StringComparison.Ordinal) ||
                   key.EndsWith(".type", StringComparison.Ordinal) ||
                   key.EndsWith(".controlPointPosX", StringComparison.Ordinal) ||
                   key.EndsWith(".controlPointPosY", StringComparison.Ordinal) ||
                   key.EndsWith(".controlPointOffsetAmount", StringComparison.Ordinal) ||
                   key.EndsWith(".controlPointDepthOffset", StringComparison.Ordinal) ||
                   key.EndsWith(".controlPointScaleOffset", StringComparison.Ordinal) ||
                   key.EndsWith(".controlPointExtraOffset", StringComparison.Ordinal);
        }

        private static string[] FloatingDebrisTypeOptions()
        {
            string[] result = new string[Watcher.FloatingDebris.types.Count];
            int index = 0;
            foreach (string name in Watcher.FloatingDebris.types.Keys)
                result[index++] = name ?? string.Empty;
            return result;
        }

        private static void AppendUrbanCandlePlacerActions(
            List<EditorPropertySnapshot> result)
        {
            result.Add(Action(
                "builtin.urbanCandles.spawn",
                "Spawn Candles",
                "Candle Brush"));
            result.Add(Action(
                "builtin.urbanCandles.remove",
                "Remove Candles",
                "Candle Brush"));
            result.Add(Action(
                "builtin.urbanCandles.removeAll",
                "Remove All Candles",
                "Candle Brush"));
        }

        private static bool IsUrbanCandlePlacerManagedProperty(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            return key.EndsWith(".placedCandles", StringComparison.Ordinal) ||
                   key.EndsWith(".candles", StringComparison.Ordinal) ||
                   key.EndsWith(".radius", StringComparison.Ordinal) ||
                   key.EndsWith(".pos", StringComparison.Ordinal);
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

        private static EditorPropertySnapshot FloatRange(
            string key,
            string displayName,
            float value,
            float min,
            float max,
            string group) =>
            new()
            {
                Key = key,
                DisplayName = displayName,
                Group = group,
                Source = "Rain World model",
                Kind = EditorPropertyKind.Float,
                HasRange = true,
                Min = min,
                Max = max,
                Step = 0.01f,
                X = value
            };

        private static EditorPropertySnapshot IntegerRange(
            string key,
            string displayName,
            int value,
            int min,
            int max,
            string group) =>
            new()
            {
                Key = key,
                DisplayName = displayName,
                Group = group,
                Source = "Rain World model",
                Kind = EditorPropertyKind.Integer,
                HasRange = true,
                Min = min,
                Max = max,
                Step = 1f,
                IntegerValue = value
            };

        private static EditorPropertySnapshot EnumProperty(
            string key,
            string displayName,
            string[] options,
            string current,
            string group)
        {
            options ??= Array.Empty<string>();
            int selected = Array.IndexOf(options, current ?? string.Empty);
            return new EditorPropertySnapshot
            {
                Key = key,
                DisplayName = displayName,
                Group = group,
                Source = "Rain World model",
                Kind = EditorPropertyKind.Enum,
                IntegerValue = Math.Max(0, selected),
                StringValue = current ?? string.Empty,
                Options = options
            };
        }

        private static EditorPropertySnapshot Action(
            string key,
            string displayName,
            string group) =>
            new()
            {
                Key = key,
                DisplayName = displayName,
                Group = group,
                Source = "Rain World model",
                Kind = EditorPropertyKind.Action
            };

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
