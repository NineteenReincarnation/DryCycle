using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace DryCycle.DevUI.DevTool.Preview;

/// <summary>
/// Conservative fallback for load-time RoomEffect controllers whose constructor needs a small
/// scalar argument that the primary recipe scanner intentionally refuses to guess.
///
/// This layer still does not know anything about a specific mod. It only inspects the already-loaded
/// IL block that contains the requested RoomEffect.Type and a nearby UAD constructor. Supported
/// scalar inference is deliberately narrow: one float amount, one bool selector/constant, one int
/// constant, or one enum constant. Ambiguous blocks fail closed.
/// </summary>
internal static class EffectPreviewExtendedRecipeBootstrap
{
    private const int MaxBackwardInstructions = 96;
    private const int MaxImmediateConstantDistance = 8;

    internal static int TryBootstrap(
        global::Room room,
        RoomSettings.RoomEffect effect,
        EffectPreviewOwnershipTransaction transaction)
    {
        string typeName = effect?.type?.value;
        if (room == null || string.IsNullOrWhiteSpace(typeName) || transaction == null)
            return 0;

        MethodInfo vanillaLoaded = typeof(global::Room).GetMethod(
            nameof(global::Room.Loaded),
            BindingFlags.Instance | BindingFlags.Public);

        int committed = TryMethod(vanillaLoaded, room, effect, transaction);
        if (committed > 0)
            return committed;

        On.Room.hook_Loaded[] hooks = HookGenLoadedHookDiscovery.Discover(vanillaLoaded);
        for (int i = 0; i < hooks.Length; i++)
        {
            committed += TryMethod(hooks[i]?.Method, room, effect, transaction);
            if (committed > 0)
                break;
        }
        return committed;
    }

    private static int TryMethod(
        MethodInfo method,
        global::Room room,
        RoomSettings.RoomEffect effect,
        EffectPreviewOwnershipTransaction transaction)
    {
        if (method == null) return 0;

        List<DecodedInstruction> il = DecodedInstructionReader.Read(method);
        if (il.Count == 0) return 0;

        string requested = effect.type?.value ?? string.Empty;
        HashSet<string> tried = new(StringComparer.Ordinal);
        int committed = 0;

        for (int i = 0; i < il.Count; i++)
        {
            if (il[i].OpCode != OpCodes.Newobj || il[i].Operand is not ConstructorInfo ctor)
                continue;
            if (!SupportsExtendedConstructor(ctor))
                continue;

            int blockStart = FindBlockStart(il, i);
            if (!BlockContainsEffect(il, blockStart, i, requested))
                continue;

            string key = ConstructorKey(ctor);
            if (!tried.Add(key))
                continue;

            if (!TryBuildArguments(method, il, blockStart, i, ctor, room, effect, out object[] args))
                continue;

            UpdatableAndDeletable primary = null;
            EffectPreviewObjectCapture.CaptureResult nested = EffectPreviewObjectCapture.Capture(
                room,
                () => primary = ctor.Invoke(args) as UpdatableAndDeletable);

            if (!nested.Success || primary == null)
            {
                EffectPreviewOwnershipTransaction.DisposeCapturedObject(primary, room);
                DisposeAll(nested.Objects, room);
                continue;
            }

            if (transaction.CommitObject(primary))
                committed++;
            else
                EffectPreviewOwnershipTransaction.DisposeCapturedObject(primary, room);

            HashSet<UpdatableAndDeletable> seen =
                new(ReferenceEqualityComparer<UpdatableAndDeletable>.Instance) { primary };
            for (int n = 0; n < nested.Objects.Length; n++)
            {
                UpdatableAndDeletable obj = nested.Objects[n];
                if (obj == null || !seen.Add(obj)) continue;
                if (transaction.CommitObject(obj))
                    committed++;
                else
                    EffectPreviewOwnershipTransaction.DisposeCapturedObject(obj, room);
            }

            if (committed > 0)
                break;
        }

        return committed;
    }

    private static bool SupportsExtendedConstructor(ConstructorInfo ctor)
    {
        if (ctor == null || !ctor.IsPublic) return false;
        Type created = ctor.DeclaringType;
        if (created == null || created.IsAbstract ||
            !typeof(UpdatableAndDeletable).IsAssignableFrom(created) ||
            typeof(PhysicalObject).IsAssignableFrom(created))
            return false;

        ParameterInfo[] parameters = ctor.GetParameters();
        if (parameters.Length > 5) return false;

        int floats = 0;
        int bools = 0;
        int ints = 0;
        int enums = 0;

        for (int i = 0; i < parameters.Length; i++)
        {
            ParameterInfo parameter = parameters[i];
            Type type = parameter.ParameterType;
            if (type == typeof(global::Room) ||
                type == typeof(RoomSettings) ||
                type == typeof(RoomSettings.RoomEffect) ||
                type == typeof(RoomSettings.RoomEffect.Type) ||
                type == typeof(RoomCamera) ||
                type == typeof(RainWorldGame) ||
                type == typeof(World) ||
                type == typeof(AbstractRoom))
                continue;

            if (type == typeof(float))
            {
                floats++;
                if (floats > 1) return false;
                continue;
            }
            if (type == typeof(bool))
            {
                bools++;
                if (bools > 1) return false;
                continue;
            }
            if (type == typeof(int))
            {
                ints++;
                if (ints > 1) return false;
                continue;
            }
            if (type.IsEnum)
            {
                enums++;
                if (enums > 1) return false;
                continue;
            }
            if (parameter.HasDefaultValue)
                continue;
            return false;
        }

        // A bool and int together are too easy to confuse when both compile to ldc.i4.*.
        return !(bools > 0 && ints > 0);
    }

