using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Logging;
using ImGuiNET;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Owns DevTool-local font discovery and the one safe registration window used before RWImGui's
/// first rendered frame. RWImGui still owns the native context, renderer backend and atlas texture;
/// DryCycle never rebuilds or mutates the atlas after the renderer has uploaded its font texture.
/// </summary>
internal static unsafe class DevToolFontCatalog
{
    internal const string DefaultChineseFamily = "HarmonyOS Sans SC";
    internal const string UbuntuMonoFamily = "Ubuntu Mono";

    private const int MaxLocalFontFaces = 24;

    private sealed class RegisteredFace
    {
        internal ImFontPtr Font;
        internal string FileName;
        internal string Family;
        internal int Weight;
    }

    private static readonly List<RegisteredFace> RegisteredFaces = new();
    private static readonly HashSet<string> RegisteredPaths = new(StringComparer.OrdinalIgnoreCase);
    private static bool registrationAttempted;
    private static bool registrationSucceeded;
    private static string registrationMessage = "尚未尝试注册本地字体。";

    // Font files and the ImGui atlas are stable after startup. The settings window is rendered every
    // frame, so never repeat filesystem enumeration or glyph probing there.
    private static string[] cachedChineseFamilies;
    private static int cachedLocalFontFileCount = -1;
    private static int cachedSelectableLocalChineseFaces = -1;

