using System;
using System.IO;
using UnityEngine;

namespace DryCycle.Rendering;

/// <summary>
/// Owns DryCycle's custom weather and creature shader bundles. AssetBundle.LoadAsset is
/// deliberately deferred until RainWorld.LoadResources; loading Unity shader assets
/// from BepInEx OnEnable/Awake can hard-crash the player before Rain World has
/// initialized its rendering resources.
/// </summary>
internal static class DryCycleShaderAssets
{
    internal const string FogCompositeShaderKey = "DryCycleFogComposite";
    internal const string HeatWaveAtmosphereShaderKey = "DryCycleHeatWaveAtmosphere";
    internal const string IntenseHeatAtmosphereShaderKey = "DryCycleIntenseHeatAtmosphere";
    internal const string DehydrationCompositeShaderKey = "DryCycleDehydrationComposite";
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
    private const string IntenseHeatAtmosphereAssetPath =
        "assets/drycycle/shaders/drycycleintenseheatatmosphere.shader";
    private const string DehydrationCompositeAssetPath =
        "assets/drycycle/shaders/drycycledehydrationcomposite.shader";

    private static AssetBundle _bundle;
    private static AssetBundle _creatureBundle;
    internal static FShader MantleCrabSurface { get; private set; }
    internal static ComputeShader MantleCrabBake { get; private set; }

    // Called only by CreatureDefinition.LoadResources, after RW/Futile initialization.
    // Same resolver, FShader ownership and safe AssetBundle lifetime as weather resources.
    internal static void EnsureCreatureAssets(RainWorld rainWorld)
    {
        if (_creatureBundle != null) return;
        string path = ResolveWeatherAssetPath("assets/drycycle/drycyclecreatures");
        if (!File.Exists(path))
        {
            SafeLogWarning("MantleCrab creature bundle missing; using opaque procedural CPU geometry/material fallback.");
            return;
        }
        try
        {
            string version = ResolveWeatherAssetPath("assets/drycycle/drycyclecreatures.version.txt");
            if (!File.Exists(version) || File.ReadAllText(version).Trim() != Application.unityVersion)
                SafeLogWarning("MantleCrab bundle editor/player version differs or metadata is missing. Player: " + Application.unityVersion);
            _creatureBundle = AssetBundle.LoadFromFile(path);
            if (_creatureBundle == null) return;
            Shader shader = _creatureBundle.LoadAsset<Shader>("assets/drycycle/creatures/mantlecrab/mantlecrabsurface.shader");
            if (shader != null && shader.isSupported)
            {
                MantleCrabSurface = FShader.CreateShader("DryCycleMantleCrabSurface", shader);
                rainWorld.Shaders["DryCycleMantleCrabSurface"] = MantleCrabSurface;
            }
            if (SystemInfo.supportsComputeShaders)
                MantleCrabBake = _creatureBundle.LoadAsset<ComputeShader>("assets/drycycle/creatures/mantlecrab/mantlecrabmaterialbake.compute");
        }
        catch (Exception ex) { SafeLogError("MantleCrab assets: " + ex); }
    }
    private static bool _enabled;
    private static bool _missingBundleLogged;

    internal static FShader FogComposite { get; private set; }
    internal static ComputeShader FogFluidCompute { get; private set; }
    internal static ComputeShader FogNoiseCompute { get; private set; }
    internal static FShader HeatWaveAtmosphere { get; private set; }
    internal static FShader IntenseHeatAtmosphere { get; private set; }
    internal static FShader DehydrationComposite { get; private set; }

    internal static bool HasFogComposite => FogComposite != null;
    internal static bool HasFluidCompute => FogFluidCompute != null;
    internal static bool HasNoiseCompute => FogNoiseCompute != null;
    internal static bool HasHeatWaveAtmosphere => HeatWaveAtmosphere != null;
    internal static bool HasIntenseHeatAtmosphere => IntenseHeatAtmosphere != null;
    internal static bool HasDehydrationComposite => DehydrationComposite != null;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        try
        {
            On.RainWorld.LoadResources += RainWorld_LoadResources;
        }
        catch (Exception error)
        {
            global::DryCycle.StartupDiagnostics.RollbackAfterFailure(
                "DryCycleShaderAssets.Enable",
                error,
                () =>
                {
                    global::DryCycle.StartupDiagnostics.RollbackStep(
                        "DryCycleShaderAssets.Enable/RainWorld.LoadResources",
                        () => On.RainWorld.LoadResources -= RainWorld_LoadResources);
                    _enabled = false;
                });
            throw;
        }
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

