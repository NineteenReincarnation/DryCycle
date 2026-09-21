using System;
using System.Collections.Generic;
using BepInEx.Logging;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Compact scrollbar presentation for the rebuilt DevTool.
///
/// ScrollbarSize is an ImGui style value and therefore global for every window drawn after it in
/// the same frame. The old implementation drove that global value directly from io.MouseWheel,
/// which made unrelated panes animate together. Keep the native rail compact and stable, then draw
/// the expansion as a per-window overlay whose activity belongs only to the hovered scroll region.
/// </summary>
internal static class DevToolScrollChrome
{
    private sealed class ScrollState
    {
        internal float Activity;
        internal int LastSeenFrame;
    }

    private static readonly Dictionary<string, ScrollState> states = new(StringComparer.Ordinal);

    /// <summary>
    /// Applies only the stable compact native scrollbar geometry. Wheel animation is deliberately
    /// not handled here because ImGuiStyle is shared by all windows in the frame.
    /// </summary>
    internal static void Apply(ImGuiIOPtr io, float uiScale)
    {
        float density = Density(uiScale);
        ImGuiStylePtr style = ImGui.GetStyle();
        style.ScrollbarSize = CompactSize(density);
        style.ScrollbarRounding = 999f;
    }

    /// <summary>
    /// Draws the animated part of the scrollbar for the current ImGui window/child only.
    /// Call this while that window is still current, after its contents have been submitted.
    /// </summary>
    internal static void DrawCurrentRegion(string key, ImGuiIOPtr io, float uiScale)
    {
        if (string.IsNullOrEmpty(key)) return;

        if (!states.TryGetValue(key, out ScrollState state))
        {
            state = new ScrollState();
            states.Add(key, state);
        }

        float dt = Math.Max(0f, Math.Min(0.10f, io.DeltaTime));
        bool hovered = ImGui.IsWindowHovered();
        float wheel = hovered ? Math.Abs(io.MouseWheel) : 0f;

        if (wheel > 0.001f)
        {
            float impulse = Math.Min(1f, 0.62f + wheel * 0.24f);
            state.Activity = Math.Max(state.Activity, impulse);
        }
        else
        {
            float decay = (float)Math.Exp(-5.6f * dt);
            state.Activity *= decay;
            if (state.Activity < 0.002f) state.Activity = 0f;
        }

        state.LastSeenFrame = ImGui.GetFrameCount();
        if (state.Activity <= 0f) return;

        float scrollMax = ImGui.GetScrollMaxY();
        if (scrollMax <= 0.5f) return;

        float density = Density(uiScale);
        float compact = CompactSize(density);
        float expanded = ExpandedSize(density);
        float width = compact + (expanded - compact) * EaseOutCubic(state.Activity);

        Num.Vector2 windowPos = ImGui.GetWindowPos();
        Num.Vector2 windowSize = ImGui.GetWindowSize();
        if (windowSize.X <= 1f || windowSize.Y <= 1f) return;

        ImGuiStylePtr style = ImGui.GetStyle();
        float top = windowPos.Y + Math.Max(2f, style.WindowPadding.Y * 0.35f);
        float bottom = windowPos.Y + windowSize.Y - Math.Max(2f, style.WindowPadding.Y * 0.35f);
        float trackHeight = Math.Max(1f, bottom - top);

        // scrollMax is the distance between the first and last legal scroll positions. Adding the
        // viewport height reconstructs a useful approximation of total content height.
        float viewportHeight = Math.Max(1f, windowSize.Y);
        float totalHeight = viewportHeight + scrollMax;
        float thumbHeight = Math.Max(20f * density, trackHeight * viewportHeight / totalHeight);
        thumbHeight = Math.Min(trackHeight, thumbHeight);
        float travel = Math.Max(0f, trackHeight - thumbHeight);
        float t = scrollMax <= 0.001f ? 0f : Math.Max(0f, Math.Min(1f, ImGui.GetScrollY() / scrollMax));
        float thumbTop = top + travel * t;

        float right = windowPos.X + windowSize.X - 1.5f;
        float left = right - width;
        uint color = ImGui.GetColorU32(hovered ? ImGuiCol.ScrollbarGrabHovered : ImGuiCol.ScrollbarGrab);
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(
            new Num.Vector2(left, thumbTop),
            new Num.Vector2(right, thumbTop + thumbHeight),
            color,
            999f);
    }

    internal static void PruneInactiveStates()
    {
        int frame = ImGui.GetFrameCount();
        if (states.Count < 24 || frame % 240 != 0) return;

        List<string> stale = null;
        foreach (KeyValuePair<string, ScrollState> pair in states)
        {
            if (frame - pair.Value.LastSeenFrame <= 600) continue;
            stale ??= new List<string>();
            stale.Add(pair.Key);
        }

        if (stale == null) return;
        for (int i = 0; i < stale.Count; i++) states.Remove(stale[i]);
    }

    private static float Density(float uiScale) =>
        Math.Max(0.92f, Math.Min(1.28f, 0.74f + uiScale * 0.20f));

    private static float CompactSize(float density) => 5.8f * density;

    private static float ExpandedSize(float density) => 12.8f * density;

    private static float EaseOutCubic(float value)
    {
        value = Math.Max(0f, Math.Min(1f, value));
        float inverse = 1f - value;
        return 1f - inverse * inverse * inverse;
    }
}

internal static class ScopedScrollChrome
{
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        logger?.LogInfo("DevTool scoped scrollbar animation enabled through direct pane calls; no self-detours attached.");
    }

    internal static void Disable() => enabled = false;

    internal static void Draw(string key, bool pruneAfter = false)
    {
        if (!enabled) return;
        float scale = Math.Max(0.75f, Math.Min(3f, DevToolUiSettings.UiScale));
        DevToolScrollChrome.DrawCurrentRegion(key, ImGui.GetIO(), scale);
        if (pruneAfter)
            DevToolScrollChrome.PruneInactiveStates();
    }
}
