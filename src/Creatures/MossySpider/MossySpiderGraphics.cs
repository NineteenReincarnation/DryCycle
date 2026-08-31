using RWCustom;
using UnityEngine;
using Random = UnityEngine.Random;

namespace DryCycle.Creatures.MossySpider;

public sealed class MossySpiderGraphics : GraphicsModule
{
    private const int UndersideSprite = 0;
    private const int MossCapSprite = 1;

    private const int LegCount = 16;
    private const int FirstLegSprite = 2;
    private const int LegSpriteCount = LegCount * 2;

    private const int TendrilCount = 12;
    private const int FirstTendrilSprite = FirstLegSprite + LegSpriteCount;

    private const int GrassCount = 44;
    private const int FirstGrassSprite = FirstTendrilSprite + TendrilCount;
    private const int TotalSprites = FirstGrassSprite + GrassCount;

    private readonly MossySpider spider;
    private readonly Vector2[] drawBody = new Vector2[MossySpider.SegmentCount];

    private readonly float[] legPhase = new float[LegCount];
    private readonly float[] legLength = new float[LegCount];
    private readonly float[] legSplay = new float[LegCount];

    private readonly float[] tendrilU = new float[TendrilCount];
    private readonly float[] tendrilLength = new float[TendrilCount];
    private readonly float[] tendrilPhase = new float[TendrilCount];

    private readonly float[] grassU = new float[GrassCount];
    private readonly float[] grassLength = new float[GrassCount];
    private readonly float[] grassLean = new float[GrassCount];
    private readonly float[] grassPhase = new float[GrassCount];
    private readonly float[] grassWidth = new float[GrassCount];
    private readonly float[] grassTint = new float[GrassCount];

    private Color undersideColor;
    private Color rearLegColor;
    private Color frontLegColor;
    private Color mossColor;
    private Color darkMossColor;

    public MossySpiderGraphics(MossySpider spider) : base(spider, false)
    {
        this.spider = spider;
        cullRange = 650f;

        Random.State oldState = Random.state;
        Random.InitState(spider.abstractPhysicalObject.ID.RandomSeed);

        for (int i = 0; i < LegCount; i++)
        {
            legPhase[i] = Random.value * Mathf.PI * 2f;
            legLength[i] = Random.Range(58f, 86f);
            legSplay[i] = Random.Range(-12f, 12f);
        }

        for (int i = 0; i < TendrilCount; i++)
        {
            tendrilU[i] = Mathf.Lerp(0.05f, 0.95f, (i + Random.Range(0.15f, 0.85f)) / TendrilCount);
            tendrilLength[i] = Random.Range(10f, 31f);
            tendrilPhase[i] = Random.value * Mathf.PI * 2f;
        }

        for (int i = 0; i < GrassCount; i++)
        {
            grassU[i] = Mathf.Clamp01((i + Random.Range(0.05f, 0.95f)) / GrassCount);
            grassLength[i] = Random.Range(18f, 58f) * Mathf.Lerp(0.72f, 1f, Mathf.Sin(grassU[i] * Mathf.PI));
            grassLean[i] = Random.Range(-0.55f, 0.55f);
            grassPhase[i] = Random.value * Mathf.PI * 2f;
            grassWidth[i] = Random.Range(0.75f, 1.7f);
            grassTint[i] = Random.value;
        }

        Random.state = oldState;
    }

