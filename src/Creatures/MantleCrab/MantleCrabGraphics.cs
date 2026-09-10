using System;
using DryCycle.Creatures.MantleCrab.Rendering;
using DryCycle.Rendering;
using UnityEngine;

namespace DryCycle.Creatures.MantleCrab;

public sealed class MantleCrabGraphics : GraphicsModule
{
    // Compatibility aliases retained for existing tests/tools. Sprite arithmetic itself lives in
    // MantleCrabSpriteLayout so future anatomy changes do not leak magic offsets through this class.
    internal const int ThreadCount = MantleCrabSpriteLayout.ThreadCount;
    internal const int Shell = MantleCrabSpriteLayout.Shell;
    internal const int PincersStart = MantleCrabSpriteLayout.PincersStart;
    internal const int EyesStart = MantleCrabSpriteLayout.EyesStart;
    internal const int SpriteCount = MantleCrabSpriteLayout.SpriteCount;

    // Development-only silhouette gate. It is intentionally off in normal play, but gives a
    // reliable way to judge the outline without changing the material/atlas pipeline.
    internal static bool SilhouetteDebug;

    private readonly MantleCrab crab;
    private readonly MantleCrabVisualPhenotype phenotype;
    private MantleCrabMaterialCache material;
    private readonly Part[] parts = new Part[SpriteCount];
    private readonly Vector2[,] threads = new Vector2[ThreadCount, 5];
    private readonly Vector2[,] lastThreads = new Vector2[ThreadCount, 5];
    private readonly Vector2[] eyes = new Vector2[2];
    private readonly Vector2[] lastEyes = new Vector2[2];

    private sealed class Part
    {
        internal readonly int Columns, Rows;
        internal readonly MantleCrabMaterial Kind;
        internal readonly float Depth;
        internal readonly Color[] Fallback;

        internal Part(
            int columns,
            int rows,
            MantleCrabMaterial kind,
            float depth,
            MantleCrabVisualPhenotype p)
        {
            Columns = columns;
            Rows = rows;
            Kind = kind;
            Depth = depth;
            Fallback = new Color[(columns + 1) * (rows + 1)];

            for (int y = 0; y <= rows; y++)
            for (int x = 0; x <= columns; x++)
            {
                MantleCrabMaterialBaker.Sample(
                    p,
                    kind,
                    new Vector2(x / (float)columns, y / (float)rows),
                    out Color pattern,
                    out Color surface);
                Color color = new(pattern.r, pattern.g, pattern.b, 1f);
                color *= Mathf.Lerp(.5f, 1f, surface.r);
                color.a = 1f;
                Fallback[y * (columns + 1) + x] = color;
            }
        }
    }

    public MantleCrabGraphics(MantleCrab crab) : base(crab, false)
    {
        this.crab = crab;
        phenotype = crab.Phenotype;
        cullRange = 650f;

        for (int leg = 0; leg < MantleCrabSpriteLayout.LegCount; leg++)
        {
            for (int segment = 0; segment < MantleCrabSpriteLayout.LegShaftCount; segment++)
                Define(
                    MantleCrabSpriteLayout.LegShaft(leg, segment),
                    12,
                    6,
                    MantleCrabMaterial.Leg,
                    crab.Legs[leg].LocalDepth);

            for (int joint = 0; joint < MantleCrabSpriteLayout.LegJointCount; joint++)
                Define(
                    MantleCrabSpriteLayout.LegJoint(leg, joint),
                    8,
                    6,
                    MantleCrabMaterial.Joint,
                    crab.Legs[leg].LocalDepth);

            Define(
                MantleCrabSpriteLayout.LegFoot(leg),
                14,
                8,
                MantleCrabMaterial.Foot,
                crab.Legs[leg].LocalDepth);
        }

        for (int i = 0; i < ThreadCount; i++)
            Define(MantleCrabSpriteLayout.Thread(i), 16, 1, MantleCrabMaterial.Fringe, .3f);
        Define(Shell, 192, 20, MantleCrabMaterial.Shell, 0f);

        for (int pincer = 0; pincer < MantleCrabSpriteLayout.PincerCount; pincer++)
        {
            for (int segment = 0; segment < MantleCrabPincerAnatomy.SegmentCount; segment++)
                Define(
                    MantleCrabSpriteLayout.PincerShaft(pincer, segment),
                    12,
                    6,
                    MantleCrabMaterial.Pincer,
                    pincer == 0 ? .08f : 0f);

            for (int joint = 0; joint < 3; joint++)
                Define(
                    MantleCrabSpriteLayout.PincerJoint(pincer, joint),
                    8,
                    6,
                    MantleCrabMaterial.Joint,
                    pincer == 0 ? .08f : 0f);

            Define(
                MantleCrabSpriteLayout.PincerPalm(pincer),
                16,
                8,
                MantleCrabMaterial.Pincer,
                pincer == 0 ? .08f : 0f);
            Define(
                MantleCrabSpriteLayout.PincerMovableFinger(pincer),
                14,
                6,
                MantleCrabMaterial.Pincer,
                pincer == 0 ? .08f : 0f);
        }

        for (int eye = 0; eye < 2; eye++)
        {
            Define(MantleCrabSpriteLayout.EyeStalk(eye), 8, 2, MantleCrabMaterial.Pincer, 0f);
            Define(MantleCrabSpriteLayout.Eye(eye), 10, 8, MantleCrabMaterial.Eye, 0f);
        }

        Reset();
    }

