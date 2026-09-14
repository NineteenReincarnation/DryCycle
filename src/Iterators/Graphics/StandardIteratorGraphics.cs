using System;
using UnityEngine;

namespace DryCycle.Iterators;

/// <summary>标准人形组合；重写 OnInitialize 可选择部件，或直接继承 IteratorGraphics 使用其他身体结构。</summary>
public class StandardIteratorGraphics : IteratorGraphics
{
    public StandardIteratorGraphics(IteratorContext context, GraphicsProfile profile = null) : base(context, profile) { }
    protected override void OnInitialize()
    {
        if (Profile.Cable) AddPart(new StandardCable());
        if (Profile.HaloCount > 0) AddPart(new StandardHalo(Profile.HaloCount));
        AddPart(new StandardBodyVisual());
        if (Profile.Gown) AddPart(new StandardGown());
        if (Profile.Face) AddPart(new StandardFace());
    }
}

public sealed class StandardHalo : IteratorMeshPart
{
    private readonly int _count;
    public StandardHalo(int count = 1) : base("Halo")
    { if (count < 1 || count > 8) throw new ArgumentOutOfRangeException(nameof(count)); _count = count; }
    protected override void OnInitialize()
    {
        for (int i = 0; i < _count; i++)
            Stroke("Ring" + i, RingPath(new Vector2(0f, 25f), 22f + i * 4f, 22f + i * 4f), 0.65f, Profile.AccentColor, -50, true);
    }
}

/// <summary>固定锚点机械臂的视觉线缆，不执行约束或物理；NoArm 时不绘制。</summary>
public sealed class StandardCable : IteratorMeshPart
{
    private SpriteHandle _cable;
    public StandardCable() : base("Cable") { }
    protected override void OnInitialize()
    {
        if (Context.Arm is not FixedArm) return;
        _cable = Register("Main", new IteratorMesh(new Vector2[34], GridTriangles(2, 17), Profile.OutlineColor), -60);
    }
    protected override void OnDraw(IteratorDrawState state)
    {
        if (_cable == null || Context.Arm is not FixedArm arm) return;
        Vector2 start = arm.Anchor, end = state.Local(0f, -3f);
        Vector2 normal = new Vector2(-(end - start).y, (end - start).x).normalized * (1.1f * Profile.Scale);
        for (int i = 0; i <= 16; i++)
        {
            float t = i / 16f;
            Vector2 center = Vector2.Lerp(start, end, t) + Vector2.down * (float)Math.Sin(t * Math.PI) * 14f * Profile.Scale;
            _cable.Mesh.SetVertex(i * 2, center - normal); _cable.Mesh.SetVertex(i * 2 + 1, center + normal);
        }
    }
}

public sealed class StandardFace : IteratorMeshPart
{
    private readonly SpriteHandle[] _eyes = new SpriteHandle[4];
    public StandardFace() : base("Face") { }
    protected override void OnInitialize()
    {
        SpriteHandle head = OutlinedEllipse("Head", new Vector2(0, 23), 14.2f, 15.2f, Profile.BodyColor, 30);
        for (int i = 0; i < head.Mesh.Vertices.Count; i++)
        {
            float shade = Mathf.Clamp01((Rest(head)[i].x - 5f) / 12f) * 0.35f;
            head.Mesh.SetColor(i, Color.Lerp(Profile.BodyColor, Profile.ShadowColor, shade));
        }
        for (int side = 0; side < 2; side++)
        {
            float x = side == 0 ? -5.5f : 5.5f;
            _eyes[side * 2] = Ellipse("Eye" + side, new Vector2(x, 21.5f), 2.5f, 4.6f, Profile.EyeColor, 33);
            _eyes[side * 2 + 1] = Ellipse("Glint" + side, new Vector2(x - 0.45f, 23.1f), 0.55f, 0.95f, Color.white, 34, 12);
        }
        if (Profile.Mark)
        {
            Stroke("Mark", RingPath(new Vector2(-0.8f, 31f), 3.3f, 3.4f, 32), 0.45f, Profile.OutlineColor, 35, true);
            OutlinedEllipse("MarkSatellite", new Vector2(2.5f, 33f), 1.15f, 1.4f, Profile.BodyColor, 36);
        }
    }
    protected override void OnDraw(IteratorDrawState state)
    {
        base.OnDraw(state);
        float blinkPhase = state.Time % 5.7f;
        float blink = blinkPhase > 5.5f ? Math.Abs(blinkPhase - 5.6f) / 0.1f : 1f;
        blink = Math.Max(0.07f, blink);
        float lookX = Vector2.Dot(state.Look, state.Right) * 1.3f, lookY = Vector2.Dot(state.Look, state.Up) * 0.7f;
        foreach (SpriteHandle eye in _eyes)
        {
            Vector2[] rest = Rest(eye);
            for (int i = 0; i < rest.Length; i++)
                eye.Mesh.SetVertex(i, state.Local(rest[i].x + lookX, 21.5f + (rest[i].y - 21.5f) * blink + lookY));
        }
    }
}

