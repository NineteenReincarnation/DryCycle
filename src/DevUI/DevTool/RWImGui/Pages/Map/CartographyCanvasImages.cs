using System;
using System.Collections.Generic;
using System.Linq;
using DryCycle.DevUI.DevTool.Map.Cartography;
using ImGuiNET;
using UnityEngine;
using Num=System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal static class CartographyCanvasImages
{
    private sealed class Entry { internal Texture2D Texture; internal readonly WorldMapTextureBridge Bridge=new(); internal long Used; internal bool Failed; }
    private static readonly object Gate=new();
    private static readonly Dictionary<CartographyRaster,Entry> Entries=new();
    private static long frame;
    internal static string Error {get;private set;}="";
    internal static void Draw(ImDrawListPtr draw,CartographyPrimitive shape,Num.Vector2 min,Num.Vector2 max,uint color)
    {
        lock(Gate)
        {
            if(!Entries.TryGetValue(shape.Raster,out Entry entry))Entries[shape.Raster]=entry=new Entry();
            entry.Used=frame;
            if(entry.Texture!=null&&!entry.Bridge.TryPresent(draw,entry.Texture,min,max,color,false))Error=entry.Bridge.Error;
            else if(entry.Texture==null)draw.AddRect(min,max,0x554A8DAB);
        }
    }
    internal static void UpdateMainThread()
    {
        lock(Gate)
        {
            frame++;int budget=12;
            foreach(var pair in Entries.ToArray())
            {
                Entry entry=pair.Value;
                if(frame-entry.Used>180)
                {entry.Bridge.Reset();if(entry.Texture!=null)UnityEngine.Object.Destroy(entry.Texture);Entries.Remove(pair.Key);continue;}
                if(entry.Texture!=null||entry.Failed||budget--<=0)continue;
                try
                {
                    CartographyRaster r=pair.Key;Color32[] pixels=new Color32[r.Pixels.Length];
                    for(int i=0;i<pixels.Length;i++){uint c=r.Pixels[i];pixels[i]=new Color32((byte)(c>>16),(byte)(c>>8),(byte)c,(byte)(c>>24));}
                    entry.Texture=new Texture2D(r.Width,r.Height,TextureFormat.RGBA32,false){filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp};
                    entry.Texture.SetPixels32(pixels);entry.Texture.Apply(false,true);entry.Bridge.Initialize(global::DryCycle.Plugin.Logger);
                }
                catch(Exception error){entry.Failed=true;Error=error.Message;global::DryCycle.Plugin.Logger?.LogError("Cartography texture creation failed: "+error);}
            }
        }
    }
}
