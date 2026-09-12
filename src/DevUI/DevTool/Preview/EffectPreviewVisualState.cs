using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Preview;

/// <summary>
/// Captures global Unity shader state that can be proven to belong to the runtime implementation
/// of one RoomEffect. Discovery is behavior based: it starts from Room.Loaded / HookGen branches
/// that read the requested RoomEffect.Type, follows the UAD constructors and same-assembly helper
/// methods found there, then inspects their runtime methods for Shader.SetGlobal* calls.
///
/// No mod id, assembly allow-list or private registry is used. If a shader write cannot be mapped
/// to a stable property id/name or to a getter/setter pair that can be restored, advanced preview
/// fails closed and the stage-one temporary RoomEffect remains available.
/// </summary>
internal sealed class EffectPreviewVisualStateJournal
{
    private const int MaxEffectScanInstructions = 96;
    private const int MaxPropertySearchInstructions = 12;
    private const int MaxRecursiveMethods = 192;
    private const int MaxRecursiveDepth = 5;

    private readonly List<ShaderPropertyState> shaderProperties = new();
    private readonly List<ShaderKeywordState> shaderKeywords = new();

    private EffectPreviewVisualStateJournal()
    {
    }

    internal int ShaderPropertyCount => shaderProperties.Count;
    internal int ShaderKeywordCount => shaderKeywords.Count;

    internal static bool TryCaptureForEffect(
        string typeName,
        out EffectPreviewVisualStateJournal journal,
        out string failureReason)
    {
        journal = new EffectPreviewVisualStateJournal();
        failureReason = string.Empty;
        if (string.IsNullOrWhiteSpace(typeName))
            return true;

        VisualShaderUsage usage = VisualShaderUsageScanner.Discover(typeName);
        if (!usage.Safe)
        {
            failureReason = usage.FailureReason;
            journal = null;
            return false;
        }

        foreach (VisualShaderProperty property in usage.Properties)
        {
            if (!ShaderReflectionBridge.TryCapture(property, out ShaderPropertyState state, out string error))
            {
                failureReason = error;
                journal = null;
                return false;
            }
            journal.shaderProperties.Add(state);
        }

        foreach (string keyword in usage.Keywords)
        {
            if (!ShaderReflectionBridge.TryCaptureKeyword(keyword, out ShaderKeywordState state, out string error))
            {
                failureReason = error;
                journal = null;
                return false;
            }
            journal.shaderKeywords.Add(state);
        }

        return true;
    }

    internal EffectPreviewVisualRollbackReport Rollback(string reason)
    {
        int failures = 0;
        List<string> failed = new();

        for (int i = shaderProperties.Count - 1; i >= 0; i--)
        {
            ShaderPropertyState state = shaderProperties[i];
            if (ShaderReflectionBridge.Restore(state))
                continue;
            failures++;
            if (failed.Count < 4)
                failed.Add(state.Property.DebugName);
        }

        for (int i = shaderKeywords.Count - 1; i >= 0; i--)
        {
            ShaderKeywordState state = shaderKeywords[i];
            if (ShaderReflectionBridge.RestoreKeyword(state))
                continue;
            failures++;
            if (failed.Count < 4)
                failed.Add("keyword:" + state.Keyword);
        }

        string summary = failures == 0
            ? string.Empty
            : "visual-state rollback failed for " + failures + " shader entries" +
              (failed.Count == 0 ? string.Empty : " [" + string.Join(", ", failed) + "]") +
              (string.IsNullOrWhiteSpace(reason) ? string.Empty : " during " + reason);

        return new EffectPreviewVisualRollbackReport(failures > 0, summary);
    }

    private sealed class VisualShaderUsage
    {
        internal bool Safe = true;
        internal string FailureReason = string.Empty;
        internal HashSet<VisualShaderProperty> Properties = new();
        internal HashSet<string> Keywords = new(StringComparer.Ordinal);

        internal void Fail(string reason)
        {
            if (!Safe) return;
            Safe = false;
            FailureReason = string.IsNullOrWhiteSpace(reason)
                ? "unrestorable global shader state"
                : reason;
        }
    }

