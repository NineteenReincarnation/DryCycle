using System;
using DevInterface;
using DryCycle.RoomSettingsExt.DevUI;
using RWCustom;
using UnityEngine;
using DevUIOwner = DevInterface.DevUI;

namespace DryCycle.RoomSettingsExt;

internal static class RGBEffectRuntime
{
    internal const string EffectAName = "RGB-ReplaceEffectColor-A";
    internal const string EffectBName = "RGB-ReplaceEffectColor-B";

    // Vanilla RoomEffect serialization uses '-' as a structural delimiter. The public
    // ExtEnum names intentionally keep the mapper-facing hyphenated names, while load
    // temporarily aliases them so RoomEffect.FromString can still use vanilla parsing.
    private const string SerializedAliasA = "DryCycleRGBReplaceEffectColorA";
    private const string SerializedAliasB = "DryCycleRGBReplaceEffectColorB";

    internal static readonly RoomSettings.RoomEffect.Type EffectA =
        new(EffectAName, register: true);

    internal static readonly RoomSettings.RoomEffect.Type EffectB =
        new(EffectBName, register: true);

    internal static readonly RoomSettingsPage.DevEffectsCategories DryCycleCategory =
        new("DryCycle", register: true);

    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.RoomSettings.RoomEffect.GetSliderCount += RoomEffect_GetSliderCount;
        On.RoomSettings.RoomEffect.GetSliderName += RoomEffect_GetSliderName;
        On.RoomSettings.RoomEffect.GetSliderDefault += RoomEffect_GetSliderDefault;
        On.RoomSettings.LoadEffects += RoomSettings_LoadEffects;
        On.DevInterface.RoomSettingsPage.DevEffectGetCategoryFromEffectType += RoomSettingsPage_DevEffectGetCategoryFromEffectType;
        On.DevInterface.EffectPanel.ctor += EffectPanel_ctor;
        On.RoomCamera.ModifyEffectColorA += RoomCamera_ModifyEffectColorA;
        On.RoomCamera.ModifyEffectColorB += RoomCamera_ModifyEffectColorB;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.RoomCamera.ModifyEffectColorB -= RoomCamera_ModifyEffectColorB;
        On.RoomCamera.ModifyEffectColorA -= RoomCamera_ModifyEffectColorA;
        On.DevInterface.EffectPanel.ctor -= EffectPanel_ctor;
        On.DevInterface.RoomSettingsPage.DevEffectGetCategoryFromEffectType -= RoomSettingsPage_DevEffectGetCategoryFromEffectType;
        On.RoomSettings.LoadEffects -= RoomSettings_LoadEffects;
        On.RoomSettings.RoomEffect.GetSliderDefault -= RoomEffect_GetSliderDefault;
        On.RoomSettings.RoomEffect.GetSliderName -= RoomEffect_GetSliderName;
        On.RoomSettings.RoomEffect.GetSliderCount -= RoomEffect_GetSliderCount;
        RGBColorClipboard.ClearTransientState();
        _enabled = false;
    }

    internal static bool IsRGBEffect(RoomSettings.RoomEffect.Type type)
        => IsA(type) || IsB(type);

    internal static bool IsA(RoomSettings.RoomEffect.Type type)
        => TypeValueEquals(type, EffectAName) || TypeValueEquals(type, SerializedAliasA);

    internal static bool IsB(RoomSettings.RoomEffect.Type type)
        => TypeValueEquals(type, EffectBName) || TypeValueEquals(type, SerializedAliasB);

    internal static Color ReadColor(RoomSettings.RoomEffect effect)
    {
        if (effect == null)
        {
            return Color.white;
        }

        float r = Mathf.Clamp01(effect.amount);
        float g = effect.extraAmounts != null && effect.extraAmounts.Length > 0
            ? Mathf.Clamp01(effect.extraAmounts[0])
            : r;
        float b = effect.extraAmounts != null && effect.extraAmounts.Length > 1
            ? Mathf.Clamp01(effect.extraAmounts[1])
            : g;
        return new Color(r, g, b, 1f);
    }

    internal static void WriteColor(RoomSettings.RoomEffect effect, Color color, DevUIOwner owner, bool force = false)
    {
        if (effect == null || (!force && effect.inherited))
        {
            return;
        }

        EnsureStorage(effect);
        effect.amount = Mathf.Clamp01(color.r);
        effect.extraAmounts[0] = Mathf.Clamp01(color.g);
        effect.extraAmounts[1] = Mathf.Clamp01(color.b);
        RefreshCurrentCamera(owner);
    }

    internal static void RefreshCurrentCamera(DevUIOwner owner)
    {
        Room room = owner?.room;
        RoomCamera camera = room?.game?.cameras != null && room.game.cameras.Length > 0
            ? room.game.cameras[0]
            : null;
        if (camera == null || room.roomSettings == null)
        {
            return;
        }

        camera.ApplyEffectColorsToAllPaletteTextures(
            room.roomSettings.EffectColorA,
            room.roomSettings.EffectColorB);
    }

    private static int RoomEffect_GetSliderCount(
        On.RoomSettings.RoomEffect.orig_GetSliderCount orig,
        RoomSettings.RoomEffect.Type type)
    {
        return IsRGBEffect(type) ? 3 : orig(type);
    }

    private static string RoomEffect_GetSliderName(
        On.RoomSettings.RoomEffect.orig_GetSliderName orig,
        RoomSettings.RoomEffect.Type type,
        int index)
    {
        if (!IsRGBEffect(type))
        {
            return orig(type, index);
        }

        return index switch
        {
            0 => "R",
            1 => "G",
            2 => "B",
            _ => "RGB"
        };
    }

    private static float RoomEffect_GetSliderDefault(
        On.RoomSettings.RoomEffect.orig_GetSliderDefault orig,
        RoomSettings.RoomEffect.Type type,
        int index)
    {
        return IsRGBEffect(type) ? 1f : orig(type, index);
    }

    private static void RoomSettings_LoadEffects(
        On.RoomSettings.orig_LoadEffects orig,
        RoomSettings self,
        string[] serializedEffects)
    {
        if (serializedEffects == null || serializedEffects.Length == 0)
        {
            orig(self, serializedEffects);
            return;
        }

        string[] normalized = new string[serializedEffects.Length];
        for (int i = 0; i < serializedEffects.Length; i++)
        {
            string value = serializedEffects[i] ?? string.Empty;
            normalized[i] = ReplaceTypePrefix(value, EffectAName, SerializedAliasA);
            normalized[i] = ReplaceTypePrefix(normalized[i], EffectBName, SerializedAliasB);
        }

        int firstAdded = self.effects?.Count ?? 0;
        orig(self, normalized);

        if (self.effects == null)
        {
            return;
        }

        for (int i = firstAdded; i < self.effects.Count; i++)
        {
            RoomSettings.RoomEffect effect = self.effects[i];
            if (effect == null)
            {
                continue;
            }

            if (TypeValueEquals(effect.type, SerializedAliasA))
            {
                effect.type = EffectA;
                EnsureStorage(effect);
            }
            else if (TypeValueEquals(effect.type, SerializedAliasB))
            {
                effect.type = EffectB;
                EnsureStorage(effect);
            }
        }
    }

    private static RoomSettingsPage.DevEffectsCategories RoomSettingsPage_DevEffectGetCategoryFromEffectType(
        On.DevInterface.RoomSettingsPage.orig_DevEffectGetCategoryFromEffectType orig,
        RoomSettingsPage self,
        RoomSettings.RoomEffect.Type type)
    {
        return IsRGBEffect(type) ? DryCycleCategory : orig(self, type);
    }

    private static void EffectPanel_ctor(
        On.DevInterface.EffectPanel.orig_ctor orig,
        EffectPanel self,
        DevUIOwner owner,
        DevUINode parentNode,
        Vector2 pos,
        RoomSettings.RoomEffect effect)
    {
        orig(self, owner, parentNode, pos, effect);
        if (!IsRGBEffect(effect?.type))
        {
            return;
        }

        for (int i = self.subNodes.Count - 1; i >= 0; i--)
        {
            self.subNodes[i].ClearSprites();
        }
        self.subNodes.Clear();

        EnsureStorage(effect);
        self.size = new Vector2(430f, 455f);
        self.subNodes.Add(new RGBColorEffectEditor(
            owner,
            "DryCycle_RGB_Color_Editor",
            self,
            Vector2.zero,
            effect,
            IsA(effect.type)));
        self.Refresh();
    }

    private static Color[] RoomCamera_ModifyEffectColorA(
        On.RoomCamera.orig_ModifyEffectColorA orig,
        RoomCamera self,
        Color[] colors)
    {
        Color[] result = orig(self, colors);
        RoomSettings.RoomEffect effect = self?.room?.roomSettings?.GetEffect(EffectA);
        return effect == null ? result : ApplyPreserveLuminance(result, ReadColor(effect));
    }

    private static Color[] RoomCamera_ModifyEffectColorB(
        On.RoomCamera.orig_ModifyEffectColorB orig,
        RoomCamera self,
        Color[] colors)
    {
        Color[] result = orig(self, colors);
        RoomSettings.RoomEffect effect = self?.room?.roomSettings?.GetEffect(EffectB);
        return effect == null ? result : ApplyPreserveLuminance(result, ReadColor(effect));
    }

    private static Color[] ApplyPreserveLuminance(Color[] colors, Color target)
    {
        if (colors == null || colors.Length == 0)
        {
            return colors;
        }

        Vector3 targetHsl = Custom.RGB2HSL(target);
        float referenceLightness = 0f;
        for (int i = 0; i < colors.Length; i++)
        {
            referenceLightness = Mathf.Max(referenceLightness, Custom.RGB2HSL(colors[i]).z);
        }
        referenceLightness = Mathf.Max(referenceLightness, 0.0001f);

        // The brightest sample in each native 2x2 EffectColor block becomes the
        // mapper-selected color lightness. Darker samples preserve their relative
        // lightness ratios, keeping Rain World's material/light structure intact.
        for (int i = 0; i < colors.Length; i++)
        {
            Color original = colors[i];
            float relativeLightness = Mathf.Clamp01(Custom.RGB2HSL(original).z / referenceLightness);
            float mappedLightness = Mathf.Clamp01(relativeLightness * targetHsl.z);
            Color mapped = Custom.HSL2RGB(targetHsl.x, targetHsl.y, mappedLightness);
            mapped.a = original.a;
            colors[i] = mapped;
        }

        return colors;
    }

    private static void EnsureStorage(RoomSettings.RoomEffect effect)
    {
        if (effect == null)
        {
            return;
        }

        if (effect.extraAmounts != null && effect.extraAmounts.Length >= 2)
        {
            return;
        }

        float fallback = Mathf.Clamp01(effect.amount);
        float g = effect.extraAmounts != null && effect.extraAmounts.Length > 0
            ? Mathf.Clamp01(effect.extraAmounts[0])
            : fallback;
        effect.extraAmounts = new[] { g, g };
    }

    private static string ReplaceTypePrefix(string serialized, string publicName, string alias)
    {
        string prefix = publicName + "-";
        if (serialized.StartsWith(prefix, StringComparison.Ordinal))
        {
            return alias + serialized.Substring(publicName.Length);
        }
        return serialized;
    }

    private static bool TypeValueEquals(RoomSettings.RoomEffect.Type type, string value)
        => type != null && string.Equals(type.value, value, StringComparison.Ordinal);
}

internal static class RoomSettingsExtRuntime
{
    internal static void Enable() => RGBEffectRuntime.Enable();
    internal static void Disable() => RGBEffectRuntime.Disable();
}