    private static bool TryBuildArguments(
        MethodInfo method,
        List<DecodedInstruction> il,
        int blockStart,
        int ctorIndex,
        ConstructorInfo ctor,
        global::Room room,
        RoomSettings.RoomEffect effect,
        out object[] args)
    {
        ParameterInfo[] parameters = ctor.GetParameters();
        args = new object[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            ParameterInfo parameter = parameters[i];
            Type type = parameter.ParameterType;

            if (type == typeof(global::Room)) args[i] = room;
            else if (type == typeof(RoomSettings)) args[i] = room.roomSettings;
            else if (type == typeof(RoomSettings.RoomEffect)) args[i] = effect;
            else if (type == typeof(RoomSettings.RoomEffect.Type)) args[i] = effect.type;
            else if (type == typeof(RoomCamera)) args[i] = ResolveCamera(room);
            else if (type == typeof(RainWorldGame)) args[i] = room.game;
            else if (type == typeof(World)) args[i] = room.world;
            else if (type == typeof(AbstractRoom)) args[i] = room.abstractRoom;
            else if (type == typeof(float))
            {
                if (!BlockReadsEffectAmount(il, blockStart, ctorIndex)) return false;
                args[i] = effect.amount;
            }
            else if (type == typeof(bool))
            {
                if (!TryInferBool(method, il, blockStart, ctorIndex, effect, parameters, i, out bool value))
                    return false;
                args[i] = value;
            }
            else if (type == typeof(int))
            {
                if (!TryInferIntConstant(method, il, blockStart, ctorIndex, out int value))
                    return false;
                args[i] = value;
            }
            else if (type.IsEnum)
            {
                if (!TryInferEnum(method, il, blockStart, ctorIndex, type, out object value))
                    return false;
                args[i] = value;
            }
            else if (parameter.HasDefaultValue)
            {
                args[i] = parameter.DefaultValue;
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    private static int FindBlockStart(List<DecodedInstruction> il, int ctorIndex)
    {
        int min = Math.Max(0, ctorIndex - MaxBackwardInstructions);
        for (int i = ctorIndex - 1; i >= min; i--)
        {
            if (il[i].Operand is MethodBase called && IsRoomAddObject(called))
                return i + 1;
            if (il[i].OpCode.FlowControl == FlowControl.Return ||
                il[i].OpCode.FlowControl == FlowControl.Throw)
                return i + 1;
        }
        return min;
    }

    private static bool BlockContainsEffect(
        List<DecodedInstruction> il,
        int start,
        int end,
        string requested)
    {
        for (int i = start; i < end; i++)
        {
            if (il[i].Operand is FieldInfo field &&
                TryEffectTypeValue(field, out string value) &&
                string.Equals(value, requested, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool BlockReadsEffectAmount(List<DecodedInstruction> il, int start, int end)
    {
        for (int i = Math.Max(start, end - 24); i < end; i++)
        {
            if (il[i].Operand is FieldInfo field &&
                field.DeclaringType == typeof(RoomSettings.RoomEffect) &&
                string.Equals(field.Name, "amount", StringComparison.Ordinal))
                return true;
            if (il[i].Operand is MethodBase method &&
                method.DeclaringType == typeof(RoomSettings.RoomEffect) &&
                method.Name.IndexOf("GetAmount", StringComparison.Ordinal) >= 0)
                return true;
        }
        return false;
    }

    private static bool TryInferBool(
        MethodInfo method,
        List<DecodedInstruction> il,
        int blockStart,
        int ctorIndex,
        RoomSettings.RoomEffect effect,
        ParameterInfo[] parameters,
        int parameterIndex,
        out bool value)
    {
        value = false;

        // Direct bool constants are accepted only when they are loaded immediately before newobj.
        int immediateStart = Math.Max(blockStart, ctorIndex - MaxImmediateConstantDistance);
        for (int i = ctorIndex - 1; i >= immediateStart; i--)
        {
            if (!TryReadIntConstant(method, il[i], out int constant))
                continue;
            if (constant == 0 || constant == 1)
            {
                value = constant != 0;
                return true;
            }
            break;
        }

        // Common universal pattern: two RoomEffect types share one constructor and the final bool
        // selects which variant is active, e.g. Lightning/BkgOnlyLightning. Accept this only for the
        // final constructor parameter and only when exactly two distinct effect types occur in the
        // same load block. The nearest type comparison is treated as the "true" selector.
        if (parameterIndex != parameters.Length - 1)
            return false;

        List<string> effectTypes = new();
        string nearest = string.Empty;
        for (int i = blockStart; i < ctorIndex; i++)
        {
            if (il[i].Operand is not FieldInfo field || !TryEffectTypeValue(field, out string typeName))
                continue;
            if (!Contains(effectTypes, typeName))
                effectTypes.Add(typeName);
            nearest = typeName;
        }

        if (effectTypes.Count != 2 || string.IsNullOrEmpty(nearest))
            return false;

        value = string.Equals(effect.type?.value, nearest, StringComparison.Ordinal);
        return true;
    }

    private static bool TryInferIntConstant(
        MethodInfo method,
        List<DecodedInstruction> il,
        int blockStart,
        int ctorIndex,
        out int value)
    {
        int start = Math.Max(blockStart, ctorIndex - MaxImmediateConstantDistance);
        for (int i = ctorIndex - 1; i >= start; i--)
        {
            if (TryReadIntConstant(method, il[i], out value))
                return true;
        }
        value = 0;
        return false;
    }

    private static bool TryInferEnum(
        MethodInfo method,
        List<DecodedInstruction> il,
        int blockStart,
        int ctorIndex,
        Type enumType,
        out object value)
    {
        int start = Math.Max(blockStart, ctorIndex - 16);
        for (int i = ctorIndex - 1; i >= start; i--)
        {
            if (il[i].Operand is FieldInfo field && field.IsStatic && field.FieldType == enumType)
            {
                try
                {
                    value = field.GetValue(null);
                    return value != null;
                }
                catch { }
            }
        }

        if (TryInferIntConstant(method, il, blockStart, ctorIndex, out int raw))
        {
            try
            {
                value = Enum.ToObject(enumType, raw);
                return true;
            }
            catch { }
        }

        value = null;
        return false;
    }

    private static bool TryReadIntConstant(MethodInfo method, DecodedInstruction instruction, out int value)
    {
        OpCode op = instruction.OpCode;
        if (op == OpCodes.Ldc_I4_M1) { value = -1; return true; }
        if (op == OpCodes.Ldc_I4_0) { value = 0; return true; }
        if (op == OpCodes.Ldc_I4_1) { value = 1; return true; }
        if (op == OpCodes.Ldc_I4_2) { value = 2; return true; }
        if (op == OpCodes.Ldc_I4_3) { value = 3; return true; }
        if (op == OpCodes.Ldc_I4_4) { value = 4; return true; }
        if (op == OpCodes.Ldc_I4_5) { value = 5; return true; }
        if (op == OpCodes.Ldc_I4_6) { value = 6; return true; }
        if (op == OpCodes.Ldc_I4_7) { value = 7; return true; }
        if (op == OpCodes.Ldc_I4_8) { value = 8; return true; }

        byte[] bytes;
        try { bytes = method?.GetMethodBody()?.GetILAsByteArray(); }
        catch { bytes = null; }
        if (bytes == null) { value = 0; return false; }

        int operand = instruction.Offset + op.Size;
        if (op == OpCodes.Ldc_I4_S && operand < bytes.Length)
        {
            value = unchecked((sbyte)bytes[operand]);
            return true;
        }
        if (op == OpCodes.Ldc_I4 && operand + 4 <= bytes.Length)
        {
            value = BitConverter.ToInt32(bytes, operand);
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryEffectTypeValue(FieldInfo field, out string value)
    {
        value = string.Empty;
        if (field == null || field.FieldType != typeof(RoomSettings.RoomEffect.Type))
            return false;

        try
        {
            if (field.GetValue(null) is RoomSettings.RoomEffect.Type type)
            {
                value = type.value ?? string.Empty;
                return !string.IsNullOrEmpty(value);
            }
        }
        catch { }

        value = field.Name ?? string.Empty;
        return !string.IsNullOrEmpty(value);
    }

    private static RoomCamera ResolveCamera(global::Room room)
    {
        RoomCamera[] cameras = room?.game?.cameras;
        if (cameras == null || cameras.Length == 0) return null;
        for (int i = 0; i < cameras.Length; i++)
            if (cameras[i] != null && ReferenceEquals(cameras[i].room, room)) return cameras[i];
        return cameras[0];
    }

    private static bool IsRoomAddObject(MethodBase method) =>
        method?.DeclaringType == typeof(global::Room) &&
        string.Equals(method.Name, nameof(global::Room.AddObject), StringComparison.Ordinal);

    private static string ConstructorKey(ConstructorInfo ctor)
    {
        try { return ctor.Module.ModuleVersionId + ":" + ctor.MetadataToken; }
        catch { return ctor.DeclaringType?.AssemblyQualifiedName + ":" + ctor; }
    }

    private static bool Contains(List<string> values, string value)
    {
        for (int i = 0; i < values.Count; i++)
            if (string.Equals(values[i], value, StringComparison.Ordinal)) return true;
        return false;
    }

    private static void DisposeAll(UpdatableAndDeletable[] objects, global::Room room)
    {
        if (objects == null) return;
        for (int i = 0; i < objects.Length; i++)
            EffectPreviewOwnershipTransaction.DisposeCapturedObject(objects[i], room);
    }
}
