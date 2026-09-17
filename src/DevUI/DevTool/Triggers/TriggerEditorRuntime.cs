using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Objects;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Triggers;

public sealed class EditorTriggeredEventSnapshot
{
    public bool HasEvent { get; init; }
    public string Type { get; init; } = string.Empty;
    public string SongName { get; init; } = string.Empty;
    public float Priority { get; init; }
    public float MaxThreatLevel { get; init; }
    public float DroneTolerance { get; init; }
    public float Volume { get; init; }
    public float FadeInSeconds { get; init; }
    public bool Loop { get; init; }
    public bool OneSongPerCycle { get; init; }
    public bool StopAtDeath { get; init; }
    public bool StopAtGate { get; init; }
    public int RoomsRange { get; init; } = -1;
    public int CyclesRest { get; init; } = -1;
    public string StopMode { get; init; } = string.Empty;
    public float FadeOutSeconds { get; init; }
    public bool AfterEncounter { get; init; }
    public bool OnlyWhenShowingDirection { get; init; }
    public int FromCycle { get; init; }
}

public sealed class EditorTriggerSnapshot
{
    public int Index { get; init; }
    public string Type { get; init; } = string.Empty;
    public bool Selected { get; init; }
    public bool IsSpot { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float Radius { get; init; }
    public int ActiveFromCycle { get; init; }
    public int ActiveToCycle { get; init; }
    public float DelaySeconds { get; init; }
    public float FireChance { get; init; }
    public bool MultiUse { get; init; }
    public int Entrance { get; init; }
    public int Karma { get; init; }
    public string CreatureType { get; init; } = string.Empty;
    public string[] AllowedSlugcats { get; init; } = Array.Empty<string>();
    public EditorTriggeredEventSnapshot Event { get; init; } = new();
}

public sealed class EditorTriggerPresentationSnapshot
{
    public static readonly EditorTriggerPresentationSnapshot Empty = new();

    public bool Available { get; init; }
    public string[] TriggerTypes { get; init; } = Array.Empty<string>();
    public string[] EventTypes { get; init; } = Array.Empty<string>();
    public string[] SongNames { get; init; } = Array.Empty<string>();
    public string[] SlugcatNames { get; init; } = Array.Empty<string>();
    public EditorTriggerSnapshot[] Triggers { get; init; } = Array.Empty<EditorTriggerSnapshot>();
    public int SelectedIndex { get; init; } = -1;
    public int EntranceCount { get; init; }
}

internal sealed class TriggerEditorState
{
    internal int SelectedIndex = -1;
    internal long Revision = 1L;

    internal bool SetSelectedIndex(int value)
    {
        if (SelectedIndex == value) return false;
        SelectedIndex = value;
        Revision = Revision >= long.MaxValue ? 1L : Revision + 1L;
        return true;
    }
}

internal static class TriggerEditorStateHub
{
    private static ConditionalWeakTable<EditorSession, TriggerEditorState> states = new();

    internal static TriggerEditorState Get(EditorSession session) =>
        session == null ? null : states.GetValue(session, _ => new TriggerEditorState());

    internal static void SynchronizeFromLegacyNode(EditorSession session, DevUINode node)
    {
        if (session?.ToolMode != EditorToolMode.Triggers || session.RoomSettings?.triggers == null || node == null)
            return;

        DevUINode current = node;
        while (current != null)
        {
            if (current is TriggerPanel panel && panel.trigger != null)
            {
                int index = session.RoomSettings.triggers.IndexOf(panel.trigger);
                if (index >= 0) Get(session)?.SetSelectedIndex(index);
                return;
            }
            current = current.parentNode;
        }
    }