public sealed class StandardBodyVisual : IteratorMeshPart
{
    private readonly SpriteHandle[,] _limbs = new SpriteHandle[2, 4];
    private readonly SpriteHandle[] _feet = new SpriteHandle[2];
    private readonly SpriteHandle[] _handEdges = new SpriteHandle[2];
    public StandardBodyVisual() : base("Body") { }
    protected override void OnInitialize()
    {
        OutlinedPolygon("Torso", new[] { new Vector2(-7, 8), new Vector2(7, 8), new Vector2(6, -14), new Vector2(-6, -14) }, Profile.ShadowColor, -20);
        OutlinedPolygon("Neck", new[] { new Vector2(-3.5f, 12), new Vector2(3.5f, 12), new Vector2(3, 5), new Vector2(0, 2), new Vector2(-3, 5) }, Profile.BodyColor, 25);
        for (int side = 0; side < 2; side++)
        {
            string name = side == 0 ? "Left" : "Right";
            _limbs[side, 0] = OutlinedPolygon(name + "Sleeve", new[] { new Vector2(0, -3), new Vector2(0, 4), new Vector2(9, 7), new Vector2(20, 4.5f), new Vector2(20, -4.5f), new Vector2(10, -7) }, Profile.BodyColor, -10);
            _limbs[side, 1] = Sprites[Name + "." + name + "SleeveEdge"];
            _limbs[side, 2] = Polygon(name + "SleeveFold", new[] { new Vector2(6, -2), new Vector2(13, -5.4f), new Vector2(19, -3.6f), new Vector2(15.6f, 3.8f) }, Profile.ShadowColor, -8);
            // Open hand silhouette with separated fingers; x extends out from the wrist.
            Vector2[] hand = { new(0, -2), new(3, -2), new(6, -5), new(6.7f, -4.7f), new(4.7f, -1.6f),
                new(8, -3), new(8.6f, -2.5f), new(5.5f, -0.3f), new(9, -0.5f), new(9.1f, 0.2f),
                new(5.5f, 1), new(8, 2.7f), new(7.7f, 3.3f), new(4, 2), new(2.6f, 4.3f), new(2, 4), new(2.5f, 1.5f), new(0, 2) };
            _limbs[side, 3] = OutlinedPolygon(name + "Hand", hand, Profile.BodyColor, -5, 0.4f);
            _handEdges[side] = Sprites[Name + "." + name + "HandEdge"];
            _feet[side] = Ellipse(name + "Foot", Vector2.zero, 2.5f, 5f, Profile.OutlineColor, -25, 20);
        }
    }
    protected override void OnDraw(IteratorDrawState state)
    {
        base.OnDraw(state);
        for (int side = 0; side < 2; side++)
        {
            float s = side == 0 ? -1f : 1f;
            Vector2 start = state.Local(s * 7f, 5f), hand = side == 0 ? state.LeftHand : state.RightHand;
            Vector2 delta = hand - start;
            Vector2 along = delta.sqrMagnitude < 0.01f ? state.Right * s : delta.normalized;
            Vector2 normal = new Vector2(-along.y, along.x) * s;
            for (int j = 0; j < 3; j++)
            {
                SpriteHandle sleeve = _limbs[side, j]; Vector2[] rest = Rest(sleeve);
                for (int i = 0; i < rest.Length; i++) sleeve.Mesh.SetVertex(i, start + delta * (rest[i].x / 20f) + normal * rest[i].y * Profile.Scale);
            }
            DrawAt(_limbs[side, 3], hand, along, normal, Profile.Scale);
            DrawAt(_handEdges[side], hand, along, normal, Profile.Scale);
            DrawAt(_feet[side], side == 0 ? state.LeftFoot : state.RightFoot, state.Right, state.Up, Profile.Scale);
        }
    }
}

