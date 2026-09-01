using DryCycle.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace DryCycle.Weather.Fog;

/// <summary>
/// Shared 3D procedural noise volume. It is generated once on the GPU from the custom
/// compute bundle and reused by every room/camera. The composite shader falls back to
/// Rain World's built-in 2D noise textures when 3D textures/compute are unavailable.
/// </summary>
internal static class DryCycleFogVolumeNoise
{
    private const int Size = 64;

    internal static RenderTexture Texture { get; private set; }
    internal static bool IsAvailable => Texture != null && Texture.IsCreated();

    internal static void Ensure()
    {
        if (IsAvailable ||
            !SystemInfo.supportsComputeShaders ||
            !SystemInfo.supports3DTextures ||
            DryCycleShaderAssets.FogNoiseCompute == null)
        {
            return;
        }

        ComputeShader generator = null;
        try
        {
            generator = UnityEngine.Object.Instantiate(DryCycleShaderAssets.FogNoiseCompute);
            int kernel = generator.FindKernel("GenerateFogNoise");

            RenderTexture volume = new(Size, Size, 0, RenderTextureFormat.ARGBHalf)
            {
                name = "DryCycleFogNoise3D",
                dimension = TextureDimension.Tex3D,
                volumeDepth = Size,
                enableRandomWrite = true,
                filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Repeat,
                useMipMap = false,
                autoGenerateMips = false
            };
            volume.Create();

            generator.SetInt("_VolumeSize", Size);
            generator.SetTexture(kernel, "_NoiseVolume", volume);
            int groups = Mathf.CeilToInt(Size / 4f);
            generator.Dispatch(kernel, groups, groups, groups);

            Texture = volume;
        }
        catch (System.Exception ex)
        {
            Plugin.Logger?.LogWarning(
                "DryCycle could not generate the 3D fog noise volume; " +
                "the composite shader will use Rain World's 2D noise fallback.");
            Plugin.Logger?.LogWarning(ex);
            Release();
        }
        finally
        {
            if (generator != null)
            {
                UnityEngine.Object.Destroy(generator);
            }
        }
    }

    internal static void Release()
    {
        if (Texture == null)
        {
            return;
        }

        Texture.Release();
        UnityEngine.Object.Destroy(Texture);
        Texture = null;
    }
}
