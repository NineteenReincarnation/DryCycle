using System;
using DryCycle.Creatures.MantleCrab.Rendering;
using DryCycle.Rendering;
using UnityEngine;

namespace DryCycle.Creatures.MantleCrab;

public sealed class MantleCrabGraphics : GraphicsModule
{
    internal const int ThreadCount = 20, Shell = 32 + ThreadCount, PincersStart = Shell + 1, EyesStart = PincersStart + 20, SpriteCount = EyesStart + 2;
    private readonly MantleCrab crab;
    private readonly MantleCrabVisualPhenotype phenotype;
    private MantleCrabMaterialCache material;
    private readonly Part[] parts = new Part[SpriteCount];
    private readonly Vector2[,] threads = new Vector2[ThreadCount, 5], lastThreads = new Vector2[ThreadCount, 5];
    private readonly Vector2[] eyes = new Vector2[2], lastEyes = new Vector2[2];

    private sealed class Part
    {
        internal readonly int Columns, Rows;
        internal readonly MantleCrabMaterial Kind;
        internal readonly float Depth;
        internal readonly Color[] Fallback;
        internal Part(int columns, int rows, MantleCrabMaterial kind, float depth, MantleCrabVisualPhenotype p)
        {
            Columns = columns; Rows = rows; Kind = kind; Depth = depth;
            Fallback = new Color[(columns + 1) * (rows + 1)];
            for (int y = 0; y <= rows; y++)
            for (int x = 0; x <= columns; x++)
            {
                MantleCrabMaterialBaker.Sample(p, kind, new Vector2(x / (float)columns, y / (float)rows),
                    out Color pattern, out Color surface);
                Color color = new(pattern.r, pattern.g, pattern.b, 1f);
                color *= Mathf.Lerp(.5f, 1f, surface.r); color.a = 1f;
                Fallback[y * (columns + 1) + x] = color;
            }
        }
    }