    private void Define(int index, int columns, int rows, MantleCrabMaterial kind, float depth) =>
        parts[index] = new Part(columns, rows, kind, depth, phenotype);

    public override void InitiateSprites(RoomCamera.SpriteLeaser leaser, RoomCamera camera)
    {
        bool custom = DryCycleShaderAssets.MantleCrabSurface != null;
        if (custom)
        {
            try
            {
                if (material == null || material.Released)
                    material = new MantleCrabMaterialCache(phenotype);
                material.Attach(leaser);
            }
            catch (Exception ex)
            {
                custom = false;
                Plugin.Logger?.LogWarning("MantleCrab atlas fallback: " + ex.Message);
            }
        }

        leaser.sprites = new FSprite[SpriteCount];
        for (int i = 0; i < SpriteCount; i++)
        {
            Part p = parts[i];
            TriangleMesh mesh = MantleCrabMeshBuilder.Grid(
                custom ? material.AtlasName : "Futile_White",
                p.Columns,
                p.Rows,
                p.Kind);
            mesh.shader = custom
                ? DryCycleShaderAssets.MantleCrabSurface
                : camera.game.rainWorld.Shaders["Basic"];
            if (!custom)
                mesh.SetAtlasedImage(Futile.atlasManager.GetElementWithName("Futile_White"));
            leaser.sprites[i] = mesh;
        }

        AddToContainer(leaser, camera, null);
    }

    public override void AddToContainer(
        RoomCamera.SpriteLeaser leaser,
        RoomCamera camera,
        FContainer container)
    {
        container ??= camera.ReturnFContainer("Midground");
        foreach (FSprite sprite in leaser.sprites)
            sprite.RemoveFromContainer();

        foreach (int index in MantleCrabSpriteLayout.DrawOrder())
            container.AddChild(leaser.sprites[index]);
    }

    private Vector2 EyeAnchor(int i) => Local(new Vector2(i == 0 ? -16f : 23f, i == 0 ? 18f : 19f));

    private Vector2 Local(Vector2 point) =>
        crab.bodyChunks[2].pos + crab.Axis * point.x +
        new Vector2(-crab.Axis.y, crab.Axis.x) * point.y;