/// <summary>纯视觉袍子网格，确定性轻摆和褶皱；不会为 Body 增加碰撞或改变重力。</summary>
public sealed class StandardGown : IteratorMeshPart
{
    private SpriteHandle _skirt, _edge;
    private SpriteHandle _fold;
    private const int Columns = 9, Rows = 13;
    public StandardGown() : base("Gown") { }
    protected override void OnInitialize()
    {
        var points = new Vector2[Columns * Rows];
        for (int row = 0; row < Rows; row++)
        for (int col = 0; col < Columns; col++)
        {
            float t = row / (Rows - 1f), u = col / (Columns - 1f);
            points[row * Columns + col] = new Vector2((u * 2f - 1f) * (7f + 18f * (float)Math.Pow(t, 1.22)),
                -10f - t * 59f + ((float)Math.Cos(u * Math.PI * 4f) * 0.75f + (u * 2f - 1f) * (u * 2f - 1f) * 2.5f) * t);
        }
        _skirt = Remember(Register("Skirt", new IteratorMesh(points, GridTriangles(Columns, Rows), Profile.BodyColor), 0), points);
        float[] shading = { 0.02f, 0.08f, 0.13f, 0f, 0.06f, 0.7f, 0.27f, 0.5f, 0.65f };
        for (int row = 0; row < Rows; row++)
        for (int col = 0; col < Columns; col++)
        {
            float shade = shading[col];
            _skirt.Mesh.SetColor(row * Columns + col, Color.Lerp(Profile.BodyColor, Profile.ShadowColor, shade));
        }
        var outline = new Vector2[2 * Rows + Columns - 2]; int n = 0;
        for (int row = 0; row < Rows; row++) outline[n++] = points[row * Columns];
        for (int col = 1; col < Columns; col++) outline[n++] = points[(Rows - 1) * Columns + col];
        for (int row = Rows - 2; row >= 0; row--) outline[n++] = points[row * Columns + Columns - 1];
        _edge = Stroke("SkirtEdge", outline, 0.65f, Profile.OutlineColor, 2);
        OutlinedPolygon("Cape", new[] { new Vector2(-3, 6), new Vector2(-12, 8), new Vector2(-23, 12), new Vector2(-22, 1),
            new Vector2(-18, -9), new Vector2(-9, -15), new Vector2(0, -13), new Vector2(9, -15), new Vector2(18, -9),
            new Vector2(22, 1), new Vector2(23, 12), new Vector2(12, 8), new Vector2(3, 6), new Vector2(0, 1) }, Profile.BodyColor, 12);
        Polygon("CapeRightShade", new[] { new Vector2(3, 3), new Vector2(21, 10), new Vector2(20, 0), new Vector2(16, -8), new Vector2(9, -14), new Vector2(7, -7) }, Color.Lerp(Profile.BodyColor, Profile.ShadowColor, 0.45f), 14);
        Polygon("CapeLeftFold", new[] { new Vector2(-6, 3), new Vector2(-17, -6), new Vector2(-9, -14), new Vector2(-12, -6) }, Profile.ShadowColor, 14);
        Stroke("CapeSeam", new[] { new Vector2(0, 1), new Vector2(-3, -5), new Vector2(-1, -12) }, 0.4f, Profile.OutlineColor, 16);
        _fold = Polygon("SkirtFold", new[] { new Vector2(3, -15), new Vector2(13, -47), new Vector2(10, -56), new Vector2(8, -42) }, Profile.ShadowColor, 1);
    }
    protected override void OnDraw(IteratorDrawState state)
    {
        base.OnDraw(state);
        Sway(_skirt, state); Sway(_edge, state); Sway(_fold, state);
    }
    private void Sway(SpriteHandle sprite, IteratorDrawState state)
    {
        Vector2[] rest = Rest(sprite);
        for (int i = 0; i < rest.Length; i++)
        {
            Vector2 p = rest[i]; float t = Mathf.Clamp01((-p.y - 10f) / 59f);
            p.x += t * t * ((float)Math.Sin(state.Time * 1.4f + p.y * 0.025f) * 1.5f - Vector2.Dot(state.Velocity, state.Right) * 1.6f);
            sprite.Mesh.SetVertex(i, state.Local(p));
        }
    }
}
