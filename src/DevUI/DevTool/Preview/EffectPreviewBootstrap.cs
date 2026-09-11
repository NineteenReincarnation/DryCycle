using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Preview;

/// <summary>
/// Second-stage preview bootstrap for effects whose runtime object is normally created only from
/// Room.Loaded. The implementation intentionally has no knowledge of mod ids, namespaces or custom
/// registries. It uses two generic strategies:
///
/// 1. Replay only HookGen Room.Loaded callbacks whose IL demonstrably reads RoomEffect state and
///    reaches Room.AddObject. The original Room.Loaded body is replaced with a no-op continuation,
///    so the room is never loaded a second time. Each callback is A/B-probed without/with the
///    temporary effect while Room.AddObject is captured instead of committed.
/// 2. If no hook produced a runtime object, try an exact-name constructor convention. A class whose
///    simple name equals the RoomEffect.Type and derives from UpdatableAndDeletable can be created
///    with public constructor parameters drawn from {Room, RoomEffect, float amount}.
///
/// Both paths fail closed. Unknown or suspicious callbacks simply remain stage-one previews.
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

        // Hook replay is the primary universal path for mods. The naming convention is mainly a
        // safe fallback for vanilla/DLC load-time scenes such as AboveCloudsView and for mods that
        // register an effect class directly without a Room.Loaded HookGen callback.
        if (committed == 0)
            ConstructorConventionBootstrap.TryBootstrap(room, previewEffect, transaction);
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

        // Begin() has already inserted the preview overlay. Baseline callbacks must observe the
        // exact real document, so temporarily detach only our exact effect object.
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

        // Never replay our own callbacks through this generic compatibility path.
        if (ReferenceEquals(method.Module.Assembly, typeof(LoadedHookReplayProbe).Assembly))
            return false;

        lock (CandidateCache)
        {
            if (CandidateCache.TryGetValue(method, out bool cached))
                return cached;
        }

        bool result = HookMethodAnalyzer.LooksLikeEffectObjectBootstrap(method);
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

            // Reference mutations are adopted only when they point at an object that survived the
            // A/B diff. This supports patterns such as room.customManager = obj without ever
            // replacing core Room references merely because a replayed hook touched them.
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
        // Intentionally empty. The whole point of this probe is to run a HookGen callback without
        // re-entering vanilla Room.Loaded or the rest of the detour chain.
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
/// Reflects HookGen's own registration store rather than any third-party mod API. MonoMod has used
/// more than one internal map shape over Rain World's lifetime, so discovery supports the modern
/// (MethodBase, Delegate) key and the older MethodBase -> delegate collection shape. Failure simply
/// disables this compatibility layer; normal stage-one preview remains intact.
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
        if (store == null || depth > 4 || result.Count > MaxEnumeratedEntries) return;
        if (store is On.Room.hook_Loaded direct)
        {
            result.Add(direct);
            return;
        }
        if (store is string) return;

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
                    if (callback is On.Room.hook_Loaded loaded)
                        result.Add(loaded);
                    else
                        CollectDelegates(value, result, depth + 1);
                }
                else if (method == null)
                {
                    // Some older maps have another dictionary level before the MethodBase key.
                    CollectStore(value, target, result, depth + 1);
                }
            }
        }
    }

    private static void CollectDelegates(object value, HashSet<On.Room.hook_Loaded> result, int depth)
    {
        if (value == null || depth > 4) return;
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
        if (value is IEnumerable enumerable)
        {
            int count = 0;
            foreach (object item in enumerable)
            {
                if (++count > MaxEnumeratedEntries) break;
                object key = ReadMember(item, "Key");
                object nested = ReadMember(item, "Value");
                Delegate keyDelegate = FindDelegate(key);
                if (keyDelegate is On.Room.hook_Loaded loaded) result.Add(loaded);
                CollectDelegates(nested ?? item, result, depth + 1);
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
        AddObject = 2
    }

    private static readonly OpCode[] OneByte = new OpCode[256];
    private static readonly OpCode[] TwoByte = new OpCode[256];

    static HookMethodAnalyzer()
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

    internal static bool LooksLikeEffectObjectBootstrap(MethodInfo root)
    {
        if (root == null) return false;
        HashSet<MethodBase> visited = new();
        Signals signals = Scan(root, root.Module.Assembly, visited, 0);
        return (signals & (Signals.EffectRead | Signals.AddObject)) ==
               (Signals.EffectRead | Signals.AddObject);
    }

    private static Signals Scan(
        MethodBase method,
        Assembly rootAssembly,
        HashSet<MethodBase> visited,
        int depth)
    {
        if (method == null || depth > 4 || visited.Count > 96 || !visited.Add(method))
            return Signals.None;

        MethodBody body;
        byte[] il;
        try
        {
            body = method.GetMethodBody();
            il = body?.GetILAsByteArray();
        }
        catch
        {
            return Signals.None;
        }
        if (il == null || il.Length == 0) return Signals.None;

        Signals signals = Signals.None;
        int p = 0;
        while (p < il.Length)
        {
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

            if (op.OperandType == OperandType.InlineMethod && operandSize >= 4)
            {
                int token = BitConverter.ToInt32(il, operandStart);
                MethodBase called = ResolveMethod(method, token);
                if (called != null)
                {
                    if (called.DeclaringType == typeof(global::Room) &&
                        string.Equals(called.Name, nameof(global::Room.AddObject), StringComparison.Ordinal))
                        signals |= Signals.AddObject;

                    if (called.DeclaringType == typeof(RoomSettings) &&
                        called.Name.IndexOf("GetEffect", StringComparison.Ordinal) >= 0)
                        signals |= Signals.EffectRead;

                    if (called.Module?.Assembly == rootAssembly && called != method)
                        signals |= Scan(called, rootAssembly, visited, depth + 1);
                }
            }
            else if (op.OperandType == OperandType.InlineField && operandSize >= 4)
            {
                int token = BitConverter.ToInt32(il, operandStart);
                FieldInfo field = ResolveField(method, token);
                if (field?.DeclaringType == typeof(RoomSettings) &&
                    string.Equals(field.Name, "effects", StringComparison.Ordinal))
                    signals |= Signals.EffectRead;
            }

            if ((signals & (Signals.EffectRead | Signals.AddObject)) ==
                (Signals.EffectRead | Signals.AddObject))
                return signals;

            p += operandSize;
        }
        return signals;
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

internal sealed class RoomProbeState
{
    private readonly Dictionary<FieldInfo, object> roomFields;
    private readonly RoomSettings.RoomEffect[] effects;
    private readonly PlacedObject[] placedObjects;
    private readonly AmbientSound[] ambientSounds;
    private readonly Trigger[] triggers;
    private readonly bool firstTimeRealized;
    private readonly UnityEngine.Random.State randomState;

    private RoomProbeState(
        Dictionary<FieldInfo, object> roomFields,
        RoomSettings.RoomEffect[] effects,
        PlacedObject[] placedObjects,
        AmbientSound[] ambientSounds,
        Trigger[] triggers,
        bool firstTimeRealized,
        UnityEngine.Random.State randomState)
    {
        this.roomFields = roomFields;
        this.effects = effects;
        this.placedObjects = placedObjects;
        this.ambientSounds = ambientSounds;
        this.triggers = triggers;
        this.firstTimeRealized = firstTimeRealized;
        this.randomState = randomState;
    }

    internal Dictionary<FieldInfo, object> RoomFields => roomFields;

    internal static RoomProbeState Capture(global::Room room, RoomSettings settings)
    {
        return new RoomProbeState(
            CaptureSafeRoomFields(room),
            settings?.effects?.ToArray() ?? Array.Empty<RoomSettings.RoomEffect>(),
            settings?.placedObjects?.ToArray() ?? Array.Empty<PlacedObject>(),
            settings?.ambientSounds?.ToArray() ?? Array.Empty<AmbientSound>(),
            settings?.triggers?.ToArray() ?? Array.Empty<Trigger>(),
            room?.abstractRoom?.firstTimeRealized ?? false,
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
            if (room.abstractRoom != null)
                room.abstractRoom.firstTimeRealized = firstTimeRealized;
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

    private static Type[] FindTypes(string simpleName)
    {
        lock (TypeCache)
        {
            if (TypeCache.TryGetValue(simpleName, out Type[] cached)) return cached;
        }

        List<Type> result = new();
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
                    !string.Equals(type.Name, simpleName, StringComparison.Ordinal) ||
                    !typeof(UpdatableAndDeletable).IsAssignableFrom(type))
                    continue;
                result.Add(type);
            }
        }

        Type[] array = result.ToArray();
        lock (TypeCache) TypeCache[simpleName] = array;
        return array;
    }

    private static ConstructorInfo ChooseConstructor(Type type)
    {
        ConstructorInfo[] constructors = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public);
        ConstructorInfo best = null;
        int bestScore = int.MinValue;
        for (int i = 0; i < constructors.Length; i++)
        {
            ParameterInfo[] parameters = constructors[i].GetParameters();
            if (parameters.Length > 3) continue;

            int score = 0;
            bool valid = true;
            bool hasContext = false;
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
                else if (parameter == typeof(float))
                {
                    score += 10;
                }
                else
                {
                    valid = false;
                    break;
                }
            }
            if (!valid || (!hasContext && parameters.Length != 0)) continue;
            if (parameters.Length == 0) score = 1;
            if (score <= bestScore) continue;
            bestScore = score;
            best = constructors[i];
        }
        return best;
    }

    private static object[] BuildArguments(
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
        }
        return args;
    }
}
