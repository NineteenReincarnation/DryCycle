using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using DryCycle.Creatures.LanceScavenger;
using DryCycle.Items.ScavengerLance;
using IteratorFramework.Tests;
using RWCustom;
using UnityEngine;
using Color = UnityEngine.Color;
using Graphics = System.Drawing.Graphics;
using static LanceScavenger.Tests.Program;

namespace LanceScavenger.Tests;

internal static class VisualTests
{
    internal static void MasksAndMeshes()
    {
        var oldAtlas = Futile.atlasManager;
        var oldShader = FShader.defaultShader;
        ScavengerLanceHooks.Enable();
        try
        {
            PrepareFutile();
            RuntimeScene scene = IntegrationTests.Scene();
            RoomCamera camera = Camera(scene.Room);
            string directory = Path.Combine(Environment.CurrentDirectory, "artifacts/lance-scavenger");
            Directory.CreateDirectory(directory);

            using var sheet = new Bitmap(1000, 420, PixelFormat.Format32bppArgb);
            using Graphics canvas = Graphics.FromImage(sheet);
            using var font = new Font("Segoe UI", 12f);
            canvas.Clear(System.Drawing.Color.FromArgb(29, 34, 42));
            canvas.DrawString("LANCE SCAVENGER / equipment + lance component preview", font, Brushes.White, 22, 16);

            ScavengerLance lance = IntegrationTests.Weapon(scene);
            var weaponLeaser = RuntimeScene.Raw<RoomCamera.SpriteLeaser>();
            lance.InitiateSprites(weaponLeaser, camera);
            lance.DrawSprites(weaponLeaser, camera, 1f, Vector2.zero);
            foreach (FSprite sprite in weaponLeaser.sprites)
            {
                if (sprite is TriangleMesh mesh)
                {
                    foreach (Vector2 vertex in mesh.vertices) Check(Finite(vertex), "Lance mesh is finite");
                    DrawMesh(canvas, mesh, new PointF(95 - 147 * 4, 210 + 90 * 4), 4f);
                }
                else DrawSprite(canvas, sprite, new PointF(95 - 147 * 4, 210 + 90 * 4), 4f);
            }

            var sash = TriangleMesh.MakeLongMesh(6, false, false);
            LanceScavengerGraphics.DrawSash(sash, new Vector2(0, 18), new Vector2(18, -14), Vector2.right, Vector2.zero, 3.2f);
            sash.color = new Color(0.59f, 0.43f, 0.18f);
            foreach (Vector2 vertex in sash.vertices) Check(Finite(vertex), "Sash mesh is finite");
            DrawMesh(canvas, sash, new PointF(540, 205), 4f);

            var ribbon = new LanceAdornment(5, 4.2f);
            ribbon.Reset(Vector2.zero);
            for (int step = 0; step < 90; step++) ribbon.Update(Vector2.zero, new Vector2(18, 0), 0.9f);
            var ribbonMesh = TriangleMesh.MakeLongMesh(4, false, false);
            ribbon.DrawRibbon(ribbonMesh, 1f, Vector2.zero, 2.3f);
            ribbonMesh.color = sash.color;
            DrawMesh(canvas, ribbonMesh, new PointF(790, 195), 5f);
            for (int i = 1; i < ribbon.Positions.Length; i++)
                Check(Finite(ribbon.Positions[i]) && Vector2.Distance(ribbon.Positions[i], ribbon.Positions[i - 1]) <= 4.21f,
                    "Short ribbon stays bounded during a sustained charge");

            canvas.DrawString("80px independent lance", font, Brushes.LightGray, 30, 360);
            canvas.DrawString("Runtime sash mesh", font, Brushes.LightGray, 470, 360);
            canvas.DrawString("Short ribbon in motion", font, Brushes.LightGray, 720, 360);
            canvas.DrawString("Mask rendering intentionally removed; creature pose and camera occlusion still require live validation.",
                font, Brushes.LightGray, 22, 392);
            sheet.Save(Path.Combine(directory, "lance-components.png"), ImageFormat.Png);
            weaponLeaser.CleanSpritesAndRemove();
        }
        finally
        {
            ScavengerLanceHooks.Disable();
            Futile.atlasManager = oldAtlas;
            FShader.defaultShader = oldShader;
        }
    }

