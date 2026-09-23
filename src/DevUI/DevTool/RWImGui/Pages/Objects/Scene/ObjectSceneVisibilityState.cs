using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal enum ObjectSceneVisibility
{
    Hidden,
    Ghost,
    Normal,
    Hovered,
    Selected
}

internal enum ObjectCategoryVisibility
{
    Normal,
    Ghost,
    Hidden
}

/// <summary>
/// Retained frontend-only visibility state. Filtering and focus never mutate RoomSettings.
/// </summary>
internal static class ObjectSceneVisibilityState
{
    private static readonly Dictionary<string, ObjectCategoryVisibility> CategoryModes =
        new(StringComparer.OrdinalIgnoreCase);

    private static string focusedCategory = string.Empty;
    private static string searchQuery = string.Empty;
    private static int hoveredIndex = -1;
    private static long revision = 1;

    internal static long Revision => revision;
    internal static string FocusedCategory => focusedCategory;
    internal static string SearchQuery => searchQuery;
    internal static int HoveredIndex => hoveredIndex;

    internal static ObjectCategoryVisibility GetCategoryMode(string category)
    {
        category = NormalizeCategory(category);
        return CategoryModes.TryGetValue(category, out ObjectCategoryVisibility mode)
            ? mode
            : ObjectCategoryVisibility.Normal;
    }

    internal static void CycleCategoryMode(string category)
    {
        category = NormalizeCategory(category);
        ObjectCategoryVisibility current = GetCategoryMode(category);
        ObjectCategoryVisibility next = current switch
        {
            ObjectCategoryVisibility.Normal => ObjectCategoryVisibility.Ghost,
            ObjectCategoryVisibility.Ghost => ObjectCategoryVisibility.Hidden,
            _ => ObjectCategoryVisibility.Normal
        };

        if (next == ObjectCategoryVisibility.Normal) CategoryModes.Remove(category);
        else CategoryModes[category] = next;
        revision++;
    }

    internal static void SetFocusedCategory(string category)
    {
        category = string.IsNullOrWhiteSpace(category) ? string.Empty : category.Trim();
        if (string.Equals(focusedCategory, category, StringComparison.OrdinalIgnoreCase))
            category = string.Empty;
        if (string.Equals(focusedCategory, category, StringComparison.OrdinalIgnoreCase)) return;
        focusedCategory = category;
        revision++;
    }

    internal static void ClearFocus()
    {
        if (focusedCategory.Length == 0) return;
        focusedCategory = string.Empty;
        revision++;
    }

    internal static void SetSearchQuery(string query)
    {
        query = query?.Trim() ?? string.Empty;
        if (string.Equals(searchQuery, query, StringComparison.OrdinalIgnoreCase)) return;
        searchQuery = query;
        revision++;
    }

    internal static void SetHoveredIndex(int index)
    {
        if (hoveredIndex == index) return;
        // Hover is transient interaction state. It must not invalidate retained label layout;
        // otherwise the hovered label can move under the pointer when its priority changes.
        hoveredIndex = index;
    }

    internal static ObjectSceneVisibility Resolve(EditorObjectSnapshot item)
    {
        if (item == null) return ObjectSceneVisibility.Hidden;
        if (item.Selected) return ObjectSceneVisibility.Selected;
        if (item.Index == hoveredIndex) return ObjectSceneVisibility.Hovered;

        ObjectCategoryVisibility categoryMode = GetCategoryMode(item.Category);
        if (categoryMode == ObjectCategoryVisibility.Hidden) return ObjectSceneVisibility.Hidden;

        if (searchQuery.Length > 0 && !MatchesQuery(item, searchQuery))
            return ObjectSceneVisibility.Ghost;

        if (focusedCategory.Length > 0 &&
            !string.Equals(NormalizeCategory(item.Category), focusedCategory, StringComparison.OrdinalIgnoreCase))
            return ObjectSceneVisibility.Ghost;

        return categoryMode == ObjectCategoryVisibility.Ghost
            ? ObjectSceneVisibility.Ghost
            : ObjectSceneVisibility.Normal;
    }

    internal static bool IsInteractive(EditorObjectSnapshot item)
    {
        ObjectSceneVisibility state = Resolve(item);
        return state == ObjectSceneVisibility.Normal ||
               state == ObjectSceneVisibility.Hovered ||
               state == ObjectSceneVisibility.Selected;
    }

    internal static void Reset()
    {
        CategoryModes.Clear();
        focusedCategory = string.Empty;
        searchQuery = string.Empty;
        hoveredIndex = -1;
        revision++;
    }

    internal static bool MatchesQuery(EditorObjectSnapshot item, string query)
    {
        if (item == null) return false;
        if (string.IsNullOrWhiteSpace(query)) return true;
        return Contains(item.Type, query) ||
               Contains(item.DisplayName, query) ||
               Contains(item.Category, query) ||
               Contains(item.Source, query) ||
               Fuzzy(item.Type, query) ||
               Fuzzy(item.DisplayName, query);
    }

    internal static bool IsSearchMatch(EditorObjectSnapshot item) =>
        searchQuery.Length > 0 && MatchesQuery(item, searchQuery);

    internal static bool IsFocusedCategory(EditorObjectSnapshot item) =>
        item != null &&
        focusedCategory.Length > 0 &&
        string.Equals(
            NormalizeCategory(item.Category),
            focusedCategory,
            StringComparison.OrdinalIgnoreCase);

    internal static bool IsExplicitlyEmphasized(EditorObjectSnapshot item) =>
        IsSearchMatch(item) || IsFocusedCategory(item);

    private static bool Contains(string value, string query) =>
        !string.IsNullOrEmpty(value) &&
        value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool Fuzzy(string value, string query)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(query)) return false;
        int q = 0;
        for (int i = 0; i < value.Length && q < query.Length; i++)
            if (char.ToUpperInvariant(value[i]) == char.ToUpperInvariant(query[q])) q++;
        return q == query.Length;
    }

    private static string NormalizeCategory(string category) =>
        string.IsNullOrWhiteSpace(category) ? "Unsorted" : category.Trim();
}
