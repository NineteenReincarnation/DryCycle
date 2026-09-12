using DryCycle.DevUI.DevTool.Core;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal enum DevToolUiLanguage
{
    Chinese,
    English
}

/// <summary>
/// Lightweight frontend-only presentation settings. Keep these separate from editor data so
/// language, font and transparency never enter room saves or Undo/Redo history.
/// </summary>
internal static class DevToolUiSettings
{
    internal const float WindowAlpha = 0.52f;
    internal const float PopupAlpha = 0.68f;

    // 18 px is the original rebuilt-editor design scale. English keeps the established 36 px
    // presentation size, while Simplified Chinese gets a slightly larger 42 px default because
    // the CJK atlas has a visibly smaller glyph body at the same nominal ImGui font size.
    internal const float ReferenceFontSize = 18f;
    internal const float DefaultFontSize = 36f;
    internal const float DefaultChineseFontSize = 42f;
    internal const int DefaultFontWeight = 400;
    internal const int DefaultChineseFontWeight = 500;
    internal const float WindowOutlineWidth = 2f;

    private static readonly Num.Vector4 DefaultTextColor = new(0.94f, 0.94f, 0.94f, 1f);
    // Secondary labels are deliberately brighter than stock ImGui disabled text. In the DevTool
    // these labels are structural headings, not disabled controls, and must remain readable over
    // the semi-transparent room view.
    private static readonly Num.Vector4 DefaultDisabledTextColor = new(0.76f, 0.80f, 0.86f, 1f);

    // Chinese is intentionally the default for DryCycle's development workflow. Keep independent
    // font size/weight preferences for the two language modes so changing CJK typography does not
    // silently alter the English editor presentation.
    private static DevToolUiLanguage language = DevToolUiLanguage.Chinese;
    private static float chineseFontSize = DefaultChineseFontSize;
    private static float englishFontSize = DefaultFontSize;
    private static int chineseFontWeight = DefaultChineseFontWeight;
    private static int englishFontWeight = DefaultFontWeight;
    private static string chineseFontFamily = DevToolFontCatalog.DefaultChineseFamily;

    internal static DevToolUiLanguage Language => language;

    internal static float FontSize
    {
        get => IsChinese ? chineseFontSize : englishFontSize;
        set
        {
            if (IsChinese) chineseFontSize = value;
            else englishFontSize = value;
        }
    }

    internal static int FontWeight
    {
        get => IsChinese ? chineseFontWeight : englishFontWeight;
        set
        {
            if (IsChinese) chineseFontWeight = value;
            else englishFontWeight = value;
        }
    }

    internal static string ChineseFontFamily
    {
        get => chineseFontFamily;
        set => chineseFontFamily = string.IsNullOrWhiteSpace(value)
            ? DevToolFontCatalog.DefaultChineseFamily
            : value.Trim();
    }

    internal static Num.Vector4 TextColor { get; set; } = DefaultTextColor;
    internal static Num.Vector4 DisabledTextColor { get; set; } = DefaultDisabledTextColor;

    internal static bool IsChinese => language == DevToolUiLanguage.Chinese;
    internal static float UiScale => FontSize / ReferenceFontSize;

    internal static void SetLanguage(DevToolUiLanguage value)
    {
        language = value;
    }

    internal static void ResetFontAppearance()
    {
        chineseFontSize = DefaultChineseFontSize;
        englishFontSize = DefaultFontSize;
        chineseFontWeight = DefaultChineseFontWeight;
        englishFontWeight = DefaultFontWeight;
        chineseFontFamily = DevToolFontCatalog.DefaultChineseFamily;
        TextColor = DefaultTextColor;
        DisabledTextColor = DefaultDisabledTextColor;
    }

    internal static string T(string chinese, string english)
    {
        // Early DevTool builds prefixed the seven main tool labels with R/O/S/T/M/D/L.
        // The rebuilt toolbar now presents the localized full name only. Keep the cleanup here
        // so both Chinese and English stay consistent without carrying duplicate abbreviation UI.
        return StripLegacyToolPrefix(IsChinese ? chinese : english);
    }

    private static string StripLegacyToolPrefix(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 4 || value[1] != ' ' || value[2] != ' ')
            return value;

        switch (value[0])
        {
            case 'R':
            case 'O':
            case 'S':
            case 'T':
            case 'M':
            case 'D':
            case 'L':
                return value.Substring(3);
            default:
                return value;
        }
    }

    internal static string ToolMode(EditorToolMode mode)
    {
        if (!IsChinese) return mode.ToString();
        return mode switch
        {
            EditorToolMode.Room => "房间",
            EditorToolMode.Objects => "物件",
            EditorToolMode.Sound => "声音",
            EditorToolMode.Triggers => "触发器",
            EditorToolMode.Map => "地图",
            EditorToolMode.Dialog => "对话",
            EditorToolMode.Relationships => "关系",
            _ => mode.ToString()
        };
    }
}
