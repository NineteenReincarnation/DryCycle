using System;
using UnityEngine;

namespace DryCycle.Iterators;

/// <summary>PWN_AI 的第四阶段样例。无剧情命名、AI 或对话；区域未安装时仅保留无副作用的房间绑定。</summary>
public static class PwnIteratorExample
{
    public const string ID = "DryCycle_PWN_Sample";
    private static IteratorDescriptor _definition;
    public static IteratorDescriptor Register()
    {
        if (_definition != null && IteratorRegistry.TryGet(new IteratorID(ID), out IteratorDescriptor current) && ReferenceEquals(current, _definition)) return _definition;
        _definition = Iterator.Create(ID).Name("PWN 示例迭代器").Room("PWN_AI")
            .Runtime(context => new SampleRuntime(context))
            .Graphics(context => new PwnIteratorGraphics(context))
            .WithMetadata("DryCycle.Example", "Phase4").Register();
        return _definition;
    }
    public static void Unregister()
    {
        if (_definition == null) return;
        IteratorDescriptor definition = _definition; _definition = null;
        IteratorRegistry.Unregister(definition);
    }
    internal static void Enable()
    {
        try { Register(); }
        catch (Exception exception) { new IteratorLogger(new IteratorID(ID)).ForModule("Example").Error("PWN_AI example registration failed.", exception); }
    }
    private sealed class SampleRuntime : IteratorRuntime
    {
        private static readonly IteratorPose DisplayPose = new("PwnDisplay", leftHandOffset: new Vector2(-37, 3),
            rightHandOffset: new Vector2(37, 9), leftFootOffset: new Vector2(-4, -71), rightFootOffset: new Vector2(4, -71));
        internal SampleRuntime(IteratorContext context) : base(context) { }
        protected override void OnActivate() => Body.SetPose(DisplayPose);
    }
}

/// <summary>参考形象的白色面部、金色扭转头饰及白紫长袍；没有两侧蓝球。</summary>
public sealed class PwnIteratorGraphics : StandardIteratorGraphics
{
    public static GraphicsProfile Appearance { get; } = new(bodyColor: new Color(0.98f, 0.98f, 1f),
        shadowColor: new Color(0.59f, 0.56f, 0.98f), outlineColor: new Color(0.065f, 0.085f, 0.26f),
        eyeColor: new Color(0.02f, 0.015f, 0.14f), accentColor: new Color(0.97f, 0.86f, 0.34f),
        haloCount: 0, cable: false, paletteInfluence: 0.08f);
    public PwnIteratorGraphics(IteratorContext context) : base(context, Appearance) { }
    protected override void OnInitialize()
    {
        base.OnInitialize();
        AddPart(new GoldenCrown());
    }
}

internal sealed class GoldenCrown : IteratorMeshPart
{
    internal GoldenCrown() : base("Crown") { }
    protected override void OnInitialize()
    {
        for (int side = 0; side < 2; side++)
        {
            float s = side == 0 ? -1f : 1f;
            string prefix = side == 0 ? "Left" : "Right";
            // Back-to-front overlapping leaves make the headdress read as a twist.
            Leaf(prefix + "Tip", s, new Vector2(17, 39), new Vector2(27, 52), new Vector2(12, 58), new Vector2(16, 64), 5.6f, 40);
            Leaf(prefix + "Upper", s, new Vector2(17, 30), new Vector2(25, 40), new Vector2(13, 44), new Vector2(21, 49), 4.4f, 42);
            Leaf(prefix + "Middle", s, new Vector2(16, 21), new Vector2(25, 29), new Vector2(13, 33), new Vector2(21, 38), 4.1f, 44);
            Leaf(prefix + "Lower", s, new Vector2(17, 12), new Vector2(24, 14), new Vector2(17, 23), new Vector2(20, 27), 3.6f, 46);
            Leaf(prefix + "Tail", s, new Vector2(14, 15), new Vector2(11, 11), new Vector2(18, 9), new Vector2(14, 5), 2.3f, 27);
            OutlinedEllipse(prefix + "Joint", new Vector2(s * 16.5f, 14.2f), 2f, 2.4f, new Color(0.39f, 0.19f, 0.13f), 50);
        }
    }
    private void Leaf(string name, float side, Vector2 a, Vector2 b, Vector2 c, Vector2 d, float width, int layer)
    {
        const int segments = 20;
        var points = new Vector2[segments * 2];
        for (int i = 0; i <= segments; i++) points[i] = Point(i / (float)segments, 1f);
        for (int i = segments - 1; i > 0; i--) points[segments * 2 - i] = Point(i / (float)segments, -1f);
        SpriteHandle leaf = OutlinedPolygon(name, points, Profile.AccentColor, layer, 0.5f);
        for (int i = 0; i < points.Length; i++)
        {
            float t = Mathf.InverseLerp(a.y, d.y, points[i].y);
            Color color = Color.Lerp(new Color(0.49f, 0.43f, 0.22f), Profile.AccentColor, Mathf.Clamp01(t * 2.7f));
            leaf.Mesh.SetColor(i, color);
        }
        if (name.EndsWith("Tip", StringComparison.Ordinal))
        {
            var highlight = new Vector2[13];
            for (int i = 0; i < highlight.Length; i++) highlight[i] = Point(0.28f + i * 0.045f, -0.92f);
            Stroke(name + "Rim", highlight, 0.6f, new Color(0.68f, 1f, 0.92f), layer + 2);
        }
        Vector2 Point(float t, float flank)
        {
            float u = 1f - t;
            Vector2 center = u * u * u * a + 3f * u * u * t * b + 3f * u * t * t * c + t * t * t * d;
            Vector2 tangent = (3f * u * u * (b - a) + 6f * u * t * (c - b) + 3f * t * t * (d - c)).normalized;
            Vector2 p = center + new Vector2(-tangent.y, tangent.x) * (float)Math.Pow(Math.Max(0, Math.Sin(t * Math.PI)), 0.7) * width * flank;
            p.x *= side;
            if (side > 0) p.y += 1f;
            return p;
        }
    }
}
