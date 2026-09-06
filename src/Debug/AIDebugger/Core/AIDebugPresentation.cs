using System;
using System.Collections.Concurrent;
using System.Threading;

namespace DryCycle.Debugging.AI;

internal enum AIDebugViewMode
{
    Live,
    Historical
}

internal sealed class AIDebugPresentationTimeline
{
    internal static readonly AIDebugPresentationTimeline Empty = new(
        0, 0, 0, 0,
        Array.Empty<AIDebugMotionSample>(),
        Array.Empty<AIDebugFastStateSample>(),
        false);

    internal readonly int StartTick;
    internal readonly int EndTick;
    internal readonly int OldestRetainedTick;
    internal readonly int NewestRetainedTick;
    internal readonly AIDebugMotionSample[] Motion;
    internal readonly AIDebugFastStateSample[] States;
    internal readonly bool Truncated;

    internal AIDebugPresentationTimeline(
        int startTick,
        int endTick,
        int oldestRetainedTick,
        int newestRetainedTick,
        AIDebugMotionSample[] motion,
        AIDebugFastStateSample[] states,
        bool truncated)
    {
        StartTick = startTick;
        EndTick = endTick;
        OldestRetainedTick = oldestRetainedTick;
        NewestRetainedTick = newestRetainedTick;
        Motion = motion ?? Array.Empty<AIDebugMotionSample>();
        States = states ?? Array.Empty<AIDebugFastStateSample>();
        Truncated = truncated;
    }
}

internal sealed class AIDebugPresentationTrackedEntity
{
    internal readonly DebugEntityKey Key;
    internal readonly string DisplayName;
    internal readonly string Room;
    internal readonly bool Selected;
    internal readonly bool Pinned;
    internal readonly AIDebugResolvedMotion Motion;
    internal readonly AIDebugResolvedFastState FastState;

    internal AIDebugPresentationTrackedEntity(AIDebugPresentationEntity entity, int cursorTick)
    {
        Key = entity.Key;
        DisplayName = entity.DisplayName;
        Room = entity.Room;
        Selected = entity.Selected;
        Pinned = entity.Pinned;
        AIDebugRecorderReadApi.TryResolveMotion(Key, cursorTick, out Motion);
        AIDebugRecorderReadApi.TryResolveFastState(Key, cursorTick, out FastState);
    }
}

// Thread boundary between Rain World's Unity/main-thread AI capture and the RWImGUI
// Present callback. Everything published here is detached from live Rain World / Unity
// objects. The frontend may read it from the render/present thread without touching the
// simulation directly.
internal sealed class AIDebugPresentationSnapshot
{
    internal static readonly AIDebugPresentationSnapshot Empty = new(
        false,
        false,
        false,
        0,
        AIDebugLanguage.English,
        Array.Empty<AIDebugPresentationEntity>(),
        null,
        "AI Observatory is waiting for RainWorldGame.",
        AIDebugViewMode.Live,
        0,
        default,
        default,
        AIDebugPresentationTimeline.Empty);

    internal readonly bool Visible;
    internal readonly bool HasGame;
    internal readonly bool Paused;
    internal readonly int Tick;
    internal readonly AIDebugLanguage Language;
    internal readonly AIDebugPresentationEntity[] Entities;
    internal readonly AIDebugPresentationCreature Selected;
    internal readonly string Status;
    internal readonly AIDebugViewMode ViewMode;
    internal readonly int CursorTick;
    internal readonly AIDebugResolvedMotion CursorMotion;
    internal readonly AIDebugResolvedFastState CursorFastState;
    internal readonly AIDebugPresentationTimeline Timeline;
    internal readonly int PinnedCount;
    internal readonly bool SelectedPinned;
    internal readonly AIDebugPresentationTrackedEntity[] Tracked;

