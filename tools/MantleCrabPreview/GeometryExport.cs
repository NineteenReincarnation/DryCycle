using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using DryCycle.Creatures.MantleCrab;
using DryCycle.Creatures.MantleCrab.Rendering;
using UnityEngine;

internal static partial class Program
{
    private static void ExportGeometry()
    {
        MantleCrabVisualPhenotype phenotype = CreatePhenotype();
        MantleCrab crab = CreatePreviewCrab(phenotype);
        MantleCrabGraphics graphics = new(crab);
        RoomCamera.SpriteLeaser leaser = CreatePreviewLeaser(graphics, out Array parts);

        graphics.PoseSprites(leaser, 1f, Vector2.zero);
        List<object> meshes = CollectMeshes(leaser, parts);
        WriteFixture(phenotype, meshes);
        WriteCpuAtlas(phenotype);
    }

    private static MantleCrab CreatePreviewCrab(MantleCrabVisualPhenotype phenotype)
    {
        MantleCrab crab = Empty<MantleCrab>();
        SetField(crab, "Phenotype", phenotype);
        crab.ShellScale = 1f;

        BodyChunk[] chunks = new BodyChunk[5];
        for (int i = 0; i < chunks.Length; i++)
        {
            chunks[i] = Empty<BodyChunk>();
            chunks[i].pos = chunks[i].lastPos = MantleCrab.ShellRest[i] + new Vector2(0f, 292f);
        }
        crab.bodyChunks = chunks;

        MantleCrabLimb[] legs = new MantleCrabLimb[4];
        for (int i = 0; i < legs.Length; i++)
            legs[i] = CreatePreviewLimb(crab, i, false);
        SetField(crab, "Legs", legs);

        MantleCrabLimb[] pincers = new MantleCrabLimb[2];
        for (int i = 0; i < pincers.Length; i++)
            pincers[i] = CreatePreviewLimb(crab, i, true);
        SetField(crab, "Pincers", pincers);

        return crab;
    }

    private static MantleCrabLimb CreatePreviewLimb(MantleCrab crab, int index, bool pincer)
    {
        MantleCrabLimb limb = new(index, pincer);
        Vector2 anchor = crab.Anchor(limb);
        limb.Reset(anchor);
        limb.GroundNormal = Vector2.up;
        if (!pincer) limb.SolvePose(anchor, anchor + limb.RestTipOffset, true);
        Array.Copy(limb.Pos, limb.LastPos, limb.Pos.Length);
        return limb;
    }

    private static RoomCamera.SpriteLeaser CreatePreviewLeaser(MantleCrabGraphics graphics, out Array parts)
    {
        parts = (Array)GetField(graphics, "parts");
        RoomCamera.SpriteLeaser leaser = Empty<RoomCamera.SpriteLeaser>();
        leaser.sprites = new FSprite[parts.Length];

        for (int i = 0; i < parts.Length; i++)
        {
            object part = parts.GetValue(i);
            int columns = (int)GetField(part, "Columns");
            int rows = (int)GetField(part, "Rows");
            TriangleMesh mesh = Empty<TriangleMesh>();
            mesh.vertices = new Vector2[(columns + 1) * (rows + 1)];
            leaser.sprites[i] = mesh;
        }

        return leaser;
    }

