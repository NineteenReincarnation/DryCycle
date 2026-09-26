using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using UnityEngine;

public static partial class MapRenderIsolationTests
{
    private static void ExerciseCartographySprites()
    {
        string fixture=Argument("-cartographySpriteFixture")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),"Cornifer");
        Type assets=Core("Map.Cartography.CartographyAssets");
        assets.GetMethod("LoadDirectory",Flags).Invoke(null,new object[]{fixture});
        string atlas=Path.Combine(fixture,"Assets/Atlases/uisprites");
        var json=(Dictionary<string,object>)Core("Map.Cartography.CartographyJson").GetMethod("Parse",Flags).Invoke(null,new object[]{File.ReadAllText(atlas+".txt")});
        var frames=(Dictionary<string,object>)json["frames"];
        var texture=new Texture2D(2,2,TextureFormat.RGBA32,false);
        Check(texture.LoadImage(File.ReadAllBytes(atlas+".png")),"Real Cornifer/game UI atlas decodes in Unity.");
        Assembly gameAssembly=Assembly.LoadFrom(Path.Combine(game,"RainWorld_Data/Managed/Assembly-CSharp.dll"));
        object fAtlas=FormatterServices.GetUninitializedObject(gameAssembly.GetType("FAtlas",true));
        Set(fAtlas,"_texture",texture);
        var read=Core("Map.Cartography.CartographyGameAssets").GetMethod("ReadSprite",Flags);
        string[] names={"karma0","karma4","Symbol_DangleFruit","Symbol_Mushroom","Kill_VultureGrub","ShelterMarker"};
        var elements=new List<object>();var reference=new List<uint[]>();
        foreach(string name in names)
        {
            var frame=(Dictionary<string,object>)((Dictionary<string,object>)frames[name+".png"])["frame"];
            float x=Convert.ToSingle(frame["x"]),y=Convert.ToSingle(frame["y"]),w=Convert.ToSingle(frame["w"]),h=Convert.ToSingle(frame["h"]);
            object element=FormatterServices.GetUninitializedObject(gameAssembly.GetType("FAtlasElement",true));
            Set(element,"name",name);Set(element,"atlas",fAtlas);
            Set(element,"uvRect",new Rect(0,0,1,1)); // A stale rect must never override FSprite's actual corners.
            Set(element,"uvTopLeft",new Vector2(x/texture.width,1-y/texture.height));
            Set(element,"uvTopRight",new Vector2((x+w)/texture.width,1-y/texture.height));
            Set(element,"uvBottomLeft",new Vector2(x/texture.width,1-(y+h)/texture.height));
            Set(element,"uvBottomRight",new Vector2((x+w)/texture.width,1-(y+h)/texture.height));
            object expected=assets.GetMethod("Sprite",Flags).Invoke(null,new object[]{name});
            var pixels=(uint[])Get(expected,"Pixels");reference.Add(pixels);elements.Add(element);
            object actual=read.Invoke(null,new object[]{element,null});
            Check(pixels.SequenceEqual((uint[])Get(actual,"Pixels")),name+" reads the exact atlas frame and correct top-down orientation.");
        }
        texture.Apply(false,true);
        for(int n=0;n<names.Length;n++)
        {
            object actual=read.Invoke(null,new object[]{elements[n],null});var pixels=(uint[])Get(actual,"Pixels");
            Check(reference[n].Length==pixels.Length && Enumerable.Range(0,pixels.Length).All(i=>
                reference[n][i]>>24==0 ? pixels[i]>>24==0 : reference[n][i]==pixels[i]),names[n]+" also reads correctly through the actual GPU for non-readable textures.");
        }
        UnityEngine.Object.Destroy(texture);

        object document=Activator.CreateInstance(Core("Map.Cartography.CartographyDocument"),true);
        Set(document,"Identity","sprite-gpu-test");Set(document,"Region","TEST");Set(document,"Padding",12);Set(document,"ExportScale",4f);
        object layer=Activator.CreateInstance(Core("Map.Cartography.CartographyLayer"),true);Set(layer,"Id","notes");
        ((IList)Get(document,"Layers")).Add(layer);
        object gate=Activator.CreateInstance(Core("Map.Cartography.CartographyItem"),true);
        Set(gate,"Kind",Enum.Parse(Core("Map.Cartography.CartographyItemKind"),"Marker"));
        Set(gate,"Marker",Enum.Parse(Core("Map.Cartography.CartographyMarker"),"Gate"));Set(gate,"Size",42f);
        Set(Get(gate,"Appearance"),"LeftKarma","1");Set(Get(gate,"Appearance"),"RightKarma","R");
        ((IList)Get(document,"Items")).Add(gate);
        object source=Activator.CreateInstance(Core("Map.Cartography.CartographySource"),true);
        object scene=Core("Map.Cartography.CartographySceneBuilder").GetMethod("Build",Flags).Invoke(null,new object[]{document,source,null});
        string path=Path.Combine(output,"gate-sprites-and-mono-png.png");
        var format=Enum.Parse(Core("Map.Cartography.CartographyExportFormat"),"Png");
        var hash=Core("Map.Cartography.CartographyStorage").GetMethod("HashFile",Flags).Invoke(null,new object[]{path});
        Core("Map.Cartography.CartographyExporter").GetMethod("Export",Flags).Invoke(null,new[]{document,scene,path,format,hash,null});
        var decoded=new Texture2D(2,2,TextureFormat.RGBA32,false);
        Check(decoded.LoadImage(File.ReadAllBytes(path)) && decoded.width>300,"Production streaming PNG also encodes under game-version Unity/Mono and decodes through Unity's PNG loader.");
        UnityEngine.Object.Destroy(decoded);
    }
}
