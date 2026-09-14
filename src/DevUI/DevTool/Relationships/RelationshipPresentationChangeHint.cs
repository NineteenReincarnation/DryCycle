using System;
using System.Runtime.CompilerServices;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Relationships;

/// <summary>
/// Narrow invalidation hint for relationship edits. One rebuilt row represents both directed values
/// between the currently selected primary creature and one other creature, so type/intensity/reset
/// edits can patch that row instead of rebuilding the complete creature matrix.
///
/// Hints are optimization-only. Undo/Redo, legacy controls and unknown third-party writers do not
/// need to produce one; absence of a precise hint deliberately widens to the existing full capture.
/// </summary>
internal readonly struct RelationshipPresentationChangeHint
{
    internal RelationshipPresentationChangeHint(bool full, string primary, string other)
    {
        Full = full;
        Primary = primary ?? string.Empty;
        Other = other ?? string.Empty;
    }

    internal bool Full { get; }
    internal string Primary { get; }
    internal string Other { get; }
    internal bool HasPair => !Full && !string.IsNullOrEmpty(Primary) && !string.IsNullOrEmpty(Other);
}

internal static class RelationshipPresentationChangeHintHub
{
    private sealed class State
    {
        internal bool Full;
        internal string Primary = string.Empty;
        internal string Other = string.Empty;
        internal bool HasPair;

        internal RelationshipPresentationChangeHint Consume()
        {
            RelationshipPresentationChangeHint result = Full
                ? new RelationshipPresentationChangeHint(true, null, null)
                : HasPair
                    ? new RelationshipPresentationChangeHint(false, Primary, Other)
                    : default;
            Reset();
            return result;
        }

        internal void Reset()
        {
            Full = false;
            Primary = string.Empty;
            Other = string.Empty;
            HasPair = false;
        }
    }

    private static ConditionalWeakTable<EditorSession, State> states = new();

    internal static void MarkPair(EditorSession session, string primary, string other)
    {
        if (session == null || string.IsNullOrEmpty(primary) || string.IsNullOrEmpty(other))
        {
            MarkFull(session);
            return;
        }

        State state = Get(session);
        if (state.Full) return;
        if (!state.HasPair)
        {
            state.Primary = primary;
            state.Other = other;
            state.HasPair = true;
            return;
        }

        if (!string.Equals(state.Primary, primary, StringComparison.Ordinal) ||
            !string.Equals(state.Other, other, StringComparison.Ordinal))
            MarkFull(session);
    }

    internal static void MarkFull(EditorSession session)
    {
        if (session == null) return;
        State state = Get(session);
        state.Full = true;
        state.Primary = string.Empty;
        state.Other = string.Empty;
        state.HasPair = false;
    }

    internal static RelationshipPresentationChangeHint Consume(EditorSession session) =>
        session == null ? default : Get(session).Consume();

    internal static void Clear(EditorSession session)
    {
        if (session != null && states.TryGetValue(session, out State state))
            state.Reset();
    }

    internal static void Reset() =>
        states = new ConditionalWeakTable<EditorSession, State>();

    private static State Get(EditorSession session) =>
        states.GetValue(session, _ => new State());
}