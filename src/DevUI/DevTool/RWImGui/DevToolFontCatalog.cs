using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Logging;
using ImGuiNET;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Discovers and registers developer-supplied fonts from the mod-local ui/fonts directory.
/// Font files remain presentation assets only; editor documents never reference them.
///
/// The path intentionally follows the same convention used by other RWImGui tooling:
///   <mod root>/newest/plugins/DryCycle.DevTool.RWImGui.dll
///   <mod root>/newest/ui/fonts/*.ttf
///
/// Fonts must be registered after RWImGui creates its native ImGui context but before the first
/// renderer frame builds the font atlas texture. Runtime switching only selects faces that are
/// already present in that startup atlas.
/// </summary>
internal static unsafe class DevToolFontCatalog
{
    internal const string DefaultChineseFamily = "HarmonyOS Sans SC";
    internal const string UbuntuMonoFamily = "Ubuntu Mono";

    private sealed class RegisteredFace
    {
        internal ImFontPtr Font;
        internal string FileName;
        internal string Family;
        internal int Weight;
    }

    private static readonly List<RegisteredFace> RegisteredFaces = new();
    private static readonly HashSet<string> RegisteredPaths = new(StringComparer.OrdinalIgnoreCase);
    private static bool registrationComplete;
    private static bool registrationBusy;
    private static bool registrationFailureLogged;
    private static bool lateRegistrationWarningLogged;

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
    /// Adds every local font face to the shared ImGui atlas once. The atlas uses ImGui's
    /// Simplified-Chinese common glyph set so several developer-selectable weights/families can
    /// coexist without the extreme texture cost of rasterising the entire CJK block per face.
    /// Faces that do not actually contain Chinese remain harmless and are filtered from the UI,
    /// except for explicitly allowed Chinese-interface faces such as UbuntuMono-Regular.ttf.
    /// </summary>
    internal static bool TryRegisterFonts(ManualLogSource log)
    {
        if (registrationComplete) return true;
        if (registrationBusy) return false;

        // AddFontFromFileTTF invalidates an already-built atlas. RWImGui's DX11 backend only
        // uploads that texture during its normal frame lifecycle, so mutating the atlas after a
        // frame has started causes ImGui::NewFrame() to assert with "Font Atlas not built".
        // Refuse late mutation rather than risking a native process abort.
        if (ImGui.GetFrameCount() > 0)
        {
            if (!lateRegistrationWarningLogged)
            {
                lateRegistrationWarningLogged = true;
                log?.LogWarning(
                    "DryCycle DevTool refused late font registration because the ImGui font atlas " +
                    "has already entered the render loop. Local fonts must be registered before the first frame.");
            }
            return false;
        }

        registrationBusy = true;
        try
        {
            string directory = FontDirectory;
            if (!Directory.Exists(directory))
            {
                log?.LogWarning("DryCycle DevTool font directory not found: " + directory);
                return false;
            }

            string[] files = Directory.GetFiles(directory, "*.*", SearchOption.TopDirectoryOnly);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            ImGuiIOPtr io = ImGui.GetIO();
            IntPtr glyphRanges = io.Fonts.GetGlyphRangesChineseSimplifiedCommon();
            int added = 0;

            for (int i = 0; i < files.Length; i++)
            {
                string file = files[i];
                string extension = Path.GetExtension(file);
                if (!string.Equals(extension, ".ttf", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(extension, ".otf", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(extension, ".ttc", StringComparison.OrdinalIgnoreCase))
                    continue;

                string fullPath = Path.GetFullPath(file);
                if (!RegisteredPaths.Add(fullPath)) continue;

                ImFontPtr font;
                try
                {
                    font = io.Fonts.AddFontFromFileTTF(
                        fullPath,
                        DevToolUiSettings.ReferenceFontSize,
                        default,
                        glyphRanges);
                }
                catch (Exception error)
                {
                    RegisteredPaths.Remove(fullPath);
                    log?.LogWarning("DryCycle DevTool could not register font '" + Path.GetFileName(file) + "': " + error.Message);
                    continue;
                }

                if (font.NativePtr == null)
                {
                    RegisteredPaths.Remove(fullPath);
                    continue;
                }

                string faceName = Path.GetFileNameWithoutExtension(file);
                RegisteredFaces.Add(new RegisteredFace
                {
                    Font = font,
                    FileName = Path.GetFileName(file),
                    Family = FamilyFromName(faceName),
                    Weight = InferWeight(faceName)
                });
                added++;
            }

            registrationComplete = true;
            log?.LogInfo($"DryCycle DevTool registered {added} local font face(s) before the first ImGui frame from {directory}.");
            return true;
        }
        catch (Exception error)
        {
            if (!registrationFailureLogged)
            {
                registrationFailureLogged = true;
                log?.LogWarning("DryCycle DevTool local font registration is unavailable: " + error.Message);
            }
            return false;
        }
        finally
        {
            registrationBusy = false;
        }
    }

    internal static string[] GetAvailableChineseFamilies()
    {
        List<string> families = new();
        for (int i = 0; i < RegisteredFaces.Count; i++)
        {
            RegisteredFace face = RegisteredFaces[i];
            if (!IsChineseUiSelectable(face.Font, face.FileName)) continue;
            AddUnique(families, face.Family);
        }

        // Other RWImGui users may have registered their own CJK faces before DryCycle. Include
        // those as well so the selector is a shared-atlas selector rather than a DryCycle-only list.
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
            // Font settings remain usable while RWImGui is still finalising the atlas.
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
    /// Determines whether a face is allowed in the Chinese-interface font selector. Normally a
    /// face must expose representative Simplified-Chinese glyphs. UbuntuMono-Regular.ttf is an
    /// explicit developer-facing option and is therefore allowed by family name as well.
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

    private static unsafe string ReadFontName(ImFontPtr font, int index)
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