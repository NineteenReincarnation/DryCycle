#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace DryCycle.Editor
{
    /// <summary>
    /// Builds the platform-specific weather/creature shader bundles directly into mod/assets so
    /// the normal DryCycle MSBuild target can copy them into the active Rain World mod. The script
    /// intentionally uses conservative C# syntax for the Unity editor version used by Rain World.
    /// </summary>
    public static class BuildDryCycleWeatherBundle
    {
        private const string BundleName = "drycycleweather";
        private const string VersionSidecarName = "drycycleweather.version.txt";
        private const string CreatureBundleName = "drycyclecreatures";
        private const string CreatureVersionSidecarName = "drycyclecreatures.version.txt";
        private const string MantleCrabSurfaceAsset =
            "Assets/DryCycle/Creatures/MantleCrab/MantleCrabSurface.shader";
        private const string MantleCrabBakeAsset =
            "Assets/DryCycle/Creatures/MantleCrab/MantleCrabMaterialBake.compute";
        private const string MantleCrabBakeKernel = "BakeV4";

        private static readonly string[] WeatherAssets =
        {
            "Assets/DryCycle/Shaders/DryCycleFogComposite.shader",
            "Assets/DryCycle/Compute/DryCycleFogFluid.compute",
            "Assets/DryCycle/Compute/DryCycleFogNoise.compute",
            "Assets/DryCycle/Shaders/DryCycleHeatWaveAtmosphere.shader",
            "Assets/DryCycle/Shaders/DryCycleIntenseHeatAtmosphere.shader",
            "Assets/DryCycle/Shaders/DryCycleDehydrationComposite.shader",
            "Assets/DryCycle/Shaders/DryCycleMenuTextFlow.shader"
        };

        private static readonly string[] CreatureAssets =
        {
            MantleCrabSurfaceAsset,
            MantleCrabBakeAsset
        };

        [MenuItem("DryCycle/Build Weather AssetBundle (Windows x64)")]
        public static void BuildFromMenu()
        {
            Build(BuildTarget.StandaloneWindows64);
        }

        [MenuItem("DryCycle/Build Creature AssetBundle (Windows x64)")]
        public static void BuildCreaturesFromMenu()
        {
            Build(BuildTarget.StandaloneWindows64, true);
        }

        // Entry point for:
        // Unity.exe -batchmode -quit -projectPath shader-src
        //   -executeMethod DryCycle.Editor.BuildDryCycleWeatherBundle.BuildFromCommandLine
        public static void BuildFromCommandLine()
        {
            Build(BuildTarget.StandaloneWindows64);
        }

        public static void BuildCreaturesFromCommandLine()
        {
            Build(BuildTarget.StandaloneWindows64, true);
        }

        private static void Build(BuildTarget target, bool creaturesOnly = false)
        {
            if (!creaturesOnly)
                ValidateSourceAssets();
            ValidateCreatureAssets();

            DirectoryInfo projectDirectory = Directory.GetParent(Application.dataPath);
            string projectRoot = projectDirectory == null ? null : projectDirectory.FullName;
            DirectoryInfo repositoryDirectory = string.IsNullOrEmpty(projectRoot)
                ? null
                : Directory.GetParent(projectRoot);
            string repositoryRoot = repositoryDirectory == null
                ? null
                : repositoryDirectory.FullName;

            if (string.IsNullOrEmpty(repositoryRoot))
                throw new InvalidOperationException("Could not resolve DryCycle repository root.");

            string output = Path.Combine(repositoryRoot, "mod", "assets", "drycycle");
            Directory.CreateDirectory(output);

            AssetBundleBuild weather = new AssetBundleBuild
            {
                assetBundleName = BundleName,
                assetNames = WeatherAssets
            };
            AssetBundleBuild creatures = new AssetBundleBuild
            {
                assetBundleName = CreatureBundleName,
                assetNames = CreatureAssets
            };

            BuildAssetBundleOptions options =
                BuildAssetBundleOptions.ChunkBasedCompression |
                BuildAssetBundleOptions.ForceRebuildAssetBundle;

            AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(
                output,
                creaturesOnly
                    ? new AssetBundleBuild[] { creatures }
                    : new AssetBundleBuild[] { weather, creatures },
                options,
                target);

            string primaryBundle = creaturesOnly ? CreatureBundleName : BundleName;
            string bundlePath = Path.Combine(output, primaryBundle);
            if (manifest == null || !File.Exists(bundlePath))
                throw new InvalidOperationException(
                    "DryCycle AssetBundle build failed. Expected '" + bundlePath + "'.");

            if (!File.Exists(Path.Combine(output, CreatureBundleName)))
                throw new InvalidOperationException("Creature bundle output missing.");

            string sidecarPath = Path.Combine(
                output,
                creaturesOnly ? CreatureVersionSidecarName : VersionSidecarName);
            File.WriteAllText(sidecarPath, Application.unityVersion + Environment.NewLine);
            File.WriteAllText(
                Path.Combine(output, CreatureVersionSidecarName),
                Application.unityVersion + Environment.NewLine);

            Debug.Log(
                "DryCycle AssetBundle built with Unity " +
                Application.unityVersion + ": " + bundlePath);
            Debug.Log("DryCycle AssetBundle version metadata: " + sidecarPath);
        }

        private static void ValidateCreatureAssets()
        {
            Shader creatureShader = AssetDatabase.LoadAssetAtPath<Shader>(MantleCrabSurfaceAsset);
            if (creatureShader == null || ShaderUtil.ShaderHasError(creatureShader))
                throw new InvalidOperationException(
                    "MantleCrab surface shader failed to import/compile: " + MantleCrabSurfaceAsset);

            ComputeShader compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(MantleCrabBakeAsset);
            if (compute == null)
                throw new InvalidOperationException(
                    "MantleCrab compute shader failed to import: " + MantleCrabBakeAsset);

            try
            {
                compute.FindKernel(MantleCrabBakeKernel);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "MantleCrab compute shader is not V3-compatible; missing kernel '" +
                    MantleCrabBakeKernel + "'. Reimport/rebuild the creature shader sources.", ex);
            }
        }

        private static void ValidateSourceAssets()
        {
            Shader fogShader = AssetDatabase.LoadAssetAtPath<Shader>(WeatherAssets[0]);
            if (fogShader == null)
                throw new InvalidOperationException(
                    "DryCycle fog composite shader could not be imported: " + WeatherAssets[0]);

            ComputeShader fluid = AssetDatabase.LoadAssetAtPath<ComputeShader>(WeatherAssets[1]);
            if (fluid == null)
                throw new InvalidOperationException(
                    "DryCycle fog fluid compute shader could not be imported: " + WeatherAssets[1]);

            ComputeShader noise = AssetDatabase.LoadAssetAtPath<ComputeShader>(WeatherAssets[2]);
            if (noise == null)
                throw new InvalidOperationException(
                    "DryCycle fog noise compute shader could not be imported: " + WeatherAssets[2]);

            Shader heatAtmosphere = AssetDatabase.LoadAssetAtPath<Shader>(WeatherAssets[3]);
            if (heatAtmosphere == null)
                throw new InvalidOperationException(
                    "DryCycle HeatWave atmosphere shader could not be imported: " + WeatherAssets[3]);

            Shader intenseHeatAtmosphere = AssetDatabase.LoadAssetAtPath<Shader>(WeatherAssets[4]);
            if (intenseHeatAtmosphere == null)
                throw new InvalidOperationException(
                    "DryCycle IntenseHeat atmosphere shader could not be imported: " + WeatherAssets[4]);

            Shader dehydrationComposite = AssetDatabase.LoadAssetAtPath<Shader>(WeatherAssets[5]);
            if (dehydrationComposite == null)
                throw new InvalidOperationException(
                    "DryCycle dehydration composite shader could not be imported: " + WeatherAssets[5]);

            Shader menuTextFlow = AssetDatabase.LoadAssetAtPath<Shader>(WeatherAssets[6]);
            if (menuTextFlow == null || ShaderUtil.ShaderHasError(menuTextFlow))
                throw new InvalidOperationException(
                    "DryCycle menu text flow shader failed to import/compile: " + WeatherAssets[6]);

            Debug.Log(
                "DryCycle weather source assets imported successfully. " +
                "Editor Unity=" + Application.unityVersion +
                ", Graphics API target=" + EditorUserBuildSettings.activeBuildTarget + ".");
        }
    }
}
#endif
