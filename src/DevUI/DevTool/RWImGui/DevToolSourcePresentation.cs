using System;
using DryCycle.DevUI.DevTool.Sound;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Canonical source identity used by every RWImGui browser that groups content by origin.
/// Views should pass source metadata here instead of inventing their own colours or DLC labels.
/// </summary>
internal readonly struct DevToolSourceMark
{
    internal DevToolSourceMark(string key, string label, Num.Vector4 color)
    {
        Key = key ?? string.Empty;
        Label = label ?? string.Empty;
        Color = color;
    }

    internal string Key { get; }
    internal string Label { get; }
    internal Num.Vector4 Color { get; }
}

/// <summary>
/// Shared source marker policy for Objects, Effects, Sounds and future DevTool catalogs.
/// Official content gets fixed semantic colours. Third-party mods get a deterministic colour
/// derived from their stable mod id (or, when no id is available, their displayed source name).
/// </summary>
internal static class DevToolSourcePresentation
{
    internal const string DownpourModId = "moreslugcats";
    internal const string WatcherModId = "watcher";

    // Official-family colours are intentionally semantic rather than random-hash colours:
    // base game = crimson/red, Downpour = water/cyan, Watcher = charcoal/black.
    private static readonly Num.Vector4 VanillaColor = new(0.80f, 0.20f, 0.24f, 1f);
    private static readonly Num.Vector4 DownpourColor = new(0.18f, 0.72f, 0.92f, 1f);
    private static readonly Num.Vector4 WatcherColor = new(0.13f, 0.15f, 0.19f, 1f);
    private static readonly Num.Vector4 MissingColor = new(0.96f, 0.43f, 0.24f, 1f);

    internal static DevToolSourceMark FromSound(
        EditorSoundSourceKind kind,
        string sourceId,
        string sourceName)
    {
        string id = Normalize(sourceId);
        string name = string.IsNullOrWhiteSpace(sourceName) ? string.Empty : sourceName.Trim();

        if (kind == EditorSoundSourceKind.Vanilla)
            return Vanilla();
        if (kind == EditorSoundSourceKind.Downpour || IsDownpour(id, name))
            return Downpour();
        if (kind == EditorSoundSourceKind.Watcher || IsWatcher(id, name))
            return Watcher();
        if (kind == EditorSoundSourceKind.Missing)
            return Missing();

        // Keep legacy/unknown DLC records source-specific instead of collapsing every DLC into
        // one colour. This also makes the presentation forward-compatible with future official
        // packages until the catalog learns an explicit semantic kind for them.
        string key = !string.IsNullOrEmpty(id) ? id : Normalize(name);
        string label = !string.IsNullOrWhiteSpace(name)
            ? name
            : kind == EditorSoundSourceKind.Dlc ? "DLC" : "MOD";
        return Named(key, label);
    }

    /// <summary>
    /// Resolves a source when a catalog currently exposes only a source/category label.
    /// This is the compatibility path used by existing Object and Room Effect browsers.
    /// </summary>
    internal static DevToolSourceMark FromLabel(string label)
    {
        string value = string.IsNullOrWhiteSpace(label) ? "Unknown" : label.Trim();
        string normalized = Normalize(value);

        if (IsVanilla(normalized)) return Vanilla();
        if (IsDownpour(normalized, value)) return Downpour();
        if (IsWatcher(normalized, value)) return Watcher();
        if (normalized.Contains("missing") || normalized.Contains("缺失")) return Missing();

        return Named(normalized, value);
    }

