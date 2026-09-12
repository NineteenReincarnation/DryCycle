using System;
using System.Collections.Generic;
using System.Linq;

namespace DryCycle.DevUI.DevTool.Objects;

public sealed class ObjectDescriptor
{
    public ObjectDescriptor(
        PlacedObject.Type type,
        string displayName,
        string category,
        string source,
        IEnumerable<string> tags = null)
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

        if (Contains(DisplayName, query) || Contains(Type?.value, query) ||
            Contains(Category, query) || Contains(Source, query))
            return true;

        for (int i = 0; i < Tags.Count; i++)
            if (Contains(Tags[i], query)) return true;

        return FuzzySubsequence(DisplayName, query) || FuzzySubsequence(Type?.value, query);
    }

    private static bool Contains(string value, string query) =>
        !string.IsNullOrEmpty(value) &&
        value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

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

/// <summary>
/// Unified catalog over Rain World's final PlacedObject.Type registry. DryCycle does not
/// query any third-party registry. Other mods may optionally register richer metadata here.
/// </summary>
public static class ObjectCatalog
{
    private sealed class Registration
    {
        internal ObjectDescriptor Descriptor;
        internal int Priority;
        internal long Order;
    }

    private static readonly List<Registration> registrations = new();
    private static List<ObjectDescriptor> cached;
    private static int cachedTypeCount = -1;
    private static long registrationOrder;

    static ObjectCatalog()
    {
        // Objects workspace initialization is guaranteed to touch ObjectCatalog before an object
        // inspector is rendered. Register the optional reflection-only POM adapter here so all
        // RegionKit/POM ManagedData objects get first-class native fields without hard-linking POM.
        PomManagedDataInspectorAdapter.EnsureRegistered();
    }

    public static void RegisterDescriptor(ObjectDescriptor descriptor, int priority = 0)
    {
        if (descriptor?.Type?.value == null) throw new ArgumentNullException(nameof(descriptor));

        for (int i = 0; i < registrations.Count; i++)
        {
            if (ReferenceEquals(registrations[i].Descriptor, descriptor)) return;
        }

        registrations.Add(new Registration
        {
            Descriptor = descriptor,
            Priority = priority,
            Order = registrationOrder++
        });
        Invalidate();
    }

    public static bool UnregisterDescriptor(ObjectDescriptor descriptor)
    {
        if (descriptor == null) return false;
        for (int i = registrations.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(registrations[i].Descriptor, descriptor)) continue;
            registrations.RemoveAt(i);
            Invalidate();
            return true;
        }
        return false;
    }

    public static IReadOnlyList<ObjectDescriptor> GetAll()
    {
        int typeCount = ExtEnum<PlacedObject.Type>.values.Count;
        if (cached != null && cachedTypeCount == typeCount) return cached;

        Dictionary<string, ObjectDescriptor> byType = new(StringComparer.Ordinal);
        List<string> order = new(typeCount);
        for (int i = 0; i < typeCount; i++)
        {
            string entry = ExtEnum<PlacedObject.Type>.values.GetEntry(i);
            PlacedObject.Type type = new(entry, false);
            if (type == PlacedObject.Type.None) continue;
            byType[entry] = CreateDefault(type);
            order.Add(entry);
        }

        List<Registration> sortedRegistrations = new(registrations);
        sortedRegistrations.Sort((a, b) =>
        {
            int priority = a.Priority.CompareTo(b.Priority);
            return priority != 0 ? priority : a.Order.CompareTo(b.Order);
        });

        // Low priority applies first; high priority replaces it last.
        for (int i = 0; i < sortedRegistrations.Count; i++)
        {
            ObjectDescriptor descriptor = sortedRegistrations[i].Descriptor;
            string typeName = descriptor?.Type?.value;
            if (string.IsNullOrEmpty(typeName) || !byType.ContainsKey(typeName)) continue;
            byType[typeName] = descriptor;
        }

        List<ObjectDescriptor> descriptors = new(byType.Count);
        for (int i = 0; i < order.Count; i++)
        {
            if (byType.TryGetValue(order[i], out ObjectDescriptor descriptor))
                descriptors.Add(descriptor);
        }

        descriptors.Sort((a, b) =>
        {
            int category = string.Compare(a.Category, b.Category, StringComparison.OrdinalIgnoreCase);
            return category != 0
                ? category
                : string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
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

    public static void Invalidate()
    {
        cached = null;
        cachedTypeCount = -1;
    }

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
