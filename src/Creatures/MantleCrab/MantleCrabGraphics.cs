using System;
using DryCycle.Creatures.MantleCrab.Rendering;
using DryCycle.Rendering;
using UnityEngine;

namespace DryCycle.Creatures.MantleCrab;

public sealed class MantleCrabGraphics : GraphicsModule
{
    private const int Shell = 38, PincersStart = 39, EyesStart = 59, SpriteCount = 61;
    private readonly MantleCrab crab;
    private readonly MantleCrabVisualPhenotype phenotype;
    private MantleCrabMaterialCache material;
    private readonly Part[] parts = new Part[SpriteCount];
    private readonly Vector2[,] threads = new Vector2[6, 4], lastThreads = new Vector2[6, 4];
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
            for (int j = 0; j < 4; j++) Define(i * 8 + j, 8, 4, MantleCrabMaterial.Leg, crab.Legs[i].LocalDepth);
            for (int j = 0; j < 3; j++) Define(i * 8 + 4 + j, 6, 4, MantleCrabMaterial.Joint, crab.Legs[i].LocalDepth);
            Define(i * 8 + 7, 6, 4, MantleCrabMaterial.Foot, crab.Legs[i].LocalDepth);
        }
        for (int i = 32; i < 38; i++) Define(i, 3, 1, MantleCrabMaterial.Fringe, .4f);
        Define(Shell, 64, 12, MantleCrabMaterial.Shell, 0f);
        for (int i = 0; i < 2; i++)
        {
            int start = PincersStart + i * 9;
            for (int j = 0; j < 3; j++) Define(start + j, 8, 4, MantleCrabMaterial.Pincer, 0f);
            for (int j = 3; j < 6; j++) Define(start + j, 6, 4, MantleCrabMaterial.Pincer, 0f);
            for (int j = 6; j < 9; j++) Define(start + j, 8, 4, MantleCrabMaterial.Pincer, 0f);
            Define(57 + i, 3, 2, MantleCrabMaterial.Pincer, 0f);
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

    private Vector2 EyeAnchor(int i) => crab.bodyChunks[2].pos + crab.Axis * (i == 0 ? -18f : 18f) + Vector2.up * 17f;
    private Vector2 ThreadAnchor(int i) => Vector2.Lerp(crab.bodyChunks[1].pos, crab.bodyChunks[3].pos, i / 5f) + Vector2.down * 16f;

    public override void Reset()
    {
        base.Reset();
        for (int i = 0; i < 2; i++) eyes[i] = lastEyes[i] = EyeAnchor(i) + Vector2.down * 8f;
        for (int i = 0; i < 6; i++)
        for (int j = 0; j < 4; j++) threads[i, j] = lastThreads[i, j] = ThreadAnchor(i) + Vector2.down * j * (5f + i % 3);
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
        for (int i = 0; i < 6; i++)
        {
            Vector2 previous = ThreadAnchor(i);
            for (int j = 0; j < 4; j++)
            {
                Vector2 next = threads[i, j] + (threads[i, j] - lastThreads[i, j]) * .85f + Vector2.down * .3f;
                lastThreads[i, j] = threads[i, j];
                threads[i, j] = j == 0 ? previous : previous + (next - previous).normalized * (5f + i % 3);
                previous = threads[i, j];
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
        for (int i = 0; i < 6; i++)
        {
            TriangleMesh mesh = (TriangleMesh)leaser.sprites[32 + i];
            for (int j = 0; j < 4; j++)
            {
                Vector2 point = Vector2.Lerp(lastThreads[i, j], threads[i, j], timeStacker) - camPos;
                mesh.MoveVertice(j, point - Vector2.right * .5f);
                mesh.MoveVertice(4 + j, point + Vector2.right * .5f);
            }
        }
        for (int i = 0; i < 2; i++)
        {
            DrawPincer(leaser, i, timeStacker, camPos);
            Vector2 eye = Vector2.Lerp(lastEyes[i], eyes[i], timeStacker);
            Segment(leaser, 57 + i, EyeAnchor(i), eye, 1.3f, .65f, camPos);
            Round(leaser, EyesStart + i, eye, crab.Axis, phenotype.EyeSize * (i == 0 ? .98f : 1.02f),
                phenotype.EyeSize * phenotype.EyeAspect, camPos);
        }
    }

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
            float half = MantleCrabRenderingMath.ShellHalfHeight(u);
            float edge = (MantleCrabRenderingMath.Noise(x * 3.7f + phenotype.NoiseSeed.x, 1f) - .5f) * phenotype.EdgeRoughness;
            center += up * (Mathf.Abs(u * 2f - 1f) * phenotype.WingAngle * 80f);
            for (int y = 0; y <= p.Rows; y++)
            {
                float v = y / (float)p.Rows;
                float offset = Mathf.Lerp(-half - edge, half * .65f + edge * .4f, v);
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
            Segment(leaser, sprite, previous, point, j == 3 ? 3.5f : 3.2f - j * .65f, .68f, camPos);
            if (j < 3)
            {
                Round(leaser, index * 8 + 4 + j, point, (point - previous).normalized,
                    (4.8f - j * .6f) * phenotype.JointBulk, 3.3f, camPos);
            }
            previous = point;
        }
        Vector2 normal = Vector2.Lerp(Vector2.up, limb.GroundNormal, .65f).normalized;
        Vector2 footAxis = new(normal.y, -normal.x);
        Round(leaser, index * 8 + 7, previous + normal * 18f, footAxis, 7.5f * phenotype.FootBulk, 19f, camPos, true);
    }

    private void DrawPincer(RoomCamera.SpriteLeaser leaser, int index, float time, Vector2 camPos)
    {
        MantleCrabLimb limb = crab.Pincers[index]; int start = PincersStart + index * 9;
        Vector2 previous = Vector2.Lerp(limb.LastAnchor, limb.Anchor, time);
        for (int j = 0; j < 3; j++)
        {
            Vector2 point = Vector2.Lerp(limb.LastPos[j], limb.Pos[j], time);
            Segment(leaser, start + j, previous, point, 2.5f - j * .45f, .7f, camPos);
            Round(leaser, start + 3 + j, point, (point - previous).normalized, 3.5f, 2.8f, camPos);
            previous = point;
        }
        Vector2 tip = Vector2.Lerp(limb.LastPos[3], limb.Pos[3], time), axis = (tip - previous).normalized;
        Vector2 cross = MantleCrabRenderingMath.Perpendicular(axis);
        Segment(leaser, start + 6, previous, Vector2.Lerp(previous, tip, .4f), 4.1f, 1f, camPos);
        // Fourth segment is a palm plus two curved, separated jaws. No grip/attack state.
        for (int jaw = 0; jaw < 2; jaw++)
        {
            TriangleMesh mesh = (TriangleMesh)leaser.sprites[start + 7 + jaw];
            Part p = parts[start + 7 + jaw]; float side = jaw == 0 ? -1f : 1f;
            for (int x = 0; x <= p.Columns; x++)
            for (int y = 0; y <= p.Rows; y++)
            {
                float u = x / (float)p.Columns, v = y / (float)p.Rows * 2f - 1f;
                Vector2 center = Vector2.Lerp(previous, tip, .3f + u * .7f) + cross * side * (2f + Mathf.Sin(u * Mathf.PI) * 5f);
                mesh.MoveVertice(y * (p.Columns + 1) + x, center + cross * v * Mathf.Lerp(2.3f, .2f, u) - camPos);
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
