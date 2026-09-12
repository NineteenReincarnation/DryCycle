using System;
using System.Collections.Generic;
using System.Reflection;
using DevInterface;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Static type inventory for every loaded DevUINode implementation. Runtime tree auditing proves
/// coverage for instantiated controls; this complementary pass finds dormant control families from
/// loaded vanilla/mod assemblies even when the current room never constructs them.
/// </summary>
public sealed class DevUiProtocolInventoryEntry
{
    public string TypeName { get; init; } = string.Empty;
    public string AssemblyName { get; init; } = string.Empty;
    public string Protocol { get; init; } = string.Empty;
    public bool PotentialGap { get; init; }
}

public sealed class DevUiProtocolInventorySnapshot
{
    public static readonly DevUiProtocolInventorySnapshot Empty = new();

    public int ConcreteNodeTypeCount { get; init; }
    public int PotentialGapCount { get; init; }
    public DevUiProtocolInventoryEntry[] PotentialGaps { get; init; } = Array.Empty<DevUiProtocolInventoryEntry>();
}

public static class DevUiProtocolInventory
{
    private const int RescanIntervalFrames = 600;
    private const int LoggedGapLimit = 32;

    private static volatile DevUiProtocolInventorySnapshot current = DevUiProtocolInventorySnapshot.Empty;
    private static int lastFrame = int.MinValue / 2;
    private static int lastAssemblyCount = -1;
    private static string lastFingerprint = string.Empty;

    public static DevUiProtocolInventorySnapshot Current
    {
        get
        {
            ObserveLoadedTypes();
            return current;
        }
    }

    internal static void ObserveLoadedTypes()
    {
        Assembly[] assemblies;
        try { assemblies = AppDomain.CurrentDomain.GetAssemblies(); }
        catch { return; }

        int frame = Time.frameCount;
        if (assemblies.Length == lastAssemblyCount && frame - lastFrame < RescanIntervalFrames)
            return;

        lastAssemblyCount = assemblies.Length;
        lastFrame = frame;

        List<DevUiProtocolInventoryEntry> gaps = new();
        int concreteCount = 0;

        for (int a = 0; a < assemblies.Length; a++)
        {
            Assembly assembly = assemblies[a];
            Type[] types = SafeGetTypes(assembly);
            for (int i = 0; i < types.Length; i++)
            {
                Type type = types[i];
                if (type == null || type.IsAbstract || type.IsInterface ||
                    !typeof(DevUINode).IsAssignableFrom(type))
                    continue;

                concreteCount++;
                string protocol = Classify(type, out bool potentialGap);
                if (!potentialGap) continue;

                gaps.Add(new DevUiProtocolInventoryEntry
                {
                    TypeName = type.FullName ?? type.Name,
                    AssemblyName = assembly.GetName().Name ?? string.Empty,
                    Protocol = protocol,
                    PotentialGap = true
                });
            }
        }

        gaps.Sort((a, b) =>
        {
            int assembly = string.Compare(a.AssemblyName, b.AssemblyName, StringComparison.Ordinal);
            return assembly != 0 ? assembly : string.Compare(a.TypeName, b.TypeName, StringComparison.Ordinal);
        });

        current = new DevUiProtocolInventorySnapshot
        {
            ConcreteNodeTypeCount = concreteCount,
            PotentialGapCount = gaps.Count,
            PotentialGaps = gaps.ToArray()
        };

        LogIfChanged(current);
    }

    private static string Classify(Type type, out bool potentialGap)
    {
        potentialGap = false;

        if (typeof(Page).IsAssignableFrom(type)) return "Page container";
        if (typeof(Handle).IsAssignableFrom(type)) return "World-space Handle";
        if (typeof(Slider).IsAssignableFrom(type)) return "Slider/NubDragged";
        if (typeof(Cycler).IsAssignableFrom(type)) return "Cycler";
        if (typeof(IntegerControl).IsAssignableFrom(type)) return "IntegerControl";
        if (typeof(ButtonWithSelectPanel).IsAssignableFrom(type)) return "Select/Button";
        if (typeof(Button).IsAssignableFrom(type)) return "Button.Clicked";
        if (typeof(DevUILabel).IsAssignableFrom(type)) return "Presentation label";

        if (HasMethod(type, "TrySetValue", new[] { typeof(string), typeof(bool) }) &&
            HasReadableMember(type, "actualValue", typeof(string)))
            return "Text value";

        if (HasWritableMember(type, "Dir", typeof(Vector2)))
            return "Direction<Vector2>";

        // Containers are recursively traversed. IDevUISignals is a parent event sink rather than
        // an interaction surface by itself, so it does not need a standalone ImGui widget.
        if (typeof(IDevUISignals).IsAssignableFrom(type)) return "Signal container";
        if (typeof(Panel).IsAssignableFrom(type)) return "Panel container";

        List<string> unknownBoundaries = new();
        AddBoundary(type, unknownBoundaries, "Clicked", Type.EmptyTypes);
        AddBoundary(type, unknownBoundaries, "NubDragged", new[] { typeof(float) });
        AddBoundary(type, unknownBoundaries, "NubDragged2", new[] { typeof(float) });
        AddBoundary(type, unknownBoundaries, "Increment", new[] { typeof(int) });
        AddBoundary(type, unknownBoundaries, "TrySetValue", new[] { typeof(string), typeof(bool) });

        if (unknownBoundaries.Count > 0)
        {
            potentialGap = true;
            return "Unknown interaction boundary: " + string.Join(", ", unknownBoundaries);
        }

        return "Container/presentation node";
    }