    internal static bool SameSource(in DevToolSourceMark a, in DevToolSourceMark b) =>
        string.Equals(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Draws the shared full-row source marker. Keep the source family itself in the background
    /// band/rail, while choosing a readable face colour for the label. This matters for Watcher:
    /// its identity remains visibly black/charcoal instead of being lifted into generic grey merely
    /// to make the text readable on the translucent Rain World editor.
    /// </summary>
    internal static void DrawHeader(
        in DevToolSourceMark source,
        float fontScale = 1.38f,
        float restoreScale = 1f)
    {
        string label = string.IsNullOrWhiteSpace(source.Label) ? "Unknown" : source.Label;
        Num.Vector4 family = source.Color;
        Num.Vector4 textColor = ReadableTextColor(family);

        ImGui.Spacing();
        ImGui.SetWindowFontScale(fontScale);

        Num.Vector2 pos = ImGui.GetCursorScreenPos();
        float width = Math.Max(1f, ImGui.GetContentRegionAvail().X);
        float height = Math.Max(ImGui.GetTextLineHeight() + 8f, 22f);
        ImDrawListPtr draw = ImGui.GetWindowDrawList();

        float luminance = Luminance(family);
        float bandAlpha = luminance < 0.24f ? 0.72f : 0.30f;
        Num.Vector4 band = new(family.X * 0.72f, family.Y * 0.72f, family.Z * 0.72f, bandAlpha);
        Num.Vector4 edge = new(family.X, family.Y, family.Z, 0.96f);
        draw.AddRectFilled(pos, new Num.Vector2(pos.X + width, pos.Y + height), ImGui.GetColorU32(band));
        draw.AddRectFilled(pos, new Num.Vector2(pos.X + 4f, pos.Y + height), ImGui.GetColorU32(edge));

        float cursorX = ImGui.GetCursorPosX();
        ImGui.SetCursorPosX(cursorX + 11f);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(textColor, label);
        ImGui.SetWindowFontScale(restoreScale);
        ImGui.Separator();
    }

    internal static void DrawInline(in DevToolSourceMark source, string prefix = null)
    {
        if (!string.IsNullOrEmpty(prefix))
            ImGui.TextDisabled(prefix);

        if (!string.IsNullOrEmpty(prefix)) ImGui.SameLine();
        ImGui.TextColored(ReadableTextColor(source.Color), source.Label);
    }

    private static DevToolSourceMark Vanilla() => new(
        "official:vanilla",
        DevToolUiSettings.T("原版", "VANILLA"),
        VanillaColor);

    private static DevToolSourceMark Downpour() => new(
        "official:" + DownpourModId,
        DevToolUiSettings.T("倾盆大雨", "DOWNPOUR"),
        DownpourColor);

    private static DevToolSourceMark Watcher() => new(
        "official:" + WatcherModId,
        DevToolUiSettings.T("守望者", "WATCHER"),
        WatcherColor);

    private static DevToolSourceMark Missing() => new(
        "missing",
        DevToolUiSettings.T("缺失", "MISSING"),
        MissingColor);

    private static DevToolSourceMark Named(string key, string label)
    {
        string stableKey = string.IsNullOrWhiteSpace(key) ? Normalize(label) : Normalize(key);
        if (string.IsNullOrEmpty(stableKey)) stableKey = "unknown";
        return new DevToolSourceMark("source:" + stableKey, label, StableModColor(stableKey));
    }

    private static bool IsVanilla(string normalized) =>
        normalized == "vanilla" ||
        normalized.Contains("rain world") ||
        normalized.Contains("rainworld") ||
        normalized.Contains("原版");

    private static bool IsDownpour(string idOrLabel, string displayName) =>
        string.Equals(idOrLabel, DownpourModId, StringComparison.OrdinalIgnoreCase) ||
        Contains(displayName, "downpour") ||
        Contains(displayName, "more slugcats") ||
        Contains(displayName, "more slugcats expansion") ||
        Contains(displayName, "倾盆大雨");

    private static bool IsWatcher(string idOrLabel, string displayName) =>
        string.Equals(idOrLabel, WatcherModId, StringComparison.OrdinalIgnoreCase) ||
        Contains(displayName, "watcher") ||
        Contains(displayName, "守望者");

    private static bool Contains(string value, string needle) =>
        !string.IsNullOrEmpty(value) &&
        value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    private static string Normalize(string value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();

    private static Num.Vector4 StableModColor(string stableKey)
    {
        uint hash = 2166136261u;
        for (int i = 0; i < stableKey.Length; i++)
        {
            hash ^= char.ToLowerInvariant(stableKey[i]);
            hash *= 16777619u;
        }

        float hue = (hash % 360u) / 360f;
        float saturation = 0.58f + ((hash >> 8) & 0xFFu) / 255f * 0.18f;
        float value = 0.78f + ((hash >> 16) & 0xFFu) / 255f * 0.14f;
        return Hsv(hue, saturation, value);
    }

    private static Num.Vector4 Hsv(float hue, float saturation, float value)
    {
        float h = (hue - (float)Math.Floor(hue)) * 6f;
        int sector = (int)Math.Floor(h);
        float fraction = h - sector;
        float p = value * (1f - saturation);
        float q = value * (1f - saturation * fraction);
        float t = value * (1f - saturation * (1f - fraction));

        return (sector % 6) switch
        {
            0 => new Num.Vector4(value, t, p, 1f),
            1 => new Num.Vector4(q, value, p, 1f),
            2 => new Num.Vector4(p, value, t, 1f),
            3 => new Num.Vector4(p, q, value, 1f),
            4 => new Num.Vector4(t, p, value, 1f),
            _ => new Num.Vector4(value, p, q, 1f)
        };
    }

    private static float Luminance(Num.Vector4 color) =>
        color.X * 0.2126f + color.Y * 0.7152f + color.Z * 0.0722f;

    private static Num.Vector4 ReadableTextColor(Num.Vector4 color)
    {
        if (Luminance(color) < 0.24f)
            return new Num.Vector4(0.82f, 0.84f, 0.88f, Math.Max(0.96f, color.W));

        const float floor = 0.58f;
        const float lift = 0.16f;
        return new Num.Vector4(
            Math.Min(1f, Math.Max(floor, color.X + lift)),
            Math.Min(1f, Math.Max(floor, color.Y + lift)),
            Math.Min(1f, Math.Max(floor, color.Z + lift)),
            Math.Max(0.94f, color.W));
    }
}