    private Vector2 ThreadRest(int i, float t)
    {
        Vector2 a;
        Vector2 control;
        Vector2 b;

        switch (i)
        {
            case 0:
                a = new Vector2(-64f, -8f);
                control = new Vector2(-69f, -117f);
                b = new Vector2(-53f, -20f);
                break;
            case 1:
                a = new Vector2(-55f, -18f);
                control = new Vector2(-58f, -82f);
                b = new Vector2(-31f, -24f);
                break;
            case 2:
                a = new Vector2(60f, -8f);
                control = new Vector2(64f, -101f);
                b = new Vector2(43f, -20f);
                break;
            case 3:
                a = new Vector2(56f, -15f);
                control = new Vector2(64f, -76f);
                b = new Vector2(40f, -29f);
                break;
            case 4:
                a = new Vector2(-27f, -26f);
                control = new Vector2(-33f, -58f);
                b = new Vector2(-20f, -61f);
                break;
            case 5:
                a = new Vector2(29f, -25f);
                control = new Vector2(36f, -58f);
                b = new Vector2(23f, -62f);
                break;
            default:
            {
                int seed = crab.abstractCreature?.ID.RandomSeed ?? 1729;
                float h = MantleCrabVisualGenome.Hash(seed, "Fringe" + i);
                float bend = MantleCrabVisualGenome.Hash(seed, "FringeBend" + i) - .5f;

                if (i < 14)
                {
                    int slot = i - 6;
                    float f = (slot + .5f) / 8f;
                    float length = Mathf.Lerp(18f, 39f, h);
                    a = new Vector2(Mathf.Lerp(-58f, 58f, f), -22f + h * 6f);
                    control = a + new Vector2(bend * 22f, -length * .55f);
                    b = a + new Vector2(bend * 9f, -length);
                }
                else
                {
                    int slot = i - 14;
                    float f = (slot + .5f) / 14f;
                    float length = Mathf.Lerp(10f, 30f, h);
                    a = new Vector2(Mathf.Lerp(-66f, 66f, f), -20f + h * 7f);
                    control = a + new Vector2(bend * 8f, -length * .48f);
                    b = a + new Vector2(bend * 5f, -length);
                }
                break;
            }
        }

        float oneMinus = 1f - t;
        return Local(a * (oneMinus * oneMinus) + control * (2f * t * oneMinus) + b * (t * t));
    }

    public override void Reset()
    {
        base.Reset();
        for (int i = 0; i < 2; i++)
            eyes[i] = lastEyes[i] = EyeAnchor(i) + Vector2.down * 8f;

        for (int i = 0; i < ThreadCount; i++)
        for (int j = 0; j < 5; j++)
            threads[i, j] = lastThreads[i, j] = ThreadRest(i, j / 4f);
    }

    public override void Update()
    {
        base.Update();

        for (int i = 0; i < 2; i++)
        {
            Vector2 desired = EyeAnchor(i) + Vector2.down * 8f;
            Vector2 next = Vector2.Lerp(
                eyes[i] + (eyes[i] - lastEyes[i]) * .5f,
                desired,
                .32f);
            lastEyes[i] = eyes[i];
            eyes[i] = desired + Vector2.ClampMagnitude(next - desired, 2.3f);
        }

        for (int i = 0; i < ThreadCount; i++)
        for (int j = 0; j < 5; j++)
        {
            Vector2 rest = ThreadRest(i, j / 4f);
            Vector2 next = threads[i, j] +
                           (threads[i, j] - lastThreads[i, j]) * .8f +
                           (rest - threads[i, j]) * .08f;
            lastThreads[i, j] = threads[i, j];
            bool fixedPoint = j == 0 || (i < 4 && j == 4);
            threads[i, j] = fixedPoint
                ? rest
                : rest + Vector2.ClampMagnitude(next - rest, 3f);
        }
    }

    public override void DrawSprites(
        RoomCamera.SpriteLeaser leaser,
        RoomCamera camera,
        float timeStacker,
        Vector2 camPos)
    {
        if (crab.slatedForDeletetion || crab.room == null || camera.room != crab.room)
        {
            leaser.CleanSpritesAndRemove();
            return;
        }

        base.DrawSprites(leaser, camera, timeStacker, camPos);
        if (culled || dispose) return;

        PoseSprites(leaser, timeStacker, camPos);
        for (int i = 0; i < SpriteCount; i++)
        {
            bool legRoot = MantleCrabSpriteLayout.IsLegSprite(i) &&
                           (i - MantleCrabSpriteLayout.LegStart) % MantleCrabSpriteLayout.LegStride == 0;
            Shade(leaser, camera, i, camPos, legRoot ? .6f : 1f);
        }
    }

