using System;
using System.IO;
using UnityEngine;

namespace DryCycle.Rendering;

/// <summary>
/// Owns DryCycle's custom Unity weather shader bundle. AssetBundle.LoadAsset is
/// deliberately deferred until RainWorld.LoadResources; loading Unity shader assets
/// from BepInEx OnEnable/Awake can hard-crash the player before Rain World has
/// initialized its rendering resources.
/// </summary>
internal static class DryCycleShaderAssets
{
    internal const string FogCompositeShaderKey = "DryCycleFogComposite";
    internal const string HeatWaveAtmosphereShaderKey = "DryCycleHeatWaveAtmosphere";
    internal const string FoehnAtmosphereShaderKey = "DryCycleFoehnAtmosphere";
    internal const string BundleRelativePath = "assets/drycycle/drycycleweather";
    internal const string BundleVersionRelativePath =
        "assets/drycycle/drycycleweather.version.txt";

    private const string FogCompositeAssetPath =
        "assets/drycycle/shaders/drycyclefogcomposite.shader";
    private const string FogFluidAssetPath =
        "assets/drycycle/compute/drycyclefogfluid.compute";
    private const string FogNoiseAssetPath =
        "assets/drycycle/compute/drycyclefognoise.compute";
    private const string HeatWaveAtmosphereAssetPath =
        "assets/drycycle/shaders/drycycleheatwaveatmosphere.shader";
    private const string FoehnAtmosphereAssetPath =
        "assets/drycycle/shaders/drycyclefoehnatmosphere.shader";

    private static AssetBundle _bundle;
    private static bool _enabled;
    private static bool _missingBundleLogged;

    internal static FShader FogComposite { get; private set; }
    internal static ComputeShader FogFluidCompute { get; private set; }
    internal static ComputeShader FogNoiseCompute { get; private set; }
    internal static FShader HeatWaveAtmosphere { get; private set; }
    internal static FShader FoehnAtmosphere { get; private set; }

    internal static bool HasFogComposite => FogComposite != null;
    internal static bool HasFluidCompute => FogFluidCompute != null;
    internal static bool HasNoiseCompute => FogNoiseCompute != null;
    internal static bool HasHeatWaveAtmosphere => HeatWaveAtmosphere != null;
    internal static bool HasFoehnAtmosphere => FoehnAtmosphere != null;

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

    internal static void EnsureLoaded(RainWorld rainWorld)
    {
        TryLoad(rainWorld);
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
        if (rainWorld == null || _bundle != null)
        {
            return;
        }

        string path = ResolveWeatherAssetPath(BundleRelativePath);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            if (!_missingBundleLogged)
            {
                _missingBundleLogged = true;
                Plugin.Logger?.LogWarning(
                    $"DryCycle weather AssetBundle not found at '{path}'. " +
                    "Fog will use the compatibility renderer. HeatWave will retain " +
                    "Rain World's LevelHeat core, and Foehn can retain its particle " +
                    "carrier, but custom atmosphere passes remain unavailable until " +
                    $"'{BundleRelativePath}' is built and installed. " +
                    $"Runtime Unity version: {Application.unityVersion}.");
            }
            return;
        }

        LogEditorPlayerVersionRelationship();

