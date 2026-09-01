using System;
using System.IO;
using UnityEngine;

namespace DryCycle.Rendering;

/// <summary>
/// Owns DryCycle's custom Unity shader bundle. AssetBundle.LoadAsset is deliberately
/// deferred until RainWorld.LoadResources; loading Unity shader assets from BepInEx
/// OnEnable/Awake can hard-crash the player before Rain World has initialized its
/// rendering resources.
/// </summary>
internal static class DryCycleShaderAssets
{
    internal const string FogCompositeShaderKey = "DryCycleFogComposite";
    internal const string BundleRelativePath = "assets/drycycle/drycycleweather";

    private const string FogCompositeAssetPath =
        "assets/drycycle/shaders/drycyclefogcomposite.shader";
    private const string FogFluidAssetPath =
        "assets/drycycle/compute/drycyclefogfluid.compute";
    private const string FogNoiseAssetPath =
        "assets/drycycle/compute/drycyclefognoise.compute";

    private static AssetBundle _bundle;
    private static bool _enabled;
    private static bool _missingBundleLogged;

    internal static FShader FogComposite { get; private set; }
    internal static ComputeShader FogFluidCompute { get; private set; }
    internal static ComputeShader FogNoiseCompute { get; private set; }

    internal static bool HasFogComposite => FogComposite != null;
    internal static bool HasFluidCompute => FogFluidCompute != null;
    internal static bool HasNoiseCompute => FogNoiseCompute != null;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        On.RainWorld.LoadResources += RainWorld_LoadResources;
        _enabled = true;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.RainWorld.LoadResources -= RainWorld_LoadResources;
        _enabled = false;

        // Do not unload the AssetBundle here. FShader stores Unity shader objects
        // originating from the bundle; unloading those assets while a RoomCamera may
        // still own a render layer can invalidate materials during a Remix hot-toggle.
        // Rain World itself owns the lifetime for the remainder of the process.
    }

    private static void RainWorld_LoadResources(
        On.RainWorld.orig_LoadResources orig,
        RainWorld self)
    {
        orig(self);
        TryLoad(self);
    }

    private static void TryLoad(RainWorld rainWorld)
    {
        if (rainWorld == null || FogComposite != null)
        {
            return;
        }

        string path = AssetManager.ResolveFilePath(BundleRelativePath);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            if (!_missingBundleLogged)
            {
                _missingBundleLogged = true;
                Plugin.Logger?.LogWarning(
                    $"DryCycle weather AssetBundle not found at '{path}'. " +
                    "Fog will use the compatibility renderer until " +
                    $"'{BundleRelativePath}' is built and installed. " +
                    $"Runtime Unity version: {Application.unityVersion}.");
            }
            return;
        }

        try
        {
            if (_bundle == null)
            {
                _bundle = AssetBundle.LoadFromFile(path);
            }

            if (_bundle == null)
            {
                Plugin.Logger?.LogError(
                    $"DryCycle failed to load weather AssetBundle '{path}'. " +
                    $"Runtime Unity version: {Application.unityVersion}.");
                return;
            }

            Shader fogShader = _bundle.LoadAsset<Shader>(FogCompositeAssetPath);
            if (fogShader == null)
            {
                Plugin.Logger?.LogError(
                    $"DryCycle weather bundle is missing shader '{FogCompositeAssetPath}'.");
                return;
            }

            FogComposite = FShader.CreateShader(FogCompositeShaderKey, fogShader);
            rainWorld.Shaders[FogCompositeShaderKey] = FogComposite;

            FogFluidCompute = _bundle.LoadAsset<ComputeShader>(FogFluidAssetPath);
            FogNoiseCompute = _bundle.LoadAsset<ComputeShader>(FogNoiseAssetPath);

            Plugin.Logger?.LogInfo(
                "DryCycle weather rendering assets loaded: " +
                $"FogComposite=yes, FluidCompute={(FogFluidCompute != null ? "yes" : "no")}, " +
                $"NoiseCompute={(FogNoiseCompute != null ? "yes" : "no")}, " +
                $"Unity={Application.unityVersion}.");
        }
        catch (Exception ex)
        {
            FogComposite = null;
            FogFluidCompute = null;
            FogNoiseCompute = null;
            Plugin.Logger?.LogError(
                "DryCycle failed to initialize custom weather shaders. " +
                "The compatibility fog renderer will remain available.");
            Plugin.Logger?.LogError(ex);
        }
    }
}