    internal void PoseSprites(RoomCamera.SpriteLeaser leaser, float timeStacker, Vector2 camPos)
    {
        for (int i = 0; i < MantleCrabSpriteLayout.LegCount; i++)
            DrawLeg(leaser, i, timeStacker, camPos);

        DrawShell((TriangleMesh)leaser.sprites[Shell], timeStacker, camPos);

        for (int i = 0; i < ThreadCount; i++)
        {
            TriangleMesh mesh = (TriangleMesh)leaser.sprites[MantleCrabSpriteLayout.Thread(i)];
            for (int j = 0; j <= 16; j++)
            {
                float u = j / 4f;
                int k = Mathf.Min(3, (int)u);
                float t = u - k;
                Vector2 a = ThreadPoint(i, Mathf.Max(0, k - 1), timeStacker);
                Vector2 b = ThreadPoint(i, k, timeStacker);
                Vector2 c = ThreadPoint(i, k + 1, timeStacker);
                Vector2 d = ThreadPoint(i, Mathf.Min(4, k + 2), timeStacker);
                Vector2 point = .5f * (
                    (2f * b) +
                    (-a + c) * t +
                    (2f * a - 5f * b + 4f * c - d) * t * t +
                    (-a + 3f * b - 3f * c + d) * t * t * t) - camPos;

                float width;
                if (i < 6)
                    width = Mathf.Lerp(1.05f, .52f, j / 16f);
                else if (i < 14)
                    width = Mathf.Lerp(1.25f, .22f, j / 16f);
                else
                    width = Mathf.Lerp(1.30f, .10f, j / 16f);

                Vector2 tangent = c - b;
                if (tangent.sqrMagnitude < .0001f) tangent = Vector2.down;
                Vector2 cross = MantleCrabRenderingMath.Perpendicular(tangent.normalized) * width;
                mesh.MoveVertice(j, point - cross);
                mesh.MoveVertice(17 + j, point + cross);
            }
        }

        for (int i = 0; i < MantleCrabSpriteLayout.PincerCount; i++)
        {
            DrawPincer(leaser, i, timeStacker, camPos);
            Vector2 eye = Vector2.Lerp(lastEyes[i], eyes[i], timeStacker);
            Vector2 stalk = new Vector2(i == 0 ? 2.5f : -4f, i == 0 ? -19f : -23f);
            Segment(leaser, MantleCrabSpriteLayout.EyeStalk(i), eye, eye + stalk, .78f, .20f, camPos);
            Round(
                leaser,
                MantleCrabSpriteLayout.Eye(i),
                eye,
                crab.Axis,
                phenotype.EyeSize * (i == 0 ? .98f : 1.02f),
                phenotype.EyeSize * phenotype.EyeAspect,
                camPos);
        }
    }

    private Vector2 ThreadPoint(int i, int j, float time) =>
        Vector2.Lerp(lastThreads[i, j], threads[i, j], time);