    private static void AddBoundary(Type type, List<string> output, string name, Type[] parameters)
    {
        if (HasDeclaredMethod(type, name, parameters)) output.Add(name);
    }

    private static bool HasMethod(Type type, string name, Type[] parameters)
    {
        Type current = type;
        while (current != null)
        {
            MethodInfo method = current.GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                null,
                parameters,
                null);
            if (method != null) return true;
            current = current.BaseType;
        }
        return false;
    }

    private static bool HasDeclaredMethod(Type type, string name, Type[] parameters)
    {
        if (type == null) return false;
        return type.GetMethod(
                   name,
                   BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                   null,
                   parameters,
                   null) != null;
    }

    private static bool HasReadableMember(Type type, string name, Type expected)
    {
        FieldInfo field = FindField(type, name);
        if (field != null && expected.IsAssignableFrom(field.FieldType)) return true;
        PropertyInfo property = FindProperty(type, name);
        return property != null && property.CanRead && property.GetIndexParameters().Length == 0 &&
               expected.IsAssignableFrom(property.PropertyType);
    }

    private static bool HasWritableMember(Type type, string name, Type expected)
    {
        FieldInfo field = FindField(type, name);
        if (field != null && !field.IsInitOnly && expected.IsAssignableFrom(field.FieldType)) return true;
        PropertyInfo property = FindProperty(type, name);
        return property != null && property.CanWrite && property.GetIndexParameters().Length == 0 &&
               expected.IsAssignableFrom(property.PropertyType);
    }

    private static FieldInfo FindField(Type type, string name)
    {
        Type current = type;
        while (current != null)
        {
            FieldInfo field = current.GetField(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null) return field;
            current = current.BaseType;
        }
        return null;
    }

    private static PropertyInfo FindProperty(Type type, string name)
    {
        Type current = type;
        while (current != null)
        {
            PropertyInfo property = current.GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property != null) return property;
            current = current.BaseType;
        }
        return null;
    }

    private static Type[] SafeGetTypes(Assembly assembly)
    {
        if (assembly == null) return Array.Empty<Type>();
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException error)
        {
            List<Type> loaded = new();
            Type[] partial = error.Types ?? Array.Empty<Type>();
            for (int i = 0; i < partial.Length; i++)
                if (partial[i] != null) loaded.Add(partial[i]);
            return loaded.ToArray();
        }
        catch { return Array.Empty<Type>(); }
    }

    private static void LogIfChanged(DevUiProtocolInventorySnapshot snapshot)
    {
        DevUiProtocolInventoryEntry[] gaps = snapshot?.PotentialGaps ?? Array.Empty<DevUiProtocolInventoryEntry>();
        List<string> lines = new(gaps.Length);
        for (int i = 0; i < gaps.Length; i++)
            lines.Add(gaps[i].AssemblyName + "|" + gaps[i].TypeName + "|" + gaps[i].Protocol);
        string fingerprint = string.Join("\n", lines);
        if (string.Equals(fingerprint, lastFingerprint, StringComparison.Ordinal)) return;
        lastFingerprint = fingerprint;

        Plugin.Logger?.LogInfo(
            "DevTool loaded-type protocol inventory: " + (snapshot?.ConcreteNodeTypeCount ?? 0) +
            " concrete DevUINode type(s), " + gaps.Length + " potential protocol family gap(s).");

        int shown = Math.Min(LoggedGapLimit, gaps.Length);
        for (int i = 0; i < shown; i++)
            Plugin.Logger?.LogWarning(
                "[DevUI loaded-type gap] " + gaps[i].AssemblyName + "|" + gaps[i].TypeName + "|" + gaps[i].Protocol);
        if (gaps.Length > shown)
            Plugin.Logger?.LogWarning(
                "[DevUI loaded-type gap] " + (gaps.Length - shown) + " additional gap(s) omitted from this log batch.");
    }
}
