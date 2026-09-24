using System;
using System.IO;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Test-output engine copy only. The fixture supplies a controlled clock and shader-name hashing so
// managed game constants/history can initialize; every other native call fails explicitly.
internal static class ManagedMapEngine
{
    private static FieldInfo frame;
    internal static void AdvanceFrame() => frame.SetValue(null, (int)frame.GetValue(null) + 1);

    internal static void Load(string game)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(Path.Combine(game, "RainWorld_Data/Managed/UnityEngine.CoreModule.dll"));
        var clock = new TypeDefinition("MapTestFixture", "Clock", Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
            assembly.MainModule.TypeSystem.Object);
        var frameCount = new FieldDefinition("FrameCount", Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static,
            assembly.MainModule.TypeSystem.Int32);
        clock.Fields.Add(frameCount); assembly.MainModule.Types.Add(clock);
        var error = assembly.MainModule.ImportReference(typeof(NotSupportedException).GetConstructor(new[] { typeof(string) }));
        var hash = assembly.MainModule.ImportReference(typeof(string).GetMethod("GetHashCode", Type.EmptyTypes));
        void Rewrite(TypeDefinition type)
        {
            foreach (var method in type.Methods)
            {
                if (!method.IsInternalCall) continue;
                method.ImplAttributes = Mono.Cecil.MethodImplAttributes.IL | Mono.Cecil.MethodImplAttributes.Managed;
                method.Body = new Mono.Cecil.Cil.MethodBody(method);
                var il = method.Body.GetILProcessor();
                if (type.FullName == "UnityEngine.Shader" && method.Name == "PropertyToID")
                { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Callvirt, hash); il.Emit(OpCodes.Ret); }
                else if (type.FullName == "UnityEngine.Time" && method.Name == "get_frameCount")
                { il.Emit(OpCodes.Ldsfld, frameCount); il.Emit(OpCodes.Ret); }
                else if (type.FullName == "UnityEngine.Time" && method.ReturnType.FullName == "System.Single")
                { il.Emit(OpCodes.Ldc_R4, 0f); il.Emit(OpCodes.Ret); }
                else
                { il.Emit(OpCodes.Ldstr, "Unity native API outside map fixture: " + method.FullName); il.Emit(OpCodes.Newobj, error); il.Emit(OpCodes.Throw); }
            }
            foreach (var nested in type.NestedTypes) Rewrite(nested);
        }
        foreach (var type in assembly.MainModule.Types) Rewrite(type);
        string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "managed-engine-fixture");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "UnityEngine.CoreModule.dll");
        assembly.Write(path);
        frame = Assembly.LoadFrom(path).GetType("MapTestFixture.Clock", true).GetField("FrameCount");
    }
}
