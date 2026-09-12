using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Compatibility;

public sealed class DevUiPageCoverageEntry
{
    public int Index { get; init; } = -1;
    public string Label { get; init; } = string.Empty;
    public bool Visited { get; init; }
    public string PageType { get; init; } = string.Empty;
    public int MirroredControlCount { get; init; }
    public int UnmappedProtocolCount { get; init; }
}

public sealed class DevUiPageCoverageSnapshot
{
    public static readonly DevUiPageCoverageSnapshot Empty = new();

    public int TotalPageCount { get; init; }
    public int VisitedPageCount { get; init; }
    public int CurrentPageIndex { get; init; } = -1;
    public int UnknownVisitedPageCount { get; init; }
    public DevUiPageCoverageEntry[] Pages { get; init; } = Array.Empty<DevUiPageCoverageEntry>();
    public bool Complete => TotalPageCount > 0 && VisitedPageCount >= TotalPageCount;
}

/// <summary>
/// Tracks which DevInterface page slots have actually been exercised by the generic runtime audit.
/// Rain World's DevUI constructs pages on demand, so a loaded-type scan alone cannot prove that a
/// page's dynamic control tree has been mirrored. This tracker makes that distinction explicit.
///
/// It never switches pages by itself and contains no page-name/type table. The slot list is read
/// structurally from DevUI's string collection and the active slot is matched from Page.name/ID/type.
/// </summary>
public static class DevUiPageCoverageTracker
{
    private sealed class SlotState
    {
        internal bool Visited;
        internal string PageType = string.Empty;
        internal int MirroredControlCount;
        internal int UnmappedProtocolCount;
    }

    private sealed class OwnerState
    {
        internal string[] Labels = Array.Empty<string>();
        internal SlotState[] Slots = Array.Empty<SlotState>();
        internal readonly HashSet<string> UnknownVisitedPages = new(StringComparer.Ordinal);
        internal int CurrentIndex = -1;
        internal DevUiPageCoverageSnapshot Snapshot = DevUiPageCoverageSnapshot.Empty;
    }

    private static ConditionalWeakTable<global::DevInterface.DevUI, OwnerState> states = new();
    private static volatile DevUiPageCoverageSnapshot current = DevUiPageCoverageSnapshot.Empty;

    public static DevUiPageCoverageSnapshot Current
    {
        get
        {
            EditorSession session = DevToolSessionHub.Current;
            global::DevInterface.DevUI owner = session?.Owner;
            if (owner == null || !DevToolSessionHub.IsCurrentSessionLive)
            {
                current = DevUiPageCoverageSnapshot.Empty;
                return current;
            }

            Observe(owner, UniversalDevUiPresentationHub.Current);
            return current;
        }
    }

    internal static void Observe(
        global::DevInterface.DevUI owner,
        UniversalDevUiPresentationSnapshot mirror)
    {
        if (owner == null)
        {
            current = DevUiPageCoverageSnapshot.Empty;
            return;
        }

        OwnerState state = states.GetValue(owner, _ => new OwnerState());
        string[] labels = ReadPageLabels(owner);
        EnsureSlots(state, labels);

        Page active = owner.activePage;
        int activeIndex = ResolveActiveIndex(active, labels);
        state.CurrentIndex = activeIndex;

        if (active != null)
        {
            string pageType = active.GetType().FullName ?? active.GetType().Name;
            int mirrorCount = mirror?.Controls?.Length ?? 0;
            int unmapped = mirror?.UnmappedProtocolCount ?? DevUiMigrationCoverage.CurrentPage?.UnmappedTypeCount ?? 0;

            if (activeIndex >= 0 && activeIndex < state.Slots.Length)
            {
                SlotState slot = state.Slots[activeIndex];
                slot.Visited = true;
                slot.PageType = pageType;
                slot.MirroredControlCount = Math.Max(slot.MirroredControlCount, mirrorCount);
                slot.UnmappedProtocolCount = unmapped;
            }
            else
            {
                // A mod can replace DevUI page construction without updating the canonical page
                // label collection. Keep such pages visible as unknown visited pages rather than
                // silently claiming full slot coverage.
                state.UnknownVisitedPages.Add(pageType);
            }
        }

        state.Snapshot = BuildSnapshot(state);
        current = state.Snapshot;
    }

    internal static void Reset()
    {
        states = new ConditionalWeakTable<global::DevInterface.DevUI, OwnerState>();
        current = DevUiPageCoverageSnapshot.Empty;
    }

    private static void EnsureSlots(OwnerState state, string[] labels)
    {
        labels ??= Array.Empty<string>();
        bool same = state.Labels.Length == labels.Length;
        if (same)
        {
            for (int i = 0; i < labels.Length; i++)
            {
                if (string.Equals(state.Labels[i], labels[i], StringComparison.Ordinal)) continue;
                same = false;
                break;
            }
        }
        if (same) return;

        string[] oldLabels = state.Labels;
        SlotState[] oldSlots = state.Slots;
        SlotState[] next = new SlotState[labels.Length];
        for (int i = 0; i < next.Length; i++)
        {
            next[i] = new SlotState();
            string label = labels[i] ?? string.Empty;
            for (int old = 0; old < oldLabels.Length && old < oldSlots.Length; old++)
            {
                if (!string.Equals(oldLabels[old], label, StringComparison.OrdinalIgnoreCase)) continue;
                next[i] = oldSlots[old] ?? new SlotState();
                break;
            }
        }

        state.Labels = Clone(labels);
        state.Slots = next;
    }