    private void DrawShell(TriangleMesh mesh, float time, Vector2 camera)
    {
        Part p = parts[Shell];
        Vector2 axis = crab.Axis;
        Vector2 up = MantleCrabRenderingMath.Perpendicular(axis);

        for (int x = 0; x <= p.Columns; x++)
        {
            float u = x / (float)p.Columns;
            float shellX = u * 202f;
            float bodyU = Mathf.Clamp01((shellX - 17f) / 168f) * 4f;
            int station = Mathf.Min(3, (int)bodyU);
            Vector2 a = Vector2.Lerp(crab.bodyChunks[station].lastPos, crab.bodyChunks[station].pos, time);
            Vector2 b = Vector2.Lerp(crab.bodyChunks[station + 1].lastPos, crab.bodyChunks[station + 1].pos, time);
            Vector2 center = Vector2.Lerp(a, b, bodyU - station);

            if (u < 17f / 202f)
                center += axis * ((shellX - 17f) * phenotype.ShellWidth);
            if (u > 185f / 202f)
                center += axis * ((shellX - 185f) * phenotype.ShellWidth);

            float signed = u * 2f - 1f;
            float wing = Mathf.Abs(signed);
            float broad = Mathf.Pow(Mathf.Max(0f, 1f - wing * wing), 1.12f);
            float edgeNoise =
                (MantleCrabRenderingMath.Noise(x * 3.7f + phenotype.NoiseSeed.x, 1f) - .5f) *
                phenotype.EdgeRoughness;
            float localAsymmetry = signed * phenotype.Asymmetry * 18f +
                                   (MantleCrabRenderingMath.Noise(x * 1.93f + phenotype.NoiseSeed.z, 2.1f) - .5f) *
                                   .65f * (1f - wing);

            center += up * (-5.5f * Mathf.Pow(wing, 1.7f) + phenotype.WingAngle * 70f + localAsymmetry);
            center += up * (
                8f - Mathf.Lerp(
                    MantleCrab.ShellRest[station].y,
                    MantleCrab.ShellRest[station + 1].y,
                    bodyU - station));

            float top = MantleCrabShellProfile.Top(wing) + MantleCrabShellProfile.Crest(u);

            float bottom = 4f - 35f * Mathf.Pow(broad, 1.25f);
            float lowerTeeth = Mathf.Pow(
                Mathf.Max(0f, Mathf.Sin(x * 2.17f + phenotype.StripePhase)),
                8f) * (1f - wing) * 5.5f;
            bottom -= lowerTeeth;

            for (int y = 0; y <= p.Rows; y++)
            {
                float v = y / (float)p.Rows;
                float offset = Mathf.Lerp(
                    bottom - edgeNoise * 1.1f,
                    top + edgeNoise * .75f,
                    v);
                mesh.MoveVertice(
                    y * (p.Columns + 1) + x,
                    center + up * offset - camera);
            }
        }
    }

    private void DrawLeg(RoomCamera.SpriteLeaser leaser, int index, float time, Vector2 camPos)
    {
        MantleCrabLimb limb = crab.Legs[index];
        Vector2 anchor = Vector2.Lerp(limb.LastAnchor, limb.Anchor, time);
        Vector2 p0 = Vector2.Lerp(limb.LastPos[0], limb.Pos[0], time);
        Vector2 p1 = Vector2.Lerp(limb.LastPos[1], limb.Pos[1], time);
        Vector2 p2 = Vector2.Lerp(limb.LastPos[2], limb.Pos[2], time);
        Vector2 p3 = Vector2.Lerp(limb.LastPos[3], limb.Pos[3], time);
        float pairScale = index < 2 ? 1f : .84f;

        DrawLegShaft(leaser, index, 0, anchor, p0, pairScale, 0f, LegJointWidth(0, pairScale), camPos);
        DrawLegShaft(leaser, index, 1, p0, p1, pairScale, LegJointWidth(0, pairScale), LegJointWidth(1, pairScale), camPos);
        DrawLegShaft(leaser, index, 2, p1, p2, pairScale, LegJointWidth(1, pairScale), LegJointWidth(2, pairScale), camPos);
        DrawLegShaft(leaser, index, 3, p2, p3, pairScale, LegJointWidth(2, pairScale), 0f, camPos);

        DrawWalkingJoint(leaser, index, 0, anchor, p0, p1, pairScale, camPos);
        DrawWalkingJoint(leaser, index, 1, p0, p1, p2, pairScale, camPos);
        DrawWalkingJoint(leaser, index, 2, p1, p2, p3, pairScale, camPos);

        int footSprite = MantleCrabSpriteLayout.LegFoot(index);
        Part foot = parts[footSprite];
        float footWidth = (index < 2 ? 6.5f : 6.0f) * phenotype.FootBulk;
        MantleCrabMeshBuilder.Foot(
            (TriangleMesh)leaser.sprites[footSprite],
            foot.Columns,
            foot.Rows,
            p2,
            p3,
            footWidth,
            limb.Side,
            limb.GroundNormal,
            camPos);
    }

