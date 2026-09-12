using System;
using System.Collections.Generic;
using System.Linq;
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
    private static string activeGroupId = string.Empty;
    private static string roomKey = string.Empty;
    private static int selectionAnchor = -1;
    private static int lastLegacySelectedIndex = -1;
    private static int expectedLegacySelection = int.MinValue;
    private static bool sceneSelectionInitialized;
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
        SoundGroupSnapshot firstLocal = null;
        for (int i = 0; i < groups.Length; i++)
        {
            SoundGroupSnapshot group = groups[i];
            if (!group.IsLocal) continue;
            firstLocal ??= group;
            if (string.Equals(group.Id, activeGroupId, StringComparison.OrdinalIgnoreCase))
                return;
        }

        activeGroupId = firstLocal?.Id ?? string.Empty;
        if (string.IsNullOrEmpty(activeGroupId) && libraryDestination != SoundLibraryDestination.Scene)
            libraryDestination = SoundLibraryDestination.Scene;
    }

    internal static bool TryGetActiveLocalGroup(out SoundGroupSnapshot group)
    {
        SynchronizeGroups();
        SoundGroupSnapshot[] groups = SoundGroupLibrary.Current.Groups ?? Array.Empty<SoundGroupSnapshot>();
        for (int i = 0; i < groups.Length; i++)
        {
            SoundGroupSnapshot candidate = groups[i];
            if (!candidate.IsLocal) continue;
            if (!string.Equals(candidate.Id, activeGroupId, StringComparison.OrdinalIgnoreCase)) continue;
            group = candidate;
            return true;
        }

        group = null;
        return false;
    }

    internal static void SetActiveGroup(string groupId)
    {
        activeGroupId = groupId?.Trim() ?? string.Empty;
    }

    internal static bool GroupIdExists(string groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId)) return false;
        SoundGroupSnapshot[] groups = SoundGroupLibrary.Current.Groups ?? Array.Empty<SoundGroupSnapshot>();
        for (int i = 0; i < groups.Length; i++)
            if (string.Equals(groups[i].Id, groupId.Trim(), StringComparison.OrdinalIgnoreCase))
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
            SceneSelectionSet.Clear();
            selectionAnchor = -1;
            lastLegacySelectedIndex = -1;
            expectedLegacySelection = int.MinValue;
            sceneSelectionInitialized = false;
        }

        int count = snapshot?.Sounds?.Length ?? 0;
        if (SceneSelectionSet.Count > 0)
        {
            int[] existing = SceneSelectionSet.ToArray();
            for (int i = 0; i < existing.Length; i++)
                if (existing[i] < 0 || existing[i] >= count)
                    SceneSelectionSet.Remove(existing[i]);
        }

        int legacyIndex = snapshot?.SelectedIndex ?? -1;
        bool legacyValid = legacyIndex >= 0 && legacyIndex < count;
        if (!sceneSelectionInitialized)
        {
            SceneSelectionSet.Clear();
            if (legacyValid) SceneSelectionSet.Add(legacyIndex);
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
                SceneSelectionSet.Clear();
                if (legacyValid) SceneSelectionSet.Add(legacyIndex);
                selectionAnchor = legacyValid ? legacyIndex : -1;
            }
            lastLegacySelectedIndex = legacyIndex;
        }
        else if (expectedLegacySelection == legacyIndex)
        {
            expectedLegacySelection = int.MinValue;
        }

        if (selectionAnchor >= count)
            selectionAnchor = SceneSelectionSet.Count > 0 ? SceneSelectionSet.Min() : -1;
    }

    internal static bool IsSceneSelected(int index) => SceneSelectionSet.Contains(index);

    internal static void HandleSceneClick(int index, bool ctrl, bool shift)
    {
        if (shift && selectionAnchor >= 0)
        {
            if (!ctrl) SceneSelectionSet.Clear();
            int min = Math.Min(selectionAnchor, index);
            int max = Math.Max(selectionAnchor, index);
            for (int i = min; i <= max; i++) SceneSelectionSet.Add(i);
        }
        else if (ctrl)
        {
            if (!SceneSelectionSet.Add(index))
                SceneSelectionSet.Remove(index);
            selectionAnchor = index;
        }
        else
        {
            SceneSelectionSet.Clear();
            SceneSelectionSet.Add(index);
            selectionAnchor = index;
        }

        sceneSelectionInitialized = true;
        expectedLegacySelection = index;
    }

    internal static void SelectAll(int count)
    {
        SceneSelectionSet.Clear();
        for (int i = 0; i < count; i++) SceneSelectionSet.Add(i);
        selectionAnchor = count > 0 ? 0 : -1;
        sceneSelectionInitialized = true;
        expectedLegacySelection = int.MinValue;
    }

    internal static int[] SelectedIndices()
    {
        int[] result = SceneSelectionSet.ToArray();
        Array.Sort(result);
        return result;
    }

    internal static void ClearSelection()
    {
        SceneSelectionSet.Clear();
        selectionAnchor = -1;
        sceneSelectionInitialized = true;
        expectedLegacySelection = int.MinValue;
    }

    internal static void ResetSelectionFromLegacy()
    {
        SceneSelectionSet.Clear();
        selectionAnchor = -1;
        sceneSelectionInitialized = false;
        expectedLegacySelection = int.MinValue;
    }
}