        try
        {
            string path = ResolveWeatherAssetPath(BundleRelativePath);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                if (!_missingBundleLogged)
                {
                    _missingBundleLogged = true;
                    global::DryCycle.StartupDiagnostics.Marker(
                        "DryCycleShaderAssets.TryLoad",
                        "BUNDLE-MISSING",
                        "path=" + (path ?? "<null>") +
                        " runtimeUnity=" + Application.unityVersion);
                }
                return;
            }

            LogEditorPlayerVersionRelationship();

            _bundle = AssetBundle.LoadFromFile(path);
            if (_bundle == null)
            {
                SafeLogError(
                    $"DryCycle failed to load weather AssetBundle '{path}'. " +
                    $"Runtime Unity version: {Application.unityVersion}.");
                return;
            }

            LoadFogAssets(rainWorld);
            LoadHeatWaveAssets(rainWorld);
            LoadIntenseHeatAssets(rainWorld);
            LoadDehydrationAssets(rainWorld);

            if (!SystemInfo.supportsComputeShaders)
            {
                SafeLogWarning(
                    "This graphics device reports no compute-shader support. DryCycle " +
                    "fog will use its non-fluid fallback. HeatWave and IntenseHeat do " +
                    "not depend on compute shaders.");
            }

            SafeLogInfo(
                "DryCycle weather rendering assets loaded: " +
                $"FogComposite={(FogComposite != null ? "yes" : "no")}, " +
                $"FogFluid={(FogFluidCompute != null ? "yes" : "no")}, " +
                $"FogNoise={(FogNoiseCompute != null ? "yes" : "no")}, " +
                $"HeatWaveAtmosphere={(HeatWaveAtmosphere != null ? "yes" : "no")}, " +
                $"IntenseHeatAtmosphere={(IntenseHeatAtmosphere != null ? "yes" : "no")}, " +
                $"DehydrationComposite={(DehydrationComposite != null ? "yes" : "no")}, " +
                $"ComputeSupported={SystemInfo.supportsComputeShaders}, " +
                $"Unity={Application.unityVersion}, GPU='{SystemInfo.graphicsDeviceName}'.");
        }
        catch (Exception ex)
        {
            FogComposite = null;
            FogFluidCompute = null;
            FogNoiseCompute = null;
            HeatWaveAtmosphere = null;
            IntenseHeatAtmosphere = null;
            DehydrationComposite = null;
            global::DryCycle.StartupDiagnostics.Failure(
                "DryCycleShaderAssets.TryLoad",
                ex);
            global::DryCycle.StartupDiagnostics.Marker(
                "DryCycleShaderAssets.TryLoad",
                "ISOLATED",
                "weather asset load failed; compatibility rendering remains active");
        }
    }

    private static void LoadFogAssets(RainWorld rainWorld)
    {
        Shader fogShader = _bundle.LoadAsset<Shader>(FogCompositeAssetPath);
        if (fogShader == null)
        {
            SafeLogError(
                $"DryCycle weather bundle is missing shader '{FogCompositeAssetPath}'.");
        }
        else if (!fogShader.isSupported)
        {
            SafeLogError(
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
                SafeLogWarning(
                    $"DryCycle weather bundle is missing compute shader " +
                    $"'{FogFluidAssetPath}'. Fog will render without room-fluid " +
                    "advection.");
            }

            if (FogNoiseCompute == null)
            {
                SafeLogWarning(
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
            SafeLogError(
                $"DryCycle weather bundle is missing shader '{HeatWaveAtmosphereAssetPath}'. " +
                "HeatWave LevelHeat will still function, but custom atmosphere rendering " +
                "requires rebuilding the weather bundle.");
        }
        else if (!heatShader.isSupported)
        {
            SafeLogError(
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

    private static void LoadIntenseHeatAssets(RainWorld rainWorld)
    {
        Shader intenseShader = _bundle.LoadAsset<Shader>(IntenseHeatAtmosphereAssetPath);
        if (intenseShader == null)
        {
            SafeLogError(
                $"DryCycle weather bundle is missing shader '{IntenseHeatAtmosphereAssetPath}'. " +
                "IntenseHeat gameplay exposure will still run, but the disaster-grade " +
                "solar atmosphere requires rebuilding the weather bundle.");
        }
        else if (!intenseShader.isSupported)
        {
            SafeLogError(
                $"DryCycle IntenseHeat atmosphere shader '{intenseShader.name}' is not " +
                $"supported by '{SystemInfo.graphicsDeviceName}' " +
                $"({SystemInfo.graphicsDeviceType}).");
        }
        else
        {
            IntenseHeatAtmosphere = FShader.CreateShader(
                IntenseHeatAtmosphereShaderKey,
                intenseShader);
            rainWorld.Shaders[IntenseHeatAtmosphereShaderKey] = IntenseHeatAtmosphere;
        }
    }

    private static void LoadDehydrationAssets(RainWorld rainWorld)
    {
        Shader dehydrationShader = _bundle.LoadAsset<Shader>(DehydrationCompositeAssetPath);
        if (dehydrationShader == null)
        {
            SafeLogError(
                $"DryCycle weather bundle is missing shader '{DehydrationCompositeAssetPath}'. " +
                "Dehydration will keep its mesh-based compatibility presentation, but " +
                "advanced tear-film, focus and retinal processing require rebuilding " +
                "the weather bundle.");
        }
        else if (!dehydrationShader.isSupported)
        {
            SafeLogError(
                $"DryCycle dehydration composite shader '{dehydrationShader.name}' is not " +
                $"supported by '{SystemInfo.graphicsDeviceName}' " +
                $"({SystemInfo.graphicsDeviceType}).");
        }
        else
        {
            DehydrationComposite = FShader.CreateShader(
                DehydrationCompositeShaderKey,
                dehydrationShader);
            rainWorld.Shaders[DehydrationCompositeShaderKey] = DehydrationComposite;
        }
    }

    private static void LogEditorPlayerVersionRelationship()
    {
        string metadataPath = ResolveWeatherAssetPath(BundleVersionRelativePath);
        if (string.IsNullOrEmpty(metadataPath) || !File.Exists(metadataPath))
        {
            SafeLogWarning(
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
                SafeLogWarning(
                    $"DryCycle weather AssetBundle was built with Unity " +
                    $"'{editorVersion}', while Rain World is running Unity " +
                    $"'{playerVersion}'. Unity AssetBundles are not forward-compatible; " +
                    "rebuild with the player-matching editor if any shader/compute " +
                    "asset fails to load or renders incorrectly.");
            }
            else
            {
                SafeLogInfo(
                    $"DryCycle weather AssetBundle Unity version matches Rain World: " +
                    $"{playerVersion}.");
            }
        }
        catch (Exception ex)
        {
            SafeLogWarning(
                $"DryCycle could not read AssetBundle version metadata: {ex.Message}");
        }
    }

    private static void SafeLogInfo(string message)
    {
        try { Plugin.Logger?.LogInfo(message); }
        catch { }
    }

    private static void SafeLogWarning(string message)
    {
        try { Plugin.Logger?.LogWarning(message); }
        catch { }
    }

    private static void SafeLogError(object message)
    {
        try { Plugin.Logger?.LogError(message); }
        catch { }
    }

    private static string ResolveWeatherAssetPath(string relativePath)
    {
        string resolvedPath = null;
        try
        {
            resolvedPath = AssetManager.ResolveFilePath(relativePath);
            if (!string.IsNullOrEmpty(resolvedPath) && File.Exists(resolvedPath))
            {
                return resolvedPath;
            }
        }
        catch (Exception error)
        {
            global::DryCycle.StartupDiagnostics.Failure(
                "DryCycleShaderAssets.Resolve/AssetManager/" + relativePath,
                error);
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
        catch (Exception error)
        {
            global::DryCycle.StartupDiagnostics.Failure(
                "DryCycleShaderAssets.Resolve/ModLocal/" + relativePath,
                error);
        }

        return resolvedPath;
    }
}