    private static void PrepareFutile()
    {
        Futile.atlasManager = new FAtlasManager();
        FShader.defaultShader = RuntimeScene.Raw<FShader>();
        var atlas = RuntimeScene.Raw<FAtlas>();
        RuntimeScene.Set(atlas, "_name", "LanceTest");
        var elements = new Dictionary<string, FAtlasElement>();
        foreach (string name in new[] { "Futile_White", "pixel", "Circle20" })
        {
            float size = name == "Circle20" ? 20f : 1f;
            elements[name] = Element(name, atlas, new Rect(0, 0, size, size), new Vector2(size, size));
        }
        RuntimeScene.Set(Futile.atlasManager, "_atlases", new List<FAtlas> { atlas });
        RuntimeScene.Set(Futile.atlasManager, "_allElementsByName", elements);
    }

    private static FAtlasElement Element(string name, FAtlas atlas, Rect rect, Vector2 size) => new()
    {
        name = name,
        atlas = atlas,
        sourceRect = rect,
        sourceSize = size,
        uvRect = new Rect(0, 0, 1, 1),
        uvTopLeft = Vector2.up,
        uvTopRight = Vector2.one,
        uvBottomRight = Vector2.right,
        uvBottomLeft = Vector2.zero
    };

    private static RoomCamera Camera(Room room)
    {
        var camera = RuntimeScene.Raw<RoomCamera>();
        camera.game = room.game;
        RuntimeScene.Set(camera, "<room>k__BackingField", room);
        RuntimeScene.Set(camera, "SpriteLayers", new[] { new FContainer() });
        RuntimeScene.Set(camera, "SpriteLayerIndex", new Dictionary<string, int> { ["Midground"] = 0, ["Items"] = 0 });
        camera.currentPalette = new RoomPalette { blackColor = Color.black };
        return camera;
    }

    private static bool Finite(Vector2 v) =>
        !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.x) && !float.IsInfinity(v.y);

    private static System.Drawing.Color Ink(Color color) => System.Drawing.Color.FromArgb(255,
        (int)(Mathf.Clamp01(color.r) * 255), (int)(Mathf.Clamp01(color.g) * 255), (int)(Mathf.Clamp01(color.b) * 255));

    private static void DrawMesh(Graphics canvas, TriangleMesh mesh, PointF origin, float scale)
    {
        using var ink = new SolidBrush(Ink(mesh.color));
        foreach (TriangleMesh.Triangle tri in mesh.triangles)
            canvas.FillPolygon(ink, new[] { Screen(mesh.vertices[tri.a]), Screen(mesh.vertices[tri.b]), Screen(mesh.vertices[tri.c]) });
        PointF Screen(Vector2 p) => new(origin.X + p.x * scale, origin.Y - p.y * scale);
    }

    private static void DrawSprite(Graphics canvas, FSprite sprite, PointF origin, float scale)
    {
        sprite.UpdateLocalVertices();
        PointF[] quad = new PointF[4];
        for (int i = 0; i < 4; i++)
        {
            Vector2 point = sprite._localVertices[i];
            point = Custom.RotateAroundOrigo(new Vector2(point.x * sprite.scaleX, point.y * sprite.scaleY), -sprite.rotation);
            quad[i] = new PointF(origin.X + (point.x + sprite.x) * scale, origin.Y - (point.y + sprite.y) * scale);
        }
        using var ink = new SolidBrush(Ink(sprite.color));
        canvas.FillPolygon(ink, quad);
    }
}