    public MantleCrabGraphics(MantleCrab crab) : base(crab, false)
    {
        this.crab = crab; phenotype = crab.Phenotype; cullRange = 650f;
        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 4; j++) Define(i * 8 + j, 12, 6, MantleCrabMaterial.Leg, crab.Legs[i].LocalDepth);
            for (int j = 0; j < 3; j++) Define(i * 8 + 4 + j, 6, 4, MantleCrabMaterial.Joint, crab.Legs[i].LocalDepth);
            Define(i * 8 + 7, 12, 6, MantleCrabMaterial.Foot, crab.Legs[i].LocalDepth);
        }
        for (int i = 32; i < Shell; i++) Define(i, 16, 1, MantleCrabMaterial.Fringe, .3f);
        Define(Shell, 96, 20, MantleCrabMaterial.Shell, 0f);
        for (int i = 0; i < 2; i++)
        {
            int start = PincersStart + i * 9;
            for (int j = 0; j < 3; j++) Define(start + j, 8, 4, MantleCrabMaterial.Pincer, 0f);
            for (int j = 3; j < 6; j++) Define(start + j, 6, 4, MantleCrabMaterial.Pincer, 0f);
            for (int j = 6; j < 9; j++) Define(start + j, 8, 4, MantleCrabMaterial.Pincer, 0f);
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
                if (material == null || material.Released) material = new MantleCrabMaterialCache(phenotype);
                material.Attach(leaser);
            }
            catch (Exception ex) { custom = false; Plugin.Logger?.LogWarning("MantleCrab atlas fallback: " + ex.Message); }
        }
        leaser.sprites = new FSprite[SpriteCount];
        for (int i = 0; i < SpriteCount; i++)
        {
            Part p = parts[i];
            TriangleMesh mesh = MantleCrabMeshBuilder.Grid(custom ? material.AtlasName : "Futile_White", p.Columns, p.Rows, p.Kind);
            mesh.shader = custom ? DryCycleShaderAssets.MantleCrabSurface : camera.game.rainWorld.Shaders["Basic"];
            if (!custom) mesh.SetAtlasedImage(Futile.atlasManager.GetElementWithName("Futile_White"));
            leaser.sprites[i] = mesh;
        }
        AddToContainer(leaser, camera, null);
    }

    public override void AddToContainer(RoomCamera.SpriteLeaser leaser, RoomCamera camera, FContainer container)
    {
        container ??= camera.ReturnFContainer("Midground");
        foreach (FSprite sprite in leaser.sprites) sprite.RemoveFromContainer();
        // Local depth is a single species-level ordering, independent of world layers.
        for (int i = 0; i < 16; i++) container.AddChild(leaser.sprites[i]);
        for (int i = 32; i <= Shell; i++) container.AddChild(leaser.sprites[i]);
        for (int i = 16; i < 32; i++) container.AddChild(leaser.sprites[i]);
        for (int i = PincersStart; i < SpriteCount; i++) container.AddChild(leaser.sprites[i]);
    }

    private Vector2 EyeAnchor(int i) => Local(new Vector2(i == 0 ? -10f : 25f, 21f));
    private Vector2 Local(Vector2 point) => crab.bodyChunks[2].pos + crab.Axis * point.x + new Vector2(-crab.Axis.y, crab.Axis.x) * point.y;
    private Vector2 ThreadRest(int i, float t)
    {
        Vector2 a, control, b;
        switch (i)
        {
            case 0: a = new(-63,-5); control = new(-61,-121); b = new(-44,-19); break;
            case 1: a = new(-46,-21); control = new(-43,-88); b = new(-21,-24); break;
            case 2: a = new(57,-6); control = new(65,-96); b = new(40,-18); break;
            case 3: a = new(48,-11); control = new(55,-59); b = new(49,-65); break;
            default:
                float h = MantleCrabVisualGenome.Hash(1729, "Fringe" + i);
                a = new Vector2(Mathf.Lerp(-43, 43, (i - 4) / 15f), -22 + h * 8);
                control = a + new Vector2((h - .5f) * 10, -10 - h * 17);
                b = a + new Vector2((h - .5f) * 6, -13 - h * 24);
                break;
        }
        return Local(a * ((1-t)*(1-t)) + control * (2*t*(1-t)) + b * (t*t));
    }

    public override void Reset()
    {
        base.Reset();
        for (int i = 0; i < 2; i++) eyes[i] = lastEyes[i] = EyeAnchor(i) + Vector2.down * 8f;
        for (int i = 0; i < ThreadCount; i++)
        for (int j = 0; j < 5; j++) threads[i, j] = lastThreads[i, j] = ThreadRest(i, j / 4f);
    }

    public override void Update()
    {
        base.Update();
        for (int i = 0; i < 2; i++)
        {
            Vector2 desired = EyeAnchor(i) + Vector2.down * 8f;
            Vector2 next = Vector2.Lerp(eyes[i] + (eyes[i] - lastEyes[i]) * .5f, desired, .32f);
            lastEyes[i] = eyes[i]; eyes[i] = desired + Vector2.ClampMagnitude(next - desired, 2.3f);
        }
        for (int i = 0; i < ThreadCount; i++)
        {
            for (int j = 0; j < 5; j++)
            {
                Vector2 rest = ThreadRest(i, j / 4f);
                Vector2 next = threads[i,j] + (threads[i,j]-lastThreads[i,j]) * .8f + (rest-threads[i,j])*.08f;
                lastThreads[i, j] = threads[i, j];
                threads[i,j] = j == 0 || (i < 3 && j == 4) ? rest : rest + Vector2.ClampMagnitude(next-rest, 3f);
            }
        }
    }

    public override void DrawSprites(RoomCamera.SpriteLeaser leaser, RoomCamera camera, float timeStacker, Vector2 camPos)
    {
        if (crab.slatedForDeletetion || crab.room == null || camera.room != crab.room)
        { leaser.CleanSpritesAndRemove(); return; }
        base.DrawSprites(leaser, camera, timeStacker, camPos);
        if (culled || dispose) return;
        PoseSprites(leaser, timeStacker, camPos);
        for (int i = 0; i < SpriteCount; i++)
            Shade(leaser, camera, i, camPos, i < 32 && i % 8 == 0 ? .6f : 1f);
    }

    // Geometry is separate from environment sampling so its exported meshes can be validated off-game.
    internal void PoseSprites(RoomCamera.SpriteLeaser leaser, float timeStacker, Vector2 camPos)
    {
        for (int i = 0; i < 4; i++) DrawLeg(leaser, i, timeStacker, camPos);
        DrawShell((TriangleMesh)leaser.sprites[Shell], timeStacker, camPos);
        for (int i = 0; i < ThreadCount; i++)
        {
            TriangleMesh mesh = (TriangleMesh)leaser.sprites[32 + i];
            for (int j = 0; j <= 16; j++)
            {
                float u = j / 4f; int k = Mathf.Min(3, (int)u); float t = u-k;
                Vector2 a = ThreadPoint(i, Mathf.Max(0,k-1), timeStacker), b = ThreadPoint(i,k,timeStacker);
                Vector2 c = ThreadPoint(i,k+1,timeStacker), d = ThreadPoint(i,Mathf.Min(4,k+2),timeStacker);
                Vector2 point = .5f*((2*b)+(-a+c)*t+(2*a-5*b+4*c-d)*t*t+(-a+3*b-3*c+d)*t*t*t)-camPos;
                float width = i < 4 ? .65f : Mathf.Lerp(1.3f,.15f,j/16f);
                Vector2 cross = MantleCrabRenderingMath.Perpendicular((c-b).normalized)*width;
                mesh.MoveVertice(j, point-cross); mesh.MoveVertice(17+j,point+cross);
            }
        }
        for (int i = 0; i < 2; i++)
        {
            DrawPincer(leaser, i, timeStacker, camPos);
            Vector2 eye = Vector2.Lerp(lastEyes[i], eyes[i], timeStacker);
            // The thin continuation below each red eye is part of its hanging stalk.
            Segment(leaser, EyesStart - 2 + i, eye, eye + new Vector2(i == 0 ? 3f : -7f,-22f), .8f, .25f, camPos);
            Round(leaser, EyesStart + i, eye, crab.Axis, phenotype.EyeSize * (i == 0 ? .98f : 1.02f),
                phenotype.EyeSize * phenotype.EyeAspect, camPos);
        }
    }

    private Vector2 ThreadPoint(int i, int j, float time) => Vector2.Lerp(lastThreads[i,j], threads[i,j], time);

    private void DrawShell(TriangleMesh mesh, float time, Vector2 camera)
    {
        Part p = parts[Shell];
        Vector2 axis = crab.Axis, up = MantleCrabRenderingMath.Perpendicular(axis);
        for (int x = 0; x <= p.Columns; x++)
        {
            float u = x / (float)p.Columns, bodyU = Mathf.Clamp01((u * 202f - 17f) / 168f) * 4f;
            int station = Mathf.Min(3, (int)bodyU);
            Vector2 a = Vector2.Lerp(crab.bodyChunks[station].lastPos, crab.bodyChunks[station].pos, time);
            Vector2 b = Vector2.Lerp(crab.bodyChunks[station + 1].lastPos, crab.bodyChunks[station + 1].pos, time);
            Vector2 center = Vector2.Lerp(a, b, bodyU - station);
            if (u < 17f / 202f) center += axis * ((u * 202f - 17f) * phenotype.ShellWidth);
            if (u > 185f / 202f) center += axis * ((u * 202f - 185f) * phenotype.ShellWidth);
            float wing = Mathf.Abs(u*2-1);
            float edge = (MantleCrabRenderingMath.Noise(x * 3.7f + phenotype.NoiseSeed.x, 1f) - .5f) * phenotype.EdgeRoughness;
            center += up * (Mathf.Abs(u * 2f - 1f) * phenotype.WingAngle * 80f);
            center += up * (8f - Mathf.Lerp(MantleCrab.ShellRest[station].y, MantleCrab.ShellRest[station+1].y, bodyU-station));
            float top = 6f + 22f * Mathf.Exp(-Mathf.Pow(wing / .53f, 3f));
            top += Mathf.Pow(Mathf.Max(0,Mathf.Sin(x*1.73f)),8f)*3.2f*Mathf.Clamp01(1-wing/.65f);
            float bottom = 5f - 36f * Mathf.Pow(Mathf.Max(0,1-wing*wing),2.2f);
            float fibers = Mathf.Pow(Mathf.Max(0,Mathf.Sin(x*2.17f+phenotype.StripePhase)),6f)*(1-wing)*2.8f;
            for (int y = 0; y <= p.Rows; y++)
            {
                float v = y / (float)p.Rows;
                float offset = Mathf.Lerp(bottom-edge-fibers, top+edge*.8f, v);
                mesh.MoveVertice(y * (p.Columns + 1) + x, center + up * offset - camera);
            }
        }
    }

    private void DrawLeg(RoomCamera.SpriteLeaser leaser, int index, float time, Vector2 camPos)
    {
        MantleCrabLimb limb = crab.Legs[index];
        Vector2 previous = Vector2.Lerp(limb.LastAnchor, limb.Anchor, time);
        for (int j = 0; j < 4; j++)
        {
            Vector2 point = Vector2.Lerp(limb.LastPos[j], limb.Pos[j], time);
            int sprite = index * 8 + j;
            Part part = parts[sprite];
            float width = (index < 2 ? 4.8f : 3.5f) * (j == 0 ? .85f : j == 1 ? 1f : .74f);
            MantleCrabMeshBuilder.Chitin((TriangleMesh)leaser.sprites[sprite], part.Columns, part.Rows, previous, point, width, index+j, camPos);
            if (j < 3)
            {
                Vector2 direction = (point-previous).normalized;
                Vector2 next = (Vector2.Lerp(limb.LastPos[j+1],limb.Pos[j+1],time)-point).normalized;
                Segment(leaser,index*8+4+j,point-direction*1.3f,point+next*1.2f,width*.78f,.65f,camPos);
            }
            previous = point;
        }
        Part foot = parts[index*8+7];
        Vector2 ankle = Vector2.Lerp(limb.LastPos[2],limb.Pos[2],time);
        MantleCrabMeshBuilder.Foot((TriangleMesh)leaser.sprites[index*8+7],foot.Columns,foot.Rows,ankle,previous,
            (index < 2 ? 6.4f : 7f)*phenotype.FootBulk, limb.Side,limb.GroundNormal,camPos);
    }

    private void DrawPincer(RoomCamera.SpriteLeaser leaser, int index, float time, Vector2 camPos)
    {
        MantleCrabLimb limb = crab.Pincers[index]; int start = PincersStart + index * 9;
        Vector2 previous = Vector2.Lerp(limb.LastAnchor, limb.Anchor, time);
        for (int j = 0; j < 3; j++)
        {
            Vector2 point = Vector2.Lerp(limb.LastPos[j], limb.Pos[j], time);
            Part part = parts[start+j];
            float width = index == 0 ? 2.3f : 2.8f;
            MantleCrabMeshBuilder.Chitin((TriangleMesh)leaser.sprites[start+j],part.Columns,part.Rows,previous,point,width,j+index,camPos);
            Vector2 direction = (point-previous).normalized;
            Vector2 next = (Vector2.Lerp(limb.LastPos[j+1],limb.Pos[j+1],time)-point).normalized;
            Segment(leaser,start+3+j,point-direction*1.1f,point+next*1.1f,width*.8f,.6f,camPos);
            previous = point;
        }
        Vector2 tip = Vector2.Lerp(limb.LastPos[3], limb.Pos[3], time), axis = (tip - previous).normalized;
        Vector2 cross = MantleCrabRenderingMath.Perpendicular(axis);
        Segment(leaser, start + 6, previous, Vector2.Lerp(previous, tip, .45f), index == 0 ? 3f : 5f, .8f, camPos);
        // Fourth segment is a palm plus two curved, separated jaws. No grip/attack state.
        for (int jaw = 0; jaw < 2; jaw++)
        {
            TriangleMesh mesh = (TriangleMesh)leaser.sprites[start + 7 + jaw];
            Part p = parts[start + 7 + jaw]; float side = jaw == 0 ? -1f : 1f;
            for (int x = 0; x <= p.Columns; x++)
            for (int y = 0; y <= p.Rows; y++)
            {
                float u = x / (float)p.Columns, v = y / (float)p.Rows * 2f - 1f;
                float spread = index == 0 ? 2f : 4.4f;
                float length = jaw == 0 ? 1f : .83f;
                Vector2 center = Vector2.Lerp(previous, tip, .34f + u * .66f * length) + cross * side * (spread * (.45f + u*.8f));
                float blade = (index == 0 ? 1.5f : 2.6f) * Mathf.Pow(1-u,.7f) + .04f;
                mesh.MoveVertice(y * (p.Columns + 1) + x, center + cross * v * blade - camPos);
            }
        }
    }

    private void Segment(RoomCamera.SpriteLeaser leaser, int index, Vector2 a, Vector2 b, float width, float taper, Vector2 camera)
    {
        Part p = parts[index]; MantleCrabMeshBuilder.Segment((TriangleMesh)leaser.sprites[index], p.Columns, p.Rows, a, b, width, taper, camera);
    }
    private void Round(RoomCamera.SpriteLeaser leaser, int index, Vector2 center, Vector2 axis, float width, float height, Vector2 camera, bool block = false)
    {
        Part p = parts[index]; MantleCrabMeshBuilder.Rounded((TriangleMesh)leaser.sprites[index], p.Columns, p.Rows, center, axis, width, height, camera, block);
    }

    private Color Environment(Vector2 position, RoomCamera camera)
    {
        Room room = crab.room;
        float darkness = room.Darkness(position), exposure = room.LightSourceExposure(position);
        Color local = room.LightSourceColor(position);
        Color ambient = Color.Lerp(Color.white, camera.currentPalette.skyColor, .2f) * Mathf.Lerp(1f, .12f, darkness);
        // Darkness already includes local exposure in RW; only add the color cast here.
        ambient = Color.Lerp(ambient, Color.Lerp(Color.white, local, .6f), exposure * .55f);
        ambient.a = 1f; return ambient;
    }

    private void Shade(RoomCamera.SpriteLeaser leaser, RoomCamera camera, int index, Vector2 camPos, float rootAO = 1f)
    {
        Part p = parts[index]; TriangleMesh mesh = (TriangleMesh)leaser.sprites[index];
        int middle = p.Rows / 2 * (p.Columns + 1);
        Color left = Environment(mesh.vertices[middle] + camPos, camera);
        Color right = Environment(mesh.vertices[middle + p.Columns] + camPos, camera);
        Color center = Environment(mesh.vertices[middle + p.Columns / 2] + camPos, camera);
        bool custom = mesh.shader == DryCycleShaderAssets.MantleCrabSurface;
        Vector2 direction = (mesh.vertices[p.Columns] - mesh.vertices[0]).normalized;
        for (int y = 0; y <= p.Rows; y++)
        for (int x = 0; x <= p.Columns; x++)
        {
            int vertex = y * (p.Columns + 1) + x;
            float u = x / (float)p.Columns;
            Color environment = (u < .5f ? Color.Lerp(left, center, u * 2f) : Color.Lerp(center, right, u * 2f - 1f)) * Mathf.Lerp(rootAO, 1f, u);
            if (custom) { environment.a = 1f - p.Depth * .5f; mesh.verticeColors[vertex] = environment; }
            else
            {
                float v = y / (float)p.Rows * 2 - 1;
                Vector2 normalXY = MantleCrabRenderingMath.Perpendicular(direction) * v;
                float lighting = .75f + .25f * Vector2.Dot(normalXY, -crab.room.lightAngle.normalized);
                Color color = p.Fallback[vertex] * environment * lighting;
                color = Color.Lerp(color, camera.currentPalette.fogColor, p.Depth * .22f + camera.currentPalette.fogAmount * .06f);
                color = Color.Lerp(color, camera.currentPalette.blackColor, .12f);
                if (p.Kind == MantleCrabMaterial.Eye) color = Color.Lerp(color, p.Fallback[vertex], .55f);
                color.a = 1f; mesh.verticeColors[vertex] = color;
            }
        }
    }
}