    private enum VisualShaderPropertyKind
    {
        Float,
        Int,
        Vector,
        Color,
        Texture
    }

    private readonly struct VisualShaderProperty : IEquatable<VisualShaderProperty>
    {
        internal VisualShaderProperty(int id, string name, VisualShaderPropertyKind kind)
        {
            Id = id;
            Name = name ?? string.Empty;
            Kind = kind;
        }

        internal int Id { get; }
        internal string Name { get; }
        internal VisualShaderPropertyKind Kind { get; }
        internal string DebugName => !string.IsNullOrEmpty(Name) ? Name : "#" + Id;

        public bool Equals(VisualShaderProperty other) => Id == other.Id && Kind == other.Kind;
        public override bool Equals(object obj) => obj is VisualShaderProperty other && Equals(other);
        public override int GetHashCode() => (Id * 397) ^ (int)Kind;
    }

    private readonly struct ShaderPropertyState
    {
        internal ShaderPropertyState(VisualShaderProperty property, object value)
        {
            Property = property;
            Value = value;
        }

        internal VisualShaderProperty Property { get; }
        internal object Value { get; }
    }

    private readonly struct ShaderKeywordState
    {
        internal ShaderKeywordState(string keyword, bool enabled)
        {
            Keyword = keyword ?? string.Empty;
            Enabled = enabled;
        }

        internal string Keyword { get; }
        internal bool Enabled { get; }
    }

    private static class VisualShaderUsageScanner
    {
        internal static VisualShaderUsage Discover(string effectTypeName)
        {
            VisualShaderUsage usage = new();
            HashSet<MethodBase> roots = new();
            MethodInfo vanillaLoaded = typeof(global::Room).GetMethod(
                nameof(global::Room.Loaded),
                BindingFlags.Instance | BindingFlags.Public);

            CollectEffectRoots(vanillaLoaded, effectTypeName, roots, usage);
            On.Room.hook_Loaded[] hooks = HookGenLoadedHookDiscovery.Discover(vanillaLoaded);
            for (int i = 0; i < hooks.Length; i++)
                CollectEffectRoots(hooks[i]?.Method, effectTypeName, roots, usage);

            HashSet<MethodBase> visited = new();
            foreach (MethodBase root in roots)
            {
                if (!usage.Safe) break;
                ScanMethodTree(root, root.Module.Assembly, usage, visited, 0);

                if (root is ConstructorInfo ctor)
                    ScanRuntimeSurface(ctor.DeclaringType, usage, visited);
            }

            return usage;
        }

        private static void CollectEffectRoots(
            MethodInfo method,
            string effectTypeName,
            HashSet<MethodBase> roots,
            VisualShaderUsage usage)
        {
            if (method == null || !usage.Safe) return;
            List<VisualInstruction> il = VisualInstructionReader.Read(method);
            if (il.Count == 0) return;

            for (int i = 0; i < il.Count; i++)
            {
                if (il[i].Operand is not FieldInfo effectField ||
                    !MatchesEffectType(effectField, effectTypeName))
                    continue;

                int limit = Math.Min(il.Count, i + MaxEffectScanInstructions);
                for (int j = i + 1; j < limit; j++)
                {
                    VisualInstruction instruction = il[j];
                    if (instruction.Operand is MethodBase called)
                    {
                        if (IsShaderMutation(called))
                            TryRecordShaderMutation(method, il, j, called, usage);

                        if (instruction.OpCode == OpCodes.Newobj && called is ConstructorInfo ctor)
                        {
                            Type created = ctor.DeclaringType;
                            if (created != null &&
                                typeof(UpdatableAndDeletable).IsAssignableFrom(created) &&
                                !typeof(PhysicalObject).IsAssignableFrom(created))
                                roots.Add(ctor);
                        }
                        else if (called.Module?.Assembly == method.Module.Assembly &&
                                 called.DeclaringType != typeof(global::Room))
                        {
                            roots.Add(called);
                        }

                        if (called.DeclaringType == typeof(global::Room) &&
                            string.Equals(called.Name, nameof(global::Room.AddObject), StringComparison.Ordinal))
                            break;
                    }

                    if (instruction.OpCode.FlowControl == FlowControl.Return ||
                        instruction.OpCode.FlowControl == FlowControl.Throw)
                        break;
                }
            }
        }

