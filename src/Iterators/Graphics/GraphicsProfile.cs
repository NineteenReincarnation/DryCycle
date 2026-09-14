using System;
using UnityEngine;

namespace DryCycle.Iterators;

/// <summary>可共享的不可变外观配置；不规定身体结构，自定义 Graphics 可以完全替换标准部件。</summary>
public sealed class GraphicsProfile
{
    public static GraphicsProfile Default { get; } = new();

    public GraphicsProfile(Color? bodyColor = null, Color? shadowColor = null, Color? outlineColor = null,
        Color? eyeColor = null, Color? accentColor = null, float scale = 1f, bool face = true,
        bool gown = true, int haloCount = 1, bool cable = true, bool mark = true,
        string shader = "Basic", string element = "Futile_White", float paletteInfluence = 0.15f,
        float glow = 0f)
    {
        if (float.IsNaN(scale) || scale < 0.1f || scale > 20f) throw new ArgumentOutOfRangeException(nameof(scale));
        if (haloCount < 0 || haloCount > 8) throw new ArgumentOutOfRangeException(nameof(haloCount));
        if (float.IsNaN(paletteInfluence) || paletteInfluence < 0f || paletteInfluence > 1f) throw new ArgumentOutOfRangeException(nameof(paletteInfluence));
        if (float.IsNaN(glow) || glow < 0f || glow > 1f) throw new ArgumentOutOfRangeException(nameof(glow));
        IteratorValidation.RequireText(shader, nameof(shader));
        IteratorValidation.RequireText(element, nameof(element));
        BodyColor = ValidColor(bodyColor ?? new Color(0.95f, 0.94f, 1f));
        ShadowColor = ValidColor(shadowColor ?? new Color(0.52f, 0.49f, 0.85f));
        OutlineColor = ValidColor(outlineColor ?? new Color(0.08f, 0.09f, 0.24f));
        EyeColor = ValidColor(eyeColor ?? new Color(0.02f, 0.02f, 0.12f));
        AccentColor = ValidColor(accentColor ?? new Color(0.95f, 0.8f, 0.3f));
        Scale = scale; Face = face; Gown = gown; HaloCount = haloCount; Cable = cable; Mark = mark;
        Shader = shader; Element = element; PaletteInfluence = paletteInfluence; Glow = glow;
    }

    public Color BodyColor { get; }
    public Color ShadowColor { get; }
    public Color OutlineColor { get; }
    public Color EyeColor { get; }
    public Color AccentColor { get; }
    public float Scale { get; }
    public bool Face { get; }
    public bool Gown { get; }
    public int HaloCount { get; }
    public bool Cable { get; }
    public bool Mark { get; }
    public string Shader { get; }
    public string Element { get; }
    public float PaletteInfluence { get; }
    /// <summary>降低环境调色的比例；仅为发光外观，不创建房间光源。</summary>
    public float Glow { get; }

    internal static Color ValidColor(Color color)
    {
        if (!Finite(color.r) || !Finite(color.g) || !Finite(color.b) || !Finite(color.a))
            throw new ArgumentException("Graphics colors must be finite.");
        return color;
    }

    internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
