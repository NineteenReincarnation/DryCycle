using System;
using System.Linq;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Map.Cartography;

// The only Unity-dependent asset adapter. Atlas readback is performed on the game thread once.
internal static class CartographyGameAssets
{
    internal static void Load(CartographySource source)
    {
        string[] standard={"ShelterMarker","ChieftainA","GhostSymbol","Symbol_Pearl","Sandbox_Unlock","Kill_Slugcat","karma0","karma1","karma2","karma3","karma4","karma5","karma6","karma7","karma8","karma9","karma9-9"};
        foreach(string name in standard.Concat(source.Decorations.Select(i=>i.Appearance.Icon)).Where(n=>n.Length>0).Distinct())
        {
            if(CartographyAssets.Sprite(name)!=null)continue;
            if(!Futile.atlasManager.DoesContainElementWithName(name))continue;
            FAtlasElement element=Futile.atlasManager.GetElementWithName(name);Texture texture=element.atlas.texture;
            Rect uv=element.uvRect;int x=Mathf.RoundToInt(uv.x*texture.width),y=Mathf.RoundToInt(uv.y*texture.height),w=Math.Max(1,Mathf.RoundToInt(uv.width*texture.width)),h=Math.Max(1,Mathf.RoundToInt(uv.height*texture.height));
            RenderTexture previous=RenderTexture.active;RenderTexture target=null;Texture2D copy=null;
            try
            {
                target=RenderTexture.GetTemporary(texture.width,texture.height,0,RenderTextureFormat.ARGB32);Graphics.Blit(texture,target);RenderTexture.active=target;
                copy=new Texture2D(w,h,TextureFormat.RGBA32,false);copy.ReadPixels(new Rect(x,y,w,h),0,0);copy.Apply();Color32[] colors=copy.GetPixels32();uint[] pixels=new uint[colors.Length];
                for(int row=0;row<h;row++)for(int col=0;col<w;col++){Color32 c=colors[(h-row-1)*w+col];pixels[row*w+col]=(uint)c.a<<24|(uint)c.r<<16|(uint)c.g<<8|c.b;}
                CartographyAssets.Register(name,new CartographyRaster(w,h,pixels));
            }
            finally{RenderTexture.active=previous;if(copy!=null)UnityEngine.Object.Destroy(copy);if(target!=null)RenderTexture.ReleaseTemporary(target);}
        }
    }
}
