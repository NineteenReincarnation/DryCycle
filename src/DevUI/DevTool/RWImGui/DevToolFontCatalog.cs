using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ImGuiNET;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Describes fonts that are already present in RWImGui's shared font atlas.
///
/// DryCycle deliberately does not call ImFontAtlas.AddFont* itself. RWImGui owns the native ImGui
/// context, renderer backend and atlas texture lifetime; mutating that shared atlas from a consumer
/// plugin after or during backend initialization can invalidate the renderer texture and trigger a
/// native ImGui "Font Atlas not built" assertion. Local files remain discoverable assets, but a face
/// is selectable only after RWImGui (or another atlas owner) has registered it during its own font
/// initialization lifecycle.
/// </summary>
internal static unsafe class DevToolFontCatalog
{
    internal const string DefaultChineseFamily = "HarmonyOS Sans SC";
    internal const string UbuntuMonoFamily = "Ubuntu Mono";

    internal static string FontDirectory
    {
        get
        {
            string pluginDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
            string modVersionDirectory = Path.GetDirectoryName(pluginDirectory) ?? pluginDirectory;
            return Path.Combine(modVersionDirectory, "ui", "fonts");
        }
    }

    /// <summary>
    /// Enumerates only faces that are already part of the live RWImGui atlas. This method is called
    /// from the DevTool render context, never during BepInEx/RainWorld startup.
    /// </summary>
    internal static string[] GetAvailableChineseFamilies()
    {
        List<string> families = new();

        try
        {
            ImVector<ImFontPtr> fonts = ImGui.GetIO().Fonts.Fonts;
            for (int i = 0; i < fonts.Size; i++)
            {
                ImFontPtr font = fonts[i];
                string name = ReadFontName(font, i);
                if (!IsChineseUiSelectable(font, name)) continue;
                AddUnique(families, FamilyFromName(name));
            }
        }
        catch
        {
            // Keep the font settings window usable if the backend is temporarily between contexts.
        }

        families.Sort((a, b) =>
        {
            bool aDefault = string.Equals(a, DefaultChineseFamily, StringComparison.OrdinalIgnoreCase);
            bool bDefault = string.Equals(b, DefaultChineseFamily, StringComparison.OrdinalIgnoreCase);
            if (aDefault != bDefault) return aDefault ? -1 : 1;
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        });
        return families.ToArray();
    }

    /// <summary>
    /// Returns how many font files are available in Ancient Site/newest/ui/fonts. This is metadata
    /// only; finding a file here never mutates the shared ImGui atlas.
    /// </summary>
    internal static int CountLocalFontFiles()
    {
        try
        {
            if (!Directory.Exists(FontDirectory)) return 0;
            string[] files = Directory.GetFiles(FontDirectory, "*.*", SearchOption.TopDirectoryOnly);
            int count = 0;
            for (int i = 0; i < files.Length; i++)
            {
                string extension = Path.GetExtension(files[i]);
                if (string.Equals(extension, ".ttf", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".otf", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".ttc", StringComparison.OrdinalIgnoreCase))
                    count++;
            }
            return count;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Determines whether a face is allowed in the Chinese-interface font selector. Normally a
    /// face must expose representative Simplified-Chinese glyphs. UbuntuMono-Regular.ttf remains an
    /// explicit developer-facing option when it is already present in RWImGui's atlas.
    /// </summary>
    internal static bool IsChineseUiSelectable(ImFontPtr font, string candidateName)
    {
        return SupportsChinese(font) || IsUbuntuMono(candidateName);
    }

    internal static bool IsFamilyMatch(string candidateName, string family)
    {
        if (string.IsNullOrWhiteSpace(family)) return true;
        return string.Equals(
            NormalizeFamily(FamilyFromName(candidateName)),
            NormalizeFamily(family),
            StringComparison.OrdinalIgnoreCase);
    }

    internal static string FriendlyFaceName(string candidateName)
    {
        if (string.IsNullOrWhiteSpace(candidateName)) return string.Empty;
        string value = candidateName.Trim();
        int comma = value.IndexOf(',');
        if (comma > 0) value = value.Substring(0, comma);
        value = Path.GetFileName(value);
        value = Path.GetFileNameWithoutExtension(value);
        return value.Replace('_', ' ').Trim();
    }

    internal static string FamilyFromName(string candidateName)
    {
        string value = FriendlyFaceName(candidateName);
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        value = value.Replace('-', ' ');
        string[] parts = value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        int count = parts.Length;
        while (count > 1 && IsWeightToken(parts[count - 1])) count--;

        string family = string.Join(" ", parts, 0, count).Trim();
        if (string.IsNullOrEmpty(family)) family = value;
        return string.Equals(NormalizeFamily(family), "UbuntuMono", StringComparison.OrdinalIgnoreCase)
            ? UbuntuMonoFamily
            : family;
    }

    internal static int InferWeight(string name)
    {
        string value = (name ?? string.Empty).ToLowerInvariant().Replace(" ", string.Empty).Replace("-", string.Empty).Replace("_", string.Empty);
        if (value.Contains("black") || value.Contains("heavy")) return 900;
        if (value.Contains("extrabold") || value.Contains("ultrabold")) return 800;
        if (value.Contains("semibold") || value.Contains("demibold")) return 600;
        if (value.Contains("bold")) return 700;
        if (value.Contains("medium")) return 500;
        if (value.Contains("extralight") || value.Contains("ultralight")) return 200;
        if (value.Contains("light")) return 300;
        if (value.Contains("thin")) return 100;
        return 400;
    }

    private static bool SupportsChinese(ImFontPtr font)
    {
        if (font.NativePtr == null) return false;
        try
        {
            return font.FindGlyphNoFallback((ushort)'中').NativePtr != null &&
                   font.FindGlyphNoFallback((ushort)'文').NativePtr != null &&
                   font.FindGlyphNoFallback((ushort)'房').NativePtr != null &&
                   font.FindGlyphNoFallback((ushort)'间').NativePtr != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsUbuntuMono(string candidateName)
    {
        return string.Equals(
            NormalizeFamily(FamilyFromName(candidateName)),
            NormalizeFamily(UbuntuMonoFamily),
            StringComparison.OrdinalIgnoreCase);
    }

    private static void AddUnique(List<string> values, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        for (int i = 0; i < values.Count; i++)
            if (string.Equals(values[i], value, StringComparison.OrdinalIgnoreCase)) return;
        values.Add(value);
    }

    private static string NormalizeFamily(string value) =>
        (value ?? string.Empty).Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty).Trim();

    private static bool IsWeightToken(string value)
    {
        string token = (value ?? string.Empty).Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
        return token == "thin" || token == "extralight" || token == "ultralight" ||
               token == "light" || token == "regular" || token == "normal" || token == "book" ||
               token == "medium" || token == "semibold" || token == "demibold" ||
               token == "bold" || token == "extrabold" || token == "ultrabold" ||
               token == "black" || token == "heavy";
    }

    private static string ReadFontName(ImFontPtr font, int index)
    {
        if (font.NativePtr == null || font.NativePtr->ConfigData == null)
            return "CJK Font " + index;

        byte* name = font.NativePtr->ConfigData->Name;
        int length = 0;
        while (length < 80 && name[length] != 0) length++;
        if (length == 0) return "CJK Font " + index;

        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = name[i];
        string value = System.Text.Encoding.UTF8.GetString(bytes).Trim();
        return string.IsNullOrEmpty(value) ? "CJK Font " + index : value;
    }
}
