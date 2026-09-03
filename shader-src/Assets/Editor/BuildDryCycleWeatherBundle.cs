#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace DryCycle.Editor
{
    /// <summary>
    /// Builds the platform-specific weather shader bundle directly into mod/assets so
    /// the normal DryCycle MSBuild target can copy it into the active Rain World mod.
    /// The script intentionally uses conservative C# syntax so it can compile in the
    /// older Unity editor versions used by Rain World releases.
    /// </summary>
    public static class BuildDryCycleWeatherBundle
    {
        private const string BundleName = "drycycleweather";
        private const string VersionSidecarName = "drycycleweather.version.txt";

        private static readonly string[] WeatherAssets =
        {
            "Assets/DryCycle/Shaders/DryCycleFogComposite.shader",
            "Assets/DryCycle/Compute/DryCycleFogFluid.compute",
            "Assets/DryCycle/Compute/DryCycleFogNoise.compute",
            "Assets/DryCycle/Shaders/DryCycleHeatWaveAtmosphere.shader",
            "Assets/DryCycle/Shaders/DryCycleFoehnAtmosphere.shader"
        };

        [MenuItem("DryCycle/Build Weather AssetBundle (Windows x64)")]
        public static void BuildFromMenu()
        {
            Build(BuildTarget.StandaloneWindows64);
        }

        // Entry point for:
        // Unity.exe -batchmode -quit -projectPath shader-src
        //   -executeMethod DryCycle.Editor.BuildDryCycleWeatherBundle.BuildFromCommandLine
        public static void BuildFromCommandLine()
        {
            Build(BuildTarget.StandaloneWindows64);
        }

        private static void Build(BuildTarget target)
        {
            ValidateSourceAssets();

            DirectoryInfo projectDirectory = Directory.GetParent(Application.dataPath);
            string projectRoot = projectDirectory == null ? null : projectDirectory.FullName;
            DirectoryInfo repositoryDirectory = string.IsNullOrEmpty(projectRoot)
                ? null
                : Directory.GetParent(projectRoot);
            string repositoryRoot = repositoryDirectory == null
                ? null
                : repositoryDirectory.FullName;

            if (string.IsNullOrEmpty(repositoryRoot))
            {
                throw new InvalidOperationException("Could not resolve DryCycle repository root.");
            }

            string output = Path.Combine(repositoryRoot, "mod", "assets", "drycycle");
            Directory.CreateDirectory(output);

            AssetBundleBuild build = new AssetBundleBuild
            {
                assetBundleName = BundleName,
                assetNames = WeatherAssets
            };

            BuildAssetBundleOptions options =
                BuildAssetBundleOptions.ChunkBasedCompression |
                BuildAssetBundleOptions.ForceRebuildAssetBundle;

            AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(
                output,
                new AssetBundleBuild[] { build },
                options,
                target);

            string bundlePath = Path.Combine(output, BundleName);
            if (manifest == null || !File.Exists(bundlePath))
            {
                throw new InvalidOperationException(
                    "DryCycle weather AssetBundle build failed. Expected '" +
                    bundlePath + "'.");
            }

            string sidecarPath = Path.Combine(output, VersionSidecarName);
            File.WriteAllText(sidecarPath, Application.unityVersion + Environment.NewLine);

            Debug.Log(
                "DryCycle weather AssetBundle built with Unity " +
                Application.unityVersion + ": " + bundlePath);
            Debug.Log(
                "DryCycle weather AssetBundle version metadata: " + sidecarPath);
        }

        private static void ValidateSourceAssets()
        {
            Shader fogShader = AssetDatabase.LoadAssetAtPath<Shader>(WeatherAssets[0]);
            if (fogShader == null)
            {
                throw new InvalidOperationException(
                    "DryCycle fog composite shader could not be imported: " + WeatherAssets[0]);
            }

            ComputeShader fluid = AssetDatabase.LoadAssetAtPath<ComputeShader>(WeatherAssets[1]);
            if (fluid == null)
            {
                throw new InvalidOperationException(
                    "DryCycle fog fluid compute shader could not be imported: " + WeatherAssets[1]);
            }

            ComputeShader noise = AssetDatabase.LoadAssetAtPath<ComputeShader>(WeatherAssets[2]);
            if (noise == null)
            {
                throw new InvalidOperationException(
                    "DryCycle fog noise compute shader could not be imported: " + WeatherAssets[2]);
            }

            Shader heatAtmosphere = AssetDatabase.LoadAssetAtPath<Shader>(WeatherAssets[3]);
            if (heatAtmosphere == null)
            {
                throw new InvalidOperationException(
                    "DryCycle HeatWave atmosphere shader could not be imported: " + WeatherAssets[3]);
            }

            Shader foehnAtmosphere = AssetDatabase.LoadAssetAtPath<Shader>(WeatherAssets[4]);
            if (foehnAtmosphere == null)
            {
                throw new InvalidOperationException(
                    "DryCycle Foehn atmosphere shader could not be imported: " + WeatherAssets[4]);
            }

            Debug.Log(
                "DryCycle weather source assets imported successfully. " +
                "Editor Unity=" + Application.unityVersion +
                ", Graphics API target=" + EditorUserBuildSettings.activeBuildTarget + ".");
        }
    }
}
#endif
