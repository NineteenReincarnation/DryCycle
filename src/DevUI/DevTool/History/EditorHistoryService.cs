using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Compatibility;
using DryCycle.DevUI.DevTool.Core;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.History;

public interface IEditorHistoryEntry
{
    string Label { get; }
    bool Undo(EditorSession session);
    bool Redo(EditorSession session);
}

public sealed class DelegateHistoryEntry : IEditorHistoryEntry
{
    private readonly Func<EditorSession, bool> undo;
    private readonly Func<EditorSession, bool> redo;

    public DelegateHistoryEntry(string label, Func<EditorSession, bool> undo, Func<EditorSession, bool> redo)
    {
        Label = string.IsNullOrEmpty(label) ? "Edit" : label;
        this.undo = undo ?? throw new ArgumentNullException(nameof(undo));
        this.redo = redo ?? throw new ArgumentNullException(nameof(redo));
    }

    public string Label { get; }
    public bool Undo(EditorSession session) => undo(session);
    public bool Redo(EditorSession session) => redo(session);
}

/// <summary>
/// One history stack per editor document. Switching Object/Room/Sound tools therefore
/// keeps Undo/Redo intact, while changing room/map/relationship documents activates a
/// separate stack.
/// </summary>
public sealed class EditorHistoryService
{
    private sealed class DocumentHistory
    {
        internal readonly List<IEditorHistoryEntry> Undo = new();
        internal readonly List<IEditorHistoryEntry> Redo = new();
    }

    private readonly Dictionary<EditorDocumentKey, DocumentHistory> documents = new();
    private readonly int capacity;
    private EditorDocumentKey activeDocument;
    private bool hasActiveDocument;
    private long revision = 1L;
    private int lastPublishedInvalidationFrame = int.MinValue;

    public EditorHistoryService(int capacity)
    {
        this.capacity = Math.Max(1, capacity);
    }

    public bool CanUndo => Current?.Undo.Count > 0;
    public bool CanRedo => Current?.Redo.Count > 0;
    public string UndoLabel => CanUndo ? Current.Undo[Current.Undo.Count - 1].Label : null;
    public string RedoLabel => CanRedo ? Current.Redo[Current.Redo.Count - 1].Label : null;

    /// <summary>
    /// Monotonic presentation revision. This changes for every successful stack mutation even when
    /// the top labels happen to stay identical (for example, several consecutive "Move object"
    /// entries), so UI invalidation never has to infer history changes from display strings.
    /// </summary>
    public long Revision => revision;

    public void ActivateDocument(EditorDocumentKey document)
    {
        bool changed = !hasActiveDocument || !activeDocument.Equals(document);
        activeDocument = document;
        hasActiveDocument = true;
        if (!documents.ContainsKey(document))
            documents.Add(document, new DocumentHistory());
        if (changed)
            BumpRevision(modelMayHaveChanged: false);
    }

    public void Push(IEditorHistoryEntry entry)
    {
        if (entry == null || !hasActiveDocument) return;
        DocumentHistory history = Current;
        history.Undo.Add(entry);
        if (history.Undo.Count > capacity)
            history.Undo.RemoveAt(0);
        history.Redo.Clear();
        BumpRevision(modelMayHaveChanged: true);
    }

    public bool Undo(EditorSession session)
    {
        DocumentHistory history = Current;
        if (history == null || history.Undo.Count == 0) return false;

        int index = history.Undo.Count - 1;
        IEditorHistoryEntry entry = history.Undo[index];
        if (!entry.Undo(session)) return false;

        history.Undo.RemoveAt(index);
        history.Redo.Add(entry);
        BumpRevision(modelMayHaveChanged: true);
        return true;
    }

    public bool Redo(EditorSession session)
    {
        DocumentHistory history = Current;
        if (history == null || history.Redo.Count == 0) return false;

        int index = history.Redo.Count - 1;
        IEditorHistoryEntry entry = history.Redo[index];
        if (!entry.Redo(session)) return false;

        history.Redo.RemoveAt(index);
        history.Undo.Add(entry);
        if (history.Undo.Count > capacity)
            history.Undo.RemoveAt(0);
        BumpRevision(modelMayHaveChanged: true);
        return true;
    }

    public void ClearActive()
    {
        if (Current == null) return;
        if (Current.Undo.Count == 0 && Current.Redo.Count == 0) return;
        Current.Undo.Clear();
        Current.Redo.Clear();
        BumpRevision(modelMayHaveChanged: false);
    }

    private void BumpRevision(bool modelMayHaveChanged)
    {
        revision = revision >= long.MaxValue ? 1L : revision + 1L;

        // History used to be converted into presentation revisions later, inside CorePresentation.
        // That was too late for the legacy-visual suppression pass, which intentionally runs before
        // snapshot publication. Undo/Redo or a completed legacy transaction could therefore create
        // or refresh vanilla nodes in this frame while suppression still saw the previous workspace
        // revision. Publish the document-wide invalidation at the authoritative mutation point.
        // Constructor/document bootstrap bumps are ignored until this history service is the live
        // session's service; the first presentation is a full capture anyway.
        EditorSession session = DevToolSessionHub.Current;
        if (session == null || !ReferenceEquals(session.History, this))
            return;

        // A command batch can legitimately push several small history entries before presentation
        // runs once at the end of the frame. One Shell/document-family invalidation is sufficient for
        // all of them. Keep History.Revision exact for stack/UI semantics, but coalesce only the
        // downstream presentation clocks so the revision layer does not reintroduce duplicate dirties.
        int frame = Time.frameCount;
        if (lastPublishedInvalidationFrame != frame)
        {
            EditorRevisionHub.Mark(session, EditorRevisionKind.Shell);
            EditorRevisionHub.MarkWorkspace(session);
            lastPublishedInvalidationFrame = frame;
        }

        // Always advance the retained History key to the exact final stack revision, even when the
        // presentation invalidation above was coalesced. Otherwise CorePresentation would see the
        // second/third push as a new edge and publish the same semantic dirty again later this frame.
        EditorPresentationHub.ObservePublishedHistoryRevision(session, revision);

        // A successful model-history mutation also means that any retained vanilla screen-space
        // representation may now be stale. Exact migrated/quiescent pages therefore register one
        // deferred full refresh here even if the specialized action only updated its live world
        // handle. Page/document activation and history-stack-only changes do not dirty the model.
        // Unknown/foreign pages fail closed because TryDeferRefresh refuses them.
        if (modelMayHaveChanged)
            LegacyDevUiQuiescenceController.TryDeferRefresh(session);
    }

    private DocumentHistory Current =>
        hasActiveDocument && documents.TryGetValue(activeDocument, out DocumentHistory value) ? value : null;
}
