using System;
using System.Runtime.CompilerServices;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.History;

/// <summary>
/// Owns long-lived interactive edits such as native gizmo drags. Intermediate preview mutations are
/// applied directly by the owning authoring service and publish normal Revision hints, but History is
/// committed exactly once from the captured before-state to the final model state.
///
/// This deliberately does not suppress EditorHistoryService.Push globally. A continuous interaction
/// must use a preview mutation path that does not create history entries; unrelated commands remain
/// safe even if they execute while a pointer transaction is active.
/// </summary>
internal static class EditorContinuousTransactionHub
{
    private sealed class State
    {
        internal string Key = string.Empty;
        internal string Label = string.Empty;
        internal IEditorStateSnapshot Before;
        internal EditorDocumentKey Document;
        internal bool Active;
    }

    private static ConditionalWeakTable<EditorSession, State> states = new();

    internal static bool Begin(
        EditorSession session,
        string key,
        string label,
        IEditorStateSnapshot before)
    {
        if (session == null || before == null || string.IsNullOrWhiteSpace(key))
            return false;

        State state = states.GetValue(session, _ => new State());
        if (state.Active)
        {
            if (string.Equals(state.Key, key, StringComparison.Ordinal))
                return true;
            return false;
        }

        state.Key = key;
        state.Label = string.IsNullOrWhiteSpace(label) ? "Interactive edit" : label;
        state.Before = before;
        state.Document = session.DocumentKey;
        state.Active = true;
        return true;
    }

    internal static bool IsActive(EditorSession session, string key = null)
    {
        if (session == null || !states.TryGetValue(session, out State state) || !state.Active)
            return false;
        return string.IsNullOrEmpty(key) || string.Equals(state.Key, key, StringComparison.Ordinal);
    }

    internal static bool Commit(EditorSession session, string key)
    {
        if (!TryTake(session, key, out State state))
            return false;

        if (!state.Document.Equals(session.DocumentKey))
            return false;

        IEditorStateSnapshot after;
        try
        {
            after = state.Before.CaptureCurrent(session);
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool continuous transaction capture failed: " + error.Message);
            return false;
        }

        if (!SnapshotHistoryEntry.TryCreate(state.Label, state.Before, after, out SnapshotHistoryEntry entry))
            return false;

        session.History.Push(entry);
        return true;
    }

    internal static bool Cancel(EditorSession session, string key)
    {
        if (!TryTake(session, key, out State state))
            return false;
        if (!state.Document.Equals(session.DocumentKey))
            return false;

        try
        {
            return state.Before.Restore(session);
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool continuous transaction rollback failed: " + error.Message);
            return false;
        }
    }

    internal static void CancelAny(EditorSession session)
    {
        if (session == null || !states.TryGetValue(session, out State state) || !state.Active)
            return;
        string key = state.Key;
        Cancel(session, key);
    }

    internal static void Reset() =>
        states = new ConditionalWeakTable<EditorSession, State>();

    private static bool TryTake(EditorSession session, string key, out State snapshot)
    {
        snapshot = null;
        if (session == null || !states.TryGetValue(session, out State state) || !state.Active)
            return false;
        if (!string.Equals(state.Key, key, StringComparison.Ordinal))
            return false;

        snapshot = new State
        {
            Key = state.Key,
            Label = state.Label,
            Before = state.Before,
            Document = state.Document,
            Active = true
        };

        state.Key = string.Empty;
        state.Label = string.Empty;
        state.Before = null;
        state.Active = false;
        return true;
    }
}
