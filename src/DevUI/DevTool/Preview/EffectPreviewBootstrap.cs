using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Preview;

/// <summary>
/// Second-stage preview bootstrap for effects whose runtime object is normally created only from
/// Room.Loaded. The implementation intentionally has no knowledge of mod ids, namespaces or custom
/// registries. It uses three generic strategies, from safest to broadest:
///
/// 1. Infer a constructor recipe directly from the IL around the matching RoomEffect.Type branch.
///    This works for vanilla Room.Loaded and third-party HookGen callbacks without executing the
///    callback itself, so unrelated load-time code is never replayed.
/// 2. For simple HookGen callbacks that are proven free of obvious persistent/global mutations,
///    run a reversible A/B probe with Room.AddObject captured rather than committed.
/// 3. If neither path produced an object, use a conservative loaded-type naming convention.
///
/// Every committed runtime object is owned by EffectPreviewOwnershipTransaction. Unknown or
/// suspicious patterns fail closed to the stage-one temporary RoomEffect overlay.
/// </summary>
internal static class EffectPreviewBootstrapper
{
    internal static void Bootstrap(
        global::Room room,
        RoomSettings settings,
        RoomSettings.RoomEffect previewEffect,
        EffectPreviewOwnershipTransaction transaction)
    {
        if (room == null || settings?.effects == null || previewEffect == null || transaction == null)
            return;

        int recipeObjects = EffectConstructorRecipeBootstrap.TryBootstrap(
            room,
            previewEffect,
            transaction);
        if (recipeObjects > 0)
            return;

        ProbeProduct product = LoadedHookReplayProbe.Probe(room, settings, previewEffect);
        int committed = 0;
        for (int i = 0; i < product.Objects.Count; i++)
        {
            if (transaction.CommitObject(product.Objects[i]))
                committed++;
        }

        foreach (KeyValuePair<FieldInfo, ProbeFieldDelta> pair in product.FieldDeltas)
        {
            ProbeFieldDelta delta = pair.Value;
            transaction.ApplyFieldMutation(pair.Key, delta.OriginalValue, delta.PreviewValue);
        }

        if (committed == 0)
            ConstructorConventionBootstrap.TryBootstrap(room, previewEffect, transaction);
    }
}

/// <summary>
/// Reads constructor recipes from already-loaded IL rather than re-running Room.Loaded. A recipe is
/// accepted only when a matching RoomEffect.Type static field is followed by a very small/simple
/// conditional branch, construction of an UpdatableAndDeletable, and Room.AddObject. Additional
/// conditional branches make the recipe ambiguous and are rejected instead of guessing.
/// </summary>
internal static class EffectConstructorRecipeBootstrap
{
    private const int MaxBranchScanInstructions = 64;
    private const int MaxConstructorDistanceFromAdd = 14;

    internal static int TryBootstrap(
        global::Room room,
        RoomSettings.RoomEffect effect,
        EffectPreviewOwnershipTransaction transaction)
    {
        string typeName = effect?.type?.value;
        if (room == null || string.IsNullOrWhiteSpace(typeName) || transaction == null)
            return 0;

        List<ConstructorInfo> recipes = new();
        HashSet<string> recipeKeys = new(StringComparer.Ordinal);

        MethodInfo vanillaLoaded = typeof(global::Room).GetMethod(
            nameof(global::Room.Loaded),
            BindingFlags.Instance | BindingFlags.Public);
        CollectRecipes(vanillaLoaded, typeName, recipes, recipeKeys);

        On.Room.hook_Loaded[] hooks = HookGenLoadedHookDiscovery.Discover(vanillaLoaded);
        for (int i = 0; i < hooks.Length; i++)
            CollectRecipes(hooks[i]?.Method, typeName, recipes, recipeKeys);

        int committed = 0;
        for (int i = 0; i < recipes.Count; i++)
        {
            ConstructorInfo ctor = recipes[i];
            UpdatableAndDeletable primary = null;
            EffectPreviewObjectCapture.CaptureResult nested = EffectPreviewObjectCapture.Capture(
                room,
                () => primary = ctor.Invoke(
                    ConstructorConventionBootstrap.BuildArguments(ctor, room, effect)) as UpdatableAndDeletable);

            if (!nested.Success || primary == null)
            {
                EffectPreviewOwnershipTransaction.DisposeCapturedObject(primary, room);
                DisposeAll(nested.Objects, room);
                continue;
            }

            if (transaction.CommitObject(primary))
                committed++;

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
        }
        return committed;
    }