    public override void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
    {
        FSprite[] sprites = sLeaser.sprites = new FSprite[TotalSprites];

        sprites[UndersideSprite] = TriangleMesh.MakeLongMesh(MossySpider.SegmentCount - 1, false, true);
        sprites[MossCapSprite] = TriangleMesh.MakeLongMesh(MossySpider.SegmentCount - 1, false, true);

        for (int i = 0; i < LegCount; i++)
        {
            sprites[LegUpperSprite(i)] = new FSprite("pixel")
            {
                anchorY = 0f,
                scaleX = i % 2 == 0 ? 4.2f : 3.5f
            };
            sprites[LegLowerSprite(i)] = new FSprite("pixel")
            {
                anchorY = 0f,
                scaleX = i % 2 == 0 ? 3.2f : 2.7f
            };
        }

        for (int i = 0; i < TendrilCount; i++)
        {
            sprites[FirstTendrilSprite + i] = new FSprite("pixel")
            {
                anchorY = 0f,
                scaleX = Random.Range(0.65f, 1.2f)
            };
        }

        for (int i = 0; i < GrassCount; i++)
        {
            sprites[FirstGrassSprite + i] = new FSprite("pixel")
            {
                anchorY = 0f,
                scaleX = grassWidth[i]
            };
        }

        ApplyPalette(sLeaser, rCam, rCam.currentPalette);
        AddToContainer(sLeaser, rCam, null);
    }

    public override void AddToContainer(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, FContainer newContainer)
    {
        sLeaser.RemoveAllSpritesFromContainer();
        FContainer container = newContainer ?? rCam.ReturnFContainer("Midground");

        // Legs and hanging fibres sit behind the heavy body mass.
        for (int i = FirstLegSprite; i < FirstTendrilSprite + TendrilCount; i++)
        {
            container.AddChild(sLeaser.sprites[i]);
        }

        container.AddChild(sLeaser.sprites[UndersideSprite]);
        container.AddChild(sLeaser.sprites[MossCapSprite]);

        // The grass is the front-most silhouette and should break up the body edge.
        for (int i = FirstGrassSprite; i < TotalSprites; i++)
        {
            container.AddChild(sLeaser.sprites[i]);
        }
    }

    public override void ApplyPalette(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, RoomPalette palette)
    {
        base.ApplyPalette(sLeaser, rCam, palette);

        float dark = Mathf.Clamp01(palette.darkness);
        undersideColor = Color.Lerp(new Color(0.19f, 0.145f, 0.115f), palette.blackColor, 0.25f + dark * 0.45f);
        rearLegColor = Color.Lerp(undersideColor, palette.blackColor, 0.34f);
        frontLegColor = Color.Lerp(undersideColor, palette.blackColor, 0.18f);
        mossColor = Color.Lerp(new Color(0.48f, 0.52f, 0.22f), palette.blackColor, dark * 0.42f);
        darkMossColor = Color.Lerp(new Color(0.27f, 0.36f, 0.12f), palette.blackColor, dark * 0.5f);

        sLeaser.sprites[UndersideSprite].color = undersideColor;
        sLeaser.sprites[MossCapSprite].color = mossColor;

        for (int i = 0; i < LegCount; i++)
        {
            Color color = i % 2 == 0 ? frontLegColor : rearLegColor;
            sLeaser.sprites[LegUpperSprite(i)].color = color;
            sLeaser.sprites[LegLowerSprite(i)].color = Color.Lerp(color, palette.blackColor, 0.12f);
        }

        for (int i = 0; i < TendrilCount; i++)
        {
            sLeaser.sprites[FirstTendrilSprite + i].color = Color.Lerp(undersideColor, palette.blackColor, 0.2f + 0.35f * (i % 3) / 2f);
        }

        for (int i = 0; i < GrassCount; i++)
        {
            sLeaser.sprites[FirstGrassSprite + i].color = Color.Lerp(mossColor, darkMossColor, grassTint[i]);
        }
    }

