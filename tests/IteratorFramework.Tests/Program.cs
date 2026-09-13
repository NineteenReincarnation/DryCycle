using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace IteratorFramework.Tests;

internal static class Program
{
    private static string _rainWorldDir;

    private static int Main(string[] args)
    {
        _rainWorldDir = args.Length > 0 ? args[0] :
            Environment.GetEnvironmentVariable("RainWorldDir") ??
            Environment.GetEnvironmentVariable("RainWorldPath") ??
            "D:/Application/Steam/steamapps/common/Rain World";
        string assemblyPath = Path.Combine(_rainWorldDir, "RainWorld_Data/Managed/Assembly-CSharp.dll");
        if (!File.Exists(assemblyPath))
        {
            Console.Error.WriteLine("Pass the installed Rain World directory as the first argument; missing " + assemblyPath);
            return 2;
        }

        AppDomain.CurrentDomain.AssemblyResolve += ResolveGameAssembly;
        try
        {
            return RunTests();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            AppDomain.CurrentDomain.AssemblyResolve -= ResolveGameAssembly;
        }
    }

    // Install resolution before JIT touches the API's game types. Execute the real
    // managed game assembly, not a mock Oracle or a source-linked copy of the Core.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunTests()
    {
        int core = CoreTests.Run();
        int runtime = RuntimeTests.Run();
        return Math.Max(core, runtime);
    }

    private static Assembly ResolveGameAssembly(object sender, ResolveEventArgs args)
    {
        string file = new AssemblyName(args.Name).Name + ".dll";
        foreach (string directory in new[] { "RainWorld_Data/Managed", "BepInEx/core", "BepInEx/plugins" })
        {
            string path = Path.Combine(_rainWorldDir, directory, file);
            if (File.Exists(path))
                return Assembly.LoadFrom(path);
        }
        return null;
    }
}