    private static void CollectRecipes(
        MethodInfo method,
        string effectTypeName,
        List<ConstructorInfo> recipes,
        HashSet<string> recipeKeys)
    {
        if (method == null) return;
        List<DecodedInstruction> il = DecodedInstructionReader.Read(method);
        if (il.Count == 0) return;

        for (int i = 0; i < il.Count; i++)
        {
            if (il[i].Operand is not FieldInfo effectField ||
                !MatchesEffectTypeField(effectField, effectTypeName))
                continue;

            int conditionalBranches = 0;
            int limit = Math.Min(il.Count, i + MaxBranchScanInstructions);
            for (int j = i + 1; j < limit; j++)
            {
                DecodedInstruction instruction = il[j];

                if (instruction.Operand is FieldInfo nextEffectField &&
                    nextEffectField.FieldType == typeof(RoomSettings.RoomEffect.Type))
                    break;

                if (instruction.OpCode.FlowControl == FlowControl.Cond_Branch)
                {
                    conditionalBranches++;
                    if (conditionalBranches > 1)
                        break;
                }
                else if (instruction.OpCode.FlowControl == FlowControl.Branch ||
                         instruction.OpCode.FlowControl == FlowControl.Return ||
                         instruction.OpCode.FlowControl == FlowControl.Throw)
                {
                    break;
                }

                if (instruction.Operand is not MethodBase called || !IsRoomAddObject(called))
                    continue;

                ConstructorInfo ctor = FindNearestConstructor(il, i + 1, j);
                if (ctor != null && ConstructorConventionBootstrap.SupportsConstructor(ctor))
                    AddRecipe(ctor, recipes, recipeKeys);
            }
        }
    }

    private static ConstructorInfo FindNearestConstructor(
        List<DecodedInstruction> il,
        int branchStart,
        int addObjectIndex)
    {
        int min = Math.Max(branchStart, addObjectIndex - MaxConstructorDistanceFromAdd);
        for (int i = addObjectIndex - 1; i >= min; i--)
        {
            if (il[i].OpCode != OpCodes.Newobj || il[i].Operand is not ConstructorInfo ctor)
                continue;

            Type createdType = ctor.DeclaringType;
            if (createdType == null || createdType.IsAbstract ||
                !typeof(UpdatableAndDeletable).IsAssignableFrom(createdType) ||
                typeof(PhysicalObject).IsAssignableFrom(createdType))
                continue;
            return ctor;
        }
        return null;
    }

    private static bool MatchesEffectTypeField(FieldInfo field, string requested)
    {
        if (field == null || field.FieldType != typeof(RoomSettings.RoomEffect.Type))
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

    private static bool IsRoomAddObject(MethodBase method) =>
        method?.DeclaringType == typeof(global::Room) &&
        string.Equals(method.Name, nameof(global::Room.AddObject), StringComparison.Ordinal);

    private static void AddRecipe(
        ConstructorInfo ctor,
        List<ConstructorInfo> recipes,
        HashSet<string> keys)
    {
        string key;
        try { key = ctor.Module.ModuleVersionId + ":" + ctor.MetadataToken; }
        catch { key = ctor.DeclaringType?.AssemblyQualifiedName + ":" + ctor; }
        if (!keys.Add(key)) return;
        recipes.Add(ctor);
    }

    private static void DisposeAll(UpdatableAndDeletable[] objects, global::Room room)
    {
        if (objects == null) return;
        for (int i = 0; i < objects.Length; i++)
            EffectPreviewOwnershipTransaction.DisposeCapturedObject(objects[i], room);
    }
}

internal readonly struct DecodedInstruction
{
    internal DecodedInstruction(int offset, OpCode opCode, object operand)
    {
        Offset = offset;
        OpCode = opCode;
        Operand = operand;
    }

    internal int Offset { get; }
    internal OpCode OpCode { get; }
    internal object Operand { get; }
}

internal static class DecodedInstructionReader
{
    private static readonly OpCode[] OneByte = new OpCode[256];
    private static readonly OpCode[] TwoByte = new OpCode[256];

    static DecodedInstructionReader()
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

    internal static List<DecodedInstruction> Read(MethodBase method)
    {
        List<DecodedInstruction> result = new();
        byte[] il;
        try { il = method?.GetMethodBody()?.GetILAsByteArray(); }
        catch { return result; }
        if (il == null || il.Length == 0) return result;

        int p = 0;
        while (p < il.Length)
        {
            int offset = p;
            OpCode op;
            byte first = il[p++];
            if (first == 0xfe)
            {
                if (p >= il.Length) break;
                op = TwoByte[il[p++]];
            }
            else
            {
                op = OneByte[first];
            }

            int operandStart = p;
            int operandSize = OperandSize(op.OperandType, il, p);
            if (operandSize < 0 || p + operandSize > il.Length) break;

            object operand = null;
            if (operandSize >= 4)
            {
                int token = BitConverter.ToInt32(il, operandStart);
                if (op.OperandType == OperandType.InlineMethod)
                    operand = ResolveMethod(method, token);
                else if (op.OperandType == OperandType.InlineField)
                    operand = ResolveField(method, token);
            }

            result.Add(new DecodedInstruction(offset, op, operand));
            p += operandSize;
        }
        return result;
    }

