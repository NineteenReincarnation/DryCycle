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
    internal const float DefaultFontSize = 18f;
    internal const int DefaultFontWeight = 400;

    private static readonly Num.Vector4 DefaultTextColor = new(0.94f, 0.94f, 0.94f, 1f);
    private static readonly Num.Vector4 DefaultDisabledTextColor = new(0.62f, 0.64f, 0.68f, 1f);

    // Chinese is intentionally the default for DryCycle's development workflow.
    internal static DevToolUiLanguage Language { get; private set; } = DevToolUiLanguage.Chinese;

    internal static float FontSize { get; set; } = DefaultFontSize;
    internal static int FontWeight { get; set; } = DefaultFontWeight;
    internal static Num.Vector4 TextColor { get; set; } = DefaultTextColor;
    internal static Num.Vector4 DisabledTextColor { get; set; } = DefaultDisabledTextColor;

    internal static bool IsChinese => Language == DevToolUiLanguage.Chinese;

    internal static void SetLanguage(DevToolUiLanguage language)
    {
        Language = language;
    }

    internal static void ResetFontAppearance()
    {
        FontSize = DefaultFontSize;
        FontWeight = DefaultFontWeight;
        TextColor = DefaultTextColor;
        DisabledTextColor = DefaultDisabledTextColor;
    }

    internal static string T(string chinese, string english) => IsChinese ? chinese : english;

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
