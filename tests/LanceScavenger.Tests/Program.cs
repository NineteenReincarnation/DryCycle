using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using IteratorFramework.Tests;

namespace LanceScavenger.Tests;

internal static class Program
{
    internal static string GameDirectory;
    internal static int Assertions;
    private static int Main(string[] args)
    {
        GameDirectory = args.Length > 0 ? args[0] : "D:/Application/Steam/steamapps/common/Rain World";
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        try { Prepare(); return Run(); }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Prepare() => LanceManagedUnityFixture.Load(GameDirectory);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run()
    {
        int failures = 0;
        foreach (Action test in new Action[] {
            CombatTests.WarningAndRecovery, CombatTests.WeaknessesAndInterruptions, CombatTests.ImpactAndSweeps,
            IntegrationTests.SaveRoundTrips, IntegrationTests.Lanes, IntegrationTests.WeaponContacts,
            IntegrationTests.DevConsoleLifecycle, VisualTests.MasksAndMeshes })
        {
            try { test(); Console.WriteLine("PASS " + test.Method.Name); }
            catch (Exception exception) { failures++; Console.Error.WriteLine("FAIL " + test.Method.Name + "\n" + exception); }
        }
        Console.WriteLine($"Lance Scavenger: {8 - failures}/8 groups, {Assertions} assertions, {failures} failures.");
        Console.WriteLine("Built DryCycle.dll + installed managed game code; Unity physics, live AI and visual acceptance are not simulated.");
        return failures == 0 ? 0 : 1;
    }
    internal static void Check(bool condition, string message)
    { Assertions++; if (!condition) throw new InvalidOperationException(message); }
    internal static void Throws(Action action)
    { try { action(); } catch (ArgumentException) { Assertions++; return; } throw new InvalidOperationException("Expected invalid argument rejection"); }
    private static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        string file = new AssemblyName(args.Name).Name + ".dll";
        if (file == "Assembly-CSharp.dll")
            return Assembly.LoadFrom(Path.Combine(GameDirectory, "BepInEx/utils/PUBLIC-Assembly-CSharp.dll"));
        foreach (string directory in new[] { "RainWorld_Data/Managed", "BepInEx/core", "BepInEx/plugins" })
        { string path = Path.Combine(GameDirectory, directory, file); if (File.Exists(path)) return Assembly.LoadFrom(path); }
        if (file == "DevConsole.dll")
        {
            string path = Path.GetFullPath(Path.Combine(GameDirectory, "../../workshop/content/312520/2920528044/newest/plugins/DevConsole.dll"));
            if (File.Exists(path)) return Assembly.LoadFrom(path);
        }
        return null;
    }
}