    internal static MethodBase ResolveMethod(MethodBase context, int token)
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

    internal static FieldInfo ResolveField(MethodBase context, int token)
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

    internal static int OperandSize(OperandType type, byte[] il, int p)
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

internal sealed class ProbeProduct
{
    internal List<UpdatableAndDeletable> Objects { get; } = new();
    internal Dictionary<FieldInfo, ProbeFieldDelta> FieldDeltas { get; } = new();
}

internal readonly struct ProbeFieldDelta
{
    internal ProbeFieldDelta(object originalValue, object previewValue)
    {
        OriginalValue = originalValue;
        PreviewValue = previewValue;
    }

    internal object OriginalValue { get; }
    internal object PreviewValue { get; }
}

internal static class LoadedHookReplayProbe
{
    private static readonly MethodInfo RoomLoadedMethod =
        typeof(global::Room).GetMethod(nameof(global::Room.Loaded), BindingFlags.Instance | BindingFlags.Public);
    private static readonly Dictionary<MethodInfo, bool> CandidateCache = new();
    private static readonly HashSet<string> LoggedFailures = new(StringComparer.Ordinal);

    internal static ProbeProduct Probe(
        global::Room room,
        RoomSettings settings,
        RoomSettings.RoomEffect previewEffect)
    {
        ProbeProduct product = new();
        if (RoomLoadedMethod == null || room == null || settings?.effects == null || previewEffect == null)
            return product;

        RemoveExact(settings.effects, previewEffect);
        try
        {
            On.Room.hook_Loaded[] hooks = HookGenLoadedHookDiscovery.Discover(RoomLoadedMethod);
            for (int i = 0; i < hooks.Length; i++)
            {
                On.Room.hook_Loaded hook = hooks[i];
                if (hook == null || !IsCandidate(hook)) continue;
                ProbeOne(hook, room, settings, previewEffect, product);
            }
        }
        finally
        {
            EnsurePreviewFirst(settings.effects, previewEffect);
        }
        return product;
    }

    private static void ProbeOne(
        On.Room.hook_Loaded hook,
        global::Room room,
        RoomSettings settings,
        RoomSettings.RoomEffect previewEffect,
        ProbeProduct product)
    {
        RoomProbeState original = RoomProbeState.Capture(room, settings);

        EffectPreviewObjectCapture.CaptureResult baseline = EffectPreviewObjectCapture.Capture(
            room,
            () => hook(ProbeOrigLoaded, room));
        Dictionary<FieldInfo, object> baselineFields = RoomProbeState.CaptureSafeRoomFields(room);
        original.Restore(room, settings);

        if (!baseline.Success)
        {
            DisposeAll(baseline.Objects, room);
            LogHookFailureOnce(hook, "baseline", baseline.Error);
            return;
        }

        EnsurePreviewFirst(settings.effects, previewEffect);
        EffectPreviewObjectCapture.CaptureResult variant = EffectPreviewObjectCapture.Capture(
            room,
            () => hook(ProbeOrigLoaded, room));
        Dictionary<FieldInfo, object> variantFields = RoomProbeState.CaptureSafeRoomFields(room);
        original.Restore(room, settings);
        RemoveExact(settings.effects, previewEffect);

        if (!variant.Success)
        {
            DisposeAll(baseline.Objects, room);
            DisposeAll(variant.Objects, room);
            LogHookFailureOnce(hook, "variant", variant.Error);
            return;
        }

        List<UpdatableAndDeletable> retained = DiffObjects(baseline.Objects, variant.Objects, room);
        for (int i = 0; i < retained.Count; i++)
            product.Objects.Add(retained[i]);

        CaptureFieldDeltas(original.RoomFields, baselineFields, variantFields, retained, product.FieldDeltas);
        DisposeAll(baseline.Objects, room);
    }

    private static bool IsCandidate(On.Room.hook_Loaded hook)
    {
        MethodInfo method = hook.Method;
        if (method == null) return false;
        if (ReferenceEquals(method.Module.Assembly, typeof(LoadedHookReplayProbe).Assembly))
            return false;

        lock (CandidateCache)
        {
            if (CandidateCache.TryGetValue(method, out bool cached))
                return cached;
        }

        bool result = HookMethodAnalyzer.LooksLikeSafeEffectObjectBootstrap(method);
        lock (CandidateCache) CandidateCache[method] = result;
        return result;
    }

