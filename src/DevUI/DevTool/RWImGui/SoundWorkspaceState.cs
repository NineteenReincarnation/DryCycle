using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Sound;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal enum SoundLibraryDestination
{
    Scene,
    WorkingGroup,
    SceneAndWorkingGroup
}

internal static class SoundWorkspaceState
{
    private static readonly HashSet<int> SceneSelectionSet = new();
    private static readonly List<int> InvalidSelectionScratch = new();

    private static string activeGroupId = string.Empty;
    private static string roomKey = string.Empty;
    private static int selectionAnchor = -1;
    private static int lastLegacySelectedIndex = -1;
    private static int expectedLegacySelection = int.MinValue;
    private static int observedSoundCount = -1;
    private static bool sceneSelectionInitialized;
    private static bool selectedIndicesDirty = true;
    private static int[] selectedIndicesCache = Array.Empty<int>();
    private static SoundGroupSnapshot[] observedGroups;
    private static SoundGroupSnapshot cachedActiveGroup;
    private static bool groupsSynchronized;
    private static SoundLibraryDestination libraryDestination = SoundLibraryDestination.Scene;

    internal static string ActiveGroupId => activeGroupId;
    internal static SoundLibraryDestination LibraryDestination
    {
        get => libraryDestination;
        set => libraryDestination = value;
    }

    internal static int SelectionCount => SceneSelectionSet.Count;

    internal static void SynchronizeGroups()
    {
        SoundGroupSnapshot[] groups = SoundGroupLibrary.Current.Groups ?? Array.Empty<SoundGroupSnapshot>();
        if (groupsSynchronized && ReferenceEquals(observedGroups, groups)) return;

        observedGroups = groups;
        groupsSynchronized = true;
        cachedActiveGroup = null;

        SoundGroupSnapshot firstLocal = null;
        for (int i = 0; i < groups.Length; i++)
        {
            SoundGroupSnapshot group = groups[i];
            if (!group.IsLocal) continue;
            firstLocal ??= group;
            if (!string.Equals(group.Id, activeGroupId, StringComparison.OrdinalIgnoreCase)) continue;
            cachedActiveGroup = group;
            return;
        }

        cachedActiveGroup = firstLocal;
        activeGroupId = firstLocal?.Id ?? string.Empty;
        if (string.IsNullOrEmpty(activeGroupId) && libraryDestination != SoundLibraryDestination.Scene)
            libraryDestination = SoundLibraryDestination.Scene;
    }

    internal static bool TryGetActiveLocalGroup(out SoundGroupSnapshot group)
    {
        SynchronizeGroups();
        group = cachedActiveGroup;
        return group != null;
    }

    internal static void SetActiveGroup(string groupId)
    {
        string next = groupId?.Trim() ?? string.Empty;
        if (string.Equals(activeGroupId, next, StringComparison.OrdinalIgnoreCase)) return;
        activeGroupId = next;
        groupsSynchronized = false;
    }

