using System;
using ImGuiNET;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Compact scrollbar presentation for the rebuilt DevTool.
///
/// The surrounding frontend already pushes ScrollbarSize as part of its layout scale. This helper
/// intentionally runs after that push and overrides only the active frame's scrollbar geometry.
/// The outer style stack restores the previous size at the end of the frame, so the animation does
/// not leak into other RWImGui consumers.
/// </summary>
internal static class DevToolScrollChrome
{
    private static float wheelActivity;

    internal static void Apply(ImGuiIOPtr io, float uiScale)
    {
        float dt = Math.Max(0f, Math.Min(0.10f, io.DeltaTime));
        float wheel = Math.Abs(io.MouseWheel);

        // Mouse-wheel input gives the bar a fast "island" expansion. It then eases back to the
        // compact rail instead of snapping instantly to the resting size.
        if (wheel > 0.001f)
        {
            float impulse = Math.Min(1f, 0.62f + wheel * 0.24f);
            wheelActivity = Math.Max(wheelActivity, impulse);
        }
        else
        {
            float decay = (float)Math.Exp(-5.6f * dt);
            wheelActivity *= decay;
            if (wheelActivity < 0.002f) wheelActivity = 0f;
        }

        float density = Math.Max(0.92f, Math.Min(1.28f, 0.74f + uiScale * 0.20f));
        float compactSize = 5.8f * density;
        float expandedSize = 12.8f * density;
        float size = compactSize + (expandedSize - compactSize) * EaseOutCubic(wheelActivity);

        ImGuiStylePtr style = ImGui.GetStyle();
        style.ScrollbarSize = size;
        style.ScrollbarRounding = 999f;
    }

    private static float EaseOutCubic(float value)
    {
        value = Math.Max(0f, Math.Min(1f, value));
        float inverse = 1f - value;
        return 1f - inverse * inverse * inverse;
    }
}
