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
    internal const string ChineseFontFileName = "HarmonyOS_Sans_SC_Medium.ttf";
    internal const string UbuntuMonoFamily = "Ubuntu Mono";

    private const int FixedChineseFontWeight = 500;
    private const long MaxLocalFontBytes = 64L * 1024L * 1024L;

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
    private static string registrationMessage = "Local font registration has not been attempted.";

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
        registrationMessage = "Local font registration has not been attempted.";
        cachedChineseFamilies = null;
        cachedLocalFontFileCount = -1;
        cachedSelectableLocalChineseFaces = -1;
    }

    internal static string ChineseFontPath
    {
        get
        {
            ResolveFontDirectories(out string modRootFonts, out string versionFonts);

            string modRootCandidate = Path.Combine(modRootFonts, ChineseFontFileName);
            if (File.Exists(modRootCandidate))
                return modRootCandidate;

            string versionCandidate = Path.Combine(versionFonts, ChineseFontFileName);
            if (File.Exists(versionCandidate))
                return versionCandidate;

            // Prefer the mod-root location in diagnostics/new installs. The legacy newest/ui/fonts
            // candidate is still accepted above so existing setups do not break.
            return modRootCandidate;
        }
    }

    internal static string FontDirectory =>
        Path.GetDirectoryName(ChineseFontPath) ?? string.Empty;

    private static void ResolveFontDirectories(
        out string modRootFonts,
        out string versionFonts)
    {
        string pluginDirectory =
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ??
            string.Empty;
        string versionDirectory =
            Path.GetDirectoryName(pluginDirectory) ??
            pluginDirectory;

        versionFonts = Path.Combine(versionDirectory, "ui", "fonts");

        string modRootDirectory = versionDirectory;
        if (string.Equals(
                Path.GetFileName(versionDirectory),
                "newest",
                StringComparison.OrdinalIgnoreCase))
        {
            modRootDirectory =
                Path.GetDirectoryName(versionDirectory) ??
                versionDirectory;
        }

        modRootFonts = Path.Combine(modRootDirectory, "ui", "fonts");
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
                registrationMessage = "RWImGui font atlas is unavailable.";
                log?.LogWarning("DryCycle DevTool skipped local font registration: RWImGui font atlas is unavailable.");
                return false;
            }

            if (io.Fonts.Locked || io.Fonts.TexID != 0UL)
            {
                registrationMessage = "The DevTool font atlas is already locked or uploaded; local fonts were not modified.";
                log?.LogWarning(
                    "DryCycle DevTool consumer font atlas was already locked/uploaded before local " +
                    "font registration. The shared RWImGUI atlas was not modified.");
                return false;
            }

            string fontPath = ChineseFontPath;
            cachedLocalFontFileCount = File.Exists(fontPath) ? 1 : 0;

            if (!File.Exists(fontPath))
            {
                registrationMessage = "Required Chinese font not found: " + fontPath;
                log?.LogWarning(
                    "DryCycle DevTool Chinese font is missing. Expected exactly: " + fontPath);
                return false;
            }

            if (!TryValidateFontFile(fontPath, out string validationError))
            {
                registrationMessage = "Required Chinese font failed validation: " + validationError;
                log?.LogWarning(
                    "DryCycle DevTool rejected required Chinese font '" +
                    ChineseFontFileName +
                    "': " +
                    validationError);
                return false;
            }

            IntPtr chineseGlyphRanges =
                GetExtendedChineseGlyphRanges(
                    io.Fonts.GetGlyphRangesChineseSimplifiedCommon());

            ImFontPtr font;
            try
            {
                font = io.Fonts.AddFontFromFileTTF(
                    Path.GetFullPath(fontPath),
                    DevToolUiSettings.ReferenceFontSize,
                    default,
                    chineseGlyphRanges);
            }
            catch (Exception error)
            {
                registrationMessage = "Required Chinese font could not be added: " + error.Message;
                log?.LogWarning(
                    "DryCycle DevTool could not register required Chinese font '" +
                    ChineseFontFileName +
                    "': " +
                    error);
                return false;
            }

            if (font.NativePtr == null)
            {
                registrationMessage = "Required Chinese font returned a null ImFont.";
                log?.LogWarning(
                    "DryCycle DevTool required Chinese font returned a null ImFont: " +
                    ChineseFontFileName);
                return false;
            }

            RegisteredFaces.Clear();
            RegisteredPaths.Clear();
            RegisteredPaths.Add(Path.GetFullPath(fontPath));
            RegisteredFaces.Add(new RegisteredFace
            {
                Font = font,
                FileName = ChineseFontFileName,
                Family = DefaultChineseFamily,
                Weight = FixedChineseFontWeight,
                ChineseCapable = true
            });

            cachedSelectableLocalChineseFaces = 1;
            cachedChineseFamilies = new[] { DefaultChineseFamily };
            registrationSucceeded = true;
            registrationMessage = "Registered fixed Chinese font: " + ChineseFontFileName + ".";

            log?.LogInfo(
                "DryCycle DevTool registered fixed Chinese font '" +
                ChineseFontFileName +
                "' into its dedicated consumer atlas.");
            return true;
        }
        catch (Exception error)
        {
            registrationMessage = "Local font registration failed: " + error.Message;
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
        cachedChineseFamilies =
            registrationSucceeded
                ? new[] { DefaultChineseFamily }
                : Array.Empty<string>();
        return cachedChineseFamilies;
    }

    internal static int CountLocalFontFiles()
    {
        if (cachedLocalFontFileCount >= 0) return cachedLocalFontFileCount;

        try
        {
            cachedLocalFontFileCount =
                File.Exists(ChineseFontPath)
                    ? 1
                    : 0;
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

    private static int CompareFontFilesForRegistration(string left, string right)
    {
        int leftPriority = FontRegistrationPriority(left);
        int rightPriority = FontRegistrationPriority(right);
        int compare = leftPriority.CompareTo(rightPriority);
        return compare != 0
            ? compare
            : StringComparer.OrdinalIgnoreCase.Compare(left, right);
    }

    private static int FontRegistrationPriority(string file)
    {
        string extension = Path.GetExtension(file);
        if (!IsFontExtension(extension)) return int.MaxValue;

        string fileName = Path.GetFileName(file);
        string faceName = Path.GetFileNameWithoutExtension(file);
        string family = FamilyFromName(faceName);
        if (!IsChineseFamilyName(family, fileName)) return int.MaxValue;

        string normalizedFamily = NormalizeFamily(family);
        string selectedFamily = NormalizeFamily(DevToolUiSettings.ChineseFontFamily);
        string defaultFamily = NormalizeFamily(DefaultChineseFamily);

        int familyRank =
            string.Equals(normalizedFamily, selectedFamily, StringComparison.OrdinalIgnoreCase)
                ? 0
                : string.Equals(normalizedFamily, defaultFamily, StringComparison.OrdinalIgnoreCase)
                    ? 1
                    : 2;

        int weightDistance = Math.Abs(InferWeight(faceName) - DevToolUiSettings.DefaultChineseFontWeight);
        return familyRank * 10000 + weightDistance;
    }

    private static bool TryValidateFontFile(string path, out string reason)
    {
        reason = string.Empty;
        try
        {
            FileInfo info = new(path);
            if (!info.Exists)
            {
                reason = "file no longer exists";
                return false;
            }

            if (info.Length < 1024)
            {
                reason = "file is too small to be a valid font";
                return false;
            }

            if (info.Length > MaxLocalFontBytes)
            {
                reason = $"file exceeds the {MaxLocalFontBytes / (1024L * 1024L)} MiB safety limit";
                return false;
            }

            byte[] header = new byte[4];
            using (FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Read(header, 0, header.Length) != header.Length)
                {
                    reason = "font header is truncated";
                    return false;
                }
            }

            bool supported =
                (header[0] == 0x00 && header[1] == 0x01 && header[2] == 0x00 && header[3] == 0x00) ||
                (header[0] == (byte)'O' && header[1] == (byte)'T' && header[2] == (byte)'T' && header[3] == (byte)'O') ||
                (header[0] == (byte)'t' && header[1] == (byte)'t' && header[2] == (byte)'c' && header[3] == (byte)'f') ||
                (header[0] == (byte)'t' && header[1] == (byte)'r' && header[2] == (byte)'u' && header[3] == (byte)'e') ||
                (header[0] == (byte)'t' && header[1] == (byte)'y' && header[2] == (byte)'p' && header[3] == (byte)'1');

            if (!supported)
            {
                reason = "unrecognized TrueType/OpenType/TTC header";
                return false;
            }

            return true;
        }
        catch (Exception error)
        {
            reason = error.Message;
            return false;
        }
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
