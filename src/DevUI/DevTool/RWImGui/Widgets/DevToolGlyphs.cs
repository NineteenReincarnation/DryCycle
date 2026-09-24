using System;
using ImGuiNET;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Centralized Unicode UI glyph selection. Unicode is preferred when the active ImGui font
/// actually contains every requested glyph; otherwise an ASCII fallback is returned.
/// </summary>
internal static unsafe class DevToolGlyphs
{
    internal static string Prefer(string unicode, string ascii)
    {
        if (string.IsNullOrEmpty(unicode)) return ascii ?? string.Empty;

        try
        {
            ImFontPtr font = ImGui.GetFont();
            if (font.NativePtr == null) return ascii ?? string.Empty;

            for (int i = 0; i < unicode.Length; i++)
            {
                char ch = unicode[i];
                if (char.IsSurrogate(ch))
                    return ascii ?? string.Empty;
                if (ch <= 0x7F)
                    continue;
                if (font.FindGlyphNoFallback((ushort)ch).NativePtr == null)
                    return ascii ?? string.Empty;
            }

            return unicode;
        }
        catch
        {
            return ascii ?? string.Empty;
        }
    }

    internal static string Separator => Prefer("·", "|");
    internal static string Ellipsis => Prefer("…", "...");
    internal static string Multiply => Prefer("×", "x");
    internal static string Degree => Prefer("°", "deg");
    internal static string Bullet => Prefer("•", "-");
    internal static string StatusDot => Prefer("●", "*");
    internal static string Check => Prefer("✓", "[OK]");
    internal static string Cross => Prefer("✕", "[X]");
    internal static string Warning => Prefer("⚠", "[!]");
    internal static string CheckedBox => Prefer("☑", "[x]");
    internal static string EmptyBox => Prefer("☐", "[ ]");
    internal static string Root => Prefer("▣", "[ROOT]");
    internal static string Branch => Prefer("▸", ">");
    internal static string ArrowBoth => Prefer("↔", "<->");
    internal static string ArrowRight => Prefer("→", "->");
    internal static string ArrowLeft => Prefer("←", "<-");
    internal static string ArrowUp => Prefer("↑", "^");
    internal static string ArrowDown => Prefer("↓", "v");
}