    internal static void Reset() => states = new ConditionalWeakTable<EditorSession, TriggerEditorState>();
}

public static class TriggerEditorPresentationHub
{
    private static volatile EditorTriggerPresentationSnapshot current = EditorTriggerPresentationSnapshot.Empty;
    private static string[] triggerTypes = Array.Empty<string>();
    private static string[] eventTypes = Array.Empty<string>();
    private static string[] slugcatNames = Array.Empty<string>();
    private static int triggerTypeCount = -1;
    private static int eventTypeCount = -1;
    private static int slugcatCount = -1;

    private static EditorSession observedSession;
    private static global::RoomSettings observedSettings;
    private static string[] observedSongNames;
    private static long observedRevision;
    private static long observedSelectionRevision;
    private static int observedTriggerCount = -1;
    private static int observedSelectedIndex = int.MinValue;
    private static int observedEntranceCount = int.MinValue;

    public static EditorTriggerPresentationSnapshot Current => current;

    internal static void Publish(EditorSession session)
    {
        if (session?.ToolMode != EditorToolMode.Triggers || session.RoomSettings?.triggers == null)
        {
            Clear();
            return;
        }

        DevUINode legacyDrag = session.Owner?.draggedNode;
        if (legacyDrag == null && session.Owner?.activePage is TriggersPage legacyPage)
            legacyDrag = legacyPage.draggedObject;
        TriggerEditorStateHub.SynchronizeFromLegacyNode(session, legacyDrag);

        TriggerEditorState state = TriggerEditorStateHub.Get(session);
        int count = session.RoomSettings.triggers.Count;
        if (state.SelectedIndex >= count) state.SetSelectedIndex(count - 1);
        if (state.SelectedIndex < -1) state.SetSelectedIndex(-1);

        bool opaqueLiveWriter = EditorRevisionHub.RequiresLiveWorkspaceRefresh(session);
        if (opaqueLiveWriter)
        {
            EditorRevisionHub.Mark(session, EditorRevisionKind.Triggers);
            TriggerPresentationChangeHintHub.MarkFull(session);
        }
        else if (legacyDrag != null)
        {
            EditorRevisionHub.Mark(session, EditorRevisionKind.Triggers);
            TriggerPresentationChangeHintHub.MarkMember(session, state.SelectedIndex);
        }

        TriggerSongCatalog.EnsureLoaded();

        long revision = EditorRevisionHub.Get(session, EditorRevisionKind.Triggers);
        long selectionRevision = state.Revision;
        string[] songNames = TriggerSongCatalog.CurrentNames ?? Array.Empty<string>();
        int entranceCount = session.Room?.abstractRoom?.connections?.Length ?? 0;
        bool staticListsStale =
            triggerTypeCount != ExtEnum<EventTrigger.TriggerType>.values.Count ||
            eventTypeCount != ExtEnum<TriggeredEvent.EventType>.values.Count ||
            slugcatCount != ExtEnum<SlugcatStats.Name>.values.Count;

        bool sameIdentity =
            ReferenceEquals(observedSession, session) &&
            ReferenceEquals(observedSettings, session.RoomSettings) &&
            current.Available;
        bool metadataStable =
            sameIdentity &&
            !staticListsStale &&
            ReferenceEquals(observedSongNames, songNames) &&
            observedEntranceCount == entranceCount;
        bool modelStable =
            metadataStable &&
            observedRevision == revision &&
            observedTriggerCount == count;

        if (modelStable &&
            observedSelectionRevision == selectionRevision &&
            observedSelectedIndex == state.SelectedIndex)
        {
            DevToolPerformanceMonitor.RecordPresentation(
                DevToolPresentationChannel.Triggers,
                DevToolPresentationOutcome.CacheHit);
            return;
        }

        if (modelStable)
        {
            PublishSelectionOnly(state.SelectedIndex, selectionRevision);
            DevToolPerformanceMonitor.RecordPresentation(
                DevToolPresentationChannel.Triggers,
                DevToolPresentationOutcome.PartialRebuild);
            return;
        }

        TriggerPresentationChangeHint hint = TriggerPresentationChangeHintHub.Consume(session);
        if (metadataStable && hint.HasChanges && !hint.Full)
        {
            if (PublishSemanticPartial(session, state, count, revision, selectionRevision, hint))
            {
                DevToolPerformanceMonitor.RecordPresentation(
                    DevToolPresentationChannel.Triggers,
                    DevToolPresentationOutcome.PartialRebuild);
                return;
            }
        }

        EditorTriggerSnapshot[] triggers = CaptureAllTriggers(session, state.SelectedIndex);
        RebuildStaticListsIfNeeded();

        current = new EditorTriggerPresentationSnapshot
        {
            Available = true,
            TriggerTypes = triggerTypes,
            EventTypes = eventTypes,
            SongNames = songNames,
            SlugcatNames = slugcatNames,
            Triggers = triggers,
            SelectedIndex = state.SelectedIndex,
            EntranceCount = entranceCount
        };

        Observe(session, songNames, revision, selectionRevision, count, state.SelectedIndex, entranceCount);
        DevToolPerformanceMonitor.RecordPresentation(
            DevToolPresentationChannel.Triggers,
            DevToolPresentationOutcome.FullRebuild);
    }

