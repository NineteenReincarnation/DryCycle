using System;
using System.Collections.Generic;
using System.Linq;
using DevInterface;

namespace DryCycle.DevUI.DevTool.Objects;

public sealed class ObjectDescriptor
{
    public ObjectDescriptor(PlacedObject.Type type, string displayName, string category, string source, IEnumerable<string> tags = null)
    {
        Type = type ?? throw new ArgumentNullException(nameof(type));
        DisplayName = displayName ?? type.value ?? "Unknown";
        Category = category ?? "Unsorted";
        Source = source ?? "Registered Object";
        Tags = tags == null
            ? Array.Empty<string>()
            : tags.Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }

    public PlacedObject.Type Type { get; }
    public string DisplayName { get; }
    public string Category { get; }
    public string Source { get; }
    public IReadOnlyList<string> Tags { get; }

    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        query = query.Trim();

        if (query.StartsWith("@", StringComparison.Ordinal))
            return Contains(Source, query.Substring(1));
        if (query.StartsWith("#", StringComparison.Ordinal))
            return Tags.Any(tag => Contains(tag, query.Substring(1)));
        if (query.StartsWith(":", StringComparison.Ordinal))
            return Contains(Category, query.Substring(1));

        if (Contains(DisplayName, query) || Contains(Type?.value, query) || Contains(Category, query) || Contains(Source, query))
            return true;
        for (int i = 0; i < Tags.Count; i++)
            if (Contains(Tags[i], query)) return true;
        return FuzzySubsequence(DisplayName, query) || FuzzySubsequence(Type?.value, query);
    }

    private static bool Contains(string value, string query) =>
        !string.IsNullOrEmpty(value) && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool FuzzySubsequence(string value, string query)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(query)) return false;
        int q = 0;
        for (int i = 0; i < value.Length && q < query.Length; i++)
        {
            if (char.ToUpperInvariant(value[i]) == char.ToUpperInvariant(query[q])) q++;
        }
        return q == query.Length;
    }
}

public interface IObjectCatalogEnricher
{
    void Enrich(List<ObjectDescriptor> descriptors);
}

/// <summary>
/// Unified catalog over Rain World's final PlacedObject.Type registry. No third-party registry
/// is queried. Mods that want richer names/categories can optionally register metadata here.
/// </summary>
public static class ObjectCatalog
{
    private static readonly List<IObjectCatalogEnricher> enrichers = new();
    private static readonly Dictionary<string, ObjectDescriptor> registered =
        new(StringComparer.Ordinal);
    private static List<ObjectDescriptor> cached;
    private static int cachedTypeCount = -1;

    public static void RegisterDescriptor(ObjectDescriptor descriptor)
    {
        if (descriptor?.Type?.value == null) throw new ArgumentNullException(nameof(descriptor));
        registered[descriptor.Type.value] = descriptor;
        cached = null;
    }

    public static bool UnregisterDescriptor(PlacedObject.Type type)
    {
        if (type?.value == null) return false;
        bool removed = registered.Remove(type.value);
        if (removed) cached = null;
        return removed;
    }

    public static void RegisterEnricher(IObjectCatalogEnricher enricher)
    {
        if (enricher == null || enrichers.Contains(enricher)) return;
        enrichers.Add(enricher);
        cached = null;
    }

    public static IReadOnlyList<ObjectDescriptor> GetAll()
    {
        int typeCount = ExtEnum<PlacedObject.Type>.values.Count;
        if (cached != null && cachedTypeCount == typeCount) return cached;

        List<ObjectDescriptor> descriptors = new(typeCount);
        for (int i = 0; i < typeCount; i++)
        {
            string entry = ExtEnum<PlacedObject.Type>.values.GetEntry(i);
            PlacedObject.Type type = new(entry, false);
            if (type == PlacedObject.Type.None) continue;

            if (registered.TryGetValue(entry, out ObjectDescriptor explicitDescriptor))
                descriptors.Add(explicitDescriptor);
            else
                descriptors.Add(CreateDefault(type));
        }

        for (int i = 0; i < enrichers.Count; i++)
        {
            try { enrichers[i].Enrich(descriptors); }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("DevTool object catalog enricher failed: " + error.Message);
            }
        }

        descriptors.Sort((a, b) =>
        {
            int category = string.Compare(a.Category, b.Category, StringComparison.OrdinalIgnoreCase);
            return category != 0 ? category : string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
        });

        cachedTypeCount = typeCount;
        cached = descriptors;
        return cached;
    }

    public static IEnumerable<ObjectDescriptor> Search(string query)
    {
        IReadOnlyList<ObjectDescriptor> all = GetAll();
        for (int i = 0; i < all.Count; i++)
            if (all[i].Matches(query)) yield return all[i];
    }

    public static void Invalidate() => cached = null;

    private static ObjectDescriptor CreateDefault(PlacedObject.Type type)
    {
        string name = SplitPascal(type.value);
        string category = GuessCategory(type.value);
        return new ObjectDescriptor(type, name, category, "Rain World registry", GuessTags(type.value));
    }

    private static string GuessCategory(string name)
    {
        string lower = name?.ToLowerInvariant() ?? string.Empty;
        if (lower.Contains("light") || lower.Contains("sun") || lower.Contains("dark")) return "Lighting";
        if (lower.Contains("sound") || lower.Contains("ambient") || lower.Contains("music")) return "Sound";
        if (lower.Contains("water") || lower.Contains("steam") || lower.Contains("wind")) return "Environment";
        if (lower.Contains("zone") || lower.Contains("rect") || lower.Contains("filter")) return "Zones";
        if (lower.Contains("creature") || lower.Contains("scav") || lower.Contains("bat") || lower.Contains("lizard")) return "Creatures";
        if (lower.Contains("decal") || lower.Contains("projected") || lower.Contains("cosmetic")) return "Decoration";
        return "Unsorted";
    }

    private static IEnumerable<string> GuessTags(string name)
    {
        if (string.IsNullOrEmpty(name)) yield break;
        string[] words = SplitPascal(name).Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++) yield return words[i].ToLowerInvariant();
    }

    private static string SplitPascal(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        System.Text.StringBuilder text = new();
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(value[i - 1])) text.Append(' ');
            text.Append(c);
        }
        return text.ToString();
    }
}