        private static void ScanRuntimeSurface(
            Type type,
            VisualShaderUsage usage,
            HashSet<MethodBase> visited)
        {
            if (type == null || !usage.Safe) return;
            Assembly assembly = type.Assembly;

            ConstructorInfo[] ctors;
            MethodInfo[] methods;
            try
            {
                ctors = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                          BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            }
            catch
            {
                return;
            }

            for (int i = 0; i < ctors.Length && usage.Safe; i++)
                ScanMethodTree(ctors[i], assembly, usage, visited, 0);
            for (int i = 0; i < methods.Length && usage.Safe; i++)
                ScanMethodTree(methods[i], assembly, usage, visited, 0);
        }

        private static void ScanMethodTree(
            MethodBase method,
            Assembly rootAssembly,
            VisualShaderUsage usage,
            HashSet<MethodBase> visited,
            int depth)
        {
            if (method == null || !usage.Safe || depth > MaxRecursiveDepth ||
                visited.Count >= MaxRecursiveMethods || !visited.Add(method))
                return;

            List<VisualInstruction> il = VisualInstructionReader.Read(method);
            if (il.Count == 0) return;

            for (int i = 0; i < il.Count; i++)
            {
                if (il[i].Operand is not MethodBase called) continue;

                if (IsShaderMutation(called))
                {
                    TryRecordShaderMutation(method, il, i, called, usage);
                    if (!usage.Safe) return;
                    continue;
                }

                if (called.Module?.Assembly != rootAssembly || called == method)
                    continue;

                ScanMethodTree(called, rootAssembly, usage, visited, depth + 1);
                if (called is ConstructorInfo ctor && ctor.DeclaringType != null &&
                    typeof(UpdatableAndDeletable).IsAssignableFrom(ctor.DeclaringType) &&
                    !typeof(PhysicalObject).IsAssignableFrom(ctor.DeclaringType))
                {
                    ScanRuntimeSurface(ctor.DeclaringType, usage, visited);
                }
            }
        }

        private static bool IsShaderMutation(MethodBase method)
        {
            if (method?.DeclaringType != typeof(Shader)) return false;
            string name = method.Name ?? string.Empty;
            return name.StartsWith("SetGlobal", StringComparison.Ordinal) ||
                   string.Equals(name, "EnableKeyword", StringComparison.Ordinal) ||
                   string.Equals(name, "DisableKeyword", StringComparison.Ordinal);
        }

        private static void TryRecordShaderMutation(
            MethodBase owner,
            List<VisualInstruction> il,
            int callIndex,
            MethodBase shaderCall,
            VisualShaderUsage usage)
        {
            string name = shaderCall.Name ?? string.Empty;
            if (name == "EnableKeyword" || name == "DisableKeyword")
            {
                if (!TryFindUniqueString(il, callIndex, out string keyword))
                {
                    usage.Fail("effect uses a dynamic global shader keyword that cannot be restored safely");
                    return;
                }
                usage.Keywords.Add(keyword);
                return;
            }

            if (!TryResolveKind(name, out VisualShaderPropertyKind kind))
            {
                usage.Fail("effect uses unsupported Unity shader mutation " + name);
                return;
            }

            ParameterInfo[] parameters;
            try { parameters = shaderCall.GetParameters(); }
            catch
            {
                usage.Fail("could not inspect Unity shader mutation " + name);
                return;
            }
            if (parameters.Length == 0)
            {
                usage.Fail("shader mutation has no property identifier: " + name);
                return;
            }

            Type keyType = parameters[0].ParameterType;
            if (keyType == typeof(int))
            {
                if (!TryFindUniqueStaticIntField(il, callIndex, out FieldInfo field))
                {
                    usage.Fail("effect uses a dynamic shader property id in " + (owner?.Name ?? "<method>"));
                    return;
                }

                int id;
                try { id = (int)field.GetValue(null); }
                catch
                {
                    usage.Fail("could not read shader property id field " + field.Name);
                    return;
                }
                usage.Properties.Add(new VisualShaderProperty(id, field.Name, kind));
                return;
            }

            if (keyType == typeof(string))
            {
                if (!TryFindUniqueString(il, callIndex, out string propertyName))
                {
                    usage.Fail("effect uses a dynamic shader property name in " + (owner?.Name ?? "<method>"));
                    return;
                }

                int id;
                try { id = Shader.PropertyToID(propertyName); }
                catch
                {
                    usage.Fail("could not resolve shader property " + propertyName);
                    return;
                }
                usage.Properties.Add(new VisualShaderProperty(id, propertyName, kind));
                return;
            }

            usage.Fail("unsupported shader property identifier type " + keyType.FullName);
        }