    private void DrawLegShaft(
        RoomCamera.SpriteLeaser leaser,
        int leg,
        int segment,
        Vector2 start,
        Vector2 end,
        float pairScale,
        float startJointWidth,
        float endJointWidth,
        Vector2 camPos)
    {
        float startTrim = segment == 0 ? 0f : MantleCrabMeshBuilder.WalkingJointTrim(startJointWidth);
        float endTrim = segment < 3 ? MantleCrabMeshBuilder.WalkingJointTrim(endJointWidth) : 0f;
        Vector2 anatomicalEnd = segment == 3 ? MantleCrabMeshBuilder.FootRoot(start, end) : end;
        TrimSegment(start, anatomicalEnd, startTrim, endTrim, out Vector2 drawStart, out Vector2 drawEnd);

        int sprite = MantleCrabSpriteLayout.LegShaft(leg, segment);
        Part part = parts[sprite];
        MantleCrabMeshBuilder.Chitin(
            (TriangleMesh)leaser.sprites[sprite],
            part.Columns,
            part.Rows,
            drawStart,
            drawEnd,
            LegSegmentWidth(segment, pairScale),
            leg + segment,
            camPos);
    }

    private void DrawWalkingJoint(
        RoomCamera.SpriteLeaser leaser,
        int leg,
        int joint,
        Vector2 before,
        Vector2 center,
        Vector2 after,
        float pairScale,
        Vector2 camPos)
    {
        int sprite = MantleCrabSpriteLayout.LegJoint(leg, joint);
        Part part = parts[sprite];
        MantleCrabMeshBuilder.JointCapsule(
            (TriangleMesh)leaser.sprites[sprite],
            part.Columns,
            part.Rows,
            center - (center - before).normalized * MantleCrabMeshBuilder.WalkingJointTrim(LegJointWidth(joint, pairScale)),
            center - before,
            after - center,
            LegSegmentWidth(joint, pairScale) * .85f,
            false,
            camPos);
    }

    private static float LegSegmentWidth(int segment, float pairScale)
    {
        float segmentScale = segment switch
        {
            0 => .68f,
            1 => 1.00f,
            2 => .77f,
            _ => .53f
        };
        return 4.9f * pairScale * segmentScale;
    }

    private float LegJointWidth(int joint, float pairScale)
    {
        float proximal = LegSegmentWidth(joint, pairScale);
        float distal = LegSegmentWidth(joint + 1, pairScale);
        float structural = Mathf.Max(proximal * .82f, distal * .76f);
        return Mathf.Max(2.35f, structural) * phenotype.JointBulk;
    }

    private void DrawPincer(RoomCamera.SpriteLeaser leaser, int index, float time, Vector2 camPos)
    {
        MantleCrabLimb limb = crab.Pincers[index];
        MantleCrabPincerRig rig = limb.PincerRig;
        Vector2 anchor = rig.InterpolatedAnchor(time);
        Vector2 p0 = rig.Point(0, time);
        Vector2 p1 = rig.Point(1, time);
        Vector2 p2 = rig.Point(2, time);
        Vector2 p3 = rig.Point(3, time);

        DrawPincerShaft(leaser, index, 0, anchor, p0, 0f, PincerJointWidth(index, 0), camPos);
        DrawPincerShaft(leaser, index, 1, p0, p1, PincerJointWidth(index, 0), PincerJointWidth(index, 1), camPos);
        DrawPincerShaft(leaser, index, 2, p1, p2, PincerJointWidth(index, 1), PincerJointWidth(index, 2), camPos);
        // The fourth link is the chela itself. This short cuff belongs to its wrist, not an
        // extra long shaft with a fifth segment appended at p3.
        Vector2 palmAxis = rig.PalmAxis(time);
        DrawPincerShaft(leaser, index, 3, p2, p2 + palmAxis * 3f, 0f, 0f, camPos);

        DrawPincerJoint(leaser, index, 0, anchor, p0, p1, camPos);
        DrawPincerJoint(leaser, index, 1, p0, p1, p2, camPos);
        DrawPincerJoint(leaser, index, 2, p1, p2, p3, camPos);

        float open = rig.InterpolatedOpen(time);
        float palmLength = MantleCrabPincerAnatomy.PalmLengths[index];
        float palmWidth = MantleCrabPincerAnatomy.PalmWidths[index];
        float fingerLength = MantleCrabPincerAnatomy.FingerLengths[index];
        float fingerWidth = MantleCrabPincerAnatomy.FingerWidths[index];

        int palmSprite = MantleCrabSpriteLayout.PincerPalm(index);
        Part fixedPart = parts[palmSprite];
        MantleCrabMeshBuilder.PincerPalmAndFixedFinger(
            (TriangleMesh)leaser.sprites[palmSprite],
            fixedPart.Columns,
            fixedPart.Rows,
            p2,
            palmAxis,
            palmLength,
            palmWidth,
            fingerLength,
            fingerWidth,
            open,
            rig.Side,
            camPos);

        int movingSprite = MantleCrabSpriteLayout.PincerMovableFinger(index);
        Part movingPart = parts[movingSprite];
        MantleCrabMeshBuilder.PincerMovableFinger(
            (TriangleMesh)leaser.sprites[movingSprite],
            movingPart.Columns,
            movingPart.Rows,
            p2,
            palmAxis,
            palmLength,
            palmWidth,
            fingerLength,
            fingerWidth,
            open,
            rig.Side,
            camPos);
    }

