using System;
using DryCycle.Creatures.MantleCrab.Rendering;
using DryCycle.Rendering;
using UnityEngine;

namespace DryCycle.Creatures.MantleCrab;

public sealed class MantleCrabGraphics : GraphicsModule
{
    internal const int ThreadCount = 28;
    internal const int Shell = 32 + ThreadCount;
    internal const int PincersStart = Shell + 1;
    internal const int EyesStart = PincersStart + 20;
    internal const int SpriteCount = EyesStart + 2;

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

        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 4; j++)
                Define(i * 8 + j, 12, 6, MantleCrabMaterial.Leg, crab.Legs[i].LocalDepth);
            for (int j = 0; j < 3; j++)
                Define(i * 8 + 4 + j, 6, 4, MantleCrabMaterial.Joint, crab.Legs[i].LocalDepth);
            Define(i * 8 + 7, 12, 6, MantleCrabMaterial.Foot, crab.Legs[i].LocalDepth);
        }

        for (int i = 32; i < Shell; i++)
            Define(i, 16, 1, MantleCrabMaterial.Fringe, .3f);
        Define(Shell, 96, 20, MantleCrabMaterial.Shell, 0f);

        for (int i = 0; i < 2; i++)
        {
            int start = PincersStart + i * 9;
            for (int j = 0; j < 3; j++)
                Define(start + j, 8, 4, MantleCrabMaterial.Pincer, 0f);
            for (int j = 3; j < 6; j++)
                Define(start + j, 6, 4, MantleCrabMaterial.Pincer, 0f);
            for (int j = 6; j < 9; j++)
                Define(start + j, 8, 4, MantleCrabMaterial.Pincer, 0f);
            Define(EyesStart - 2 + i, 8, 2, MantleCrabMaterial.Pincer, 0f);
            Define(EyesStart + i, 10, 8, MantleCrabMaterial.Eye, 0f);
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

        // Rear leg pair, mantle/fringe, front leg pair, then hanging pincers/eyes.
        for (int i = 0; i < 16; i++) container.AddChild(leaser.sprites[i]);
        for (int i = 32; i <= Shell; i++) container.AddChild(leaser.sprites[i]);
        for (int i = 16; i < 32; i++) container.AddChild(leaser.sprites[i]);
        for (int i = PincersStart; i < SpriteCount; i++) container.AddChild(leaser.sprites[i]);
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

        // Four unmistakable two-ended loops create the hanging arcs visible in the reference.
        switch (i)
        {
            case 0:
                a = new Vector2(-72f, -8f);
                control = new Vector2(-78f, -108f);
                b = new Vector2(-47f, -20f);
                break;
            case 1:
                a = new Vector2(-55f, -18f);
                control = new Vector2(-58f, -82f);
                b = new Vector2(-31f, -24f);
                break;
            case 2:
                a = new Vector2(70f, -8f);
                control = new Vector2(78f, -104f);
                b = new Vector2(47f, -20f);
                break;
            case 3:
                a = new Vector2(56f, -15f);
                control = new Vector2(64f, -76f);
                b = new Vector2(40f, -29f);
                break;
            case 4:
                a = new Vector2(-27f, -26f);
                control = new Vector2(-33f, -72f);
                b = new Vector2(-20f, -84f);
                break;
            case 5:
                a = new Vector2(29f, -25f);
                control = new Vector2(36f, -74f);
                b = new Vector2(23f, -82f);
                break;
            default:
            {
                int seed = crab.abstractCreature?.ID.RandomSeed ?? 1729;
                float h = MantleCrabVisualGenome.Hash(seed, "Fringe" + i);
                float bend = MantleCrabVisualGenome.Hash(seed, "FringeBend" + i) - .5f;

                if (i < 14)
                {
                    // Medium hanging feelers: visible, irregular, but not dominant loops.
                    int slot = i - 6;
                    float f = (slot + .5f) / 8f;
                    float length = Mathf.Lerp(28f, 58f, h);
                    a = new Vector2(Mathf.Lerp(-58f, 58f, f), -22f + h * 6f);
                    control = a + new Vector2(bend * 22f, -length * .55f);
                    b = a + new Vector2(bend * 9f, -length);
                }
                else
                {
                    // Dense short fringe breaks the lower shell edge without forming a comb.
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
            Shade(leaser, camera, i, camPos, i < 32 && i % 8 == 0 ? .6f : 1f);
    }

    // Geometry is separate from environment sampling so exported meshes remain testable.
    internal void PoseSprites(RoomCamera.SpriteLeaser leaser, float timeStacker, Vector2 camPos)
    {
        for (int i = 0; i < 4; i++)
            DrawLeg(leaser, i, timeStacker, camPos);

        DrawShell((TriangleMesh)leaser.sprites[Shell], timeStacker, camPos);

        for (int i = 0; i < ThreadCount; i++)
        {
            TriangleMesh mesh = (TriangleMesh)leaser.sprites[32 + i];
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
                    width = Mathf.Lerp(.82f, .35f, j / 16f);
                else if (i < 14)
                    width = Mathf.Lerp(1.05f, .16f, j / 16f);
                else
                    width = Mathf.Lerp(1.30f, .10f, j / 16f);

                Vector2 tangent = c - b;
                if (tangent.sqrMagnitude < .0001f) tangent = Vector2.down;
                Vector2 cross = MantleCrabRenderingMath.Perpendicular(tangent.normalized) * width;
                mesh.MoveVertice(j, point - cross);
                mesh.MoveVertice(17 + j, point + cross);
            }
        }

        for (int i = 0; i < 2; i++)
        {
            DrawPincer(leaser, i, timeStacker, camPos);
            Vector2 eye = Vector2.Lerp(lastEyes[i], eyes[i], timeStacker);
            Vector2 stalk = new Vector2(i == 0 ? 2.5f : -4f, i == 0 ? -19f : -23f);
            Segment(leaser, EyesStart - 2 + i, eye, eye + stalk, .78f, .20f, camPos);
            Round(
                leaser,
                EyesStart + i,
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
            float crownBase = Mathf.Max(0f, 1f - Mathf.Pow(wing / .82f, 2f));
            float crown = Mathf.Pow(crownBase, 1.25f);
            float edgeNoise =
                (MantleCrabRenderingMath.Noise(x * 3.7f + phenotype.NoiseSeed.x, 1f) - .5f) *
                phenotype.EdgeRoughness;
            float localAsymmetry = signed * phenotype.Asymmetry * 18f +
                                   (MantleCrabRenderingMath.Noise(x * 1.93f + phenotype.NoiseSeed.z, 2.1f) - .5f) *
                                   .65f * (1f - wing);

            // Keep the BodyChunk frame untouched while giving the visible mantle a heavy
            // centre and slightly drooping blade-like wings.
            center += up * (-5.5f * Mathf.Pow(wing, 1.7f) + phenotype.WingAngle * 70f + localAsymmetry);
            center += up * (
                8f - Mathf.Lerp(
                    MantleCrab.ShellRest[station].y,
                    MantleCrab.ShellRest[station + 1].y,
                    bodyU - station));

            float top = 5.5f + 30f * crown;
            top += Mathf.Pow(Mathf.Max(0f, Mathf.Sin(x * 1.73f)), 8f) *
                   3.4f * Mathf.Clamp01(1f - wing / .68f);

            float bottom = 4.5f - 47f * Mathf.Pow(broad, 1.25f);
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
        Vector2 previous = Vector2.Lerp(limb.LastAnchor, limb.Anchor, time);
        float pairScale = index < 2 ? 1f : .84f;

        for (int j = 0; j < 4; j++)
        {
            Vector2 point = Vector2.Lerp(limb.LastPos[j], limb.Pos[j], time);
            int sprite = index * 8 + j;
            Part part = parts[sprite];
            float segmentScale = j switch
            {
                0 => .68f,
                1 => 1.00f,
                2 => .77f,
                _ => .43f
            };
            float width = 4.9f * pairScale * segmentScale;

            MantleCrabMeshBuilder.Chitin(
                (TriangleMesh)leaser.sprites[sprite],
                part.Columns,
                part.Rows,
                previous,
                point,
                width,
                index + j,
                camPos);

            if (j < 3)
            {
                Vector2 direction = point - previous;
                if (direction.sqrMagnitude < .0001f) direction = Vector2.down;
                direction.Normalize();
                Vector2 next = Vector2.Lerp(limb.LastPos[j + 1], limb.Pos[j + 1], time) - point;
                if (next.sqrMagnitude < .0001f) next = Vector2.down;
                next.Normalize();
                Segment(
                    leaser,
                    index * 8 + 4 + j,
                    point - direction * 1.5f,
                    point + next * 1.4f,
                    Mathf.Max(2.1f, width * .82f) * phenotype.JointBulk,
                    .62f,
                    camPos);
            }

            previous = point;
        }

        Part foot = parts[index * 8 + 7];
        Vector2 ankle = Vector2.Lerp(limb.LastPos[2], limb.Pos[2], time);
        float footWidth = (index < 2 ? 8.2f : 7.4f) * phenotype.FootBulk;
        MantleCrabMeshBuilder.Foot(
            (TriangleMesh)leaser.sprites[index * 8 + 7],
            foot.Columns,
            foot.Rows,
            ankle,
            previous,
            footWidth,
            limb.Side,
            limb.GroundNormal,
            camPos);
    }

    private void DrawPincer(RoomCamera.SpriteLeaser leaser, int index, float time, Vector2 camPos)
    {
        MantleCrabLimb limb = crab.Pincers[index];
        int start = PincersStart + index * 9;
        Vector2 previous = Vector2.Lerp(limb.LastAnchor, limb.Anchor, time);
        float baseWidth = index == 0 ? 2.35f : 2.65f;

        for (int j = 0; j < 3; j++)
        {
            Vector2 point = Vector2.Lerp(limb.LastPos[j], limb.Pos[j], time);
            Part part = parts[start + j];
            float segmentScale = j == 0 ? 1f : j == 1 ? .86f : .74f;
            float width = baseWidth * segmentScale;
            MantleCrabMeshBuilder.Chitin(
                (TriangleMesh)leaser.sprites[start + j],
                part.Columns,
                part.Rows,
                previous,
                point,
                width,
                j + index,
                camPos);

            Vector2 direction = point - previous;
            if (direction.sqrMagnitude < .0001f) direction = Vector2.down;
            direction.Normalize();
            Vector2 next = Vector2.Lerp(limb.LastPos[j + 1], limb.Pos[j + 1], time) - point;
            if (next.sqrMagnitude < .0001f) next = Vector2.down;
            next.Normalize();
            Segment(
                leaser,
                start + 3 + j,
                point - direction * 1.15f,
                point + next * 1.15f,
                Mathf.Max(1.45f, width * .82f),
                .58f,
                camPos);
            previous = point;
        }

        Vector2 tip = Vector2.Lerp(limb.LastPos[3], limb.Pos[3], time);
        Vector2 axis = tip - previous;
        if (axis.sqrMagnitude < .0001f) axis = Vector2.down;
        axis.Normalize();
        Vector2 cross = MantleCrabRenderingMath.Perpendicular(axis);

        // A long wrist occupies most of the terminal link; the actual two-jaw claw is compact.
        Segment(
            leaser,
            start + 6,
            previous,
            Vector2.Lerp(previous, tip, .72f),
            index == 0 ? 3.25f : 4.05f,
            .60f,
            camPos);

        for (int jaw = 0; jaw < 2; jaw++)
        {
            TriangleMesh mesh = (TriangleMesh)leaser.sprites[start + 7 + jaw];
            Part p = parts[start + 7 + jaw];
            float side = jaw == 0 ? -1f : 1f;

            for (int x = 0; x <= p.Columns; x++)
            for (int y = 0; y <= p.Rows; y++)
            {
                float u = x / (float)p.Columns;
                float v = y / (float)p.Rows * 2f - 1f;
                float spread = index == 0 ? 1.55f : 2.65f;
                float length = jaw == 0 ? 1f : .84f;
                float along = .64f + u * .36f * length;
                float jawCurve = spread * (.35f + u * .65f) + Mathf.Sin(u * Mathf.PI) * .75f;
                Vector2 center = Vector2.Lerp(previous, tip, along) + cross * side * jawCurve;
                float blade = (index == 0 ? 1.15f : 1.75f) * Mathf.Pow(1f - u, .72f) + .035f;
                mesh.MoveVertice(
                    y * (p.Columns + 1) + x,
                    center + cross * v * blade - camPos);
            }
        }
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
                        Mathf.Lerp(1f, .12f, darkness);
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
