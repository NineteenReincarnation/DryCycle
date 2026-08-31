using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.MossySpider;

public sealed class MossySpiderGraphics : GraphicsModule
{
    private const int Samples = 13;
    private const int Carapace = 0;
    private const int MossShadow = 1;
    private const int MossCap = 2;

    private const int LegCount = 10;
    private const int LegParts = 6;
    private const int LegsStart = 3;
    private const int PlatesCount = 8;
    private const int PlatesStart = LegsStart + LegCount * LegParts;
    private const int TuftCount = 14;
    private const int TuftsStart = PlatesStart + PlatesCount;
    private const int FringeCount = 12;
    private const int FringeStart = TuftsStart + TuftCount;
    private const int GrassCount = 36;
    private const int GrassStart = FringeStart + FringeCount;
    private const int TotalSprites = GrassStart + GrassCount * 2;

    private readonly MossySpider spider;
    private readonly int seed;
    private readonly Vector2[] raw = new Vector2[MossySpider.SegmentCount];
    private readonly Vector2[] spine = new Vector2[Samples];
    private readonly Vector2[] tangent = new Vector2[Samples];
    private readonly Vector2[] up = new Vector2[Samples];

    private Color shellLow;
    private Color shellHigh;
    private Color rearLeg;
    private Color frontLeg;
    private Color joint;
    private Color plate;
    private Color mossLow;
    private Color moss;
    private Color mossHigh;

    public MossySpiderGraphics(MossySpider spider) : base(spider, false)
    {
        this.spider = spider;
        seed = spider.abstractPhysicalObject.ID.RandomSeed;
        cullRange = 900f;
    }

    public override void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
    {
        sLeaser.sprites = new FSprite[TotalSprites];
        sLeaser.sprites[Carapace] = TriangleMesh.MakeLongMesh(Samples - 1, false, true);
        sLeaser.sprites[MossShadow] = TriangleMesh.MakeLongMesh(Samples - 1, false, true);
        sLeaser.sprites[MossCap] = TriangleMesh.MakeLongMesh(Samples - 1, false, true);

        for (int i = 0; i < LegCount; i++)
        {
            float thick = Mathf.Lerp(0.92f, 1.12f, H(i, 1));
            sLeaser.sprites[Leg(i, 0)] = Line(7.2f * thick);
            sLeaser.sprites[Leg(i, 1)] = Line(5.8f * thick);
            sLeaser.sprites[Leg(i, 2)] = Line(3.6f * thick);
            sLeaser.sprites[Leg(i, 3)] = Circle(0.39f * thick, 0.34f * thick);
            sLeaser.sprites[Leg(i, 4)] = Circle(0.34f * thick, 0.30f * thick);
            sLeaser.sprites[Leg(i, 5)] = Line(1.45f * thick);
        }

        for (int i = 0; i < PlatesCount; i++)
            sLeaser.sprites[PlatesStart + i] = Circle(Mathf.Lerp(0.35f, 0.62f, H(i, 10)), Mathf.Lerp(0.16f, 0.27f, H(i, 11)));

        for (int i = 0; i < TuftCount; i++)
            sLeaser.sprites[TuftsStart + i] = Circle(Mathf.Lerp(0.72f, 1.22f, H(i, 20)), Mathf.Lerp(0.24f, 0.45f, H(i, 21)));

        for (int i = 0; i < FringeCount; i++)
            sLeaser.sprites[FringeStart + i] = Line(Mathf.Lerp(0.8f, 1.45f, H(i, 30)));

        for (int i = 0; i < GrassCount; i++)
        {
            float width = Mathf.Lerp(0.8f, 1.65f, H(i, 40));
            sLeaser.sprites[Grass(i, 0)] = Line(width * 1.12f);
            sLeaser.sprites[Grass(i, 1)] = Line(width * 0.82f);
        }

        ApplyPalette(sLeaser, rCam, rCam.currentPalette);
        AddToContainer(sLeaser, rCam, null);
    }

