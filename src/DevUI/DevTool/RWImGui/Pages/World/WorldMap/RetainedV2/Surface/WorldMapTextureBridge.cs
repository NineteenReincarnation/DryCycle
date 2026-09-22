using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using ImGuiNET;
using RWIMGUI.API;
using UnityEngine;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Runtime-resolved RWImGUI texture adapter.
///
/// No native texture pointer is assumed to be an ImGui texture ID. The bridge activates only when
/// the loaded RWImGUI/ImGui assemblies expose both a verified Unity Texture -> native-ID method and
/// an ImDrawList image method consuming that ID.
/// </summary>
internal sealed class WorldMapTextureBridge
{
    private readonly object gate = new();
    private MethodInfo acquireMethod;
    private MethodInfo releaseMethod;
    private MethodInfo addImageMethod;
    private object registeredId;
    private Texture registeredTexture;
    private Type imageIdType;
    private bool resolved;
    private string error = string.Empty;
    private ManualLogSource log;

    internal bool Available
    {
        get
        {
            lock (gate)
                return resolved && acquireMethod != null && addImageMethod != null;
        }
    }

    internal string Error
    {
        get
        {
            lock (gate)
                return error;
        }
    }

    internal bool SupportsUvSubrect
    {
        get
        {
            lock (gate)
                return resolved && SupportsUvSubrectMethod(addImageMethod);
        }
    }

    internal void Initialize(ManualLogSource logger)
    {
        lock (gate)
        {
            if (resolved) return;
            log = logger;
            Resolve();
        }
    }

    internal bool TryPresent(
        ImDrawListPtr draw,
        Texture texture,
        Num.Vector2 min,
        Num.Vector2 max) =>
        TryPresent(draw, texture, min, max, Num.Vector2.Zero, Num.Vector2.One);

    internal bool TryPresent(
        ImDrawListPtr draw,
        Texture texture,
        Num.Vector2 min,
        Num.Vector2 max,
        Num.Vector2 uvMin,
        Num.Vector2 uvMax)
    {
        lock (gate)
            return TryPresentCore(draw, texture, min, max, uvMin, uvMax);
    }

    private bool TryPresentCore(
        ImDrawListPtr draw,
        Texture texture,
        Num.Vector2 min,
        Num.Vector2 max,
        Num.Vector2 uvMin,
        Num.Vector2 uvMax)
    {
        if (!resolved) Resolve();
        if (acquireMethod == null || addImageMethod == null || texture == null)
            return false;

        try
        {
            if (!ReferenceEquals(registeredTexture, texture) || registeredId == null)
            {
                ReleaseRegistered();
                object rawId = InvokeAcquire(texture);
                if (rawId == null)
                {
                    error = "RWImGUI texture acquisition returned null.";
                    return false;
                }

                registeredId = ConvertTextureId(rawId, imageIdType);
                if (registeredId == null)
                {
                    error =
                        "RWImGUI texture ID type " + rawId.GetType().FullName +
                        " cannot be converted to " + imageIdType?.FullName + ".";
                    return false;
                }

                registeredTexture = texture;
            }

            object[] args = BuildAddImageArguments(
                addImageMethod,
                registeredId,
                min,
                max,
                uvMin,
                uvMax);
            object boxedDraw = draw;
            addImageMethod.Invoke(boxedDraw, args);
            error = string.Empty;
            return true;
        }
        catch (TargetInvocationException errorWrapper)
        {
            Exception original = errorWrapper.InnerException ?? errorWrapper;
            error = "RWImGUI texture presentation failed: " + original.Message;
            log?.LogError("World Map V2 texture presentation failed: " + original);
            return false;
        }
        catch (Exception presentationError)
        {
            error = "RWImGUI texture presentation failed: " + presentationError.Message;
            log?.LogError("World Map V2 texture presentation failed: " + presentationError);
            return false;
        }
    }

    internal void Reset()
    {
        lock (gate)
            ResetCore();
    }

    private void ResetCore()
    {
        ReleaseRegistered();
        acquireMethod = null;
        releaseMethod = null;
        addImageMethod = null;
        imageIdType = null;
        resolved = false;
        error = string.Empty;
        log = null;
    }

    private void Resolve()
    {
        resolved = true;
        try
        {
            addImageMethod = ResolveAddImageMethod();
            if (addImageMethod == null)
            {
                error = "ImDrawListPtr.AddImage-compatible method not found.";
                return;
            }

            imageIdType = addImageMethod.GetParameters()[0].ParameterType;
            acquireMethod = ResolveAcquireMethod(imageIdType);
            if (acquireMethod == null)
            {
                error =
                    "No verified RWImGUI Unity Texture -> " +
                    imageIdType.FullName + " adapter was found.";
                return;
            }

            releaseMethod = ResolveReleaseMethod(acquireMethod.ReturnType);
            error = string.Empty;
            log?.LogInfo(
                "World Map V2 texture bridge resolved: acquire=" +
                Describe(acquireMethod) + ", image=" + Describe(addImageMethod) +
                (releaseMethod == null ? ", release=<none>" : ", release=" + Describe(releaseMethod)) + ".");
        }
        catch (Exception resolveError)
        {
            error = "Texture bridge resolution failed: " + resolveError.Message;
            log?.LogError("World Map V2 texture bridge resolution failed: " + resolveError);
        }
    }