    private static bool PublishSemanticPartial(
        EditorSession session,
        TriggerEditorState state,
        int count,
        long revision,
        long selectionRevision,
        TriggerPresentationChangeHint hint)
    {
        EditorTriggerSnapshot[] source = current.Triggers ?? Array.Empty<EditorTriggerSnapshot>();
        if (!hint.Collection && source.Length != count)
            return false;

        EditorTriggerSnapshot[] triggers;
        if (hint.Collection || hint.AllMembers)
        {
            triggers = CaptureAllTriggers(session, state.SelectedIndex);
        }
        else if (hint.MemberIndex >= 0)
        {
            if (hint.MemberIndex >= count || hint.MemberIndex >= source.Length)
                return false;

            triggers = (EditorTriggerSnapshot[])source.Clone();
            triggers[hint.MemberIndex] = CaptureTrigger(session, hint.MemberIndex, state.SelectedIndex);
            triggers = ApplySelection(triggers, state.SelectedIndex);
        }
        else
        {
            triggers = ApplySelection(source, state.SelectedIndex);
        }

        current = new EditorTriggerPresentationSnapshot
        {
            Available = true,
            TriggerTypes = current.TriggerTypes,
            EventTypes = current.EventTypes,
            SongNames = current.SongNames,
            SlugcatNames = current.SlugcatNames,
            Triggers = triggers,
            SelectedIndex = state.SelectedIndex,
            EntranceCount = current.EntranceCount
        };

        Observe(
            session,
            TriggerSongCatalog.CurrentNames ?? Array.Empty<string>(),
            revision,
            selectionRevision,
            count,
            state.SelectedIndex,
            current.EntranceCount);
        return true;
    }

    private static EditorTriggerSnapshot[] CaptureAllTriggers(EditorSession session, int selectedIndex)
    {
        int count = session?.RoomSettings?.triggers?.Count ?? 0;
        if (count == 0) return Array.Empty<EditorTriggerSnapshot>();

        EditorTriggerSnapshot[] result = new EditorTriggerSnapshot[count];
        for (int i = 0; i < count; i++)
            result[i] = CaptureTrigger(session, i, selectedIndex);
        return result;
    }

