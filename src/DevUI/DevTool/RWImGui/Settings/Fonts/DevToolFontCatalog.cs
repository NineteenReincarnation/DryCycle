using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Logging;
using ImGuiNET;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Owns fonts for DryCycle's dedicated DevToolInputContext. Local fonts are never injected into
/// RWImGUI's shared/default context. Registration happens once, immediately after the dedicated
/// context is activated and before that context renders its first frame.
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
        internal bool ChineseCapable;
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

    /// <summary>
    /// Clears only managed bookkeeping for the current consumer atlas. ImFontPtr values are owned
    /// by RWImGUI's context and become invalid as soon as that context is destroyed. Reusing those
    /// pointers, or keeping RegisteredPaths populated, makes the next context either skip every font
    /// or push a stale native pointer.
    ///
    /// This method never calls ImGui/RWImGUI native APIs and is therefore safe from OnDestroyed and
    /// plugin-lifecycle cleanup paths.
    /// </summary>
    internal static void ResetConsumerContextState()
    {
        RegisteredFaces.Clear();
        RegisteredPaths.Clear();
        registrationAttempted = false;
        registrationSucceeded = false;
        registrationMessage = "尚未尝试注册本地字体。";
        cachedChineseFamilies = null;
        cachedLocalFontFileCount = -1;
        cachedSelectableLocalChineseFaces = -1;
    }

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
    /// Registers local files into the currently-active DryCycle consumer context. Call only once,
    /// immediately after ImGUIAPI.SwitchContext(DevToolInputContext) and before its first Render.
    /// </summary>
    internal static bool TryRegisterLocalFonts(ManualLogSource log)
    {
        if (registrationAttempted) return registrationSucceeded;
        registrationAttempted = true;
        InvalidatePresentationCaches();

        try
        {
            ImGuiIOPtr io = ImGui.GetIO();
            if (io.Fonts.NativePtr == null)
            {
                registrationMessage = "RWImGui 字体 Atlas 尚不可用。";
                log?.LogWarning("DryCycle DevTool skipped local font registration: RWImGui font atlas is unavailable.");
                return false;
            }

            // RWImGui ships a modified ImGui.NET binding where ImFontAtlas.TexID is ulong
            // rather than System.IntPtr. Compare against the binding's actual zero value so this
            // frontend compiles against the DLL that Rain World loads at runtime.
            if (io.Fonts.Locked || io.Fonts.TexID != 0UL)
            {
                registrationMessage = "DevTool 独立字体 Atlas 已锁定或已上传纹理，无法再注册本地字体。";
                log?.LogWarning(
                    "DryCycle DevTool consumer font atlas was already locked/uploaded before local " +
                    "font registration. The shared RWImGUI atlas was not modified.");
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

            IntPtr chineseGlyphRanges =
                GetExtendedChineseGlyphRanges(
                    io.Fonts.GetGlyphRangesChineseSimplifiedCommon());
            IntPtr defaultGlyphRanges =
                io.Fonts.GetGlyphRangesDefault();
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

                string fileName = Path.GetFileName(file);
                string faceName = Path.GetFileNameWithoutExtension(file);
                string family = FamilyFromName(faceName);
                // Decide the language role before the atlas is built. Probing ImFont afterwards is
                // not reliable on the modified RWImGui binding: fonts loaded with a requested CJK
                // range can report placeholder glyph entries even when the source face is Latin-only.
                // That is how FiraCode was incorrectly exposed as a Chinese font and produced '?'.
                bool chineseFace =
                    IsChineseFamilyName(
                        family,
                        fileName);
                IntPtr glyphRanges =
                    chineseFace
                        ? chineseGlyphRanges
                        : defaultGlyphRanges;

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

                RegisteredFaces.Add(new RegisteredFace
                {
                    Font = font,
                    FileName = fileName,
                    Family = family,
                    Weight = InferWeight(faceName),
                    ChineseCapable = chineseFace
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
                    $"DryCycle DevTool registered {added} local font face(s) into its dedicated " +
                    $"consumer atlas from {directory}. RWImGUI's shared atlas was not modified.");
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
    /// Keep Simplified Chinese registration on ImGui's proven CJK range.
    /// Optional UI symbols are selected at render time through DevToolGlyphs and fall back to
    /// ASCII when the active font does not contain the requested glyph.
    /// </summary>
    private static IntPtr GetExtendedChineseGlyphRanges(IntPtr baseRanges)
    {
        return baseRanges;
    }

    /// <summary>
    /// Enumerates Chinese-capable families once per atlas lifetime. Glyph probing is deliberately
    /// kept out of the frame loop because FindGlyphNoFallback crosses the managed/native boundary.
    /// </summary>
    internal static string[] GetAvailableChineseFamilies()
    {
        if (cachedChineseFamilies != null) return cachedChineseFamilies;

        // Only DryCycle-registered local faces participate in the Chinese selector. Do not scan
        // RWImGui's preloaded primary font here: a Latin font such as FiraCode may expose atlas
        // placeholder entries for CJK codepoints and would otherwise be misclassified as usable.
        List<string> families = new();
        for (int i = 0; i < RegisteredFaces.Count; i++)
        {
            RegisteredFace face = RegisteredFaces[i];
            if (!face.ChineseCapable) continue;
            AddUnique(families, face.Family);
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
            if (RegisteredFaces[i].ChineseCapable) count++;
        cachedSelectableLocalChineseFaces = count;
        return count;
    }

    internal static bool TryResolveRegisteredFace(
        string family,
        int preferredWeight,
        bool requireChinese,
        out ImFontPtr font,
        out string name,
        out int actualWeight,
        out int variantCount)
    {
        if (TryResolveFamily(
                family,
                preferredWeight,
                requireChinese,
                out font,
                out name,
                out actualWeight,
                out variantCount))
            return true;

        if (requireChinese &&
            !string.Equals(
                NormalizeFamily(family),
                NormalizeFamily(DefaultChineseFamily),
                StringComparison.OrdinalIgnoreCase) &&
            TryResolveFamily(
                DefaultChineseFamily,
                preferredWeight,
                true,
                out font,
                out name,
                out actualWeight,
                out variantCount))
            return true;

        if (requireChinese)
        {
            for (int i = 0; i < RegisteredFaces.Count; i++)
            {
                RegisteredFace face = RegisteredFaces[i];
                if (!face.ChineseCapable) continue;
                if (TryResolveFamily(
                        face.Family,
                        preferredWeight,
                        true,
                        out font,
                        out name,
                        out actualWeight,
                        out variantCount))
                    return true;
            }
        }

        font = default;
        name = string.Empty;
        actualWeight = preferredWeight;
        variantCount = 0;
        return false;
    }

    private static bool TryResolveFamily(
        string family,
        int preferredWeight,
        bool requireChinese,
        out ImFontPtr font,
        out string name,
        out int actualWeight,
        out int variantCount)
    {
        font = default;
        name = string.Empty;
        actualWeight = preferredWeight;
        variantCount = 0;

        int bestDistance = int.MaxValue;
        for (int i = 0; i < RegisteredFaces.Count; i++)
        {
            RegisteredFace face = RegisteredFaces[i];
            if (!string.Equals(
                    NormalizeFamily(face.Family),
                    NormalizeFamily(family),
                    StringComparison.OrdinalIgnoreCase))
                continue;
            if (requireChinese && !face.ChineseCapable)
                continue;

            variantCount++;
            int distance = Math.Abs(face.Weight - preferredWeight);
            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            font = face.Font;
            name = face.FileName;
            actualWeight = face.Weight;
        }

        return font.NativePtr != null;
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
        if (font.NativePtr == null) return false;
        for (int i = 0; i < RegisteredFaces.Count; i++)
        {
            RegisteredFace face = RegisteredFaces[i];
            if (face.Font.NativePtr == font.NativePtr)
                return face.ChineseCapable;
        }
        return false;
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

    private static bool IsChineseFamilyName(string family, string fileName)
    {
        string value =
            NormalizeFamily((family ?? string.Empty) + " " + (fileName ?? string.Empty))
                .ToLowerInvariant();

        // Keep the detector conservative. A false positive is much worse than a false negative:
        // selecting a Latin-only face for the Chinese UI turns every label into '?'.
        string[] markers =
        {
            "harmonyossanssc",
            "notosanssc",
            "notoserifsc",
            "notosanscjk",
            "notoserifcjk",
            "sourcehansans",
            "sourcehanserif",
            "pingfangsc",
            "microsoftyahei",
            "simhei",
            "simsun",
            "kaiti",
            "fangsong",
            "wenquanyi",
            "lxgwwenkai",
            "sarasa",
            "smileysans",
            "alibabapuhuiti",
            "misans",
            "opposans",
            "droidsansfallback"
        };

        for (int i = 0; i < markers.Length; i++)
            if (value.Contains(markers[i]))
                return true;

        return false;
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
}
