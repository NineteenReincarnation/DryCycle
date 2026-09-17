using System;
using System.Collections.Generic;
using System.Reflection;

namespace DryCycle.DevUI.DevTool.Factories;

/// <summary>
/// Distinguishes IDs declared by Rain World's own Assembly-CSharp from ExtEnum IDs registered by
/// third-party assemblies. Native factories may safely own the former; unknown external IDs stay on
/// the legacy compatibility path until an explicit native provider is registered.
///
/// Reflection happens only on the first create operation for a value type, never on stable frames.
/// Only static fields whose exact type matches the requested ExtEnum type are read, avoiding a sweep
/// of unrelated game state.
/// </summary>
internal static class GameDefinedExtEnumCatalog
{
    private static readonly object Gate = new();
    private static readonly Dictionary<Type, HashSet<string>> Cache = new();

    internal static bool Contains(Type valueType, string value)
    {
        if (valueType == null || string.IsNullOrEmpty(value))
            return false;

        lock (Gate)
        {
            if (!Cache.TryGetValue(valueType, out HashSet<string> values))
            {
                values = Build(valueType);
                Cache.Add(valueType, values);
            }
            return values.Contains(value);
        }
    }

    private static HashSet<string> Build(Type valueType)
    {
        HashSet<string> values = new(StringComparer.Ordinal);
        Assembly gameAssembly = typeof(RoomSettings).Assembly;
        Type[] types;
        try
        {
            types = gameAssembly.GetTypes();
        }
        catch (ReflectionTypeLoadException error)
        {
            types = error.Types ?? Array.Empty<Type>();
        }

        for (int i = 0; i < types.Length; i++)
        {
            Type owner = types[i];
            if (owner == null) continue;

            FieldInfo[] fields;
            try
            {
                fields = owner.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            }
            catch
            {
                continue;
            }

            for (int j = 0; j < fields.Length; j++)
            {
                FieldInfo field = fields[j];
                if (field.FieldType != valueType) continue;

                try
                {
                    if (field.GetValue(null) is ExtEnumBase ext && !string.IsNullOrEmpty(ext.value))
                        values.Add(ext.value);
                }
                catch
                {
                    // A game type with a guarded static initializer must not make factory discovery
                    // fatal. Its value simply remains on the conservative legacy fallback path.
                }
            }
        }

        return values;
    }
}
