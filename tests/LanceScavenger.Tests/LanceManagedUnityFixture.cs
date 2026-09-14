using System;
using System.IO;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LanceScavenger.Tests;

/// <summary>
/// Only a test-output engine copy is rewritten. Native calls fail explicitly;
/// Shader.PropertyToID is a managed name hash so RainWorld's static constants can
/// initialize. This provides no shader, texture upload, physics or rendering runtime.
/// </summary>
internal static class LanceManagedUnityFixture
{
    internal static void Load(string directory)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(Path.Combine(directory,"RainWorld_Data/Managed/UnityEngine.CoreModule.dll"));
        MethodReference error = assembly.MainModule.ImportReference(typeof(NotSupportedException).GetConstructor(new[] { typeof(string) }));
        MethodReference hash = assembly.MainModule.ImportReference(typeof(string).GetMethod("GetHashCode",Type.EmptyTypes));
        foreach (TypeDefinition type in assembly.MainModule.Types) Rewrite(type,error,hash);
        string outputDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"managed-engine-fixture");
        Directory.CreateDirectory(outputDirectory);
        string path = Path.Combine(outputDirectory,"UnityEngine.CoreModule.dll");
        assembly.Write(path); Assembly.LoadFrom(path);
    }
    private static void Rewrite(TypeDefinition type,MethodReference error,MethodReference hash)
    {
        foreach (MethodDefinition method in type.Methods)
        {
            if (!method.IsInternalCall) continue;
            method.ImplAttributes = Mono.Cecil.MethodImplAttributes.IL | Mono.Cecil.MethodImplAttributes.Managed;
            method.Body = new Mono.Cecil.Cil.MethodBody(method);
            ILProcessor il = method.Body.GetILProcessor();
            if (type.FullName == "UnityEngine.Shader" && method.Name == "PropertyToID")
            { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Callvirt,hash); il.Emit(OpCodes.Ret); }
            else
            { il.Emit(OpCodes.Ldstr,"Unity native API outside fixture: " + method.FullName); il.Emit(OpCodes.Newobj,error); il.Emit(OpCodes.Throw); }
        }
        foreach (TypeDefinition nested in type.NestedTypes) Rewrite(nested,error,hash);
    }
}
