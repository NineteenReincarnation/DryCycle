using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Dialog;

public enum EditorDialogEventKind
{
    Text,
    Wait,
    Special,
    Chatlog,
    Unknown
}

public sealed class EditorDialogEventSnapshot
{
    public EditorDialogEventKind Kind { get; init; }
    public int InitialWait { get; init; }
    public int Linger { get; init; }
    public string Text { get; init; } = string.Empty;
}

public sealed class EditorDialogPresentationSnapshot
{
    public static readonly EditorDialogPresentationSnapshot Empty = new();

    public bool Available { get; init; }
    public string Language { get; init; } = string.Empty;
    public string SelectedPath { get; init; } = string.Empty;
    public string SelectedFileName { get; init; } = string.Empty;
    public string[] DialogPaths { get; init; } = Array.Empty<string>();
    public EditorDialogEventSnapshot[] Events { get; init; } = Array.Empty<EditorDialogEventSnapshot>();
}

internal sealed class DialogEditorState
{
    internal string SelectedPath = string.Empty;
    internal bool WasNewUi;
}

internal static class DialogEditorStateHub
{
    private static ConditionalWeakTable<EditorSession, DialogEditorState> states = new();

    internal static DialogEditorState Get(EditorSession session) =>
        session == null ? null : states.GetValue(session, _ => new DialogEditorState());

    internal static void Reset() => states = new ConditionalWeakTable<EditorSession, DialogEditorState>();
}

public static class DialogEditorPresentationHub
{
    private static volatile EditorDialogPresentationSnapshot current = EditorDialogPresentationSnapshot.Empty;

    public static EditorDialogPresentationSnapshot Current => current;

    internal static void Publish(EditorSession session)
    {
        if (session?.ToolMode != EditorToolMode.Dialog || session.Owner?.activePage is not DialogPage page)
        {
            current = EditorDialogPresentationSnapshot.Empty;
            return;
        }

        DialogEditorState state = DialogEditorStateHub.Get(session);
        bool newUi = !EditorUiModeState.UseVanilla;
        if (newUi && !state.WasNewUi)
        {
            // Vanilla DialogPage renders preview text directly through HUD DialogBox/chatlog.
            // Clear it once when the rebuilt UI takes ownership so there are never two
            // previews stacked on top of each other. ConversationLoader data itself remains.
            try { page.ClearDialogs(); }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("DevTool dialog legacy preview clear failed: " + error.Message);
            }
        }
        state.WasNewUi = newUi;

        if (page.leftBoundary != null) page.leftBoundary.isVisible = !newUi;
        if (page.rightBoundary != null) page.rightBoundary.isVisible = !newUi;

        string[] paths = page.dialogPanel?.dialogPaths == null
            ? Array.Empty<string>()
            : (string[])page.dialogPanel.dialogPaths.Clone();
        Array.Sort(paths, StringComparer.OrdinalIgnoreCase);

        List<EditorDialogEventSnapshot> events = new();
        if (!string.IsNullOrEmpty(state.SelectedPath))
        {
            if (page.convoLoader?.chatlogMessages != null)
            {
                string[] messages = page.convoLoader.chatlogMessages;
                for (int i = 0; i < messages.Length; i++)
                {
                    events.Add(new EditorDialogEventSnapshot
                    {
                        Kind = EditorDialogEventKind.Chatlog,
                        Text = messages[i] ?? string.Empty
                    });
                }
            }
            else if (page.convoLoader?.events != null)
            {
                for (int i = 0; i < page.convoLoader.events.Count; i++)
                {
                    Conversation.DialogueEvent value = page.convoLoader.events[i];
                    if (value is Conversation.TextEvent text)
                    {
                        events.Add(new EditorDialogEventSnapshot
                        {
                            Kind = EditorDialogEventKind.Text,
                            InitialWait = text.initialWait,
                            Linger = text.textLinger,
                            Text = page.ReplaceParts(text.text ?? string.Empty)
                        });
                    }
                    else if (value is Conversation.WaitEvent wait)
                    {
                        events.Add(new EditorDialogEventSnapshot
                        {
                            Kind = EditorDialogEventKind.Wait,
                            InitialWait = wait.initialWait,
                            Text = "Wait"
                        });
                    }
                    else if (value is Conversation.SpecialEvent special)
                    {
                        events.Add(new EditorDialogEventSnapshot
                        {
                            Kind = EditorDialogEventKind.Special,
                            InitialWait = special.initialWait,
                            Text = special.eventName ?? string.Empty
                        });
                    }
                    else if (value != null)
                    {
                        events.Add(new EditorDialogEventSnapshot
                        {
                            Kind = EditorDialogEventKind.Unknown,
                            InitialWait = value.initialWait,
                            Text = value.GetType().Name
                        });
                    }
                }
            }
        }

        string language = session.Owner.game?.rainWorld?.inGameTranslator?.currentLanguage?.value ?? string.Empty;
        current = new EditorDialogPresentationSnapshot
        {
            Available = true,
            Language = language,
            SelectedPath = state.SelectedPath,
            SelectedFileName = string.IsNullOrEmpty(state.SelectedPath) ? string.Empty : Path.GetFileName(state.SelectedPath),
            DialogPaths = paths,
            Events = events.ToArray()
        };
    }

    internal static void Clear() => current = EditorDialogPresentationSnapshot.Empty;
}

public enum DialogEditorCommandKind
{
    SelectDialog
}

public readonly struct DialogEditorCommand
{
    public DialogEditorCommand(DialogEditorCommandKind kind, string path = null)
    {
        Kind = kind;
        Path = path;
    }

    public DialogEditorCommandKind Kind { get; }
    public string Path { get; }
}

public static class DialogEditorCommandQueue
{
    private static readonly ConcurrentQueue<DialogEditorCommand> queue = new();

    public static void Enqueue(DialogEditorCommand command) => queue.Enqueue(command);

    internal static void Process(EditorSession session)
    {
        while (queue.TryDequeue(out DialogEditorCommand command))
        {
            try
            {
                if (command.Kind == DialogEditorCommandKind.SelectDialog)
                    DialogEditorActions.SelectDialog(session, command.Path);
            }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("DevTool dialog command failed: " + error.Message);
            }
        }
    }

    internal static void Clear()
    {
        while (queue.TryDequeue(out _)) { }
    }
}