    public override void AddToContainer(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, FContainer newContainer)
    {
        sLeaser.RemoveAllSpritesFromContainer();
        FContainer c = newContainer ?? rCam.ReturnFContainer("Midground");

        for (int i = 0; i < LegCount; i++) if (!FrontLeg(i)) AddLeg(c, sLeaser, i);
        c.AddChild(sLeaser.sprites[Carapace]);
        for (int i = 0; i < PlatesCount; i++) c.AddChild(sLeaser.sprites[PlatesStart + i]);
        for (int i = 0; i < LegCount; i++) if (FrontLeg(i)) AddLeg(c, sLeaser, i);
        c.AddChild(sLeaser.sprites[MossShadow]);
        c.AddChild(sLeaser.sprites[MossCap]);
        for (int i = 0; i < TuftCount; i++) c.AddChild(sLeaser.sprites[TuftsStart + i]);
        for (int i = 0; i < FringeCount; i++) c.AddChild(sLeaser.sprites[FringeStart + i]);
        for (int i = 0; i < GrassCount; i++)
        {
            c.AddChild(sLeaser.sprites[Grass(i, 0)]);
            c.AddChild(sLeaser.sprites[Grass(i, 1)]);
        }
    }

    public override void ApplyPalette(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, RoomPalette p)
    {
        base.ApplyPalette(sLeaser, rCam, p);
        float d = Mathf.Clamp01(p.darkness);
        shellLow = Color.Lerp(new Color(0.115f, 0.085f, 0.073f), p.blackColor, 0.28f + d * 0.48f);
        shellHigh = Color.Lerp(new Color(0.225f, 0.165f, 0.135f), p.blackColor, 0.18f + d * 0.42f);
        rearLeg = Color.Lerp(shellLow, p.blackColor, 0.34f);
        frontLeg = Color.Lerp(shellHigh, p.blackColor, 0.18f);
        joint = Color.Lerp(shellHigh, p.blackColor, 0.08f);
        plate = Color.Lerp(new Color(0.34f, 0.25f, 0.20f), p.blackColor, 0.34f + d * 0.34f);
        mossLow = Color.Lerp(new Color(0.24f, 0.31f, 0.09f), p.blackColor, 0.18f + d * 0.48f);
        moss = Color.Lerp(new Color(0.50f, 0.56f, 0.20f), p.blackColor, d * 0.40f);
        mossHigh = Color.Lerp(new Color(0.64f, 0.66f, 0.29f), p.blackColor, 0.05f + d * 0.36f);

        Gradient((TriangleMesh)sLeaser.sprites[Carapace], shellLow, shellHigh);
        Gradient((TriangleMesh)sLeaser.sprites[MossShadow], mossLow, Color.Lerp(mossLow, moss, 0.45f));
        Gradient((TriangleMesh)sLeaser.sprites[MossCap], moss, mossHigh);

        for (int i = 0; i < LegCount; i++)
        {
            Color lc = FrontLeg(i) ? frontLeg : rearLeg;
            sLeaser.sprites[Leg(i, 0)].color = Color.Lerp(lc, joint, 0.12f);
            sLeaser.sprites[Leg(i, 1)].color = lc;
            sLeaser.sprites[Leg(i, 2)].color = Color.Lerp(lc, p.blackColor, 0.18f);
            sLeaser.sprites[Leg(i, 3)].color = FrontLeg(i) ? joint : Color.Lerp(joint, p.blackColor, 0.28f);
            sLeaser.sprites[Leg(i, 4)].color = Color.Lerp(lc, p.blackColor, 0.08f);
            sLeaser.sprites[Leg(i, 5)].color = Color.Lerp(lc, p.blackColor, 0.24f);
        }

        for (int i = 0; i < PlatesCount; i++)
            sLeaser.sprites[PlatesStart + i].color = Color.Lerp(plate, shellLow, H(i, 12) * 0.58f);
        for (int i = 0; i < TuftCount; i++)
            sLeaser.sprites[TuftsStart + i].color = Color.Lerp(moss, mossHigh, 0.18f + H(i, 22) * 0.48f);
        for (int i = 0; i < FringeCount; i++)
            sLeaser.sprites[FringeStart + i].color = Color.Lerp(moss, mossLow, 0.45f + (i % 4) * 0.12f);
        for (int i = 0; i < GrassCount; i++)
        {
            Color gc = Color.Lerp(moss, mossLow, H(i, 41) * 0.72f);
            sLeaser.sprites[Grass(i, 0)].color = gc;
            sLeaser.sprites[Grass(i, 1)].color = Color.Lerp(gc, mossHigh, 0.08f);
        }
    }

