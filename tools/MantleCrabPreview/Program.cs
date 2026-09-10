using System;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using DryCycle.Creatures.MantleCrab.Rendering;
using UnityEngine;

// Exports production meshes for the Unity art preview, without loading a Rain World room.
internal static partial class Program
{
    private static string game, output;
    private static int geometryMeshes, atlasPixels;
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static int Main(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("Usage: preview <RainWorldDir> <output>"); return 2; }
        game = args[0]; output = args[1];
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        try { Directory.CreateDirectory(output); ExportGeometry(); Console.WriteLine("Exported production geometry and atlas."); return 0; }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    private static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name;
        if (name == "Assembly-CSharp") name = "PUBLIC-Assembly-CSharp";
        foreach (string folder in new[] { "RainWorld_Data/Managed", "BepInEx/utils", "BepInEx/core", "BepInEx/plugins" })
        {
            string path = Path.Combine(game, folder, name + ".dll");
            if (File.Exists(path)) return Assembly.LoadFrom(path);
        }
        return null;
    }
    private static T Empty<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));
    private static void SetField(object instance, string name, object value) => instance.GetType().GetField(name, Flags).SetValue(instance, value);
    private static object GetField(object instance, string name) => instance.GetType().GetField(name, Flags).GetValue(instance);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    private static bool Finite(Vector2 v) => Finite(v.x) && Finite(v.y);
    private static MantleCrabVisualPhenotype CreatePhenotype()
    {
        var genome = Empty<MantleCrabVisualGenome>();
        SetField(genome, "Seed", 1729);
        foreach (string field in new[] { "Energy", "Bravery", "Sympathy", "Dominance", "Nervous", "Aggression" }) SetField(genome, field, .5f);
        return new MantleCrabVisualPhenotype(genome);
    }
}
