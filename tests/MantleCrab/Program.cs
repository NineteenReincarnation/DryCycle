using System;
using System.IO;
using System.Reflection;

internal static partial class Program
{
    private static string game;
    private static string output;

    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.WriteLine("Usage: MantleCrab.Tests.exe <RainWorldDir> <artifact-directory>");
            return 2;
        }

        game = args[0];
        output = args[1];
        AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;

        try
        {
            Directory.CreateDirectory(output);
            GenomeTests();
            RigTests();
            TerrainTests();
            PlatformSurfaceTests();
            PlatformRuntimeTests();
            GeometryTests();

            Console.WriteLine(
                "PASS. " +
                "Genome cases=" + genomeCases +
                "; walking IK poses=" + walkingIkPoses +
                "; pincer IK poses=" + pincerIkPoses +
                "; terrain cases=" + terrainCases +
                "; standing cases=" + standingCases +
                "; spawn-support cases=" + spawnSupportCases +
                "; platform-surface cases=" + platformSurfaceCases +
                "; platform-runtime cases=" + platformRuntimeCases +
                "; geometry meshes=" + geometryMeshes +
                "; atlas pixels=" + atlasPixels +
                "; assertions=" + assertions +
                ". Managed math/geometry/runtime-contract validation; no in-game Room collision loop.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static Assembly ResolveAssembly(object sender, ResolveEventArgs eventArgs)
    {
        string name = new AssemblyName(eventArgs.Name).Name;
        if (name == "Assembly-CSharp")
            name = "PUBLIC-Assembly-CSharp";

        string[] folders =
        {
            "RainWorld_Data/Managed",
            "BepInEx/utils",
            "BepInEx/core",
            "BepInEx/plugins",
            "RainWorld_Data/StreamingAssets/mods/Ancient Site/newest/plugins"
        };

        foreach (string folder in folders)
        {
            string path = Path.Combine(game, folder, name + ".dll");
            if (File.Exists(path))
                return Assembly.LoadFrom(path);
        }

        return null;
    }
}