        private static bool TryResolveKind(string methodName, out VisualShaderPropertyKind kind)
        {
            switch (methodName)
            {
                case "SetGlobalFloat": kind = VisualShaderPropertyKind.Float; return true;
                case "SetGlobalInt": kind = VisualShaderPropertyKind.Int; return true;
                case "SetGlobalVector": kind = VisualShaderPropertyKind.Vector; return true;
                case "SetGlobalColor": kind = VisualShaderPropertyKind.Color; return true;
                case "SetGlobalTexture": kind = VisualShaderPropertyKind.Texture; return true;
                default:
                    kind = default;
                    return false;
            }
        }

        private static bool TryFindUniqueStaticIntField(
            List<VisualInstruction> il,
            int callIndex,
            out FieldInfo result)
        {
            result = null;
            int start = Math.Max(0, callIndex - MaxPropertySearchInstructions);
            for (int i = callIndex - 1; i >= start; i--)
            {
                if (il[i].Operand is not FieldInfo field || !field.IsStatic || field.FieldType != typeof(int))
                    continue;
                if (result != null && !ReferenceEquals(result, field))
                    return false;
                result = field;
            }
            return result != null;
        }

        private static bool TryFindUniqueString(
            List<VisualInstruction> il,
            int callIndex,
            out string result)
        {
            result = string.Empty;
            int start = Math.Max(0, callIndex - MaxPropertySearchInstructions);
            for (int i = callIndex - 1; i >= start; i--)
            {
                if (il[i].Operand is not string value || string.IsNullOrEmpty(value))
                    continue;
                if (!string.IsNullOrEmpty(result) && !string.Equals(result, value, StringComparison.Ordinal))
                    return false;
                result = value;
            }
            return !string.IsNullOrEmpty(result);
        }