    public override void DrawSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, float timeStacker, Vector2 camPos)
    {
        base.DrawSprites(sLeaser, rCam, timeStacker, camPos);
        if (owner.slatedForDeletetion || owner.room != rCam.room || culled) return;

        BuildSpine(timeStacker);
        float idle = Mathf.Lerp(spider.LastIdleMotion, spider.IdleMotion, timeStacker);
        DrawBody((TriangleMesh)sLeaser.sprites[Carapace], camPos, idle, 0);
        DrawBody((TriangleMesh)sLeaser.sprites[MossShadow], camPos, idle, 1);
        DrawBody((TriangleMesh)sLeaser.sprites[MossCap], camPos, idle, 2);
        DrawPlates(sLeaser, camPos, idle);
        DrawLegs(sLeaser, camPos, idle);
        DrawTufts(sLeaser, camPos, idle);
        DrawFringe(sLeaser, camPos, idle);
        DrawGrass(sLeaser, camPos, idle);
    }

    private void BuildSpine(float stacker)
    {
        Vector2 center = Vector2.zero;
        for (int i = 0; i < raw.Length; i++)
        {
            BodyChunk c = spider.bodyChunks[i];
            raw[i] = Vector2.Lerp(c.lastPos, c.pos, stacker);
            center += raw[i];
        }
        center /= raw.Length;

        Vector2 rawAxis = raw[raw.Length - 1] - raw[0];
        if (rawAxis.sqrMagnitude < 0.001f) rawAxis = Vector2.right;
        float facing = rawAxis.x < 0f ? -1f : 1f;
        Vector2 fa = rawAxis.normalized * facing;
        float angle = Mathf.Clamp(Mathf.Atan2(fa.y, fa.x) * Mathf.Rad2Deg, -24f, 24f) * Mathf.Deg2Rad;
        Vector2 axis = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * facing;
        Vector2 worldUp = Custom.PerpendicularVector(axis);
        if (worldUp.y < 0f) worldUp = -worldUp;

        float half = (MossySpider.SegmentCount - 1) * MossySpider.SegmentSpacing * 0.5f + 12f;
        for (int i = 0; i < Samples; i++)
        {
            float u = i / (float)(Samples - 1);
            float h = Mathf.Clamp(Vector2.Dot(RawPoint(u) - center, worldUp), -12f, 12f) * 0.34f;
            spine[i] = center + axis * Mathf.Lerp(-half, half, u) + worldUp * (h + Mathf.Sin(u * Mathf.PI) * 2.2f);
        }

        for (int i = 0; i < Samples; i++)
        {
            int a = Mathf.Max(0, i - 1);
            int b = Mathf.Min(Samples - 1, i + 1);
            Vector2 t = spine[b] - spine[a];
            if (t.sqrMagnitude < 0.001f) t = axis;
            tangent[i] = t.normalized;
            Vector2 n = Custom.PerpendicularVector(tangent[i]);
            if (Vector2.Dot(n, worldUp) < 0f) n = -n;
            up[i] = n;
        }
    }

    private void DrawBody(TriangleMesh mesh, Vector2 cam, float idle, int layer)
    {
        for (int i = 0; i < Samples - 1; i++)
        {
            float u0 = i / (float)(Samples - 1);
            float u1 = (i + 1) / (float)(Samples - 1);
            float low0;
            float high0;
            float low1;
            float high1;

            if (layer == 0)
            {
                low0 = -Bottom(u0, idle); high0 = ShellTop(u0, idle);
                low1 = -Bottom(u1, idle); high1 = ShellTop(u1, idle);
            }
            else if (layer == 1)
            {
                low0 = ShellTop(u0, idle) - 2f; high0 = ShellTop(u0, idle) + 9f;
                low1 = ShellTop(u1, idle) - 2f; high1 = ShellTop(u1, idle) + 9f;
            }
            else
            {
                low0 = ShellTop(u0, idle) + 4f; high0 = ShellTop(u0, idle) + Cap(u0, idle);
                low1 = ShellTop(u1, idle) + 4f; high1 = ShellTop(u1, idle) + Cap(u1, idle);
            }

            Ribbon(mesh, i, spine[i], spine[i + 1], up[i], up[i + 1], low0, high0, low1, high1, cam);
        }
    }

    private void DrawLegs(RoomCamera.SpriteLeaser sLeaser, Vector2 cam, float idle)
    {
        for (int i = 0; i < LegCount; i++)
        {
            int station = i / 2;
            bool front = FrontLeg(i);
            float u = Mathf.Lerp(0.12f, 0.88f, station / 4f);
            Point(u, out Vector2 body, out Vector2 t, out Vector2 n);
            Vector2 hip = body - n * (Bottom(u, idle) * 0.76f) + t * (front ? 3.5f : -3.5f);

            float len = Mathf.Lerp(72f, 94f, H(i, 2));
            float reach = Mathf.Lerp(-43f, 43f, station / 4f) + (front ? 11f : -8f) + Mathf.Lerp(-10f, 10f, H(i, 3));
            reach += Mathf.Sin(idle * 0.42f + H(i, 4) * Mathf.PI * 2f) * 3.2f;
            Vector2 wanted = hip - n * len + t * reach;
            Vector2? hit = spider.room == null ? null : SharedPhysics.ExactTerrainRayTracePos(spider.room, hip, wanted);
            Vector2 foot = hit ?? wanted;

            float bend = ((station + (front ? 0 : 1)) % 2 == 0) ? 1f : -1f;
            Vector2 j1 = hip - n * (len * 0.27f) + t * (reach * 0.16f + bend * 7f);
            Vector2 knee = hip - n * (len * 0.53f) + t * (reach * 0.53f - bend * 10f);
            Vector2 correction = foot - wanted;
            j1 += correction * 0.08f;
            knee += correction * 0.34f;

            SetLine(sLeaser.sprites[Leg(i, 0)], hip, j1, cam);
            SetLine(sLeaser.sprites[Leg(i, 1)], j1, knee, cam);
            SetLine(sLeaser.sprites[Leg(i, 2)], knee, foot, cam);
            SetCircle(sLeaser.sprites[Leg(i, 3)], j1, t, cam);
            SetCircle(sLeaser.sprites[Leg(i, 4)], knee, foot - j1, cam);

            Vector2 mid = knee - j1;
            if (mid.sqrMagnitude < 0.001f) mid = -n;
            mid.Normalize();
            Vector2 normal = Custom.PerpendicularVector(mid);
            if (Vector2.Dot(normal, t) < 0f) normal = -normal;
            Vector2 sr = Vector2.Lerp(j1, knee, 0.58f);
            SetLine(sLeaser.sprites[Leg(i, 5)], sr, sr + normal * (8f + station * 0.8f) - n * 2f, cam);
        }
    }

    private void DrawPlates(RoomCamera.SpriteLeaser sLeaser, Vector2 cam, float idle)
    {
        for (int i = 0; i < PlatesCount; i++)
        {
            float u = Mathf.Lerp(0.11f, 0.89f, (i + 0.5f) / PlatesCount);
            Point(u, out Vector2 body, out Vector2 t, out Vector2 n);
            float vertical = Mathf.Lerp(-Bottom(u, idle) * 0.62f, ShellTop(u, idle) * 0.18f, Mathf.Lerp(0.28f, 0.72f, H(i, 13)));
            FSprite s = sLeaser.sprites[PlatesStart + i];
            s.SetPosition(body + n * vertical - cam);
            s.rotation = -Mathf.Atan2(t.y, t.x) * Mathf.Rad2Deg;
        }
    }

    private void DrawTufts(RoomCamera.SpriteLeaser sLeaser, Vector2 cam, float idle)
    {
        for (int i = 0; i < TuftCount; i++)
        {
            float u = Mathf.Clamp01((i + 0.5f) / TuftCount + Mathf.Lerp(-0.018f, 0.018f, H(i, 23)));
            Point(u, out Vector2 body, out Vector2 t, out Vector2 n);
            float y = ShellTop(u, idle) + Cap(u, idle) - 1.5f + Mathf.Lerp(-2.5f, 3.5f, H(i, 24));
            FSprite s = sLeaser.sprites[TuftsStart + i];
            s.SetPosition(body + n * y - cam);
            s.rotation = -Mathf.Atan2(t.y, t.x) * Mathf.Rad2Deg;
        }
    }

    private void DrawFringe(RoomCamera.SpriteLeaser sLeaser, Vector2 cam, float idle)
    {
        for (int i = 0; i < FringeCount; i++)
        {
            float u = Mathf.Clamp01((i + 0.5f) / FringeCount + Mathf.Lerp(-0.02f, 0.02f, H(i, 31)));
            Point(u, out Vector2 body, out Vector2 t, out Vector2 n);
            Vector2 root = body + n * (ShellTop(u, idle) + 5f);
            float sway = Mathf.Sin(idle * 0.72f + H(i, 32) * Mathf.PI * 2f) * 0.12f;
            Vector2 tip = root + (-n + t * sway).normalized * Mathf.Lerp(5f, 16f, H(i, 33));
            SetLine(sLeaser.sprites[FringeStart + i], root, tip, cam);
        }
    }

    private void DrawGrass(RoomCamera.SpriteLeaser sLeaser, Vector2 cam, float idle)
    {
        for (int i = 0; i < GrassCount; i++)
        {
            float u = Mathf.Clamp01((i + 0.5f) / GrassCount + Mathf.Lerp(-0.012f, 0.012f, H(i, 42)));
            Point(u, out Vector2 body, out Vector2 t, out Vector2 n);
            Vector2 root = body + n * (ShellTop(u, idle) + Cap(u, idle) - 1f);
            float length = Mathf.Lerp(27f, 72f, H(i, 43)) * Mathf.Lerp(0.76f, 1f, Mathf.Sin(u * Mathf.PI));
            float wind = Mathf.Sin(idle * 0.55f + H(i, 44) * Mathf.PI * 2f) * 0.09f;
            float lean = Mathf.Lerp(-0.48f, 0.82f, H(i, 45)) + wind;
            float curve = Mathf.Lerp(-0.32f, 0.32f, H(i, 46));
            Vector2 d1 = (n + t * lean).normalized;
            Vector2 d2 = (n + t * (lean + curve + wind * 0.6f)).normalized;
            Vector2 bend = root + d1 * (length * 0.57f);
            Vector2 tip = bend + d2 * (length * 0.43f);
            SetLine(sLeaser.sprites[Grass(i, 0)], root, bend, cam);
            SetLine(sLeaser.sprites[Grass(i, 1)], bend, tip, cam);
        }
    }

    private Vector2 RawPoint(float u)
    {
        float x = Mathf.Clamp01(u) * (raw.Length - 1);
        int i = Mathf.Min(raw.Length - 2, Mathf.FloorToInt(x));
        return Vector2.Lerp(raw[i], raw[i + 1], x - i);
    }

    private void Point(float u, out Vector2 pos, out Vector2 t, out Vector2 n)
    {
        float x = Mathf.Clamp01(u) * (Samples - 1);
        int i = Mathf.Min(Samples - 2, Mathf.FloorToInt(x));
        float f = x - i;
        pos = Vector2.Lerp(spine[i], spine[i + 1], f);
        t = Vector2.Lerp(tangent[i], tangent[i + 1], f);
        if (t.sqrMagnitude < 0.001f) t = Vector2.right;
        t.Normalize();
        n = Vector2.Lerp(up[i], up[i + 1], f);
        if (n.sqrMagnitude < 0.001f) n = Vector2.up;
        n.Normalize();
    }

    private static float Profile(float u)
    {
        float a = Mathf.Max(0f, Mathf.Sin(Mathf.Clamp01(u) * Mathf.PI));
        return 0.38f + 0.62f * Mathf.Pow(a, 0.58f);
    }

    private static float Bottom(float u, float idle) => Mathf.Lerp(21f, 35f, Profile(u)) + Mathf.Sin(idle * 0.48f + u * 4.2f) * 0.55f;
    private static float ShellTop(float u, float idle) => Mathf.Lerp(7f, 13f, Profile(u)) + Mathf.Sin(idle * 0.43f + u * 3.1f) * 0.35f;
    private static float Cap(float u, float idle) => Mathf.Lerp(13f, 25f, Profile(u)) + Mathf.Sin(u * 19.1f + 0.7f) * 1.25f + Mathf.Sin(u * 31.7f + 2.1f) * 0.65f + Mathf.Sin(idle * 0.31f + u * 5.3f) * 0.35f;

    private float H(int index, int salt)
    {
        float x = Mathf.Sin(seed * 0.0137f + index * 12.9898f + salt * 78.233f) * 43758.5453f;
        return x - Mathf.Floor(x);
    }

    private static void Ribbon(TriangleMesh mesh, int seg, Vector2 p0, Vector2 p1, Vector2 n0, Vector2 n1, float l0, float h0, float l1, float h1, Vector2 cam)
    {
        int v = seg * 4;
        mesh.MoveVertice(v, p0 + n0 * l0 - cam);
        mesh.MoveVertice(v + 1, p0 + n0 * h0 - cam);
        mesh.MoveVertice(v + 2, p1 + n1 * l1 - cam);
        mesh.MoveVertice(v + 3, p1 + n1 * h1 - cam);
    }

    private static void Gradient(TriangleMesh mesh, Color low, Color high)
    {
        for (int i = 0; i < mesh.verticeColors.Length; i++) mesh.verticeColors[i] = i % 2 == 0 ? low : high;
    }

    private static FSprite Line(float width) => new FSprite("pixel") { anchorY = 0f, scaleX = width };
    private static FSprite Circle(float x, float y) => new FSprite("Circle20") { scaleX = x, scaleY = y };

    private static void SetLine(FSprite s, Vector2 a, Vector2 b, Vector2 cam)
    {
        s.SetPosition(a - cam);
        s.scaleY = Vector2.Distance(a, b);
        s.rotation = Custom.AimFromOneVectorToAnother(a, b);
    }

    private static void SetCircle(FSprite s, Vector2 pos, Vector2 dir, Vector2 cam)
    {
        s.SetPosition(pos - cam);
        if (dir.sqrMagnitude > 0.001f) s.rotation = -Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
    }

    private static bool FrontLeg(int i) => i % 2 == 0;
    private static int Leg(int i, int part) => LegsStart + i * LegParts + part;
    private static int Grass(int i, int part) => GrassStart + i * 2 + part;

    private static void AddLeg(FContainer c, RoomCamera.SpriteLeaser sLeaser, int leg)
    {
        for (int part = 0; part < LegParts; part++) c.AddChild(sLeaser.sprites[Leg(leg, part)]);
    }
}