    internal AIDebugPresentationSnapshot(
        bool visible,
        bool hasGame,
        bool paused,
        int tick,
        AIDebugLanguage language,
        AIDebugPresentationEntity[] entities,
        AIDebugPresentationCreature selected,
        string status,
        AIDebugViewMode viewMode = AIDebugViewMode.Live,
        int cursorTick = 0,
        AIDebugResolvedMotion cursorMotion = default,
        AIDebugResolvedFastState cursorFastState = default,
        AIDebugPresentationTimeline timeline = null)
    {
        Visible = visible;
        HasGame = hasGame;
        Paused = paused;
        Tick = tick;
        Language = language;
        Entities = entities ?? Array.Empty<AIDebugPresentationEntity>();
        Selected = selected;
        Status = status ?? string.Empty;
        ViewMode = viewMode;
        CursorTick = cursorTick;
        CursorMotion = cursorMotion;
        CursorFastState = cursorFastState;

        int effectiveCursor = cursorTick == 0 ? tick : cursorTick;
        int pinned = 0;
        int trackedCount = 0;
        bool selectedPinned = false;
        DebugEntityKey timelineKey = default;
        bool hasTimelineKey = false;

        for (int i = 0; i < Entities.Length; i++)
        {
            AIDebugPresentationEntity entity = Entities[i];
            if (entity == null) continue;
            if (entity.Pinned) pinned++;
            if (entity.Selected || entity.Pinned) trackedCount++;
            if (entity.Selected)
            {
                timelineKey = entity.Key;
                hasTimelineKey = true;
                if (entity.Pinned) selectedPinned = true;
            }
        }

        if (!hasTimelineKey && selected != null)
        {
            timelineKey = selected.Key;
            hasTimelineKey = true;
        }

        if (selected != null && !selectedPinned &&
            AIDebugRecorderReadApi.TryGetEntityStatus(selected.Key, out AIDebugRecorderEntityStatus selectedStatus))
        {
            selectedPinned = selectedStatus.Pinned;
        }

        PinnedCount = pinned;
        SelectedPinned = selectedPinned;

        if (trackedCount <= 0)
        {
            Tracked = Array.Empty<AIDebugPresentationTrackedEntity>();
        }
        else
        {
            var tracked = new AIDebugPresentationTrackedEntity[trackedCount];
            int write = 0;
            for (int i = 0; i < Entities.Length && write < tracked.Length; i++)
            {
                AIDebugPresentationEntity entity = Entities[i];
                if (entity == null || (!entity.Selected && !entity.Pinned)) continue;
                tracked[write++] = new AIDebugPresentationTrackedEntity(entity, effectiveCursor);
            }
            Tracked = tracked;
        }

        Timeline = timeline ?? (hasGame && hasTimelineKey
            ? AIDebugTimelinePresentationBuilder.Build(timelineKey, tick, effectiveCursor, viewMode)
            : AIDebugPresentationTimeline.Empty);
    }
}

internal sealed class AIDebugPresentationEntity
{
    internal readonly DebugEntityKey Key;
    internal readonly string DisplayName;
    internal readonly string Room;
    internal readonly AIDebugEntityState State;
    internal readonly bool Selected;
    internal readonly bool VisibleRoom;
    internal readonly bool Pinned;

    internal AIDebugPresentationEntity(
        DebugEntityKey key,
        string displayName,
        string room,
        AIDebugEntityState state,
        bool selected,
        bool visibleRoom)
    {
        Key = key;
        DisplayName = displayName ?? key.ToString();
        Room = room ?? "—";
        State = state;
        Selected = selected;
        VisibleRoom = visibleRoom;
        Pinned = AIDebugRecorderReadApi.TryGetEntityStatus(key, out AIDebugRecorderEntityStatus status) && status.Pinned;
    }
}

internal sealed class AIDebugPresentationCreature
{
    internal readonly DebugEntityKey Key;
    internal readonly string DisplayName;
    internal readonly AIDebugEntityState State;
    internal readonly string ControlOwner;
    internal readonly AIDebugPresentationSection[] Sections;
    internal readonly AIDebugPresentationDecision[] Decisions;
    internal readonly AIDebugUtilityRow[] Utilities;
    internal readonly bool UtilitiesTruncated;
    internal readonly AIDebugPerceptionRow[] Perception;
    internal readonly bool PerceptionTruncated;
    internal readonly AIDebugPathState Path;
    internal readonly int AdvancedAgeTicks;

    internal AIDebugPresentationCreature(
        DebugEntityKey key,
        string displayName,
        AIDebugEntityState state,
        string controlOwner,
        AIDebugPresentationSection[] sections,
        AIDebugPresentationDecision[] decisions,
        AIDebugUtilityRow[] utilities = null,
        bool utilitiesTruncated = false,
        AIDebugPerceptionRow[] perception = null,
        bool perceptionTruncated = false,
        AIDebugPathState path = default,
        int advancedAgeTicks = 0)
    {
        Key = key;
        DisplayName = displayName ?? key.ToString();
        State = state;
        ControlOwner = controlOwner ?? "—";
        Sections = sections ?? Array.Empty<AIDebugPresentationSection>();
        Decisions = decisions ?? Array.Empty<AIDebugPresentationDecision>();
        Utilities = utilities ?? Array.Empty<AIDebugUtilityRow>();
        UtilitiesTruncated = utilitiesTruncated;
        Perception = perception ?? Array.Empty<AIDebugPerceptionRow>();
        PerceptionTruncated = perceptionTruncated;
        Path = path;
        AdvancedAgeTicks = Math.Max(0, advancedAgeTicks);
    }
}

