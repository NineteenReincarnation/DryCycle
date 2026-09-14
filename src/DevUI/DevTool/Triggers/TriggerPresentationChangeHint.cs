using System.Runtime.CompilerServices;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Triggers;

/// <summary>
/// Trusted semantic invalidation for Trigger presentation. Known edits identify one row or a
/// collection-shape change; unknown legacy/third-party writes deliberately widen to Full.
/// </summary>
internal readonly struct TriggerPresentationChangeHint
{
    internal TriggerPresentationChangeHint(bool full, bool allMembers, bool collection, int memberIndex)
    {
        Full = full;
        AllMembers = allMembers;
        Collection = collection;
        MemberIndex = memberIndex;
    }

    internal bool Full { get; }
    internal bool AllMembers { get; }
    internal bool Collection { get; }
    internal int MemberIndex { get; }
    internal bool HasChanges => Full || AllMembers || Collection || MemberIndex >= 0;
}

internal static class TriggerPresentationChangeHintHub
{
    private sealed class State
    {
        internal bool Full;
        internal bool AllMembers;
        internal bool Collection;
        internal int MemberIndex = -1;
        internal bool HasMember;

        internal TriggerPresentationChangeHint Consume()
        {
            int member = HasMember && !Full && !AllMembers && !Collection
                ? MemberIndex
                : -1;
            TriggerPresentationChangeHint result = new(Full, AllMembers, Collection, member);
            Reset();
            return result;
        }

        internal void Reset()
        {
            Full = false;
            AllMembers = false;
            Collection = false;
            MemberIndex = -1;
            HasMember = false;
        }
    }

    private static ConditionalWeakTable<EditorSession, State> states = new();

    internal static void MarkMember(EditorSession session, int index)
    {
        if (session == null || index < 0)
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
            state.MemberIndex = index;
            return;
        }

        if (state.MemberIndex != index)
        {
            state.HasMember = false;
            state.MemberIndex = -1;
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
        state.MemberIndex = -1;
    }

    internal static void MarkCollection(EditorSession session)
    {
        if (session == null) return;
        State state = Get(session);
        if (state.Full) return;
        state.Collection = true;
        state.AllMembers = false;
        state.HasMember = false;
        state.MemberIndex = -1;
    }

    internal static void MarkFull(EditorSession session)
    {
        if (session == null) return;
        State state = Get(session);
        state.Full = true;
        state.AllMembers = true;
        state.Collection = true;
        state.HasMember = false;
        state.MemberIndex = -1;
    }

    internal static TriggerPresentationChangeHint Consume(EditorSession session) =>
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