    private static List<UpdatableAndDeletable> DiffObjects(
        UpdatableAndDeletable[] baseline,
        UpdatableAndDeletable[] variant,
        global::Room room)
    {
        Dictionary<string, int> baselineCounts = new(StringComparer.Ordinal);
        for (int i = 0; i < baseline.Length; i++)
        {
            string key = ObjectFingerprint(baseline[i]);
            if (string.IsNullOrEmpty(key)) continue;
            baselineCounts.TryGetValue(key, out int count);
            baselineCounts[key] = count + 1;
        }

        List<UpdatableAndDeletable> retained = new();
        for (int i = 0; i < variant.Length; i++)
        {
            UpdatableAndDeletable obj = variant[i];
            string key = ObjectFingerprint(obj);
            if (!string.IsNullOrEmpty(key) && baselineCounts.TryGetValue(key, out int count) && count > 0)
            {
                baselineCounts[key] = count - 1;
                EffectPreviewOwnershipTransaction.DisposeCapturedObject(obj, room);
                continue;
            }
            if (obj != null) retained.Add(obj);
        }
        return retained;
    }

    private static void CaptureFieldDeltas(
        Dictionary<FieldInfo, object> original,
        Dictionary<FieldInfo, object> baseline,
        Dictionary<FieldInfo, object> variant,
        List<UpdatableAndDeletable> retained,
        Dictionary<FieldInfo, ProbeFieldDelta> output)
    {
        HashSet<UpdatableAndDeletable> retainedSet =
            new(retained, ReferenceEqualityComparer<UpdatableAndDeletable>.Instance);

        foreach (KeyValuePair<FieldInfo, object> pair in variant)
        {
            FieldInfo field = pair.Key;
            object variantValue = pair.Value;
            baseline.TryGetValue(field, out object baselineValue);
            if (EffectPreviewOwnershipTransaction.SameValue(baselineValue, variantValue))
                continue;

            original.TryGetValue(field, out object originalValue);
            Type type = field.FieldType;
            bool scalar = type.IsValueType || type == typeof(string);
            bool ownedReference = variantValue is UpdatableAndDeletable obj && retainedSet.Contains(obj);
            if (!scalar && !ownedReference)
                continue;

            output[field] = new ProbeFieldDelta(originalValue, variantValue);
        }
    }

    private static string ObjectFingerprint(UpdatableAndDeletable obj)
    {
        Type type = obj?.GetType();
        return type?.AssemblyQualifiedName ?? type?.FullName ?? string.Empty;
    }

    private static void DisposeAll(UpdatableAndDeletable[] objects, global::Room room)
    {
        if (objects == null) return;
        for (int i = 0; i < objects.Length; i++)
            EffectPreviewOwnershipTransaction.DisposeCapturedObject(objects[i], room);
    }

    private static void ProbeOrigLoaded(global::Room self)
    {
    }

    private static void LogHookFailureOnce(On.Room.hook_Loaded hook, string phase, Exception error)
    {
        string key = (hook.Method?.DeclaringType?.FullName ?? "<unknown>") + "." +
                     (hook.Method?.Name ?? "<hook>") + ":" + phase;
        lock (LoggedFailures)
        {
            if (!LoggedFailures.Add(key)) return;
        }
        Plugin.Logger?.LogDebug(
            "DevTool skipped Room.Loaded effect probe callback " + key + ": " +
            (error?.GetBaseException().Message ?? "unknown error"));
    }

    internal static void EnsurePreviewFirst(
        List<RoomSettings.RoomEffect> effects,
        RoomSettings.RoomEffect previewEffect)
    {
        if (effects == null || previewEffect == null) return;
        RemoveExact(effects, previewEffect);
        effects.Insert(0, previewEffect);
    }

    internal static void RemoveExact(
        List<RoomSettings.RoomEffect> effects,
        RoomSettings.RoomEffect previewEffect)
    {
        if (effects == null || previewEffect == null) return;
        for (int i = effects.Count - 1; i >= 0; i--)
            if (ReferenceEquals(effects[i], previewEffect)) effects.RemoveAt(i);
    }
}

/// <summary>
/// Reflects HookGen's own registration store rather than any third-party mod API. Different
/// RuntimeDetour generations have stored callbacks as MethodBase -> HookEndpoint maps, owner ->
/// HookEntry lists, tuple keys, or direct delegate collections.
/// </summary>
internal static class HookGenLoadedHookDiscovery
{
    private const int MaxEnumeratedEntries = 4096;