internal sealed class AIDebugPresentationSection
{
    internal readonly string TitleKey;
    internal readonly AIDebugPresentationValue[] Values;

    internal AIDebugPresentationSection(string titleKey, AIDebugPresentationValue[] values)
    {
        TitleKey = titleKey ?? string.Empty;
        Values = values ?? Array.Empty<AIDebugPresentationValue>();
    }
}

internal sealed class AIDebugPresentationValue
{
    internal readonly string LabelKey;
    internal readonly string RawName;
    internal readonly string Value;
    internal readonly int AgeTicks;
    internal readonly string Source;

    internal AIDebugPresentationValue(string labelKey, string rawName, string value, int ageTicks, string source)
    {
        LabelKey = labelKey ?? string.Empty;
        RawName = rawName ?? string.Empty;
        Value = value ?? "—";
        AgeTicks = ageTicks < 0 ? 0 : ageTicks;
        Source = source ?? string.Empty;
    }
}

internal sealed class AIDebugPresentationDecision
{
    internal readonly string LabelKey;
    internal readonly AIDebugDecisionState State;
    internal readonly string Detail;
    internal readonly string RawName;
    internal readonly int Depth;

    internal AIDebugPresentationDecision(string labelKey, AIDebugDecisionState state, string detail, string rawName, int depth)
    {
        LabelKey = labelKey ?? string.Empty;
        State = state;
        Detail = detail ?? string.Empty;
        RawName = rawName ?? string.Empty;
        Depth = depth < 0 ? 0 : depth;
    }
}

internal enum AIDebugUiCommandKind
{
    SelectEntity,
    ClearSelection,
    TogglePause,
    Step,
    ExportSession,
    ToggleLanguage,
    Refresh,
    SeekCursorTicks,
    SetCursorTick,
    ReturnLive
}

internal readonly struct AIDebugUiCommand
{
    internal readonly AIDebugUiCommandKind Kind;
    internal readonly DebugEntityKey Key;
    internal readonly int IntValue;

    private AIDebugUiCommand(AIDebugUiCommandKind kind, DebugEntityKey key, int intValue)
    {
        Kind = kind;
        Key = key;
        IntValue = intValue;
    }

    internal static AIDebugUiCommand Select(DebugEntityKey key) =>
        new(AIDebugUiCommandKind.SelectEntity, key, 0);

    internal static AIDebugUiCommand SeekTicks(int deltaTicks) =>
        new(AIDebugUiCommandKind.SeekCursorTicks, default, deltaTicks);

    internal static AIDebugUiCommand SetCursor(int tick) =>
        new(AIDebugUiCommandKind.SetCursorTick, default, tick);

    internal static AIDebugUiCommand Simple(AIDebugUiCommandKind kind) =>
        new(kind, default, 0);
}

internal static class AIDebugPresentationHub
{
    private static readonly ConcurrentQueue<AIDebugUiCommand> Commands = new();
    private static AIDebugPresentationSnapshot current = AIDebugPresentationSnapshot.Empty;
    private static int wantsMouse;
    private static int wantsKeyboard;

    internal static AIDebugPresentationSnapshot Current =>
        Volatile.Read(ref current) ?? AIDebugPresentationSnapshot.Empty;

    internal static bool WantsMouse => Volatile.Read(ref wantsMouse) != 0;
    internal static bool WantsKeyboard => Volatile.Read(ref wantsKeyboard) != 0;

    internal static void Publish(AIDebugPresentationSnapshot snapshot)
    {
        Volatile.Write(ref current, snapshot ?? AIDebugPresentationSnapshot.Empty);
    }

    internal static void Enqueue(AIDebugUiCommand command) => Commands.Enqueue(command);

    internal static bool TryDequeue(out AIDebugUiCommand command) => Commands.TryDequeue(out command);

    internal static void SetCaptureState(bool mouse, bool keyboard)
    {
        Volatile.Write(ref wantsMouse, mouse ? 1 : 0);
        Volatile.Write(ref wantsKeyboard, keyboard ? 1 : 0);
    }

    internal static void Reset()
    {
        Publish(AIDebugPresentationSnapshot.Empty);
        SetCaptureState(false, false);
        while (Commands.TryDequeue(out _)) { }
    }
}