    private static MethodInfo ResolveAddImageMethod()
    {
        MethodInfo[] methods = typeof(ImDrawListPtr).GetMethods(
            BindingFlags.Public | BindingFlags.Instance);
        MethodInfo best = null;
        int bestScore = int.MinValue;

        for (int i = 0; i < methods.Length; i++)
        {
            MethodInfo method = methods[i];
            if (!string.Equals(method.Name, "AddImage", StringComparison.Ordinal))
                continue;

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length < 3 ||
                parameters[1].ParameterType != typeof(Num.Vector2) ||
                parameters[2].ParameterType != typeof(Num.Vector2) ||
                !IsNativeIdType(parameters[0].ParameterType))
                continue;

            int vectorCount = CountVectorParameters(parameters);
            int score = (vectorCount >= 4 ? 1000 : 0) + vectorCount * 10 + parameters.Length;
            if (score <= bestScore) continue;
            best = method;
            bestScore = score;
        }

        return best;
    }

    private static MethodInfo ResolveAcquireMethod(Type targetIdType)
    {
        Assembly[] assemblies =
        {
            typeof(ImGUIAPI).Assembly,
            typeof(ImGui).Assembly
        };

        MethodInfo best = null;
        int bestScore = int.MinValue;
        HashSet<string> seen = new(StringComparer.Ordinal);

        for (int a = 0; a < assemblies.Length; a++)
        {
            Type[] types = GetLoadableTypes(assemblies[a]);
            for (int t = 0; t < types.Length; t++)
            {
                MethodInfo[] methods;
                try
                {
                    methods = types[t].GetMethods(
                        BindingFlags.Public | BindingFlags.Static);
                }
                catch
                {
                    continue;
                }

                for (int m = 0; m < methods.Length; m++)
                {
                    MethodInfo method = methods[m];
                    if (!IsNativeIdType(method.ReturnType)) continue;
                    if (!CanConvertNativeId(method.ReturnType, targetIdType)) continue;

                    ParameterInfo[] parameters = method.GetParameters();
                    int textureParameter = -1;
                    bool unsupportedRequired = false;
                    for (int p = 0; p < parameters.Length; p++)
                    {
                        Type parameterType = parameters[p].ParameterType;
                        if (typeof(Texture).IsAssignableFrom(parameterType))
                        {
                            if (parameterType.IsAssignableFrom(typeof(RenderTexture)) ||
                                parameterType == typeof(Texture))
                                textureParameter = p;
                            continue;
                        }

                        if (!parameters[p].IsOptional)
                            unsupportedRequired = true;
                    }

                    if (textureParameter < 0 || unsupportedRequired) continue;

                    string signature = Describe(method);
                    if (!seen.Add(signature)) continue;

                    string name = method.Name.ToLowerInvariant();
                    int score = 0;
                    if (name.Contains("register")) score += 40;
                    if (name.Contains("texture")) score += 30;
                    if (name.Contains("image")) score += 15;
                    if (parameters.Length == 1) score += 20;
                    if (method.ReturnType == targetIdType) score += 15;

                    if (score <= bestScore) continue;
                    best = method;
                    bestScore = score;
                }
            }
        }

        return best;
    }

    private static MethodInfo ResolveReleaseMethod(Type acquiredIdType)
    {
        Type[] apiTypes = GetLoadableTypes(typeof(ImGUIAPI).Assembly);
        for (int t = 0; t < apiTypes.Length; t++)
        {
            MethodInfo[] methods;
            try
            {
                methods = apiTypes[t].GetMethods(
                    BindingFlags.Public | BindingFlags.Static);
            }
            catch
            {
                continue;
            }

            for (int m = 0; m < methods.Length; m++)
            {
                MethodInfo method = methods[m];
                string name = method.Name.ToLowerInvariant();
                if (!name.Contains("unregister") &&
                    !name.Contains("release") &&
                    !name.Contains("remove"))
                    continue;

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != 1) continue;
                Type parameterType = parameters[0].ParameterType;
                if (parameterType == acquiredIdType ||
                    parameterType.IsAssignableFrom(typeof(RenderTexture)) ||
                    parameterType == typeof(Texture) ||
                    IsNativeIdType(parameterType))
                    return method;
            }
        }
        return null;
    }

    private object InvokeAcquire(Texture texture)
    {
        ParameterInfo[] parameters = acquireMethod.GetParameters();
        object[] args = new object[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            Type parameterType = parameters[i].ParameterType;
            if (typeof(Texture).IsAssignableFrom(parameterType))
            {
                args[i] = texture;
            }
            else if (parameters[i].IsOptional)
            {
                args[i] = parameters[i].DefaultValue;
            }
            else
            {
                throw new InvalidOperationException(
                    "Unresolved required texture-adapter argument: " +
                    parameters[i].Name);
            }
        }
        return acquireMethod.Invoke(null, args);
    }

    private void ReleaseRegistered()
    {
        if (registeredId == null && registeredTexture == null)
            return;

        try
        {
            if (releaseMethod != null)
            {
                Type parameterType = releaseMethod.GetParameters()[0].ParameterType;
                object value = typeof(Texture).IsAssignableFrom(parameterType)
                    ? registeredTexture
                    : ConvertTextureId(registeredId, parameterType);
                if (value != null)
                    releaseMethod.Invoke(null, new[] { value });
            }
        }
        catch (Exception releaseError)
        {
            log?.LogWarning(
                "World Map V2 texture bridge release failed: " +
                (releaseError is TargetInvocationException tie
                    ? tie.InnerException?.Message ?? tie.Message
                    : releaseError.Message));
        }
        finally
        {
            registeredId = null;
            registeredTexture = null;
        }
    }

    private static object[] BuildAddImageArguments(
        MethodInfo method,
        object textureId,
        Num.Vector2 min,
        Num.Vector2 max,
        Num.Vector2 uvMin,
        Num.Vector2 uvMax)
    {
        ParameterInfo[] parameters = method.GetParameters();
        object[] args = new object[parameters.Length];
        int vectorOrdinal = 0;

        for (int i = 0; i < parameters.Length; i++)
        {
            Type type = parameters[i].ParameterType;
            if (i == 0)
            {
                args[i] = textureId;
                continue;
            }

            if (type == typeof(Num.Vector2))
            {
                if (vectorOrdinal == 0) args[i] = min;
                else if (vectorOrdinal == 1) args[i] = max;
                else if (vectorOrdinal == 2)
                    args[i] = ToTextureUv(uvMin);
                else if (vectorOrdinal == 3)
                    args[i] = ToTextureUv(uvMax);
                else
                    args[i] = Num.Vector2.Zero;
                vectorOrdinal++;
                continue;
            }

            if (parameters[i].IsOptional)
            {
                args[i] = parameters[i].DefaultValue;
                continue;
            }

            if (type == typeof(uint))
            {
                args[i] = uint.MaxValue;
                continue;
            }

            args[i] = type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        return args;
    }

    private static int CountVectorParameters(ParameterInfo[] parameters)
    {
        int count = 0;
        if (parameters == null) return count;
        for (int i = 1; i < parameters.Length; i++)
            if (parameters[i].ParameterType == typeof(Num.Vector2))
                count++;
        return count;
    }

    private static bool SupportsUvSubrectMethod(MethodInfo method) =>
        method != null && CountVectorParameters(method.GetParameters()) >= 4;

    private static Num.Vector2 ToTextureUv(Num.Vector2 topDownUv) =>
        SystemInfo.graphicsUVStartsAtTop
            ? new Num.Vector2(topDownUv.X, 1f - topDownUv.Y)
            : topDownUv;

    private static object ConvertTextureId(object value, Type targetType)
    {
        if (value == null || targetType == null) return null;
        Type sourceType = value.GetType();
        if (targetType.IsAssignableFrom(sourceType)) return value;

        try
        {
            ulong raw = sourceType == typeof(IntPtr)
                ? unchecked((ulong)((IntPtr)value).ToInt64())
                : sourceType == typeof(UIntPtr)
                    ? ((UIntPtr)value).ToUInt64()
                    : Convert.ToUInt64(value);

            if (targetType == typeof(IntPtr)) return new IntPtr(unchecked((long)raw));
            if (targetType == typeof(UIntPtr)) return new UIntPtr(raw);
            if (targetType == typeof(ulong)) return raw;
            if (targetType == typeof(long)) return unchecked((long)raw);
            if (targetType == typeof(uint)) return unchecked((uint)raw);
            if (targetType == typeof(int)) return unchecked((int)raw);
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static bool CanConvertNativeId(Type source, Type target) =>
        IsNativeIdType(source) && IsNativeIdType(target);

    private static bool IsNativeIdType(Type type) =>
        type == typeof(IntPtr) ||
        type == typeof(UIntPtr) ||
        type == typeof(long) ||
        type == typeof(ulong) ||
        type == typeof(int) ||
        type == typeof(uint);

    private static Type[] GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException error)
        {
            List<Type> result = new();
            Type[] source = error.Types ?? Array.Empty<Type>();
            for (int i = 0; i < source.Length; i++)
                if (source[i] != null) result.Add(source[i]);
            return result.ToArray();
        }
    }

    private static string Describe(MethodInfo method) =>
        method?.DeclaringType?.FullName + "." + method;
}
