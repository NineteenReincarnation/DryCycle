using System;
using System.Diagnostics;
using System.Threading;

namespace DryCycle.DevUI.DevTool.Core;

public enum EditorShortcutFeedbackKind
{
    None = 0,
    Save = 1,
    Undo = 2,
    Redo = 3,
    Generic = 4
}

public enum EditorShortcutFeedbackVisual
{
    Success = 0,
    Save = 1,
    Undo = 2,
    Redo = 3,
    Toggle = 4,
    Duplicate = 5,
    Delete = 6,
    Select = 7,
    Group = 8,
    Move = 9,
    Copy = 10,
    Paste = 11,
    Layer = 12,
    Cancel = 13,
    Warning = 14
}

public sealed class EditorShortcutFeedbackSnapshot
{
    internal EditorShortcutFeedbackSnapshot(
        EditorShortcutFeedbackKind kind,
        EditorShortcutFeedbackVisual visual,
        bool succeeded,
        string chineseTitle,
        string englishTitle,
        string keys)
    {
        Kind = kind;
        Visual = visual;
        Succeeded = succeeded;
        ChineseTitle = chineseTitle ?? string.Empty;
        EnglishTitle = englishTitle ?? string.Empty;
        Keys = keys ?? string.Empty;
    }

    public EditorShortcutFeedbackKind Kind { get; }
    public EditorShortcutFeedbackVisual Visual { get; }
    public bool Succeeded { get; }
    public string ChineseTitle { get; }
    public string EnglishTitle { get; }
    public string Keys { get; }
}

/// <summary>
/// Data-only bridge for application-level and view-specific editor shortcuts.
///
/// Keyboard handlers may live on Rain World's Unity thread or inside a specific RWImGui view. They
/// all publish through this one channel so feedback presentation is never reimplemented per page.
/// The immutable snapshot is atomically swapped and a monotonic revision wakes the top-center UI.
/// </summary>
public static class EditorShortcutFeedback
{
    private const double PresentationHoldSeconds = 1.60d;

    private static int revision;
    private static EditorShortcutFeedbackSnapshot snapshot =
        new(
            EditorShortcutFeedbackKind.None,
            EditorShortcutFeedbackVisual.Success,
            true,
            string.Empty,
            string.Empty,
            string.Empty);
    private static long holdUntilTimestamp;

    public static int Revision =>
        Volatile.Read(ref revision);

    public static EditorShortcutFeedbackSnapshot Current =>
        Volatile.Read(ref snapshot);

    /// <summary>
    /// Keeps the tiny feedback-only frontend alive long enough to animate even when the shortcut
    /// itself hides the normal editor UI (for example Esc).
    /// </summary>
    public static bool PresentationHoldActive =>
        Stopwatch.GetTimestamp() <
        Volatile.Read(ref holdUntilTimestamp);

    internal static void Publish(
        EditorShortcutFeedbackKind action,
        bool actionSucceeded)
    {
        switch (action)
        {
            case EditorShortcutFeedbackKind.Save:
                PublishCustom(
                    actionSucceeded ? "已保存" : "保存失败",
                    actionSucceeded ? "Saved" : "Save failed",
                    "Ctrl+S",
                    actionSucceeded,
                    EditorShortcutFeedbackVisual.Save,
                    EditorShortcutFeedbackKind.Save);
                return;

            case EditorShortcutFeedbackKind.Undo:
                PublishCustom(
                    actionSucceeded ? "已撤销" : "没有可撤销内容",
                    actionSucceeded ? "Undone" : "Nothing to undo",
                    "Ctrl+Z",
                    actionSucceeded,
                    actionSucceeded ? EditorShortcutFeedbackVisual.Undo : EditorShortcutFeedbackVisual.Warning,
                    EditorShortcutFeedbackKind.Undo);
                return;

            case EditorShortcutFeedbackKind.Redo:
                PublishCustom(
                    actionSucceeded ? "已重做" : "没有可重做内容",
                    actionSucceeded ? "Redone" : "Nothing to redo",
                    "Ctrl+Y / Ctrl+Shift+Z",
                    actionSucceeded,
                    actionSucceeded ? EditorShortcutFeedbackVisual.Redo : EditorShortcutFeedbackVisual.Warning,
                    EditorShortcutFeedbackKind.Redo);
                return;
        }
    }

    public static void PublishCustom(
        string chineseTitle,
        string englishTitle,
        string keys,
        bool succeeded = true,
        EditorShortcutFeedbackVisual visual = EditorShortcutFeedbackVisual.Success) =>
        PublishCustom(
            chineseTitle,
            englishTitle,
            keys,
            succeeded,
            visual,
            EditorShortcutFeedbackKind.Generic);

    private static void PublishCustom(
        string chineseTitle,
        string englishTitle,
        string keys,
        bool succeeded,
        EditorShortcutFeedbackVisual visual,
        EditorShortcutFeedbackKind kind)
    {
        EditorShortcutFeedbackSnapshot next =
            new(
                kind,
                visual,
                succeeded,
                chineseTitle,
                englishTitle,
                keys);

        Volatile.Write(
            ref snapshot,
            next);

        long now =
            Stopwatch.GetTimestamp();
        long hold =
            (long)(Stopwatch.Frequency *
                   PresentationHoldSeconds);
        Volatile.Write(
            ref holdUntilTimestamp,
            now + hold);

        Interlocked.Increment(
            ref revision);
    }
}
