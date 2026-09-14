using System.Runtime.CompilerServices;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Sound;

/// <summary>
/// Trusted semantic invalidation for Sound presentation.
///
/// Hints are optimization-only. Known rebuilt-editor writes and our history snapshots provide them;
/// legacy/third-party writers do not, so the presentation hub safely widens to a full model capture.
/// Multiple hints in one DevUI update merge monotonically toward a wider scope.
/// </summary>
internal readonly struct SoundPresentationChangeHint
{
    internal SoundPresentationChangeHint(
        bool full,
        bool roomValues,
        bool allMembers,
        bool collection,
        int memberIndex)
    {
        Full = full;
        RoomValues = roomValues;
        AllMembers = allMembers;
        Collection = collection;
        MemberIndex = memberIndex;
    }

    internal bool Full { get; }
    internal bool RoomValues { get; }
    internal bool AllMembers { get; }
    internal bool Collection { get; }

    // >= 0 only when exactly one stable collection row is known to have changed.
    internal int MemberIndex { get; }

    internal bool HasChanges =>
        Full || RoomValues || AllMembers || Collection || MemberIndex >= 0;
}

internal static class SoundPresentationChangeHintHub
{
    private sealed class State
    {
        internal bool Full;
        internal bool RoomValues;
        internal bool AllMembers;
        internal bool Collection;
        internal int MemberIndex = -1;
        internal bool HasMember;

        internal SoundPresentationChangeHint Consume()
        {
            int member = HasMember && !Full && !AllMembers && !Collection
                ? MemberIndex
                : -1;

            SoundPresentationChangeHint result = new(
                Full,
                RoomValues,
                AllMembers,
                Collection,
                member);
            Reset();
            return result;
        }

        internal void Reset()
        {
            Full = false;
            RoomValues = false;
            AllMembers = false;
            Collection = false;
            MemberIndex = -1;
            HasMember = false;
        }
    }

    private static ConditionalWeakTable<EditorSession, State> states = new();

    internal static void MarkRoomValues(EditorSession session)
    {
        if (session == null) return;
        Get(session).RoomValues = true;
    }

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
        state.RoomValues = true;
        state.AllMembers = true;
        state.Collection = true;
        state.HasMember = false;
        state.MemberIndex = -1;
    }

    internal static SoundPresentationChangeHint Consume(EditorSession session) =>
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
