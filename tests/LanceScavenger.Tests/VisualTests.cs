using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Web.Script.Serialization;
using DryCycle.Creatures.LanceScavenger;
using DryCycle.Items.ScavengerLance;
using IteratorFramework.Tests;
using MoreSlugcats;
using RWCustom;
using UnityEngine;
using Color = UnityEngine.Color;
using Graphics = System.Drawing.Graphics;
using static LanceScavenger.Tests.Program;

namespace LanceScavenger.Tests;

internal static class VisualTests
{
    private static readonly Dictionary<string, Rectangle> SourceFrames = new();
    private static string AtlasPath => Path.Combine(GameDirectory, "RainWorld_Data/StreamingAssets/mods/Ancient Site/atlas/LanceScavenger/LanceScavengerMask");
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
            var leaser = RuntimeScene.Raw<RoomCamera.SpriteLeaser>();
            leaser.sprites = new FSprite[3];
            var mask = new VultureMaskGraphics(null, VultureMask.MaskType.NORMAL, 0, LanceScavengerAssets.Prefix)
            { ColorA = new HSLColor(0.12f,0.18f,0.79f), overrideDrawVector = Vector2.zero, overrideRotationVector = Vector2.up };
            mask.InitiateSprites(leaser, camera);
            mask.ApplyPalette(leaser, camera, camera.currentPalette);
            string directory = Path.Combine(Environment.CurrentDirectory, "artifacts/lance-scavenger");
            Directory.CreateDirectory(directory);
            using (var sheet = new Bitmap(1200, 570, PixelFormat.Format32bppArgb))
            using (Graphics canvas = Graphics.FromImage(sheet))
            using (var atlas = new Bitmap(AtlasPath + ".png"))
            using (var font = new Font("Segoe UI", 12f))
            {
                canvas.Clear(System.Drawing.Color.FromArgb(29,34,42));
                canvas.InterpolationMode = InterpolationMode.NearestNeighbor;
                canvas.PixelOffsetMode = PixelOffsetMode.Half;
                canvas.DrawString("LANCE SCAVENGER / original atlas + vanilla VultureMaskGraphics (3x)", font, Brushes.White, 22, 16);
                for (int side = 0; side < 2; side++)
                    for (int frame = 0; frame < 9; frame++)
                    {
                        mask.overrideAnchorVector = Custom.DegToVec(frame * 22.5f * (side == 0 ? 1 : -1));
                        mask.DrawSprites(leaser, camera, 1f, Vector2.zero);
                        Check(leaser.sprites[0].element.name == LanceScavengerAssets.Prefix + frame, "Vanilla mask selects existing direction " + frame);
                        Check(frame == 0 || Math.Sign(leaser.sprites[0].scaleX) == (side == 0 ? 1 : -1), "Vanilla mirroring remains correct");
                        var origin = new PointF(74 + frame * 130, 128 + side * 166);
                        foreach (int layer in new[] { 2,1,0 }) DrawSprite(canvas, leaser.sprites[layer], atlas, origin, 3f);
                        canvas.DrawString(frame + (side == 0 ? " R" : " L"), font, Brushes.LightGray, origin.X - 16, origin.Y + 62);
                    }
                ScavengerLance lance = IntegrationTests.Weapon(scene);
                var weaponLeaser = RuntimeScene.Raw<RoomCamera.SpriteLeaser>();
                lance.InitiateSprites(weaponLeaser, camera);
                lance.DrawSprites(weaponLeaser, camera, 1f, Vector2.zero);
                foreach (FSprite sprite in weaponLeaser.sprites)
                {
                    if (sprite is TriangleMesh mesh)
                    {
                        foreach (Vector2 vertex in mesh.vertices) Check(Finite(vertex), "Lance mesh is finite");
                        DrawMesh(canvas, mesh, new PointF(70-147*4,455+90*4), 4f);
                    }
                    else DrawSprite(canvas, sprite, null, new PointF(70-147*4,455+90*4),4f);
                }
                var sash = TriangleMesh.MakeLongMesh(6,false,false);
                LanceScavengerGraphics.DrawSash(sash, new Vector2(0,18), new Vector2(18,-14), Vector2.right, Vector2.zero, 3.2f);
                sash.color = new Color(0.59f,0.43f,0.18f);
                DrawMesh(canvas,sash,new PointF(610,450),3f);
                var ribbon = new LanceAdornment(5,4.2f);
                ribbon.Reset(Vector2.zero);
                for (int step = 0; step < 90; step++) ribbon.Update(Vector2.zero, new Vector2(18,0), 0.9f);
                var ribbonMesh = TriangleMesh.MakeLongMesh(4,false,false);
                ribbon.DrawRibbon(ribbonMesh,1f,Vector2.zero,2.3f);
                ribbonMesh.color = sash.color;
                DrawMesh(canvas,ribbonMesh,new PointF(850,430),4f);
                for (int i = 1; i < ribbon.Positions.Length; i++)
                    Check(Finite(ribbon.Positions[i]) && Vector2.Distance(ribbon.Positions[i],ribbon.Positions[i-1]) <= 4.21f,
                        "Short ribbon stays bounded during a sustained charge");
                canvas.DrawString("80px independent lance (4x)",font,Brushes.LightGray,28,515);
                canvas.DrawString("Runtime sash mesh",font,Brushes.LightGray,540,515);
                canvas.DrawString("Short ribbon in motion",font,Brushes.LightGray,780,515);
                canvas.DrawString("CPU component preview. Creature pose, camera occlusion and in-game lighting require live validation.",font,Brushes.LightGray,22,548);
                sheet.Save(Path.Combine(directory,"lance-components.png"),ImageFormat.Png);
            }
            leaser.CleanSpritesAndRemove();
        }
        finally { ScavengerLanceHooks.Disable(); Futile.atlasManager = oldAtlas; FShader.defaultShader = oldShader; }
    }

    private static void PrepareFutile()
    {
        Futile.atlasManager = new FAtlasManager(); FShader.defaultShader = RuntimeScene.Raw<FShader>();
        var atlas = RuntimeScene.Raw<FAtlas>(); RuntimeScene.Set(atlas,"_name","LanceTest");
        var elements = new Dictionary<string,FAtlasElement>();
        foreach (string name in new[] { "Futile_White","pixel","Circle20" })
        {
            float size = name == "Circle20" ? 20f : 1f;
            elements[name] = Element(name,atlas,new Rect(0,0,size,size),new Vector2(size,size));
        }
        var document = new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(AtlasPath + ".txt"));
        foreach (KeyValuePair<string,object> item in (Dictionary<string,object>)document["frames"])
        {
            var data = (Dictionary<string,object>)item.Value;
            var frame = (Dictionary<string,object>)data["frame"];
            var size = (Dictionary<string,object>)data["sourceSize"];
            var rect = (Dictionary<string,object>)data["spriteSourceSize"];
            string name = Path.GetFileNameWithoutExtension(item.Key);
            SourceFrames[name] = new Rectangle((int)frame["x"],(int)frame["y"],(int)frame["w"],(int)frame["h"]);
            elements[name] = Element(name,atlas,new Rect((int)rect["x"],(int)rect["y"],(int)rect["w"],(int)rect["h"]),new Vector2((int)size["w"],(int)size["h"]));
        }
        RuntimeScene.Set(Futile.atlasManager,"_atlases",new List<FAtlas> { atlas });
        RuntimeScene.Set(Futile.atlasManager,"_allElementsByName",elements);
    }
    private static FAtlasElement Element(string name,FAtlas atlas,Rect rect,Vector2 size) => new()
    { name=name,atlas=atlas,sourceRect=rect,sourceSize=size,uvRect=new Rect(0,0,1,1),
      uvTopLeft=Vector2.up,uvTopRight=Vector2.one,uvBottomRight=Vector2.right,uvBottomLeft=Vector2.zero };
    private static RoomCamera Camera(Room room)
    {
        var camera = RuntimeScene.Raw<RoomCamera>(); camera.game = room.game;
        RuntimeScene.Set(camera,"<room>k__BackingField",room);
        RuntimeScene.Set(camera,"SpriteLayers",new[] { new FContainer() });
        RuntimeScene.Set(camera,"SpriteLayerIndex",new Dictionary<string,int> { ["Midground"]=0,["Items"]=0 });
        camera.currentPalette = new RoomPalette { blackColor = Color.black };
        return camera;
    }
    private static bool Finite(Vector2 v) => !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.x) && !float.IsInfinity(v.y);
    private static System.Drawing.Color Ink(Color color) => System.Drawing.Color.FromArgb(255,
        (int)(Mathf.Clamp01(color.r)*255),(int)(Mathf.Clamp01(color.g)*255),(int)(Mathf.Clamp01(color.b)*255));
    private static void DrawMesh(Graphics canvas,TriangleMesh mesh,PointF origin,float scale)
    {
        using var ink = new SolidBrush(Ink(mesh.color));
        foreach (TriangleMesh.Triangle tri in mesh.triangles)
            canvas.FillPolygon(ink,new[] { Screen(mesh.vertices[tri.a]),Screen(mesh.vertices[tri.b]),Screen(mesh.vertices[tri.c]) });
        PointF Screen(Vector2 p) => new(origin.X+p.x*scale,origin.Y-p.y*scale);
    }
    private static void DrawSprite(Graphics canvas,FSprite sprite,Bitmap atlas,PointF origin,float scale)
    {
        sprite.UpdateLocalVertices();
        PointF[] quad = new PointF[4];
        for (int i = 0; i < 4; i++)
        {
            Vector2 point = sprite._localVertices[i];
            point = Custom.RotateAroundOrigo(new Vector2(point.x*sprite.scaleX,point.y*sprite.scaleY),-sprite.rotation);
            quad[i] = new PointF(origin.X+(point.x+sprite.x)*scale,origin.Y-(point.y+sprite.y)*scale);
        }
        if (atlas != null && SourceFrames.TryGetValue(sprite.element.name,out Rectangle source))
        {
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(new ColorMatrix(new[] { new[] { sprite.color.r,0f,0f,0f,0f },new[] { 0f,sprite.color.g,0f,0f,0f },
                new[] { 0f,0f,sprite.color.b,0f,0f },new[] { 0f,0f,0f,1f,0f },new[] { 0f,0f,0f,0f,1f } }));
            canvas.DrawImage(atlas,new[] { quad[0],quad[1],quad[3] },source,GraphicsUnit.Pixel,attributes);
        }
        else { using var ink = new SolidBrush(Ink(sprite.color)); canvas.FillPolygon(ink,quad); }
    }
}