    private static EditorTriggerSnapshot CaptureTrigger(EditorSession session, int index, int selectedIndex)
    {
        EventTrigger trigger = session?.RoomSettings?.triggers != null &&
                               index >= 0 && index < session.RoomSettings.triggers.Count
            ? session.RoomSettings.triggers[index]
            : null;
        SpotTrigger spot = trigger as SpotTrigger;
        string[] allowed = new string[trigger?.slugcats?.Count ?? 0];
        for (int s = 0; s < allowed.Length; s++)
            allowed[s] = trigger.slugcats[s]?.value ?? string.Empty;

        return new EditorTriggerSnapshot
        {
            Index = index,
            Type = trigger?.type?.value ?? string.Empty,
            Selected = index == selectedIndex,
            IsSpot = spot != null,
            X = spot?.pos.x ?? 0f,
            Y = spot?.pos.y ?? 0f,
            Radius = spot?.rad ?? 0f,
            ActiveFromCycle = trigger?.activeFromCycle ?? 0,
            ActiveToCycle = trigger?.activeToCycle ?? -1,
            DelaySeconds = (trigger?.delay ?? 0) / 40f,
            FireChance = trigger?.fireChance ?? 1f,
            MultiUse = trigger?.multiUse ?? false,
            Entrance = trigger?.entrance ?? -1,
            Karma = trigger?.karma ?? 0,
            CreatureType = trigger is SeeCreatureTrigger see
                ? see.creatureType?.value ?? string.Empty
                : string.Empty,
            AllowedSlugcats = allowed,
            Event = CaptureEvent(trigger?.tEvent)
        };
    }

    private static void PublishSelectionOnly(int selectedIndex, long selectionRevision)
    {
        EditorTriggerSnapshot[] source = current.Triggers ?? Array.Empty<EditorTriggerSnapshot>();
        current = new EditorTriggerPresentationSnapshot
        {
            Available = current.Available,
            TriggerTypes = current.TriggerTypes,
            EventTypes = current.EventTypes,
            SongNames = current.SongNames,
            SlugcatNames = current.SlugcatNames,
            Triggers = ApplySelection(source, selectedIndex),
            SelectedIndex = selectedIndex,
            EntranceCount = current.EntranceCount
        };

        observedSelectionRevision = selectionRevision;
        observedSelectedIndex = selectedIndex;
    }

    private static EditorTriggerSnapshot[] ApplySelection(EditorTriggerSnapshot[] source, int selectedIndex)
    {
        source ??= Array.Empty<EditorTriggerSnapshot>();
        EditorTriggerSnapshot[] next = null;

        for (int i = 0; i < source.Length; i++)
        {
            EditorTriggerSnapshot trigger = source[i];
            bool selected = i == selectedIndex;
            if (trigger == null || trigger.Selected == selected) continue;

            next ??= (EditorTriggerSnapshot[])source.Clone();
            next[i] = CloneWithSelection(trigger, selected);
        }

        return next ?? source;
    }

    private static EditorTriggerSnapshot CloneWithSelection(EditorTriggerSnapshot trigger, bool selected) => new()
    {
        Index = trigger.Index,
        Type = trigger.Type,
        Selected = selected,
        IsSpot = trigger.IsSpot,
        X = trigger.X,
        Y = trigger.Y,
        Radius = trigger.Radius,
        ActiveFromCycle = trigger.ActiveFromCycle,
        ActiveToCycle = trigger.ActiveToCycle,
        DelaySeconds = trigger.DelaySeconds,
        FireChance = trigger.FireChance,
        MultiUse = trigger.MultiUse,
        Entrance = trigger.Entrance,
        Karma = trigger.Karma,
        CreatureType = trigger.CreatureType,
        AllowedSlugcats = trigger.AllowedSlugcats,
        Event = trigger.Event
    };

    private static void Observe(
        EditorSession session,
        string[] songNames,
        long revision,
        long selectionRevision,
        int count,
        int selectedIndex,
        int entranceCount)
    {
        observedSession = session;
        observedSettings = session?.RoomSettings;
        observedSongNames = songNames;
        observedRevision = revision;
        observedSelectionRevision = selectionRevision;
        observedTriggerCount = count;
        observedSelectedIndex = selectedIndex;
        observedEntranceCount = entranceCount;
    }

    internal static void Clear()
    {
        TriggerPresentationChangeHintHub.Clear(observedSession);
        current = EditorTriggerPresentationSnapshot.Empty;
        observedSession = null;
        observedSettings = null;
        observedSongNames = null;
        observedRevision = 0L;
        observedSelectionRevision = 0L;
        observedTriggerCount = -1;
        observedSelectedIndex = int.MinValue;
        observedEntranceCount = int.MinValue;
    }

