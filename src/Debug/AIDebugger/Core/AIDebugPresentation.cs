using System;
using System.Collections.Concurrent;
using System.Threading;

namespace DryCycle.Debugging.AI;

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
        "AI Observatory is waiting for RainWorldGame.");

    internal readonly bool Visible;
    internal readonly bool HasGame;
    internal readonly bool Paused;
    internal readonly int Tick;
    internal readonly AIDebugLanguage Language;
    internal readonly AIDebugPresentationEntity[] Entities;
    internal readonly AIDebugPresentationCreature Selected;
    internal readonly string Status;

    internal AIDebugPresentationSnapshot(
        bool visible,
        bool hasGame,
        bool paused,
        int tick,
        AIDebugLanguage language,
        AIDebugPresentationEntity[] entities,
        AIDebugPresentationCreature selected,
        string status)
    {
        Visible = visible;
        HasGame = hasGame;
        Paused = paused;
        Tick = tick;
        Language = language;
        Entities = entities ?? Array.Empty<AIDebugPresentationEntity>();
        Selected = selected;
        Status = status ?? string.Empty;
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

    internal AIDebugPresentationCreature(
        DebugEntityKey key,
        string displayName,
        AIDebugEntityState state,
        string controlOwner,
        AIDebugPresentationSection[] sections,
        AIDebugPresentationDecision[] decisions)
    {
        Key = key;
        DisplayName = displayName ?? key.ToString();
        State = state;
        ControlOwner = controlOwner ?? "—";
        Sections = sections ?? Array.Empty<AIDebugPresentationSection>();
        Decisions = decisions ?? Array.Empty<AIDebugPresentationDecision>();
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
    Refresh
}

internal readonly struct AIDebugUiCommand
{
    internal readonly AIDebugUiCommandKind Kind;
    internal readonly DebugEntityKey Key;

    private AIDebugUiCommand(AIDebugUiCommandKind kind, DebugEntityKey key)
    {
        Kind = kind;
        Key = key;
    }

    internal static AIDebugUiCommand Select(DebugEntityKey key) =>
        new(AIDebugUiCommandKind.SelectEntity, key);

    internal static AIDebugUiCommand Simple(AIDebugUiCommandKind kind) =>
        new(kind, default);
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
