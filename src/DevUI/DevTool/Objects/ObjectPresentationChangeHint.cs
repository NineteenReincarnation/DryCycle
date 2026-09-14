using System.Runtime.CompilerServices;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Objects;

/// <summary>
/// Trusted invalidation scope for the Objects workspace. Object identity is carried by the actual
/// PlacedObject reference rather than a list index so a stable member edit remains precise even when
/// unrelated operations have shifted indices earlier in the document lifetime.
///
/// Hints are optimization-only. Unknown legacy/third-party writers never need to produce one; the
/// presentation hub widens those writes to a full capture automatically.
/// </summary>
internal readonly struct ObjectPresentationChangeHint
{
    internal ObjectPresentationChangeHint(
        bool full,
        bool allMembers,
        bool collection,
        PlacedObject member)
    {
        Full = full;
        AllMembers = allMembers;
        Collection = collection;
        Member = member;
    }

    internal bool Full { get; }
    internal bool AllMembers { get; }
    internal bool Collection { get; }
    internal PlacedObject Member { get; }
    internal bool HasChanges => Full || AllMembers || Collection || Member != null;
}

internal static class ObjectPresentationChangeHintHub
{
    private sealed class State
    {
        internal bool Full;
        internal bool AllMembers;
        internal bool Collection;
        internal PlacedObject Member;
        internal bool HasMember;

        internal ObjectPresentationChangeHint Consume()
        {
            PlacedObject member = HasMember && !Full && !AllMembers && !Collection
                ? Member
                : null;
            ObjectPresentationChangeHint result = new(Full, AllMembers, Collection, member);
            Reset();
            return result;
        }

        internal void Reset()
        {
            Full = false;
            AllMembers = false;
            Collection = false;
            Member = null;
            HasMember = false;
        }
    }

    private static ConditionalWeakTable<EditorSession, State> states = new();

    internal static void MarkMember(EditorSession session, PlacedObject member)
    {
        if (session == null || member == null)
        {
            MarkAllMembers(session);
            return;
        }

        State state = Get(session);
        if (state.Full || state.Collection || state.AllMembers)
            return;

        if (!state.HasMember)
        {
            state.HasMember = true;
            state.Member = member;
            return;
        }

        if (!ReferenceEquals(state.Member, member))
        {
            state.HasMember = false;
            state.Member = null;
            state.AllMembers = true;
        }
    }

    internal static void MarkAllMembers(EditorSession session)
    {
        if (session == null) return;
        State state = Get(session);
        if (state.Full || state.Collection) return;
        state.AllMembers = true;
        state.HasMember = false;
        state.Member = null;
    }

    internal static void MarkCollection(EditorSession session)
    {
        if (session == null) return;
        State state = Get(session);
        if (state.Full) return;
        state.Collection = true;
        state.AllMembers = false;
        state.HasMember = false;
        state.Member = null;
    }

    internal static void MarkFull(EditorSession session)
    {
        if (session == null) return;
        State state = Get(session);
        state.Full = true;
        state.AllMembers = true;
        state.Collection = true;
        state.HasMember = false;
        state.Member = null;
    }

    internal static ObjectPresentationChangeHint Consume(EditorSession session) =>
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