    private static EditorTriggeredEventSnapshot CaptureEvent(TriggeredEvent value)
    {
        if (value == null) return new EditorTriggeredEventSnapshot();

        EditorTriggeredEventSnapshot snapshot = new()
        {
            HasEvent = true,
            Type = value.type?.value ?? string.Empty
        };

        if (value is MusicEvent music)
        {
            return new EditorTriggeredEventSnapshot
            {
                HasEvent = true,
                Type = value.type?.value ?? string.Empty,
                SongName = music.songName ?? string.Empty,
                Priority = music.prio,
                MaxThreatLevel = music.maxThreatLevel,
                DroneTolerance = music.droneTolerance,
                Volume = music.volume,
                FadeInSeconds = music.fadeInTime / 40f,
                Loop = music.loop,
                OneSongPerCycle = music.oneSongPerCycle,
                StopAtDeath = music.stopAtDeath,
                StopAtGate = music.stopAtGate,
                RoomsRange = music.roomsRange,
                CyclesRest = music.cyclesRest
            };
        }

        if (value is StopMusicEvent stop)
        {
            return new EditorTriggeredEventSnapshot
            {
                HasEvent = true,
                Type = value.type?.value ?? string.Empty,
                SongName = stop.songName ?? string.Empty,
                Priority = stop.prio,
                StopMode = stop.type?.value ?? string.Empty,
                FadeOutSeconds = stop.fadeOutTime / 40f
            };
        }

        if (value is ShowProjectedImageEvent image)
        {
            return new EditorTriggeredEventSnapshot
            {
                HasEvent = true,
                Type = value.type?.value ?? string.Empty,
                AfterEncounter = image.afterEncounter,
                OnlyWhenShowingDirection = image.onlyWhenShowingDirection,
                FromCycle = image.fromCycle
            };
        }

        return snapshot;
    }

    private static void RebuildStaticListsIfNeeded()
    {
        int nextTriggerCount = ExtEnum<EventTrigger.TriggerType>.values.Count;
        if (triggerTypeCount != nextTriggerCount)
        {
            triggerTypes = new string[nextTriggerCount];
            for (int i = 0; i < nextTriggerCount; i++)
                triggerTypes[i] = ExtEnum<EventTrigger.TriggerType>.values.GetEntry(i);
            triggerTypeCount = nextTriggerCount;
        }

        int nextEventCount = ExtEnum<TriggeredEvent.EventType>.values.Count;
        if (eventTypeCount != nextEventCount)
        {
            eventTypes = new string[nextEventCount];
            for (int i = 0; i < nextEventCount; i++)
                eventTypes[i] = ExtEnum<TriggeredEvent.EventType>.values.GetEntry(i);
            eventTypeCount = nextEventCount;
        }

        int nextSlugcatCount = ExtEnum<SlugcatStats.Name>.values.Count;
        if (slugcatCount != nextSlugcatCount)
        {
            int visibleCount = 0;
            for (int i = 0; i < nextSlugcatCount; i++)
            {
                SlugcatStats.Name name = new(ExtEnum<SlugcatStats.Name>.values.GetEntry(i), false);
                if (!SlugcatStats.HiddenOrUnplayableSlugcat(name)) visibleCount++;
            }

            string[] names = new string[visibleCount];
            int write = 0;
            for (int i = 0; i < nextSlugcatCount; i++)
            {
                SlugcatStats.Name name = new(ExtEnum<SlugcatStats.Name>.values.GetEntry(i), false);
                if (SlugcatStats.HiddenOrUnplayableSlugcat(name)) continue;
                names[write++] = name.value;
            }
            slugcatNames = names;
            slugcatCount = nextSlugcatCount;
        }
    }
}

public enum TriggerEditorCommandKind
{
    Select,
    Create,
    Delete,
    SetValue,
    ToggleSlugcat,
    SetEventType,
    ClearEvent,
    SetEventValue
}

public readonly struct TriggerEditorCommand
{
    public TriggerEditorCommand(
        TriggerEditorCommandKind kind,
        int index = -1,
        string key = null,
        string text = null,
        EditorPropertyValue value = default)
    {
        Kind = kind;
        Index = index;
        Key = key;
        Text = text;
        Value = value;
    }

    public TriggerEditorCommandKind Kind { get; }
    public int Index { get; }
    public string Key { get; }
    public string Text { get; }
    public EditorPropertyValue Value { get; }
}

public static class TriggerEditorCommandQueue
{
    private static readonly ConcurrentQueue<TriggerEditorCommand> queue = new();