    internal static bool RegistrationAttempted => registrationAttempted;
    internal static bool RegistrationSucceeded => registrationSucceeded;
    internal static int RegisteredLocalFaceCount => RegisteredFaces.Count;
    internal static string RegistrationMessage => registrationMessage;

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
    /// Registers local fonts only after RWImGui has created/configured its ImGui context and before
    /// the first frame/font texture upload. This is intentionally called from RainWorld.OnModsInit,
    /// never from RainWorld.Start, BepInEx load, or an active Render() call.
    /// </summary>
    internal static bool TryRegisterLocalFonts(ManualLogSource log)
    {
        if (registrationAttempted) return registrationSucceeded;
        registrationAttempted = true;
        InvalidatePresentationCaches();

        try
        {
            if (ImGui.GetFrameCount() != 0)
            {
                registrationMessage = "已错过安全注册窗口：ImGui 已开始渲染帧。";
                log?.LogWarning("DryCycle DevTool skipped local font registration: ImGui has already started rendering frames.");
                return false;
            }

            ImGuiIOPtr io = ImGui.GetIO();
            if (io.Fonts.NativePtr == null)
            {
                registrationMessage = "RWImGui 字体 Atlas 尚不可用。";
                log?.LogWarning("DryCycle DevTool skipped local font registration: RWImGui font atlas is unavailable.");
                return false;
            }

            if (io.Fonts.Locked || io.Fonts.TexID != IntPtr.Zero)
            {
                registrationMessage = "已错过安全注册窗口：字体 Atlas 已锁定或已上传纹理。";
                log?.LogWarning(
                    "DryCycle DevTool refused late font registration because RWImGui's font atlas " +
                    "is already locked or has a live renderer texture.");
                return false;
            }

            string directory = FontDirectory;
            if (!Directory.Exists(directory))
            {
                cachedLocalFontFileCount = 0;
                registrationMessage = "字体目录不存在：" + directory;
                log?.LogWarning("DryCycle DevTool font directory not found: " + directory);
                return false;
            }

            string[] files = Directory.GetFiles(directory, "*.*", SearchOption.TopDirectoryOnly);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            IntPtr glyphRanges = io.Fonts.GetGlyphRangesChineseSimplifiedCommon();
            int added = 0;
            int eligibleFiles = 0;

            for (int i = 0; i < files.Length; i++)
            {
                string file = files[i];
                string extension = Path.GetExtension(file);
                if (!IsFontExtension(extension)) continue;
                eligibleFiles++;

                if (added >= MaxLocalFontFaces)
                {
                    log?.LogWarning(
                        $"DryCycle DevTool font directory contains more than {MaxLocalFontFaces} font faces. " +
                        "Extra faces were skipped to keep the shared atlas bounded.");
                    break;
                }

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
                    log?.LogWarning(
                        "DryCycle DevTool could not register font '" + Path.GetFileName(file) + "': " + error.Message);
                    continue;
                }

                if (font.NativePtr == null)
                {
                    RegisteredPaths.Remove(fullPath);
                    log?.LogWarning("DryCycle DevTool font returned a null ImFont: " + Path.GetFileName(file));
                    continue;
                }

                string fileName = Path.GetFileName(file);
                string faceName = Path.GetFileNameWithoutExtension(file);
                RegisteredFaces.Add(new RegisteredFace
                {
                    Font = font,
                    FileName = fileName,
                    Family = FamilyFromName(faceName),
                    Weight = InferWeight(faceName)
                });
                added++;
            }

            cachedLocalFontFileCount = eligibleFiles;
            cachedSelectableLocalChineseFaces = -1;
            cachedChineseFamilies = null;
            registrationSucceeded = added > 0;
            registrationMessage = registrationSucceeded
                ? $"安全注册窗口内已加入 {added} 个本地字体面。"
                : $"目录中检测到 {eligibleFiles} 个字体文件，但没有字体成功加入 Atlas。";

            if (registrationSucceeded)
            {
                log?.LogInfo(
                    $"DryCycle DevTool registered {added} local font face(s) into RWImGui's atlas " +
                    $"before the first frame from {directory}. The renderer remains the sole atlas texture owner.");
            }
            else
            {
                log?.LogWarning("DryCycle DevTool found no local font face that could be registered from: " + directory);
            }

            return registrationSucceeded;
        }
        catch (Exception error)
        {
            registrationMessage = "本地字体注册失败：" + error.Message;
            log?.LogWarning("DryCycle DevTool local font registration failed safely: " + error);
            return false;
        }
    }

    /// <summary>
    /// Enumerates Chinese-capable families once per atlas lifetime. Glyph probing is deliberately
    /// kept out of the frame loop because FindGlyphNoFallback crosses the managed/native boundary.
    /// </summary>
    internal static string[] GetAvailableChineseFamilies()
    {
        if (cachedChineseFamilies != null) return cachedChineseFamilies;

        List<string> families = new();

        for (int i = 0; i < RegisteredFaces.Count; i++)
        {
            RegisteredFace face = RegisteredFaces[i];
            if (!IsChineseUiSelectable(face.Font, face.FileName)) continue;
            AddUnique(families, face.Family);
        }

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
        cachedChineseFamilies = families.ToArray();
        return cachedChineseFamilies;
    }

    internal static int CountLocalFontFiles()
    {
        if (cachedLocalFontFileCount >= 0) return cachedLocalFontFileCount;

        try
        {
            if (!Directory.Exists(FontDirectory)) return cachedLocalFontFileCount = 0;
            string[] files = Directory.GetFiles(FontDirectory, "*.*", SearchOption.TopDirectoryOnly);
            int count = 0;
            for (int i = 0; i < files.Length; i++)
                if (IsFontExtension(Path.GetExtension(files[i]))) count++;
            cachedLocalFontFileCount = count;
        }
        catch
        {
            cachedLocalFontFileCount = 0;
        }
        return cachedLocalFontFileCount;
    }

    internal static int CountSelectableLocalChineseFaces()
    {
        if (cachedSelectableLocalChineseFaces >= 0) return cachedSelectableLocalChineseFaces;

        int count = 0;
        for (int i = 0; i < RegisteredFaces.Count; i++)
            if (IsChineseUiSelectable(RegisteredFaces[i].Font, RegisteredFaces[i].FileName)) count++;
        cachedSelectableLocalChineseFaces = count;
        return count;
    }

    internal static bool TryGetRegisteredFace(ImFontPtr font, out string name, out int weight)
    {
        for (int i = 0; i < RegisteredFaces.Count; i++)
        {
            RegisteredFace face = RegisteredFaces[i];
            if (face.Font.NativePtr != font.NativePtr) continue;
            name = face.FileName;
            weight = face.Weight;
            return true;
        }

        name = string.Empty;
        weight = DevToolUiSettings.DefaultFontWeight;
        return false;
    }

    internal static bool IsChineseUiSelectable(ImFontPtr font, string candidateName)
    {
        return SupportsChinese(font);
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

    private static void InvalidatePresentationCaches()
    {
        cachedChineseFamilies = null;
        cachedSelectableLocalChineseFaces = -1;
    }

    private static bool IsFontExtension(string extension)
    {
        return string.Equals(extension, ".ttf", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".otf", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".ttc", StringComparison.OrdinalIgnoreCase);
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