    private float PincerJointWidth(int pincer, int joint) =>
        MantleCrabPincerAnatomy.JointWidth(pincer, joint) * phenotype.JointBulk;

    private void DrawPincerShaft(
        RoomCamera.SpriteLeaser leaser,
        int pincer,
        int segment,
        Vector2 start,
        Vector2 end,
        float startJointWidth,
        float endJointWidth,
        Vector2 camPos)
    {
        float startTrim = segment == 0 ? 0f : MantleCrabMeshBuilder.PincerJointTrim(startJointWidth);
        float endTrim = segment < 3 ? MantleCrabMeshBuilder.PincerJointTrim(endJointWidth) : 0f;
        TrimSegment(start, end, startTrim, endTrim, out Vector2 drawStart, out Vector2 drawEnd);

        int sprite = MantleCrabSpriteLayout.PincerShaft(pincer, segment);
        Part part = parts[sprite];
        MantleCrabMeshBuilder.PincerShaft(
            (TriangleMesh)leaser.sprites[sprite],
            part.Columns,
            part.Rows,
            drawStart,
            drawEnd,
            MantleCrabPincerAnatomy.SegmentWidth(pincer, segment) * (segment == 3 ? .5f : 1f),
            pincer * 4 + segment,
            camPos);
    }

    private void DrawPincerJoint(
        RoomCamera.SpriteLeaser leaser,
        int pincer,
        int joint,
        Vector2 before,
        Vector2 center,
        Vector2 after,
        Vector2 camPos)
    {
        int sprite = MantleCrabSpriteLayout.PincerJoint(pincer, joint);
        Part part = parts[sprite];
        MantleCrabMeshBuilder.JointCapsule(
            (TriangleMesh)leaser.sprites[sprite],
            part.Columns,
            part.Rows,
            center - (center - before).normalized * MantleCrabMeshBuilder.PincerJointTrim(PincerJointWidth(pincer, joint)),
            center - before,
            after - center,
            MantleCrabPincerAnatomy.SegmentWidth(pincer, joint) * .955f,
            true,
            camPos);
    }

    private static void TrimSegment(
        Vector2 start,
        Vector2 end,
        float startTrim,
        float endTrim,
        out Vector2 trimmedStart,
        out Vector2 trimmedEnd)
    {
        Vector2 delta = end - start;
        float length = delta.magnitude;
        if (length < .0001f)
        {
            trimmedStart = start;
            trimmedEnd = end;
            return;
        }

        Vector2 direction = delta / length;
        float requested = Mathf.Max(0f, startTrim) + Mathf.Max(0f, endTrim);
        float maximum = length * .42f;
        float scale = requested > maximum && requested > .0001f ? maximum / requested : 1f;
        trimmedStart = start + direction * (Mathf.Max(0f, startTrim) * scale);
        trimmedEnd = end - direction * (Mathf.Max(0f, endTrim) * scale);
    }

