using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Optional, read-only framework metadata. No Pom.dll reference and no per-object names: all
/// registrations using the standard managed representation share the same capability check.
/// Custom representations/fields keep their original selected-object compatibility backend.
/// </summary>
internal static class ManagedObjectRegistration
{
    private sealed class Protocol
    {
        internal Type Framework;
        internal Type StandardFactory;
        internal MethodInfo Resolve;
        internal FieldInfo Registry;
        internal FieldInfo Fields;
    }

    private sealed class Result
    {
        internal string ObjectType;
        internal int RegistryCount;
        internal bool Standard;
        internal bool Faulted;
    }

    private static readonly ConcurrentDictionary<Type, Protocol> protocols = new();
    private static ConditionalWeakTable<PlacedObject.Data, Result> results = new();

    internal static bool HasStandardRepresentation(PlacedObject target)
    {
        if (target?.data == null || target.type == null) return false;
        if (results.TryGetValue(target.data, out Result failure) && failure.Faulted &&
            failure.ObjectType == target.type.value) return false;
        Protocol protocol = protocols.GetOrAdd(target.data.GetType(), FindProtocol);
        if (protocol.Resolve == null) return false;
        try
        {
            int count = (protocol.Registry?.GetValue(null) as ICollection)?.Count ?? -1;
            if (results.TryGetValue(target.data, out Result cached) &&
                cached.ObjectType == target.type.value && cached.RegistryCount == count)
                return cached.Standard;

            object factory = protocol.Resolve.Invoke(null, new object[] { target.type });
            bool standard = factory?.GetType() == protocol.StandardFactory;
            if (standard && protocol.Fields.GetValue(target.data) is IEnumerable fields)
            {
                foreach (object descriptor in fields)
                {
                    // A custom field can override MakeAditionalNodes even in an otherwise standard
                    // registration. Inspect its original controls instead of claiming full coverage.
                    if (descriptor?.GetType().DeclaringType != protocol.Framework)
                    {
                        standard = false;
                        break;
                    }
                }
            }
            else standard = false;

            results.Remove(target.data);
            results.Add(target.data, new Result
            {
                ObjectType = target.type.value, RegistryCount = count, Standard = standard
            });
            return standard;
        }
        catch (Exception error)
        {
            // Metadata discovery failure keeps the isolated original representation path available.
            results.Remove(target.data);
            results.Add(target.data, new Result
            {
                ObjectType = target.type.value, RegistryCount = -1, Standard = false, Faulted = true
            });
            Plugin.Logger?.LogWarning("DevTool managed registration discovery failed: " + error);
            return false;
        }
    }

    private static Protocol FindProtocol(Type dataType)
    {
        for (Type type = dataType; type != null && type != typeof(PlacedObject.Data); type = type.BaseType)
        {
            Type framework = type.DeclaringType;
            if (framework == null) continue;
            FieldInfo fields = type.GetField("fields", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            MethodInfo resolve = framework.GetMethod("GetManagerForType",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null,
                new[] { typeof(PlacedObject.Type) }, null);
            Type standard = framework.GetNestedType("FullyManagedObjectType", BindingFlags.Public | BindingFlags.NonPublic);
            if (fields == null || resolve == null || standard == null) continue;
            return new Protocol
            {
                Framework = framework, Fields = fields, Resolve = resolve, StandardFactory = standard,
                Registry = framework.GetField("managedObjectTypes", BindingFlags.Static | BindingFlags.NonPublic)
            };
        }
        return new Protocol();
    }

    internal static void Reset()
    {
        protocols.Clear();
        results = new ConditionalWeakTable<PlacedObject.Data, Result>();
    }
}
