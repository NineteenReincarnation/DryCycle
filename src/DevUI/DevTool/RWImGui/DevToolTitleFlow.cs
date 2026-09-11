using System;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Reproduces the moving highlight used by Rain World's menu title without taking ownership of
/// RWImGui's renderer. The reference executable uses a 100x1 gradient texture and a screen-space
/// diagonal coordinate. RWImGui does not expose a per-command Unity material/shader binding, so
/// the frontend evaluates the same gradient on the CPU and clips ordinary ImGui text into narrow
/// strips. Every title shares one screen-space phase, so the highlight travels continuously through
/// the whole editor rather than restarting on every label.
/// </summary>
internal static class DevToolTitleFlow
{
    private const float ReferenceBase = 163f;
    private const float ReferenceDark = 129f;
    private const float FlowSpeed = -0.125f;
    private const float ScreenXWeight = 0.70f;
    private const float ScreenYWeight = -0.19f;

    // Luminance of the 100x1 textgradient.png embedded in RainWorldRender.exe. Keeping the source
    // data here makes the frontend self-contained and avoids deploying another texture beside the
    // RWImGui DLL. The Unity shader in shader-src implements the same profile analytically.
    private static readonly byte[] Gradient =
    {
        163, 163, 163, 163, 163, 163, 163, 163, 163, 163,
        163, 163, 163, 163, 163, 163, 163, 163, 163, 163,
        163, 163, 163, 156, 146, 136, 129, 163, 164, 166,
        167, 133, 134, 137, 140, 143, 146, 150, 155, 158,
        164, 169, 174, 179, 184, 189, 194, 198, 203, 207,
        212, 214, 217, 219, 222, 224, 227, 229, 232, 234,
        236, 238, 241, 243, 244, 246, 248, 250, 251, 252,
        254, 255, 163, 163, 163, 163, 163, 163, 255, 255,
        255, 255, 163, 163, 163, 255, 251, 245, 238, 230,
        222, 214, 204, 196, 188, 180, 173, 167, 163, 163
    };

    internal static void Draw(
        ImDrawListPtr draw,
        Num.Vector2 textPosition,
        Num.Vector2 textSize,
        string text,
        Num.Vector4 baseColor,
        float strength)
    {
        if (string.IsNullOrEmpty(text) || textSize.X <= 0.5f || textSize.Y <= 0.5f || strength <= 0f)
            return;

        Num.Vector2 display = ImGui.GetIO().DisplaySize;
        float screenWidth = Math.Max(1f, display.X);
        float screenHeight = Math.Max(1f, display.Y);
        float seconds = (Environment.TickCount & int.MaxValue) * 0.001f;
        float phase = seconds * FlowSpeed;

        // The reference Y weight is small enough that one title-height sample preserves its
        // diagonal motion. Only slicing along X avoids multiplying ImGui draw commands by 2-3x.
        float desiredStripWidth = Math.Max(3f, Math.Min(7f, textSize.Y * 0.22f));
        int columns = Math.Max(1, Math.Min(48, (int)Math.Ceiling(textSize.X / desiredStripWidth)));
        float stripWidth = textSize.X / columns;
        float sampleY = (textPosition.Y + textSize.Y * 0.5f) / screenHeight;
        float y0 = textPosition.Y;
        float y1 = textPosition.Y + textSize.Y;

        for (int column = 0; column < columns; column++)
        {
            float x0 = textPosition.X + column * stripWidth;
            float x1 = column == columns - 1 ? textPosition.X + textSize.X : x0 + stripWidth + 0.5f;
            float sampleX = (x0 + x1) * 0.5f / screenWidth;
            float coordinate = Frac(ScreenXWeight * sampleX + ScreenYWeight * sampleY + phase);
            float sample = SampleGradient(coordinate);

            float bright = Math.Max(0f, (sample - ReferenceBase) / (255f - ReferenceBase));
            float dark = Math.Max(0f, (ReferenceBase - sample) / (ReferenceBase - ReferenceDark));
            if (bright < 0.015f && dark < 0.015f)
                continue;

            Num.Vector4 color = FlowColor(baseColor, bright, dark, strength);
            draw.PushClipRect(new Num.Vector2(x0, y0), new Num.Vector2(x1, y1), true);
            draw.AddText(textPosition, ImGui.GetColorU32(color), text);
            draw.PopClipRect();
        }
    }

    private static Num.Vector4 FlowColor(Num.Vector4 source, float bright, float dark, float strength)
    {
        float shine = Math.Min(1f, bright * strength);
        float dim = Math.Min(0.22f, dark * 0.16f);
        float r = source.X * (1f - dim);
        float g = source.Y * (1f - dim);
        float b = source.Z * (1f - dim);

        // Rain World's reference texture is neutral rather than yellow. Blend toward white instead
        // of changing hue, which preserves the editor's existing title hierarchy colours.
        r += (1f - r) * shine;
        g += (1f - g) * shine;
        b += (1f - b) * shine;
        return new Num.Vector4(Clamp01(r), Clamp01(g), Clamp01(b), source.W);
    }

    private static float SampleGradient(float t)
    {
        float position = Clamp01(t) * (Gradient.Length - 1);
        int left = Math.Max(0, Math.Min(Gradient.Length - 1, (int)position));
        int right = Math.Min(Gradient.Length - 1, left + 1);
        float blend = position - left;
        return Gradient[left] + (Gradient[right] - Gradient[left]) * blend;
    }

    private static float Frac(float value) => value - (float)Math.Floor(value);

    private static float Clamp01(float value)
    {
        if (value < 0f) return 0f;
        return value > 1f ? 1f : value;
    }
}
