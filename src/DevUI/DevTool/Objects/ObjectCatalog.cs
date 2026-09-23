using System;
using System.Collections.Generic;
using System.Linq;

namespace DryCycle.DevUI.DevTool.Objects;

public enum ObjectPresentationKind
{
    Point,
    Radius,
    Area,
    Directional,
    Path,
    Connection,
    Volume,
    Complex
}

public sealed class ObjectDescriptor
{
    public ObjectDescriptor(
        PlacedObject.Type type,
        string displayName,
        string category,
        string source,
        IEnumerable<string> tags = null,
        ObjectPresentationKind presentationKind = ObjectPresentationKind.Point,
        int importance = 0)
    {
        Type = type ?? throw new ArgumentNullException(nameof(type));
        DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? type.value ?? "Unknown"
            : displayName.Trim();
        Category = string.IsNullOrWhiteSpace(category)
            ? "Unsorted"
            : category.Trim();
        Source = string.IsNullOrWhiteSpace(source)
            ? "Registered Object"
            : source.Trim();
        Tags = tags == null
            ? Array.Empty<string>()
            : tags.Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        PresentationKind = presentationKind;
        Importance = importance;
    }

    public PlacedObject.Type Type { get; }
    public string DisplayName { get; }
    public string Category { get; }
    public string Source { get; }
    public IReadOnlyList<string> Tags { get; }
    public ObjectPresentationKind PresentationKind { get; }
    public int Importance { get; }

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
    private static Dictionary<string, ObjectDescriptor> cachedByType;
    private static int cachedTypeCount = -1;
    private static long registrationOrder;
    private static long revision = 1;

    /// <summary>
    /// Changes whenever descriptor metadata can produce a different catalog projection.
    /// Presentation snapshots observe this revision independently from the PlacedObject type count.
    /// </summary>
    public static long Revision => revision;

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
        cachedByType = new Dictionary<string, ObjectDescriptor>(descriptors.Count, StringComparer.Ordinal);
        for (int i = 0; i < descriptors.Count; i++)
        {
            ObjectDescriptor descriptor = descriptors[i];
            string typeName = descriptor?.Type?.value;
            if (!string.IsNullOrEmpty(typeName))
                cachedByType[typeName] = descriptor;
        }
        return cached;
    }

    public static bool TryGet(PlacedObject.Type type, out ObjectDescriptor descriptor) =>
        TryGet(type?.value, out descriptor);

    public static bool TryGet(string typeName, out ObjectDescriptor descriptor)
    {
        descriptor = null;
        if (string.IsNullOrEmpty(typeName)) return false;
        GetAll();
        return cachedByType != null && cachedByType.TryGetValue(typeName, out descriptor);
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
        cachedByType = null;
        cachedTypeCount = -1;
        unchecked { revision++; }
    }

    private static ObjectDescriptor CreateDefault(PlacedObject.Type type)
    {
        string name = SplitPascal(type.value);
        string category = GuessCategory(type.value);
        return new ObjectDescriptor(
            type,
            name,
            category,
            "PlacedObject Registry",
            GuessTags(type.value),
            GuessPresentationKind(type.value),
            GuessImportance(type.value));
    }

    private static string GuessCategory(string name)
    {
        string lower = name?.ToLowerInvariant() ?? string.Empty;
        if (lower.Contains("connection") || lower.Contains("link")) return "Connections";
        if (lower.Contains("trigger") || lower.Contains("gate")) return "Triggers";
        if (lower.Contains("spline") || lower.Contains("path")) return "Paths";
        if (lower.Contains("light") || lower.Contains("sun") || lower.Contains("dark")) return "Lighting";
        if (lower.Contains("sound") || lower.Contains("ambient") || lower.Contains("music")) return "Sound";
        if (lower.Contains("water") || lower.Contains("steam") || lower.Contains("wind") ||
            lower.Contains("flow") || lower.Contains("geyser") || lower.Contains("fog"))
            return "Environment";
        if (lower.Contains("zone") || lower.Contains("rect") || lower.Contains("filter") ||
            lower.Contains("area") || lower.Contains("cutoff"))
            return "Zones";
        if (lower.Contains("creature") || lower.Contains("scav") || lower.Contains("bat") ||
            lower.Contains("lizard") || lower.Contains("spawn"))
            return "Creatures";
        if (lower.Contains("decal") || lower.Contains("projected") || lower.Contains("cosmetic"))
            return "Decoration";
        return "Unsorted";
    }

    private static ObjectPresentationKind GuessPresentationKind(string name)
    {
        string lower = name?.ToLowerInvariant() ?? string.Empty;
        if (lower.Contains("connection") || lower.Contains("link"))
            return ObjectPresentationKind.Connection;
        if (lower.Contains("spline") || lower.Contains("path"))
            return ObjectPresentationKind.Path;
        if (lower.Contains("rect") || lower.Contains("zone") || lower.Contains("area") || lower.Contains("cutoff"))
            return ObjectPresentationKind.Area;
        if (lower.Contains("flow") || lower.Contains("wind") || lower.Contains("jet") || lower.Contains("direction"))
            return ObjectPresentationKind.Directional;
        if (lower.Contains("light") || lower.Contains("radius") || lower.Contains("circle") || lower.Contains("spot"))
            return ObjectPresentationKind.Radius;
        if (lower.Contains("fog") || lower.Contains("field") || lower.Contains("volume"))
            return ObjectPresentationKind.Volume;
        return ObjectPresentationKind.Point;
    }

    private static int GuessImportance(string name)
    {
        string lower = name?.ToLowerInvariant() ?? string.Empty;
        if (lower.Contains("trigger") || lower.Contains("gate") ||
            lower.Contains("connection") || lower.Contains("link"))
            return 2;
        if (lower.Contains("zone") || lower.Contains("path") ||
            lower.Contains("spline") || lower.Contains("light"))
            return 1;
        return 0;
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
