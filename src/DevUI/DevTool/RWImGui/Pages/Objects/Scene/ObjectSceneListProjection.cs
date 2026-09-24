using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// One retained, category-first projection of the current room's object scene.
///
/// Center Scene and Browser Scene are two hosts for the same semantic list. Keeping projection,
/// ordering and range-selection here prevents the hosts from drifting into different search/group
/// behavior and makes Shift-selection follow the rows the user can actually see.
/// </summary>
internal sealed class ObjectSceneProjectedRow : IDevToolExplorerListItem
{
    internal EditorObjectSnapshot Item;
    internal string Category;
    internal string Source;
    internal string Label;
    internal string TooltipText;

    public string StableId => "ObjectScene:" + (Item?.StableId ?? 0L);
    public string PrimaryText => Label ?? string.Empty;
    public string SecondaryText => string.Empty;
    public string StatusText => string.Empty;
    public string Tooltip => TooltipText ?? string.Empty;
}

internal sealed class ObjectSceneProjectedCategory
{
    internal string Category;
    internal readonly List<ObjectSceneProjectedRow> Rows = new();
}

internal sealed class ObjectSceneListProjectionSnapshot
{
    internal static readonly ObjectSceneListProjectionSnapshot Empty = new()
    {
        Categories = Array.Empty<ObjectSceneProjectedCategory>(),
        Rows = Array.Empty<ObjectSceneProjectedRow>()
    };

    internal ObjectSceneProjectedCategory[] Categories = Array.Empty<ObjectSceneProjectedCategory>();
    internal ObjectSceneProjectedRow[] Rows = Array.Empty<ObjectSceneProjectedRow>();
    internal int MatchCount;
}

internal static class ObjectSceneListProjection
{
    private static EditorObjectSnapshot[] cachedObjects;
    private static string cachedQuery = string.Empty;
    private static ObjectSceneListProjectionSnapshot cached = ObjectSceneListProjectionSnapshot.Empty;

    private static readonly Dictionary<string, ObjectSceneProjectedCategory> CategoriesByName =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<ObjectSceneProjectedCategory> Categories = new();
    private static readonly List<ObjectSceneProjectedRow> Rows = new();

    internal static ObjectSceneListProjectionSnapshot Get(
        EditorObjectSnapshot[] objects,
        string query)
    {
        objects ??= Array.Empty<EditorObjectSnapshot>();
        query = query?.Trim() ?? string.Empty;

        if (ReferenceEquals(cachedObjects, objects) &&
            string.Equals(cachedQuery, query, StringComparison.Ordinal))
            return cached;

        Rebuild(objects, query);
        return cached;
    }

    internal static void Reset()
    {
        cachedObjects = null;
        cachedQuery = string.Empty;
        cached = ObjectSceneListProjectionSnapshot.Empty;
        CategoriesByName.Clear();
        Categories.Clear();
        Rows.Clear();
    }

    /// <summary>
    /// Enqueues one visible-row range. The range is based on the projection order, not underlying
    /// RoomSettings indices, so filtering/grouping can never cause hidden or unrelated objects to
    /// enter a Shift selection.
    /// </summary>
    internal static bool EnqueueRangeSelection(
        ObjectSceneListProjectionSnapshot projection,
        long anchorStableId,
        long targetStableId,
        bool additive)
    {
        ObjectSceneProjectedRow[] rows = projection?.Rows;
        if (rows == null || rows.Length == 0 || anchorStableId == 0L || targetStableId == 0L)
            return false;

        int anchor = -1;
        int target = -1;
        for (int i = 0; i < rows.Length && (anchor < 0 || target < 0); i++)
        {
            long stableId = rows[i]?.Item?.StableId ?? 0L;
            if (stableId == anchorStableId) anchor = i;
            if (stableId == targetStableId) target = i;
        }

        if (anchor < 0 || target < 0)
            return false;

        if (!additive)
            EditorUiCommandQueue.Enqueue(new EditorUiCommand(EditorUiCommandKind.SelectObject, index: -1));

        int min = Math.Min(anchor, target);
        int max = Math.Max(anchor, target);
        for (int i = min; i <= max; i++)
        {
            EditorObjectSnapshot item = rows[i]?.Item;
            if (item == null) continue;

            // In additive mode preserve already-selected rows. In replacement mode a Clear command
            // is already queued, so every visible row in the range must be re-added.
            if (additive && item.Selected)
                continue;

            EditorUiCommandQueue.Enqueue(new EditorUiCommand(
                EditorUiCommandKind.ToggleObjectSelection,
                index: item.Index,
                stableId: item.StableId));
        }

        return true;
    }

    private static void Rebuild(EditorObjectSnapshot[] objects, string query)
    {
        CategoriesByName.Clear();
        Categories.Clear();
        Rows.Clear();

        for (int i = 0; i < objects.Length; i++)
        {
            EditorObjectSnapshot item = objects[i];
            if (item == null || !ObjectSceneVisibilityState.MatchesQuery(item, query))
                continue;

            string category = Canonical(item.Category, "Unsorted");
            string source = Canonical(item.Source, "Unknown Source");
            string displayName = Canonical(item.DisplayName, item.Type ?? "Unknown");

            if (!CategoriesByName.TryGetValue(category, out ObjectSceneProjectedCategory group))
            {
                group = new ObjectSceneProjectedCategory { Category = category };
                CategoriesByName.Add(category, group);
                Categories.Add(group);
            }

            ObjectSceneProjectedRow row = new()
            {
                Item = item,
                Category = category,
                Source = source,
                Label = displayName + "  |  (" + item.X.ToString("0") + ", " + item.Y.ToString("0") + ")",
                TooltipText = source + " | " + (item.Type ?? string.Empty) + " | " + category
            };
            group.Rows.Add(row);
        }

        Categories.Sort((a, b) =>
            string.Compare(a.Category, b.Category, StringComparison.OrdinalIgnoreCase));

        for (int i = 0; i < Categories.Count; i++)
        {
            List<ObjectSceneProjectedRow> groupRows = Categories[i].Rows;
            groupRows.Sort(CompareRows);
            for (int j = 0; j < groupRows.Count; j++)
                Rows.Add(groupRows[j]);
        }

        cachedObjects = objects;
        cachedQuery = query;
        cached = new ObjectSceneListProjectionSnapshot
        {
            Categories = Categories.ToArray(),
            Rows = Rows.ToArray(),
            MatchCount = Rows.Count
        };
    }

    private static int CompareRows(ObjectSceneProjectedRow a, ObjectSceneProjectedRow b)
    {
        int name = string.Compare(a?.Label, b?.Label, StringComparison.OrdinalIgnoreCase);
        if (name != 0) return name;

        int source = string.Compare(a?.Source, b?.Source, StringComparison.OrdinalIgnoreCase);
        if (source != 0) return source;

        long aId = a?.Item?.StableId ?? 0L;
        long bId = b?.Item?.StableId ?? 0L;
        int stable = aId.CompareTo(bId);
        return stable != 0
            ? stable
            : (a?.Item?.Index ?? -1).CompareTo(b?.Item?.Index ?? -1);
    }

    private static string Canonical(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