    internal static On.Room.hook_Loaded[] Discover(MethodBase target)
    {
        HashSet<On.Room.hook_Loaded> result = new();
        if (target == null) return Array.Empty<On.Room.hook_Loaded>();

        try
        {
            Type manager = Type.GetType(
                "MonoMod.RuntimeDetour.HookGen.HookEndpointManager, MonoMod.RuntimeDetour",
                throwOnError: false);
            if (manager == null) return Array.Empty<On.Room.hook_Loaded>();

            FieldInfo[] fields = manager.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                if (field.Name.IndexOf("hook", StringComparison.OrdinalIgnoreCase) < 0 ||
                    field.Name.IndexOf("il", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                object store;
                try { store = field.GetValue(null); }
                catch { continue; }
                CollectStore(store, target, result, 0);
            }
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogDebug("DevTool HookGen Room.Loaded discovery unavailable: " + error.Message);
        }

        On.Room.hook_Loaded[] array = new On.Room.hook_Loaded[result.Count];
        result.CopyTo(array);
        return array;
    }

    private static void CollectStore(
        object store,
        MethodBase target,
        HashSet<On.Room.hook_Loaded> result,
        int depth)
    {
        if (store == null || depth > 5 || result.Count > MaxEnumeratedEntries) return;
        if (store is On.Room.hook_Loaded direct)
        {
            result.Add(direct);
            return;
        }
        if (store is string) return;

        MethodBase directMethod = ReadMember(store, "Method") as MethodBase;
        Delegate directHook = ReadMember(store, "Hook") as Delegate;
        if (SameMethod(directMethod, target) && directHook != null)
        {
            CollectDelegates(directHook, result, depth + 1);
            return;
        }

        object hookMap = ReadMember(store, "HookMap");
        if (SameMethod(directMethod, target) && hookMap != null)
        {
            CollectDelegates(hookMap, result, depth + 1);
            return;
        }

        if (store is IEnumerable enumerable)
        {
            int visited = 0;
            foreach (object entry in enumerable)
            {
                if (++visited > MaxEnumeratedEntries) break;
                object key = ReadMember(entry, "Key");
                object value = ReadMember(entry, "Value");
                if (key == null && value == null)
                {
                    CollectStore(entry, target, result, depth + 1);
                    continue;
                }

                MethodBase method = FindMethod(key);
                Delegate callback = FindDelegate(key);
                if (SameMethod(method, target))
                {
                    if (callback != null)
                        CollectDelegates(callback, result, depth + 1);
                    CollectStore(value, target, result, depth + 1);
                    CollectDelegates(value, result, depth + 1);
                }
                else if (method == null)
                {
                    CollectStore(value, target, result, depth + 1);
                }
            }
        }
    }

    private static void CollectDelegates(object value, HashSet<On.Room.hook_Loaded> result, int depth)
    {
        if (value == null || depth > 5) return;
        if (value is On.Room.hook_Loaded hook)
        {
            result.Add(hook);
            return;
        }
        if (value is Delegate del)
        {
            Delegate[] list = del.GetInvocationList();
            for (int i = 0; i < list.Length; i++)
                if (list[i] is On.Room.hook_Loaded loaded) result.Add(loaded);
            return;
        }
        if (value is string) return;

        Delegate memberHook = ReadMember(value, "Hook") as Delegate;
        if (memberHook != null)
            CollectDelegates(memberHook, result, depth + 1);

        if (value is IEnumerable enumerable)
        {
            int count = 0;
            foreach (object item in enumerable)
            {
                if (++count > MaxEnumeratedEntries) break;
                object key = ReadMember(item, "Key");
                object nested = ReadMember(item, "Value");
                Delegate keyDelegate = FindDelegate(key);
                if (keyDelegate != null)
                    CollectDelegates(keyDelegate, result, depth + 1);
                if (nested != null)
                    CollectDelegates(nested, result, depth + 1);
                else if (key == null)
                    CollectDelegates(item, result, depth + 1);
            }
        }
    }

    private static MethodBase FindMethod(object value)
    {
        if (value is MethodBase method) return method;
        if (value == null) return null;
        object item1 = ReadMember(value, "Item1");
        object item2 = ReadMember(value, "Item2");
        return item1 as MethodBase ?? item2 as MethodBase;
    }

    private static Delegate FindDelegate(object value)
    {
        if (value is Delegate del) return del;
        if (value == null) return null;
        object item1 = ReadMember(value, "Item1");
        object item2 = ReadMember(value, "Item2");
        return item1 as Delegate ?? item2 as Delegate;
    }

    private static object ReadMember(object value, string name)
    {
        if (value == null) return null;
        Type type = value.GetType();
        try
        {
            PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.GetIndexParameters().Length == 0)
                return property.GetValue(value, null);
        }
        catch { }
        try
        {
            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field?.GetValue(value);
        }
        catch { return null; }
    }

    private static bool SameMethod(MethodBase a, MethodBase b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;
        try { return a.Module == b.Module && a.MetadataToken == b.MetadataToken; }
        catch { return a.Equals(b); }
    }
}

internal static class HookMethodAnalyzer
{
    [Flags]
    private enum Signals
    {
        None = 0,
        EffectRead = 1,
        AddObject = 2,
        RiskySideEffect = 4
    }

    internal static bool LooksLikeSafeEffectObjectBootstrap(MethodInfo root)
    {
        if (root == null) return false;
        HashSet<MethodBase> visited = new();
        Signals signals = Scan(root, root.Module.Assembly, visited, 0);
        Signals required = Signals.EffectRead | Signals.AddObject;
        return (signals & required) == required && (signals & Signals.RiskySideEffect) == 0;
    }

