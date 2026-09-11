using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using DryCycle.DevUI.DevTool.Objects;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Soft adapter for POM/RegionKit. No compile-time dependency is introduced; when POM is
/// loaded we read its category registry and enrich the unified Object Catalog. Creation is
/// still delegated through ObjectsPage.CreateObjRep so POM's normal hooks initialize data
/// and custom representations.
/// </summary>
internal sealed class PomCatalogEnricher : IObjectCatalogEnricher
{
    public void Enrich(List<ObjectDescriptor> descriptors)
    {
        Type pomType = FindPomType();
        if (pomType == null) return;

        FieldInfo categoryField = pomType.GetField("__objectCategories", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        if (categoryField?.GetValue(null) is not IDictionary categories) return;

        Dictionary<string, string> categoryByType = new(StringComparer.Ordinal);
        foreach (DictionaryEntry pair in categories)
        {
            string typeName = pair.Key as string;
            if (string.IsNullOrEmpty(typeName)) continue;
            string category = pair.Value?.ToString();
            categoryByType[typeName] = string.IsNullOrEmpty(category) ? "POM" : category;
        }

        for (int i = 0; i < descriptors.Count; i++)
        {
            ObjectDescriptor descriptor = descriptors[i];
            string typeName = descriptor.Type?.value;
            if (string.IsNullOrEmpty(typeName) || !categoryByType.TryGetValue(typeName, out string category)) continue;

            descriptors[i] = new ObjectDescriptor(
                descriptor.Type,
                descriptor.DisplayName,
                category,
                "POM / RegionKit",
                MergeTags(descriptor.Tags, "pom", "regionkit"));
        }
    }

    private static Type FindPomType()
    {
        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
        for (int i = 0; i < assemblies.Length; i++)
        {
            Type type = assemblies[i].GetType("Pom.Pom", false);
            if (type != null) return type;
        }
        return null;
    }

    private static IEnumerable<string> MergeTags(IReadOnlyList<string> existing, params string[] extra)
    {
        if (existing != null)
            for (int i = 0; i < existing.Count; i++) yield return existing[i];
        for (int i = 0; i < extra.Length; i++) yield return extra[i];
    }
}

internal static class CompatibilityBootstrap
{
    private static bool registered;

    internal static void Enable()
    {
        if (registered) return;
        ObjectCatalog.RegisterEnricher(new PomCatalogEnricher());
        registered = true;
    }
}