    private void Segment(
        RoomCamera.SpriteLeaser leaser,
        int index,
        Vector2 a,
        Vector2 b,
        float width,
        float taper,
        Vector2 camera)
    {
        Part p = parts[index];
        MantleCrabMeshBuilder.Segment(
            (TriangleMesh)leaser.sprites[index],
            p.Columns,
            p.Rows,
            a,
            b,
            width,
            taper,
            camera);
    }

    private void Round(
        RoomCamera.SpriteLeaser leaser,
        int index,
        Vector2 center,
        Vector2 axis,
        float width,
        float height,
        Vector2 camera,
        bool block = false)
    {
        Part p = parts[index];
        MantleCrabMeshBuilder.Rounded(
            (TriangleMesh)leaser.sprites[index],
            p.Columns,
            p.Rows,
            center,
            axis,
            width,
            height,
            camera,
            block);
    }

    private Color Environment(Vector2 position, RoomCamera camera)
    {
        Room room = crab.room;
        float darkness = room.Darkness(position);
        float exposure = room.LightSourceExposure(position);
        Color local = room.LightSourceColor(position);
        Color ambient = Color.Lerp(Color.white, camera.currentPalette.skyColor, .2f) *
                        Mathf.Lerp(1f, .22f, darkness);
        ambient = Color.Lerp(
            ambient,
            Color.Lerp(Color.white, local, .6f),
            exposure * .55f);
        ambient.a = 1f;
        return ambient;
    }

    private void Shade(
        RoomCamera.SpriteLeaser leaser,
        RoomCamera camera,
        int index,
        Vector2 camPos,
        float rootAO = 1f)
    {
        Part p = parts[index];
        TriangleMesh mesh = (TriangleMesh)leaser.sprites[index];

        if (SilhouetteDebug)
        {
            for (int i = 0; i < mesh.verticeColors.Length; i++)
                mesh.verticeColors[i] = Color.black;
            return;
        }

        int middle = p.Rows / 2 * (p.Columns + 1);
        Color left = Environment(mesh.vertices[middle] + camPos, camera);
        Color right = Environment(mesh.vertices[middle + p.Columns] + camPos, camera);
        Color center = Environment(mesh.vertices[middle + p.Columns / 2] + camPos, camera);
        bool custom = mesh.shader == DryCycleShaderAssets.MantleCrabSurface;
        Vector2 direction = mesh.vertices[p.Columns] - mesh.vertices[0];
        if (direction.sqrMagnitude < .0001f) direction = Vector2.right;
        direction.Normalize();

        for (int y = 0; y <= p.Rows; y++)
        for (int x = 0; x <= p.Columns; x++)
        {
            int vertex = y * (p.Columns + 1) + x;
            float u = x / (float)p.Columns;
            Color environment =
                (u < .5f
                    ? Color.Lerp(left, center, u * 2f)
                    : Color.Lerp(center, right, u * 2f - 1f)) *
                Mathf.Lerp(rootAO, 1f, u);

            if (custom)
            {
                environment.a = 1f - p.Depth * .5f;
                mesh.verticeColors[vertex] = environment;
            }
            else
            {
                float v = y / (float)p.Rows * 2f - 1f;
                Vector2 normalXY = MantleCrabRenderingMath.Perpendicular(direction) * v;
                float lighting = .75f + .25f * Vector2.Dot(normalXY, -crab.room.lightAngle.normalized);
                Color color = p.Fallback[vertex] * environment * lighting;
                color = Color.Lerp(
                    color,
                    camera.currentPalette.fogColor,
                    p.Depth * .22f + camera.currentPalette.fogAmount * .06f);
                color = Color.Lerp(color, camera.currentPalette.blackColor, .12f);
                if (p.Kind == MantleCrabMaterial.Eye)
                    color = Color.Lerp(color, p.Fallback[vertex], .55f);
                color.a = 1f;
                mesh.verticeColors[vertex] = color;
            }
        }
    }
}