    internal static bool GroupIdExists(string groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId)) return false;
        string value = groupId.Trim();
        SoundGroupSnapshot[] groups = SoundGroupLibrary.Current.Groups ?? Array.Empty<SoundGroupSnapshot>();
        for (int i = 0; i < groups.Length; i++)
            if (string.Equals(groups[i].Id, value, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    internal static string SuggestUniqueGroupId(string displayName)
    {
        string source = displayName ?? string.Empty;
        var chars = new List<char>(source.Length);
        bool upperNext = true;
        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            bool asciiLetter = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
            bool digit = c >= '0' && c <= '9';
            if (!asciiLetter && !digit)
            {
                upperNext = true;
                continue;
            }

            if (chars.Count == 0 && digit)
                continue;

            if (asciiLetter)
            {
                char next = upperNext ? char.ToUpperInvariant(c) : c;
                chars.Add(next);
            }
            else
            {
                chars.Add(c);
            }
            upperNext = false;
        }

        string root = chars.Count > 0 ? new string(chars.ToArray()) : "SoundGroup";
        string candidate = root;
        int suffix = 2;
        while (GroupIdExists(candidate))
            candidate = root + suffix++;
        return candidate;
    }

    internal static bool IsValidGroupId(string groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId)) return false;
        string value = groupId.Trim();
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            bool valid = (c >= 'A' && c <= 'Z') ||
                         (c >= 'a' && c <= 'z') ||
                         (c >= '0' && c <= '9') ||
                         c == '_' || c == '-' || c == '.';
            if (!valid) return false;
        }
        return true;
    }

    internal static void SynchronizeScene(EditorSoundPresentationSnapshot snapshot)
    {
        string nextRoom = snapshot?.RoomKey ?? string.Empty;
        if (!string.Equals(roomKey, nextRoom, StringComparison.Ordinal))
        {
            roomKey = nextRoom;
            ClearSelectionSet();
            selectionAnchor = -1;
            lastLegacySelectedIndex = -1;
            expectedLegacySelection = int.MinValue;
            observedSoundCount = -1;
            sceneSelectionInitialized = false;
        }

        int count = snapshot?.Sounds?.Length ?? 0;
        if (count != observedSoundCount)
        {
            observedSoundCount = count;
            PruneSelection(count);
        }

        int legacyIndex = snapshot?.SelectedIndex ?? -1;
        bool legacyValid = legacyIndex >= 0 && legacyIndex < count;
        if (!sceneSelectionInitialized)
        {
            ClearSelectionSet();
            if (legacyValid) AddSelection(legacyIndex);
            selectionAnchor = legacyValid ? legacyIndex : -1;
            sceneSelectionInitialized = true;
            lastLegacySelectedIndex = legacyIndex;
            expectedLegacySelection = int.MinValue;
            return;
        }

        if (legacyIndex != lastLegacySelectedIndex)
        {
            if (legacyIndex == expectedLegacySelection)
            {
                expectedLegacySelection = int.MinValue;
            }
            else
            {
                ClearSelectionSet();
                if (legacyValid) AddSelection(legacyIndex);
                selectionAnchor = legacyValid ? legacyIndex : -1;
            }
            lastLegacySelectedIndex = legacyIndex;
        }
        else if (expectedLegacySelection == legacyIndex)
        {
            expectedLegacySelection = int.MinValue;
        }

        if (selectionAnchor >= count)
            selectionAnchor = FindMinimumSelection();
    }

    internal static bool IsSceneSelected(int index) => SceneSelectionSet.Contains(index);

    internal static void HandleSceneClick(int index, bool ctrl, bool shift)
    {
        if (shift && selectionAnchor >= 0)
        {
            if (!ctrl) ClearSelectionSet();
            int min = Math.Min(selectionAnchor, index);
            int max = Math.Max(selectionAnchor, index);
            for (int i = min; i <= max; i++) AddSelection(i);
        }
        else if (ctrl)
        {
            if (!SceneSelectionSet.Add(index))
                SceneSelectionSet.Remove(index);
            MarkSelectionDirty();
            selectionAnchor = index;
        }
        else
        {
            ClearSelectionSet();
            AddSelection(index);
            selectionAnchor = index;
        }

        sceneSelectionInitialized = true;
        expectedLegacySelection = index;
    }

    internal static void SelectAll(int count)
    {
        ClearSelectionSet();
        for (int i = 0; i < count; i++) SceneSelectionSet.Add(i);
        if (count > 0) MarkSelectionDirty();
        selectionAnchor = count > 0 ? 0 : -1;
        sceneSelectionInitialized = true;
        expectedLegacySelection = int.MinValue;
    }

    internal static int[] SelectedIndices()
    {
        if (!selectedIndicesDirty) return selectedIndicesCache;
        if (SceneSelectionSet.Count == 0)
        {
            selectedIndicesCache = Array.Empty<int>();
            selectedIndicesDirty = false;
            return selectedIndicesCache;
        }

        // Publish a fresh immutable snapshot only when selection actually changes. Commands queued
        // from the UI may retain this array after the frame; reusing and refilling the same backing
        // array would let a later selection mutation silently rewrite an already queued command.
        int[] next = new int[SceneSelectionSet.Count];
        int cursor = 0;
        foreach (int index in SceneSelectionSet)
            next[cursor++] = index;
        Array.Sort(next);
        selectedIndicesCache = next;
        selectedIndicesDirty = false;
        return selectedIndicesCache;
    }

    internal static void ClearSelection()
    {
        ClearSelectionSet();
        selectionAnchor = -1;
        sceneSelectionInitialized = true;
        expectedLegacySelection = int.MinValue;
    }

    internal static void ResetSelectionFromLegacy()
    {
        ClearSelectionSet();
        selectionAnchor = -1;
        sceneSelectionInitialized = false;
        expectedLegacySelection = int.MinValue;
    }

    private static void PruneSelection(int count)
    {
        if (SceneSelectionSet.Count == 0)
        {
            if (selectionAnchor >= count) selectionAnchor = -1;
            return;
        }

        InvalidSelectionScratch.Clear();
        foreach (int index in SceneSelectionSet)
            if (index < 0 || index >= count)
                InvalidSelectionScratch.Add(index);

        for (int i = 0; i < InvalidSelectionScratch.Count; i++)
            SceneSelectionSet.Remove(InvalidSelectionScratch[i]);
        if (InvalidSelectionScratch.Count > 0) MarkSelectionDirty();
        InvalidSelectionScratch.Clear();

        if (selectionAnchor >= count)
            selectionAnchor = FindMinimumSelection();
    }

    private static int FindMinimumSelection()
    {
        if (SceneSelectionSet.Count == 0) return -1;
        int minimum = int.MaxValue;
        foreach (int index in SceneSelectionSet)
            if (index < minimum) minimum = index;
        return minimum == int.MaxValue ? -1 : minimum;
    }

    private static void AddSelection(int index)
    {
        if (SceneSelectionSet.Add(index)) MarkSelectionDirty();
    }

    private static void ClearSelectionSet()
    {
        if (SceneSelectionSet.Count == 0) return;
        SceneSelectionSet.Clear();
        MarkSelectionDirty();
    }

    private static void MarkSelectionDirty()
    {
        selectedIndicesDirty = true;
    }
}