    public override void DrawSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, float timeStacker, Vector2 camPos)
    {
        base.DrawSprites(sLeaser, rCam, timeStacker, camPos);
        if (owner.slatedForDeletetion || owner.room != rCam.room || culled)
        {
            return;
        }

        FillDrawBody(timeStacker);

        float idle = Mathf.Lerp(spider.LastIdleMotion, spider.IdleMotion, timeStacker);
        DrawUnderside((TriangleMesh)sLeaser.sprites[UndersideSprite], camPos, idle);
        DrawMossCap((TriangleMesh)sLeaser.sprites[MossCapSprite], camPos, idle);
        DrawLegs(sLeaser, camPos, idle);
        DrawTendrils(sLeaser, camPos, idle);
        DrawGrass(sLeaser, camPos, idle);
    }

    private void FillDrawBody(float timeStacker)
    {
        for (int i = 0; i < MossySpider.SegmentCount; i++)
        {
            BodyChunk chunk = spider.bodyChunks[i];
            drawBody[i] = Vector2.Lerp(chunk.lastPos, chunk.pos, timeStacker);
        }
    }

    private void DrawUnderside(TriangleMesh mesh, Vector2 camPos, float idle)
    {
        for (int i = 0; i < MossySpider.SegmentCount - 1; i++)
        {
            Vector2 p0 = drawBody[i];
            Vector2 p1 = drawBody[i + 1];
            Vector2 perp0 = BodyPerpendicular(i);
            Vector2 perp1 = BodyPerpendicular(i + 1);

            float breath0 = 1f + Mathf.Sin(idle + i * 0.55f) * 0.018f;
            float breath1 = 1f + Mathf.Sin(idle + (i + 1) * 0.55f) * 0.018f;
            float r0 = spider.bodyChunks[i].rad * breath0;
            float r1 = spider.bodyChunks[i + 1].rad * breath1;

            int v = i * 4;
            mesh.MoveVertice(v, p0 - perp0 * r0 - camPos);
            mesh.MoveVertice(v + 1, p0 + perp0 * (r0 * 0.55f) - camPos);
            mesh.MoveVertice(v + 2, p1 - perp1 * r1 - camPos);
            mesh.MoveVertice(v + 3, p1 + perp1 * (r1 * 0.55f) - camPos);
        }
    }

    private void DrawMossCap(TriangleMesh mesh, Vector2 camPos, float idle)
    {
        for (int i = 0; i < MossySpider.SegmentCount - 1; i++)
        {
            Vector2 p0 = drawBody[i];
            Vector2 p1 = drawBody[i + 1];
            Vector2 perp0 = BodyPerpendicular(i);
            Vector2 perp1 = BodyPerpendicular(i + 1);

            float r0 = spider.bodyChunks[i].rad;
            float r1 = spider.bodyChunks[i + 1].rad;
            float pulse0 = Mathf.Sin(idle * 0.73f + i * 0.41f) * 1.2f;
            float pulse1 = Mathf.Sin(idle * 0.73f + (i + 1) * 0.41f) * 1.2f;

            Vector2 c0 = p0 + perp0 * (r0 * 0.5f + 4f + pulse0);
            Vector2 c1 = p1 + perp1 * (r1 * 0.5f + 4f + pulse1);
            float half0 = r0 * 0.48f;
            float half1 = r1 * 0.48f;

            int v = i * 4;
            mesh.MoveVertice(v, c0 - perp0 * half0 - camPos);
            mesh.MoveVertice(v + 1, c0 + perp0 * half0 - camPos);
            mesh.MoveVertice(v + 2, c1 - perp1 * half1 - camPos);
            mesh.MoveVertice(v + 3, c1 + perp1 * half1 - camPos);
        }
    }

    private void DrawLegs(RoomCamera.SpriteLeaser sLeaser, Vector2 camPos, float idle)
    {
        Room room = spider.room;
        for (int leg = 0; leg < LegCount; leg++)
        {
            int pair = leg / 2;
            int side = leg % 2;
            float u = Mathf.Lerp(0.055f, 0.945f, pair / 7f);
            BodyPoint(u, out Vector2 bodyPos, out Vector2 tangent, out Vector2 perp, out float radius);

            Vector2 hip = bodyPos - perp * (radius * 0.72f);
            float edge = (pair - 3.5f) / 3.5f;
            float sideOffset = side == 0 ? -11f : 11f;
            float sway = Mathf.Sin(idle * 0.85f + legPhase[leg]) * 6f;
            float horizontal = edge * 25f + sideOffset + legSplay[leg] + sway;
            Vector2 desiredFoot = hip - perp * legLength[leg] + tangent * horizontal;

            Vector2? terrainHit = SharedPhysics.ExactTerrainRayTracePos(room, hip, desiredFoot);
            Vector2 foot = terrainHit ?? desiredFoot;

            float bend = side == 0 ? -1f : 1f;
            Vector2 knee = Vector2.Lerp(hip, foot, 0.48f)
                + tangent * (bend * (17f + pair % 3 * 3f))
                + perp * (5f + Mathf.Sin(idle + legPhase[leg]) * 2.5f);

            SetLine(sLeaser.sprites[LegUpperSprite(leg)], hip, knee, camPos);
            SetLine(sLeaser.sprites[LegLowerSprite(leg)], knee, foot, camPos);
        }
    }

    private void DrawTendrils(RoomCamera.SpriteLeaser sLeaser, Vector2 camPos, float idle)
    {
        for (int i = 0; i < TendrilCount; i++)
        {
            BodyPoint(tendrilU[i], out Vector2 bodyPos, out Vector2 tangent, out Vector2 perp, out float radius);
            Vector2 root = bodyPos - perp * (radius * 0.82f);
            float swing = Mathf.Sin(idle * 1.25f + tendrilPhase[i]) * 0.28f;
            Vector2 dir = (-perp + tangent * swing).normalized;
            Vector2 tip = root + dir * tendrilLength[i];
            SetLine(sLeaser.sprites[FirstTendrilSprite + i], root, tip, camPos);
        }
    }

    private void DrawGrass(RoomCamera.SpriteLeaser sLeaser, Vector2 camPos, float idle)
    {
        for (int i = 0; i < GrassCount; i++)
        {
            BodyPoint(grassU[i], out Vector2 bodyPos, out Vector2 tangent, out Vector2 perp, out float radius);
            Vector2 root = bodyPos + perp * (radius * 0.98f + 1.5f);

            float wind = Mathf.Sin(idle * 0.92f + grassPhase[i]) * 0.15f;
            float localLean = grassLean[i] + wind;
            Vector2 dir = (perp + tangent * localLean).normalized;
            Vector2 tip = root + dir * grassLength[i];

            SetLine(sLeaser.sprites[FirstGrassSprite + i], root, tip, camPos);
        }
    }

    private void BodyPoint(float u, out Vector2 position, out Vector2 tangent, out Vector2 perpendicular, out float radius)
    {
        float scaled = Mathf.Clamp01(u) * (MossySpider.SegmentCount - 1);
        int index = Mathf.Min(MossySpider.SegmentCount - 2, Mathf.FloorToInt(scaled));
        float t = scaled - index;

        position = Vector2.Lerp(drawBody[index], drawBody[index + 1], t);
        tangent = Vector2.Lerp(BodyTangent(index), BodyTangent(index + 1), t).normalized;
        if (tangent.sqrMagnitude < 0.001f)
        {
            tangent = Vector2.right;
        }

        perpendicular = Custom.PerpendicularVector(tangent);
        radius = Mathf.Lerp(spider.bodyChunks[index].rad, spider.bodyChunks[index + 1].rad, t);
    }

    private Vector2 BodyTangent(int index)
    {
        int prev = Mathf.Max(0, index - 1);
        int next = Mathf.Min(MossySpider.SegmentCount - 1, index + 1);
        Vector2 delta = drawBody[next] - drawBody[prev];
        if (delta.sqrMagnitude < 0.001f)
        {
            return Vector2.right;
        }

        return delta.normalized;
    }

    private Vector2 BodyPerpendicular(int index) => Custom.PerpendicularVector(BodyTangent(index));

    private static int LegUpperSprite(int leg) => FirstLegSprite + leg * 2;

    private static int LegLowerSprite(int leg) => FirstLegSprite + leg * 2 + 1;

    private static void SetLine(FSprite sprite, Vector2 from, Vector2 to, Vector2 camPos)
    {
        sprite.SetPosition(from - camPos);
        sprite.scaleY = Vector2.Distance(from, to);
        sprite.rotation = Custom.AimFromOneVectorToAnother(from, to);
    }
}