        private static bool MatchesEffectType(FieldInfo field, string requested)
        {
            if (field == null || !field.IsStatic || field.FieldType != typeof(RoomSettings.RoomEffect.Type))
                return false;
            if (string.Equals(field.Name, requested, StringComparison.Ordinal))
                return true;
            try
            {
                return field.GetValue(null) is RoomSettings.RoomEffect.Type type &&
                       string.Equals(type.value, requested, StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }
    }

    private static class ShaderReflectionBridge
    {
        private static readonly Type ShaderType = typeof(Shader);
        private static readonly Dictionary<string, MethodInfo> MethodCache = new(StringComparer.Ordinal);

        internal static bool TryCapture(
            VisualShaderProperty property,
            out ShaderPropertyState state,
            out string error)
        {
            state = default;
            error = string.Empty;
            string getter = property.Kind switch
            {
                VisualShaderPropertyKind.Float => "GetGlobalFloat",
                VisualShaderPropertyKind.Int => "GetGlobalInt",
                VisualShaderPropertyKind.Vector => "GetGlobalVector",
                VisualShaderPropertyKind.Color => "GetGlobalColor",
                VisualShaderPropertyKind.Texture => "GetGlobalTexture",
                _ => string.Empty
            };

            MethodInfo method = FindMethod(getter, typeof(int));
            if (method == null)
            {
                error = "Unity does not expose " + getter + "(int) needed to restore " + property.DebugName;
                return false;
            }

            try
            {
                object value = method.Invoke(null, new object[] { property.Id });
                state = new ShaderPropertyState(property, value);
                return true;
            }
            catch (Exception ex)
            {
                error = "could not snapshot shader property " + property.DebugName + ": " +
                        (ex.GetBaseException().Message ?? ex.Message);
                return false;
            }
        }

        internal static bool Restore(ShaderPropertyState state)
        {
            string setter;
            Type valueType;
            switch (state.Property.Kind)
            {
                case VisualShaderPropertyKind.Float:
                    setter = "SetGlobalFloat";
                    valueType = typeof(float);
                    break;
                case VisualShaderPropertyKind.Int:
                    setter = "SetGlobalInt";
                    valueType = typeof(int);
                    break;
                case VisualShaderPropertyKind.Vector:
                    setter = "SetGlobalVector";
                    valueType = typeof(Vector4);
                    break;
                case VisualShaderPropertyKind.Color:
                    setter = "SetGlobalColor";
                    valueType = typeof(Color);
                    break;
                case VisualShaderPropertyKind.Texture:
                    setter = "SetGlobalTexture";
                    valueType = typeof(Texture);
                    break;
                default:
                    return false;
            }

            MethodInfo method = FindMethod(setter, typeof(int), valueType);
            if (method == null) return false;
            try
            {
                method.Invoke(null, new[] { (object)state.Property.Id, state.Value });
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryCaptureKeyword(
            string keyword,
            out ShaderKeywordState state,
            out string error)
        {
            state = default;
            error = string.Empty;
            MethodInfo method = FindMethod("IsKeywordEnabled", typeof(string));
            if (method == null)
            {
                error = "Unity does not expose Shader.IsKeywordEnabled(string) needed for preview rollback";
                return false;
            }
            try
            {
                bool enabled = method.Invoke(null, new object[] { keyword }) is bool value && value;
                state = new ShaderKeywordState(keyword, enabled);
                return true;
            }
            catch (Exception ex)
            {
                error = "could not snapshot shader keyword " + keyword + ": " +
                        (ex.GetBaseException().Message ?? ex.Message);
                return false;
            }
        }

        internal static bool RestoreKeyword(ShaderKeywordState state)
        {
            MethodInfo method = FindMethod(
                state.Enabled ? "EnableKeyword" : "DisableKeyword",
                typeof(string));
            if (method == null) return false;
            try
            {
                method.Invoke(null, new object[] { state.Keyword });
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static MethodInfo FindMethod(string name, params Type[] parameters)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string key = name + "(" + string.Join(",", Array.ConvertAll(parameters, t => t.FullName)) + ")";
            lock (MethodCache)
            {
                if (MethodCache.TryGetValue(key, out MethodInfo cached))
                    return cached;
            }

            MethodInfo found;
            try
            {
                found = ShaderType.GetMethod(
                    name,
                    BindingFlags.Static | BindingFlags.Public,
                    binder: null,
                    types: parameters,
                    modifiers: null);
            }
            catch
            {
                found = null;
            }

            lock (MethodCache) MethodCache[key] = found;
            return found;
        }
    }

    private readonly struct VisualInstruction
    {
        internal VisualInstruction(int offset, OpCode opCode, object operand)
        {
            Offset = offset;
            OpCode = opCode;
            Operand = operand;
        }

        internal int Offset { get; }
        internal OpCode OpCode { get; }
        internal object Operand { get; }
    }

    private static class VisualInstructionReader
    {
        private static readonly OpCode[] OneByte = new OpCode[256];
        private static readonly OpCode[] TwoByte = new OpCode[256];

        static VisualInstructionReader()
        {
            FieldInfo[] fields = typeof(OpCodes).GetFields(BindingFlags.Static | BindingFlags.Public);
            for (int i = 0; i < fields.Length; i++)
            {
                if (fields[i].GetValue(null) is not OpCode op) continue;
                ushort value = unchecked((ushort)op.Value);
                if (op.Size == 1) OneByte[value & 0xff] = op;
                else if (op.Size == 2) TwoByte[value & 0xff] = op;
            }
        }

        internal static List<VisualInstruction> Read(MethodBase method)
        {
            List<VisualInstruction> result = new();
            byte[] bytes;
            try { bytes = method?.GetMethodBody()?.GetILAsByteArray(); }
            catch { return result; }
            if (bytes == null || bytes.Length == 0) return result;

            int p = 0;
            while (p < bytes.Length)
            {
                int offset = p;
                OpCode op;
                byte first = bytes[p++];
                if (first == 0xfe)
                {
                    if (p >= bytes.Length) break;
                    op = TwoByte[bytes[p++]];
                }
                else
                {
                    op = OneByte[first];
                }

                int operandStart = p;
                int operandSize = OperandSize(op.OperandType, bytes, p);
                if (operandSize < 0 || p + operandSize > bytes.Length) break;

                object operand = null;
                if (operandSize >= 4)
                {
                    int token = BitConverter.ToInt32(bytes, operandStart);
                    if (op.OperandType == OperandType.InlineMethod)
                        operand = ResolveMethod(method, token);
                    else if (op.OperandType == OperandType.InlineField)
                        operand = ResolveField(method, token);
                    else if (op.OperandType == OperandType.InlineString)
                        operand = ResolveString(method, token);
                }

                result.Add(new VisualInstruction(offset, op, operand));
                p += operandSize;
            }
            return result;
        }

        private static MethodBase ResolveMethod(MethodBase context, int token)
        {
            try
            {
                Type[] typeArgs = context.DeclaringType?.IsGenericType == true
                    ? context.DeclaringType.GetGenericArguments()
                    : Type.EmptyTypes;
                Type[] methodArgs = context.IsGenericMethod ? context.GetGenericArguments() : Type.EmptyTypes;
                return context.Module.ResolveMethod(token, typeArgs, methodArgs);
            }
            catch
            {
                try { return context.Module.ResolveMethod(token); }
                catch { return null; }
            }
        }

        private static FieldInfo ResolveField(MethodBase context, int token)
        {
            try
            {
                Type[] typeArgs = context.DeclaringType?.IsGenericType == true
                    ? context.DeclaringType.GetGenericArguments()
                    : Type.EmptyTypes;
                Type[] methodArgs = context.IsGenericMethod ? context.GetGenericArguments() : Type.EmptyTypes;
                return context.Module.ResolveField(token, typeArgs, methodArgs);
            }
            catch
            {
                try { return context.Module.ResolveField(token); }
                catch { return null; }
            }
        }

        private static string ResolveString(MethodBase context, int token)
        {
            try { return context.Module.ResolveString(token); }
            catch { return null; }
        }

        private static int OperandSize(OperandType type, byte[] il, int p)
        {
            return type switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget => 1,
                OperandType.ShortInlineI => 1,
                OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineBrTarget => 4,
                OperandType.InlineField => 4,
                OperandType.InlineI => 4,
                OperandType.InlineMethod => 4,
                OperandType.InlineSig => 4,
                OperandType.InlineString => 4,
                OperandType.InlineTok => 4,
                OperandType.InlineType => 4,
                OperandType.ShortInlineR => 4,
                OperandType.InlineI8 => 8,
                OperandType.InlineR => 8,
                OperandType.InlineSwitch => SwitchSize(il, p),
                _ => -1
            };
        }

        private static int SwitchSize(byte[] il, int p)
        {
            if (p + 4 > il.Length) return -1;
            int count = BitConverter.ToInt32(il, p);
            if (count < 0 || count > 65535) return -1;
            return 4 + count * 4;
        }
    }
}

internal readonly struct EffectPreviewVisualRollbackReport
{
    internal EffectPreviewVisualRollbackReport(bool hasLeak, string summary)
    {
        HasLeak = hasLeak;
        Summary = summary ?? string.Empty;
    }

    internal bool HasLeak { get; }
    internal string Summary { get; }

    internal static EffectPreviewVisualRollbackReport Clean => new(false, string.Empty);
}