    private static DevUiPageCoverageSnapshot BuildSnapshot(OwnerState state)
    {
        int visited = 0;
        DevUiPageCoverageEntry[] entries = new DevUiPageCoverageEntry[state.Slots.Length];
        for (int i = 0; i < state.Slots.Length; i++)
        {
            SlotState slot = state.Slots[i] ?? new SlotState();
            if (slot.Visited) visited++;
            entries[i] = new DevUiPageCoverageEntry
            {
                Index = i,
                Label = i < state.Labels.Length ? state.Labels[i] ?? string.Empty : string.Empty,
                Visited = slot.Visited,
                PageType = slot.PageType ?? string.Empty,
                MirroredControlCount = slot.MirroredControlCount,
                UnmappedProtocolCount = slot.UnmappedProtocolCount
            };
        }

        return new DevUiPageCoverageSnapshot
        {
            TotalPageCount = entries.Length,
            VisitedPageCount = visited,
            CurrentPageIndex = state.CurrentIndex,
            UnknownVisitedPageCount = state.UnknownVisitedPages.Count,
            Pages = entries
        };
    }

    private static int ResolveActiveIndex(Page page, string[] labels)
    {
        if (page == null || labels == null || labels.Length == 0) return -1;

        List<string> candidates = new();
        AddCandidate(candidates, ReadStringMember(page, "name"));
        AddCandidate(candidates, page.IDstring);

        string typeName = page.GetType().Name;
        if (typeName.EndsWith("Page", StringComparison.OrdinalIgnoreCase))
            typeName = typeName.Substring(0, typeName.Length - 4);
        AddCandidate(candidates, typeName);

        for (int c = 0; c < candidates.Count; c++)
        {
            string candidate = Normalize(candidates[c]);
            for (int i = 0; i < labels.Length; i++)
            {
                if (string.Equals(candidate, Normalize(labels[i]), StringComparison.OrdinalIgnoreCase))
                    return i;
            }
        }

        // Last-resort structural match for labels such as "Room" vs "RoomSettingsPage". Require
        // a unique prefix relationship so similarly named mod pages are never guessed arbitrarily.
        int match = -1;
        string normalizedType = Normalize(typeName);
        for (int i = 0; i < labels.Length; i++)
        {
            string label = Normalize(labels[i]);
            if (string.IsNullOrEmpty(label) || string.IsNullOrEmpty(normalizedType)) continue;
            if (!normalizedType.StartsWith(label, StringComparison.OrdinalIgnoreCase) &&
                !label.StartsWith(normalizedType, StringComparison.OrdinalIgnoreCase))
                continue;
            if (match >= 0) return -1;
            match = i;
        }
        return match;
    }

    private static string[] ReadPageLabels(global::DevInterface.DevUI owner)
    {
        object raw = ReadMember(owner, "pages");
        if (raw is string[] strings) return Clone(strings);
        if (raw is IList<string> list)
        {
            string[] result = new string[list.Count];
            for (int i = 0; i < result.Length; i++) result[i] = list[i] ?? string.Empty;
            return result;
        }
        return Array.Empty<string>();
    }

    private static object ReadMember(object instance, string name)
    {
        if (instance == null || string.IsNullOrEmpty(name)) return null;
        Type current = instance.GetType();
        while (current != null)
        {
            FieldInfo field = current.GetField(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null)
            {
                try { return field.GetValue(instance); }
                catch { return null; }
            }

            PropertyInfo property = current.GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property != null && property.CanRead && property.GetIndexParameters().Length == 0)
            {
                try { return property.GetValue(instance, null); }
                catch { return null; }
            }
            current = current.BaseType;
        }
        return null;
    }

    private static string ReadStringMember(object instance, string name) => ReadMember(instance, name) as string ?? string.Empty;

    private static void AddCandidate(List<string> values, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        for (int i = 0; i < values.Count; i++)
            if (string.Equals(values[i], value, StringComparison.OrdinalIgnoreCase)) return;
        values.Add(value);
    }

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        char[] buffer = new char[value.Length];
        int count = 0;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (!char.IsLetterOrDigit(c)) continue;
            buffer[count++] = char.ToLowerInvariant(c);
        }
        return new string(buffer, 0, count);
    }

    private static string[] Clone(string[] source)
    {
        if (source == null || source.Length == 0) return Array.Empty<string>();
        string[] copy = new string[source.Length];
        for (int i = 0; i < copy.Length; i++) copy[i] = source[i] ?? string.Empty;
        return copy;
    }
}
