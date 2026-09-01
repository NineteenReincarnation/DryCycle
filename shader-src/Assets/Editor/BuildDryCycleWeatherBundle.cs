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

        private static readonly string[] WeatherAssets =
        {
            "Assets/DryCycle/Shaders/DryCycleFogComposite.shader",
            "Assets/DryCycle/Compute/DryCycleFogFluid.compute",
            "Assets/DryCycle/Compute/DryCycleFogNoise.compute"
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

            Debug.Log(
                "DryCycle weather AssetBundle built with Unity " +
                Application.unityVersion + ": " + bundlePath);
        }
    }
}
#endif