    private static Signals Scan(
        MethodBase method,
        Assembly rootAssembly,
        HashSet<MethodBase> visited,
        int depth)
    {
        if (method == null || depth > 4 || visited.Count > 96 || !visited.Add(method))
            return Signals.None;

        List<DecodedInstruction> il = DecodedInstructionReader.Read(method);
        if (il.Count == 0) return Signals.None;

        Signals signals = Signals.None;
        for (int i = 0; i < il.Count; i++)
        {
            DecodedInstruction instruction = il[i];
            if (instruction.OpCode == OpCodes.Stsfld)
                signals |= Signals.RiskySideEffect;

            if (instruction.Operand is FieldInfo field &&
                field.DeclaringType == typeof(RoomSettings) &&
                string.Equals(field.Name, "effects", StringComparison.Ordinal))
            {
                signals |= Signals.EffectRead;
            }

            if (instruction.Operand is not MethodBase called)
                continue;

            if (called.DeclaringType == typeof(global::Room) &&
                string.Equals(called.Name, nameof(global::Room.AddObject), StringComparison.Ordinal))
                signals |= Signals.AddObject;

            if (called.DeclaringType == typeof(RoomSettings) &&
                called.Name.IndexOf("GetEffect", StringComparison.Ordinal) >= 0)
                signals |= Signals.EffectRead;

            if (IsRiskyCall(called))
                signals |= Signals.RiskySideEffect;

            if (called.Module?.Assembly == rootAssembly && called != method)
                signals |= Scan(called, rootAssembly, visited, depth + 1);
        }
        return signals;
    }