    private static List<object> CollectMeshes(RoomCamera.SpriteLeaser leaser, Array parts)
    {
        // Geometry validation is allocation-order agnostic. Layering is owned by
        // MantleCrabGraphics.AddToContainer and index ownership by MantleCrabSpriteLayoutTests.
        IEnumerable<int> order = MantleCrabSpriteLayout.DrawOrder();

        List<object> meshes = new();
        foreach (int index in order)
        {
            object part = parts.GetValue(index);
            TriangleMesh mesh = (TriangleMesh)leaser.sprites[index];
            bool finite = true;
            float largestCoordinate = 0f;
            foreach (Vector2 point in mesh.vertices)
            {
                finite &= Finite(point);
                if (Finite(point))
                    largestCoordinate = Math.Max(largestCoordinate, Math.Max(Math.Abs(point.x), Math.Abs(point.y)));
            }
            Require(finite, "Non-finite MantleCrab mesh vertex at sprite=" + index);
            Require(largestCoordinate < 5000f,
                "MantleCrab mesh escaped sane bounds at sprite=" + index + "; maxCoordinate=" + largestCoordinate);

            meshes.Add(new
            {
                columns = (int)GetField(part, "Columns"),
                rows = (int)GetField(part, "Rows"),
                kind = Convert.ToInt32(GetField(part, "Kind")),
                depth = (float)GetField(part, "Depth"),
                vertices = mesh.vertices.Select(vertex => new { x = vertex.x, y = vertex.y }).ToArray()
            });
            geometryMeshes++;
        }

        Require(geometryMeshes == MantleCrabSpriteLayout.SpriteCount,
            "Geometry fixture did not include every production sprite; expected=" + MantleCrabSpriteLayout.SpriteCount + " actual=" + geometryMeshes);
        return meshes;
    }

    private static void WriteFixture(MantleCrabVisualPhenotype phenotype, List<object> meshes)
    {
        int tileSize = MantleCrabMeshBuilder.TileSize;
        int materialCount = MantleCrabMeshBuilder.MaterialCount;
        int atlasWidth = tileSize * materialCount;
        int atlasHeight = tileSize * 2;

        var fixture = new
        {
            tileSize,
            materialCount,
            atlasWidth,
            atlasHeight,
            motif = new[] { phenotype.MotifScale, phenotype.MotifSharpness, phenotype.MotifContrast, phenotype.MotifWarp },
            pattern = new[] { phenotype.Fragmentation, phenotype.StripePhase, phenotype.Curvature, phenotype.Asymmetry },
            material = new[] { phenotype.ChitinRoughness, phenotype.LegDetail, phenotype.PincerAccent, phenotype.Hue },
            eye = new[] { phenotype.EyeCore, phenotype.EyeWarmth, 0f, 0f },
            noise = new[] { phenotype.NoiseSeed.x, phenotype.NoiseSeed.y, phenotype.NoiseSeed.z, phenotype.NoiseSeed.w },
            meshes
        };

        string json = new JavaScriptSerializer { MaxJsonLength = 4000000 }.Serialize(fixture);
        File.WriteAllText(Path.Combine(output, "fixture.json"), json);
    }

    private static void WriteCpuAtlas(MantleCrabVisualPhenotype phenotype)
    {
        int tileSize = MantleCrabMeshBuilder.TileSize;
        int materialCount = MantleCrabMeshBuilder.MaterialCount;
        int width = tileSize * materialCount;
        int height = tileSize * 2;

        using BinaryWriter writer = new(File.Create(Path.Combine(output, "cpu-atlas.rgba")));
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            MantleCrabMaterial kind = (MantleCrabMaterial)(x / tileSize);
            Vector2 uv = new(
                (x % tileSize) / (float)(tileSize - 1),
                (y % tileSize) / (float)(tileSize - 1));
            MantleCrabMaterialBaker.Sample(phenotype, kind, uv, out Color pattern, out Color surface);
            Color color = y < tileSize ? pattern : surface;
            WriteByte(writer, color.r);
            WriteByte(writer, color.g);
            WriteByte(writer, color.b);
            WriteByte(writer, color.a);
            atlasPixels++;
        }

        Require(atlasPixels == width * height,
            "CPU atlas pixel count mismatch; expected=" + (width * height) + " actual=" + atlasPixels);
    }

    private static void WriteByte(BinaryWriter writer, float value)
    {
        value = Math.Max(0f, Math.Min(1f, value));
        writer.Write((byte)Math.Round(value * 255f));
    }
}
