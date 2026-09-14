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
        internal long CollectionRevision = 1L;

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

        internal void TouchCollection()
        {
            CollectionRevision = CollectionRevision >= long.MaxValue ? 1L : CollectionRevision + 1L;
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

        // One pending semantic collection change is enough to invalidate selection membership.
        // Avoid bumping repeatedly when several command/history paths report the same batch before
        // presentation consumes the hint; a later distinct batch will bump again after Consume().
        if (!state.Collection)
            state.TouchCollection();

        state.Collection = true;
        state.AllMembers = false;
        state.HasMember = false;
        state.Member = null;
    }

    internal static void MarkFull(EditorSession session)
    {
        if (session == null) return;
        State state = Get(session);

        // Full includes collection uncertainty. Preserve the same coalescing rule as MarkCollection
        // so one real batch cannot produce several selection-membership invalidations.
        if (!state.Collection)
            state.TouchCollection();

        state.Full = true;
        state.AllMembers = true;
        state.Collection = true;
        state.HasMember = false;
        state.Member = null;
    }

    /// <summary>
    /// Non-consuming semantic revision used by EditorSession to validate selection membership only
    /// when the object collection may have changed. The pending presentation hint can still be
    /// consumed independently by the Objects presentation pipeline.
    /// </summary>
    internal static long GetCollectionRevision(EditorSession session) =>
        session == null ? 0L : Get(session).CollectionRevision;

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