    private static bool IsRiskyCall(MethodBase called)
    {
        Type type = called?.DeclaringType;
        string typeName = type?.FullName ?? string.Empty;
        string methodName = called?.Name ?? string.Empty;

        if (type == typeof(AbstractRoom) &&
            (methodName.IndexOf("AddEntity", StringComparison.Ordinal) >= 0 ||
             methodName.IndexOf("RemoveEntity", StringComparison.Ordinal) >= 0))
            return true;

        if (typeName.StartsWith("System.IO.File", StringComparison.Ordinal) ||
            typeName.StartsWith("System.IO.Directory", StringComparison.Ordinal))
            return true;

        if (typeName == "UnityEngine.AssetBundle" &&
            methodName.IndexOf("Load", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        if (typeName.StartsWith("System.Collections.Generic.Dictionary", StringComparison.Ordinal) &&
            string.Equals(methodName, "set_Item", StringComparison.Ordinal))
            return true;

        if ((typeName.IndexOf("SaveState", StringComparison.OrdinalIgnoreCase) >= 0 ||
             typeName.IndexOf("RegionState", StringComparison.OrdinalIgnoreCase) >= 0 ||
             typeName.IndexOf("PlayerProgression", StringComparison.OrdinalIgnoreCase) >= 0) &&
            (methodName.StartsWith("Save", StringComparison.OrdinalIgnoreCase) ||
             methodName.StartsWith("Set", StringComparison.OrdinalIgnoreCase) ||
             methodName.StartsWith("Consume", StringComparison.OrdinalIgnoreCase) ||
             methodName.StartsWith("Destroy", StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }
}

internal sealed class RoomProbeState
{
    private readonly Dictionary<FieldInfo, object> roomFields;
    private readonly RoomSettings.RoomEffect[] effects;
    private readonly PlacedObject[] placedObjects;
    private readonly AmbientSound[] ambientSounds;
    private readonly EventTrigger[] triggers;
    private readonly AbstractWorldEntity[] abstractEntities;
    private readonly AbstractWorldEntity[] abstractEntitiesInDens;
    private readonly AbstractCreature[] abstractCreatures;
    private readonly bool firstTimeRealized;
    private readonly UnityEngine.Random.State randomState;

    private RoomProbeState(
        Dictionary<FieldInfo, object> roomFields,
        RoomSettings.RoomEffect[] effects,
        PlacedObject[] placedObjects,
        AmbientSound[] ambientSounds,
        EventTrigger[] triggers,
        AbstractWorldEntity[] abstractEntities,
        AbstractWorldEntity[] abstractEntitiesInDens,
        AbstractCreature[] abstractCreatures,
        bool firstTimeRealized,
        UnityEngine.Random.State randomState)
    {
        this.roomFields = roomFields;
        this.effects = effects;
        this.placedObjects = placedObjects;
        this.ambientSounds = ambientSounds;
        this.triggers = triggers;
        this.abstractEntities = abstractEntities;
        this.abstractEntitiesInDens = abstractEntitiesInDens;
        this.abstractCreatures = abstractCreatures;
        this.firstTimeRealized = firstTimeRealized;
        this.randomState = randomState;
    }

    internal Dictionary<FieldInfo, object> RoomFields => roomFields;

    internal static RoomProbeState Capture(global::Room room, RoomSettings settings)
    {
        AbstractRoom abstractRoom = room?.abstractRoom;
        return new RoomProbeState(
            CaptureSafeRoomFields(room),
            settings?.effects?.ToArray() ?? Array.Empty<RoomSettings.RoomEffect>(),
            settings?.placedObjects?.ToArray() ?? Array.Empty<PlacedObject>(),
            settings?.ambientSounds?.ToArray() ?? Array.Empty<AmbientSound>(),
            settings?.triggers?.ToArray() ?? Array.Empty<EventTrigger>(),
            abstractRoom?.entities?.ToArray() ?? Array.Empty<AbstractWorldEntity>(),
            abstractRoom?.entitiesInDens?.ToArray() ?? Array.Empty<AbstractWorldEntity>(),
            abstractRoom?.creatures?.ToArray() ?? Array.Empty<AbstractCreature>(),
            abstractRoom?.firstTimeRealized ?? false,
            UnityEngine.Random.state);
    }

    internal void Restore(global::Room room, RoomSettings settings)
    {
        if (room != null)
        {
            foreach (KeyValuePair<FieldInfo, object> pair in roomFields)
            {
                try { pair.Key.SetValue(room, pair.Value); }
                catch { }
            }

            AbstractRoom abstractRoom = room.abstractRoom;
            if (abstractRoom != null)
            {
                RestoreList(abstractRoom.entities, abstractEntities);
                RestoreList(abstractRoom.entitiesInDens, abstractEntitiesInDens);
                RestoreList(abstractRoom.creatures, abstractCreatures);
                abstractRoom.firstTimeRealized = firstTimeRealized;
            }
        }

        if (settings != null)
        {
            RestoreList(settings.effects, effects);
            RestoreList(settings.placedObjects, placedObjects);
            RestoreList(settings.ambientSounds, ambientSounds);
            RestoreList(settings.triggers, triggers);
        }
        UnityEngine.Random.state = randomState;
    }

    internal static Dictionary<FieldInfo, object> CaptureSafeRoomFields(global::Room room)
    {
        Dictionary<FieldInfo, object> result = new();
        if (room == null) return result;

        for (Type type = room.GetType(); type != null && type != typeof(object); type = type.BaseType)
        {
            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                if (field.IsStatic || field.IsInitOnly || field.IsLiteral || !SafeFieldType(field.FieldType))
                    continue;
                try { result[field] = field.GetValue(room); }
                catch { }
            }
        }
        return result;
    }

    private static bool SafeFieldType(Type type)
    {
        if (type == null) return false;
        if (type.IsValueType || type == typeof(string)) return true;
        if (typeof(UpdatableAndDeletable).IsAssignableFrom(type)) return true;
        if (typeof(IDrawable).IsAssignableFrom(type)) return true;
        return false;
    }

    private static void RestoreList<T>(List<T> list, T[] values)
    {
        if (list == null) return;
        list.Clear();
        if (values != null && values.Length > 0) list.AddRange(values);
    }
}

internal static class ConstructorConventionBootstrap
{
    private static readonly Dictionary<string, Type[]> TypeCache = new(StringComparer.Ordinal);

    internal static bool TryBootstrap(
        global::Room room,
        RoomSettings.RoomEffect effect,
        EffectPreviewOwnershipTransaction transaction)
    {
        string name = effect?.type?.value;
        if (room == null || string.IsNullOrWhiteSpace(name) || transaction == null)
            return false;

        Type[] types = FindTypes(name);
        for (int t = 0; t < types.Length; t++)
        {
            Type type = types[t];
            ConstructorInfo ctor = ChooseConstructor(type);
            if (ctor == null) continue;

            UpdatableAndDeletable primary = null;
            EffectPreviewObjectCapture.CaptureResult nested = EffectPreviewObjectCapture.Capture(
                room,
                () => primary = ctor.Invoke(BuildArguments(ctor, room, effect)) as UpdatableAndDeletable);

            if (!nested.Success || primary == null)
            {
                EffectPreviewOwnershipTransaction.DisposeCapturedObject(primary, room);
                for (int i = 0; i < nested.Objects.Length; i++)
                    EffectPreviewOwnershipTransaction.DisposeCapturedObject(nested.Objects[i], room);
                continue;
            }

            bool committed = transaction.CommitObject(primary);
            HashSet<UpdatableAndDeletable> seen =
                new(ReferenceEqualityComparer<UpdatableAndDeletable>.Instance) { primary };
            for (int i = 0; i < nested.Objects.Length; i++)
            {
                UpdatableAndDeletable obj = nested.Objects[i];
                if (obj == null || !seen.Add(obj)) continue;
                if (!transaction.CommitObject(obj))
                    EffectPreviewOwnershipTransaction.DisposeCapturedObject(obj, room);
            }

            if (committed) return true;
        }
        return false;
    }

    internal static bool SupportsConstructor(ConstructorInfo ctor)
    {
        if (ctor == null || !ctor.IsPublic) return false;
        Type created = ctor.DeclaringType;
        if (created == null || created.IsAbstract ||
            !typeof(UpdatableAndDeletable).IsAssignableFrom(created) ||
            typeof(PhysicalObject).IsAssignableFrom(created))
            return false;

        ParameterInfo[] parameters = ctor.GetParameters();
        if (parameters.Length > 3) return false;
        for (int i = 0; i < parameters.Length; i++)
        {
            Type parameter = parameters[i].ParameterType;
            if (parameter != typeof(global::Room) &&
                parameter != typeof(RoomSettings.RoomEffect) &&
                parameter != typeof(float) &&
                parameter != typeof(RoomCamera))
                return false;
        }
        return true;
    }

    internal static object[] BuildArguments(
        ConstructorInfo ctor,
        global::Room room,
        RoomSettings.RoomEffect effect)
    {
        ParameterInfo[] parameters = ctor.GetParameters();
        object[] args = new object[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            Type type = parameters[i].ParameterType;
            if (type == typeof(global::Room)) args[i] = room;
            else if (type == typeof(RoomSettings.RoomEffect)) args[i] = effect;
            else if (type == typeof(float)) args[i] = effect.amount;
            else if (type == typeof(RoomCamera)) args[i] = ResolveCamera(room);
        }
        return args;
    }

    private static RoomCamera ResolveCamera(global::Room room)
    {
        RoomCamera[] cameras = room?.game?.cameras;
        if (cameras == null || cameras.Length == 0) return null;
        for (int i = 0; i < cameras.Length; i++)
            if (cameras[i] != null && ReferenceEquals(cameras[i].room, room)) return cameras[i];
        return cameras[0];
    }

    private static Type[] FindTypes(string effectName)
    {
        lock (TypeCache)
        {
            if (TypeCache.TryGetValue(effectName, out Type[] cached)) return cached;
        }

        List<Type> exact = new();
        List<Type> related = new();
        string normalizedEffect = Normalize(effectName);
        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
        for (int a = 0; a < assemblies.Length; a++)
        {
            Type[] types;
            try { types = assemblies[a].GetTypes(); }
            catch (ReflectionTypeLoadException load) { types = load.Types; }
            catch { continue; }

            if (types == null) continue;
            for (int i = 0; i < types.Length; i++)
            {
                Type type = types[i];
                if (type == null || type.IsAbstract ||
                    !typeof(UpdatableAndDeletable).IsAssignableFrom(type) ||
                    typeof(PhysicalObject).IsAssignableFrom(type) ||
                    ChooseConstructor(type) == null)
                    continue;

                if (string.Equals(type.Name, effectName, StringComparison.Ordinal))
                {
                    exact.Add(type);
                    continue;
                }

                if (RelatedName(normalizedEffect, Normalize(type.Name)))
                    related.Add(type);
            }
        }

        Type[] result;
        if (exact.Count > 0)
            result = exact.ToArray();
        else if (related.Count == 1)
            result = related.ToArray();
        else
            result = Array.Empty<Type>();

        if (result.Length > 0)
        {
            lock (TypeCache) TypeCache[effectName] = result;
        }
        return result;
    }

    private static bool RelatedName(string effect, string type)
    {
        if (string.IsNullOrEmpty(effect) || string.IsNullOrEmpty(type)) return false;
        if (effect.Length < 5 || type.Length < 5) return false;
        if (Math.Abs(effect.Length - type.Length) > 14) return false;
        return type.StartsWith(effect, StringComparison.Ordinal) ||
               type.EndsWith(effect, StringComparison.Ordinal) ||
               effect.StartsWith(type, StringComparison.Ordinal) ||
               effect.EndsWith(type, StringComparison.Ordinal);
    }

    private static string Normalize(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        StringBuilder builder = new(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }

    private static ConstructorInfo ChooseConstructor(Type type)
    {
        ConstructorInfo[] constructors = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public);
        ConstructorInfo best = null;
        int bestScore = int.MinValue;
        for (int i = 0; i < constructors.Length; i++)
        {
            ConstructorInfo ctor = constructors[i];
            if (!SupportsConstructor(ctor)) continue;

            ParameterInfo[] parameters = ctor.GetParameters();
            int score = parameters.Length == 0 ? 1 : 0;
            bool hasContext = parameters.Length == 0;
            for (int p = 0; p < parameters.Length; p++)
            {
                Type parameter = parameters[p].ParameterType;
                if (parameter == typeof(global::Room))
                {
                    score += 50;
                    hasContext = true;
                }
                else if (parameter == typeof(RoomSettings.RoomEffect))
                {
                    score += 70;
                    hasContext = true;
                }
                else if (parameter == typeof(RoomCamera))
                {
                    score += 30;
                    hasContext = true;
                }
                else if (parameter == typeof(float))
                {
                    score += 10;
                }
            }
            if (!hasContext || score <= bestScore) continue;
            bestScore = score;
            best = ctor;
        }
        return best;
    }
}