        try
        {
            _bundle = AssetBundle.LoadFromFile(path);
            if (_bundle == null)
            {
                Plugin.Logger?.LogError(
                    $"DryCycle failed to load weather AssetBundle '{path}'. " +
                    $"Runtime Unity version: {Application.unityVersion}.");
                return;
            }

            LoadFogAssets(rainWorld);
            LoadHeatWaveAssets(rainWorld);
            LoadFoehnAssets(rainWorld);

            if (!SystemInfo.supportsComputeShaders)
            {
                Plugin.Logger?.LogWarning(
                    "This graphics device reports no compute-shader support. DryCycle " +
                    "fog will use its non-fluid fallback. HeatWave and Foehn do not " +
                    "depend on compute shaders.");
            }

            Plugin.Logger?.LogInfo(
                "DryCycle weather rendering assets loaded: " +
                $"FogComposite={(FogComposite != null ? "yes" : "no")}, " +
                $"FogFluid={(FogFluidCompute != null ? "yes" : "no")}, " +
                $"FogNoise={(FogNoiseCompute != null ? "yes" : "no")}, " +
                $"HeatWaveAtmosphere={(HeatWaveAtmosphere != null ? "yes" : "no")}, " +
                $"FoehnAtmosphere={(FoehnAtmosphere != null ? "yes" : "no")}, " +
                $"ComputeSupported={SystemInfo.supportsComputeShaders}, " +
                $"Unity={Application.unityVersion}, GPU='{SystemInfo.graphicsDeviceName}'.");
        }
        catch (Exception ex)
        {
            FogComposite = null;
            FogFluidCompute = null;
            FogNoiseCompute = null;
            HeatWaveAtmosphere = null;
            FoehnAtmosphere = null;
            Plugin.Logger?.LogError(
                "DryCycle failed to initialize custom weather shaders. " +
                "Compatibility renderers will remain available where implemented.");
            Plugin.Logger?.LogError(ex);
        }
    }

    private static void LoadFogAssets(RainWorld rainWorld)
    {
        Shader fogShader = _bundle.LoadAsset<Shader>(FogCompositeAssetPath);
        if (fogShader == null)
        {
            Plugin.Logger?.LogError(
                $"DryCycle weather bundle is missing shader '{FogCompositeAssetPath}'.");
        }
        else if (!fogShader.isSupported)
        {
            Plugin.Logger?.LogError(
                $"DryCycle fog shader '{fogShader.name}' is not supported by the " +
                $"current graphics device '{SystemInfo.graphicsDeviceName}' " +
                $"({SystemInfo.graphicsDeviceType}). The compatibility fog " +
                "renderer will be used instead.");
        }
        else
        {
            FogComposite = FShader.CreateShader(FogCompositeShaderKey, fogShader);
            rainWorld.Shaders[FogCompositeShaderKey] = FogComposite;
        }

        FogFluidCompute = _bundle.LoadAsset<ComputeShader>(FogFluidAssetPath);
        FogNoiseCompute = _bundle.LoadAsset<ComputeShader>(FogNoiseAssetPath);

        if (SystemInfo.supportsComputeShaders)
        {
            if (FogFluidCompute == null)
            {
                Plugin.Logger?.LogWarning(
                    $"DryCycle weather bundle is missing compute shader " +
                    $"'{FogFluidAssetPath}'. Fog will render without room-fluid " +
                    "advection.");
            }

            if (FogNoiseCompute == null)
            {
                Plugin.Logger?.LogWarning(
                    $"DryCycle weather bundle is missing compute shader " +
                    $"'{FogNoiseAssetPath}'. Fog will use Rain World's 2D-noise " +
                    "pseudo-volume fallback.");
            }
        }
    }

    private static void LoadHeatWaveAssets(RainWorld rainWorld)
    {
        Shader heatShader = _bundle.LoadAsset<Shader>(HeatWaveAtmosphereAssetPath);
        if (heatShader == null)
        {
            Plugin.Logger?.LogError(
                $"DryCycle weather bundle is missing shader '{HeatWaveAtmosphereAssetPath}'. " +
                "HeatWave LevelHeat will still function, but custom atmosphere rendering " +
                "requires rebuilding the weather bundle.");
        }
        else if (!heatShader.isSupported)
        {
            Plugin.Logger?.LogError(
                $"DryCycle HeatWave atmosphere shader '{heatShader.name}' is not " +
                $"supported by '{SystemInfo.graphicsDeviceName}' " +
                $"({SystemInfo.graphicsDeviceType}).");
        }
        else
        {
            HeatWaveAtmosphere = FShader.CreateShader(HeatWaveAtmosphereShaderKey, heatShader);
            rainWorld.Shaders[HeatWaveAtmosphereShaderKey] = HeatWaveAtmosphere;
        }
    }

    private static void LoadFoehnAssets(RainWorld rainWorld)
    {
        Shader foehnShader = _bundle.LoadAsset<Shader>(FoehnAtmosphereAssetPath);
        if (foehnShader == null)
        {
            Plugin.Logger?.LogError(
                $"DryCycle weather bundle is missing shader '{FoehnAtmosphereAssetPath}'. " +
                "Foehn windblown particles will remain visible, but the directional " +
                "hot-air refraction pass requires rebuilding the weather bundle.");
        }
        else if (!foehnShader.isSupported)
        {
            Plugin.Logger?.LogError(
                $"DryCycle Foehn atmosphere shader '{foehnShader.name}' is not " +
                $"supported by '{SystemInfo.graphicsDeviceName}' " +
                $"({SystemInfo.graphicsDeviceType}).");
        }
        else
        {
            FoehnAtmosphere = FShader.CreateShader(FoehnAtmosphereShaderKey, foehnShader);
            rainWorld.Shaders[FoehnAtmosphereShaderKey] = FoehnAtmosphere;
        }
    }

    private static void LogEditorPlayerVersionRelationship()
    {
        string metadataPath = ResolveWeatherAssetPath(BundleVersionRelativePath);
        if (string.IsNullOrEmpty(metadataPath) || !File.Exists(metadataPath))
        {
            Plugin.Logger?.LogWarning(
                "DryCycle weather AssetBundle has no Unity-version sidecar. " +
                $"Expected '{BundleVersionRelativePath}'. Runtime Unity is " +
                $"{Application.unityVersion}; if the bundle fails, rebuild it with " +
                "the matching Unity Editor before debugging weather rendering.");
            return;
        }

        try
        {
            string editorVersion = File.ReadAllText(metadataPath).Trim();
            string playerVersion = (Application.unityVersion ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(editorVersion))
            {
                return;
            }

            if (!string.Equals(
                    editorVersion,
                    playerVersion,
                    StringComparison.OrdinalIgnoreCase))
            {
                Plugin.Logger?.LogWarning(
                    $"DryCycle weather AssetBundle was built with Unity " +
                    $"'{editorVersion}', while Rain World is running Unity " +
                    $"'{playerVersion}'. Unity AssetBundles are not forward-compatible; " +
                    "rebuild with the player-matching editor if any shader/compute " +
                    "asset fails to load or renders incorrectly.");
            }
            else
            {
                Plugin.Logger?.LogInfo(
                    $"DryCycle weather AssetBundle Unity version matches Rain World: " +
                    $"{playerVersion}.");
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning(
                $"DryCycle could not read AssetBundle version metadata: {ex.Message}");
        }
    }

    private static string ResolveWeatherAssetPath(string relativePath)
    {
        string resolvedPath = AssetManager.ResolveFilePath(relativePath);
        if (!string.IsNullOrEmpty(resolvedPath) && File.Exists(resolvedPath))
        {
            return resolvedPath;
        }

        try
        {
            string assemblyDirectory = Path.GetDirectoryName(
                typeof(DryCycleShaderAssets).Assembly.Location);
            DirectoryInfo directory = string.IsNullOrEmpty(assemblyDirectory)
                ? null
                : new DirectoryInfo(assemblyDirectory);
            string platformRelativePath = relativePath.Replace(
                '/',
                Path.DirectorySeparatorChar);

            while (directory != null)
            {
                string candidate = Path.Combine(
                    directory.FullName,
                    platformRelativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning(
                $"DryCycle could not resolve mod-local weather asset " +
                $"'{relativePath}': {ex.Message}");
        }

        return resolvedPath;
    }
}
