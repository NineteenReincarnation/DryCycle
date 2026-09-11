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

    // MusicEvent
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

    // StopMusicEvent
    public string StopMode { get; init; } = string.Empty;
    public float FadeOutSeconds { get; init; }

    // ShowProjectedImageEvent
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
                if (index >= 0) Get(session).SelectedIndex = index;
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

    public static EditorTriggerPresentationSnapshot Current => current;

    internal static void Publish(EditorSession session)
    {
        if (session?.ToolMode != EditorToolMode.Triggers || session.RoomSettings?.triggers == null ||
            session.Owner?.activePage is not TriggersPage page)
        {
            current = EditorTriggerPresentationSnapshot.Empty;
            return;
        }

        TriggerEditorStateHub.SynchronizeFromLegacyNode(session, session.Owner.draggedNode ?? page.draggedObject);
        TriggerEditorState state = TriggerEditorStateHub.Get(session);
        int count = session.RoomSettings.triggers.Count;
        if (state.SelectedIndex >= count) state.SelectedIndex = count - 1;
        if (state.SelectedIndex < -1) state.SelectedIndex = -1;

        EditorTriggerSnapshot[] triggers = new EditorTriggerSnapshot[count];
        for (int i = 0; i < count; i++)
        {
            EventTrigger trigger = session.RoomSettings.triggers[i];
            SpotTrigger spot = trigger as SpotTrigger;
            string[] allowed = new string[trigger?.slugcats?.Count ?? 0];
            for (int s = 0; s < allowed.Length; s++) allowed[s] = trigger.slugcats[s]?.value ?? string.Empty;

            triggers[i] = new EditorTriggerSnapshot
            {
                Index = i,
                Type = trigger?.type?.value ?? string.Empty,
                Selected = i == state.SelectedIndex,
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
                CreatureType = trigger is SeeCreatureTrigger see ? see.creatureType?.value ?? string.Empty : string.Empty,
                AllowedSlugcats = allowed,
                Event = CaptureEvent(trigger?.tEvent)
            };
        }

        RebuildStaticListsIfNeeded();
        string[] songs = page.songNames == null ? Array.Empty<string>() : (string[])page.songNames.Clone();
        Array.Sort(songs, StringComparer.OrdinalIgnoreCase);

        current = new EditorTriggerPresentationSnapshot
        {
            Available = true,
            TriggerTypes = triggerTypes,
            EventTypes = eventTypes,
            SongNames = songs,
            SlugcatNames = slugcatNames,
            Triggers = triggers,
            SelectedIndex = state.SelectedIndex,
            EntranceCount = session.Room?.abstractRoom?.connections?.Length ?? 0
        };
    }

    internal static void Clear() => current = EditorTriggerPresentationSnapshot.Empty;

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
            for (int i = 0; i < nextTriggerCount; i++) triggerTypes[i] = ExtEnum<EventTrigger.TriggerType>.values.GetEntry(i);
            triggerTypeCount = nextTriggerCount;
        }

        int nextEventCount = ExtEnum<TriggeredEvent.EventType>.values.Count;
        if (eventTypeCount != nextEventCount)
        {
            eventTypes = new string[nextEventCount];
            for (int i = 0; i < nextEventCount; i++) eventTypes[i] = ExtEnum<TriggeredEvent.EventType>.values.GetEntry(i);
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
        while (queue.TryDequeue(out TriggerEditorCommand command))
        {
            try
            {
                switch (command.Kind)
                {
                    case TriggerEditorCommandKind.Select:
                        TriggerEditorActions.Select(session, command.Index);
                        break;
                    case TriggerEditorCommandKind.Create:
                        TriggerEditorActions.Create(session, command.Text);
                        break;
                    case TriggerEditorCommandKind.Delete:
                        TriggerEditorActions.Delete(session, command.Index);
                        break;
                    case TriggerEditorCommandKind.SetValue:
                        TriggerEditorActions.SetValue(session, command.Index, command.Key, command.Value);
                        break;
                    case TriggerEditorCommandKind.ToggleSlugcat:
                        TriggerEditorActions.ToggleSlugcat(session, command.Index, command.Text);
                        break;
                    case TriggerEditorCommandKind.SetEventType:
                        TriggerEditorActions.SetEventType(session, command.Index, command.Text);
                        break;
                    case TriggerEditorCommandKind.ClearEvent:
                        TriggerEditorActions.ClearEvent(session, command.Index);
                        break;
                    case TriggerEditorCommandKind.SetEventValue:
                        TriggerEditorActions.SetEventValue(session, command.Index, command.Key, command.Value);
                        break;
                }
            }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("DevTool trigger command failed: " + error.Message);
            }
        }
    }

    internal static void Clear()
    {
        while (queue.TryDequeue(out _)) { }
    }
}