    public static void Enqueue(TriggerEditorCommand command) => queue.Enqueue(command);

    internal static void Process(EditorSession session)
    {
        long historyBeforeBatch = session?.History.Revision ?? 0L;
        bool nonHistoryDirty = false;

        while (queue.TryDequeue(out TriggerEditorCommand command))
        {
            try
            {
                long historyBeforeCommand = session?.History.Revision ?? 0L;
                bool changed = false;
                bool modelCommand = command.Kind != TriggerEditorCommandKind.Select;

                switch (command.Kind)
                {
                    case TriggerEditorCommandKind.Select:
                        TriggerEditorActions.Select(session, command.Index);
                        break;
                    case TriggerEditorCommandKind.Create:
                        changed = TriggerEditorActions.Create(session, command.Text);
                        break;
                    case TriggerEditorCommandKind.Delete:
                        changed = TriggerEditorActions.Delete(session, command.Index);
                        break;
                    case TriggerEditorCommandKind.SetValue:
                        changed = TriggerEditorActions.SetValue(session, command.Index, command.Key, command.Value);
                        break;
                    case TriggerEditorCommandKind.ToggleSlugcat:
                        changed = TriggerEditorActions.ToggleSlugcat(session, command.Index, command.Text);
                        break;
                    case TriggerEditorCommandKind.SetEventType:
                        changed = TriggerEditorActions.SetEventType(session, command.Index, command.Text);
                        break;
                    case TriggerEditorCommandKind.ClearEvent:
                        changed = TriggerEditorActions.ClearEvent(session, command.Index);
                        break;
                    case TriggerEditorCommandKind.SetEventValue:
                        changed = TriggerEditorActions.SetEventValue(session, command.Index, command.Key, command.Value);
                        break;
                }

                if (!modelCommand || !changed)
                    continue;

                MarkPresentationChange(session, command);
                if ((session?.History.Revision ?? 0L) == historyBeforeCommand)
                    nonHistoryDirty = true;
            }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("DevTool trigger command failed: " + error.Message);
            }
        }

        if (nonHistoryDirty && (session?.History.Revision ?? 0L) == historyBeforeBatch)
            EditorRevisionHub.Mark(session, EditorRevisionKind.Triggers);
    }

    private static void MarkPresentationChange(EditorSession session, TriggerEditorCommand command)
    {
        switch (command.Kind)
        {
            case TriggerEditorCommandKind.Create:
            case TriggerEditorCommandKind.Delete:
                TriggerPresentationChangeHintHub.MarkCollection(session);
                break;
            case TriggerEditorCommandKind.SetValue:
            case TriggerEditorCommandKind.ToggleSlugcat:
            case TriggerEditorCommandKind.SetEventType:
            case TriggerEditorCommandKind.ClearEvent:
            case TriggerEditorCommandKind.SetEventValue:
                TriggerPresentationChangeHintHub.MarkMember(session, command.Index);
                break;
            default:
                TriggerPresentationChangeHintHub.MarkFull(session);
                break;
        }
    }

    internal static void Clear()
    {
        while (queue.TryDequeue(out _)) { }
    }
}
