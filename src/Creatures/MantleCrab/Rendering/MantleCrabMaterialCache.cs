using System;
using System.Collections.Generic;
using DryCycle.Rendering;
using UnityEngine;

namespace DryCycle.Creatures.MantleCrab.Rendering;

/// <summary>Atlas lifetime follows SpriteLeasers, including off-camera cleanup and multiple cameras.</summary>
internal sealed class MantleCrabMaterialCache
{
    private static readonly Dictionary<RoomCamera.SpriteLeaser, MantleCrabMaterialCache> Leases = new();
    private static bool enabled, computeFailed;
    private static int serial;
    internal readonly string AtlasName;
    private Texture texture;
    private int references;
    internal bool Released => texture == null;

    internal static void Enable()
    {
        if (enabled) return;
        enabled = true;
        On.RoomCamera.SpriteLeaser.CleanSpritesAndRemove += Clean;
    }

    internal MantleCrabMaterialCache(MantleCrabVisualPhenotype phenotype)
    {
        AtlasName = "DryCycleMantleCrab_" + (++serial);
        try
        {
            if (!computeFailed && DryCycleShaderAssets.MantleCrabBake != null &&
                SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGB32))
            {
                try { texture = BakeCompute(phenotype); }
                catch (Exception ex)
                {
                    computeFailed = true;
                    Plugin.Logger?.LogWarning("MantleCrab compute bake failed; using CPU baker: " + ex.Message);
                }
            }
            texture ??= MantleCrabMaterialBaker.BakeCPU(phenotype);
            texture.name = AtlasName;
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
            // Generated texture ownership stays here, rather than Resources.UnloadAsset.
            Futile.atlasManager.LoadAtlasFromTexture(AtlasName, texture, false);
        }
        catch { ReleaseTexture(); throw; }
    }

    internal void Attach(RoomCamera.SpriteLeaser leaser)
    {
        Enable();
        if (Leases.TryGetValue(leaser, out MantleCrabMaterialCache old))
        {
            if (old == this) return;
            old.Release();
        }
        Leases[leaser] = this; references++;
    }

    private static void Clean(On.RoomCamera.SpriteLeaser.orig_CleanSpritesAndRemove orig, RoomCamera.SpriteLeaser self)
    {
        orig(self);
        if (Leases.TryGetValue(self, out MantleCrabMaterialCache entry))
        { Leases.Remove(self); entry.Release(); }
    }

    private void Release()
    {
        if (--references > 0) return;
        Futile.atlasManager.UnloadAtlas(AtlasName);
        ReleaseTexture();
    }

    private void ReleaseTexture()
    {
        if (texture is RenderTexture rt) rt.Release();
        if (texture != null) UnityEngine.Object.Destroy(texture);
        texture = null;
    }

    private static RenderTexture BakeCompute(MantleCrabVisualPhenotype p)
    {
        RenderTexture target = new(896, 256, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
        { enableRandomWrite = true };
        try
        {
            if (!target.Create()) throw new InvalidOperationException("Atlas RenderTexture.Create failed");
            ComputeShader shader = DryCycleShaderAssets.MantleCrabBake;
            int kernel = shader.FindKernel("Bake");
            shader.SetVector("_Motif", new Vector4(p.MotifScale, p.MotifSharpness, p.MotifContrast, p.MotifWarp));
            shader.SetVector("_Pattern", new Vector4(p.Fragmentation, p.StripePhase, p.Curvature, p.Asymmetry));
            shader.SetVector("_Material", new Vector4(p.ChitinRoughness, p.LegDetail, p.PincerAccent, p.Hue));
            shader.SetVector("_Eye", new Vector4(p.EyeCore, p.EyeWarmth, 0, 0));
            shader.SetVector("_NoiseSeed", p.NoiseSeed);
            shader.SetTexture(kernel, "_Atlas", target);
            shader.Dispatch(kernel, 112, 16, 1);
            return target;
        }
        catch { target.Release(); UnityEngine.Object.Destroy(target); throw; }
    }
}
