using System;
using System.IO;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IteratorFramework.Tests;

/// <summary>
/// The CLR cannot JIT Unity ECalls even on branches a fixture never takes. Load an
/// test-output CoreModule whose native entry points throw on use. Managed game and
/// physics methods remain unchanged; water/rendering/weather native paths are NOT
/// simulated, and touching one fails the test. No installed assembly is rewritten.
/// </summary>
internal static class ManagedUnityFixture
{
    internal static void Load(string rainWorldDir)
    {
        using (AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(
            Path.Combine(rainWorldDir, "RainWorld_Data/Managed/UnityEngine.CoreModule.dll")))
        {
            MethodReference unsupported = assembly.MainModule.ImportReference(
                typeof(NotSupportedException).GetConstructor(new[] { typeof(string) }));
            foreach (TypeDefinition type in assembly.MainModule.Types) ReplaceNative(type, unsupported);
            string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "managed-engine-fixture");
            Directory.CreateDirectory(directory);
            string output = Path.Combine(directory, "UnityEngine.CoreModule.dll");
            assembly.Write(output);
            Assembly.LoadFrom(output);
        }
    }

    private static void ReplaceNative(TypeDefinition type, MethodReference unsupported)
    {
        foreach (MethodDefinition method in type.Methods)
        {
            if (!method.IsInternalCall) continue;
            method.ImplAttributes = Mono.Cecil.MethodImplAttributes.IL | Mono.Cecil.MethodImplAttributes.Managed;
            method.Body = new Mono.Cecil.Cil.MethodBody(method);
            ILProcessor il = method.Body.GetILProcessor();
            il.Emit(OpCodes.Ldstr, "Unity native API is outside managed fixture coverage: " + method.FullName);
            il.Emit(OpCodes.Newobj, unsupported);
            il.Emit(OpCodes.Throw);
        }
        foreach (TypeDefinition nested in type.NestedTypes) ReplaceNative(nested, unsupported);
    }
}
