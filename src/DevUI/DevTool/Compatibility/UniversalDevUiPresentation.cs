using System;
using System.Collections.Concurrent;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.History;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Detached mirror of the currently active DevInterface page. The frontend consumes only this
/// semantic model; it never needs to reference a RegionKit/DryCycle/custom control type directly.
/// </summary>
public sealed class UniversalDevUiPresentationSnapshot
{
    public static readonly UniversalDevUiPresentationSnapshot Empty = new();

    public bool Available { get; init; }
    public string PageType { get; init; } = string.Empty;
    public string PageId { get; init; } = string.Empty;
    public LegacyControlSnapshot[] Controls { get; init; } = Array.Empty<LegacyControlSnapshot>();
    public int UnmappedProtocolCount { get; init; }
}

public static class UniversalDevUiPresentationHub
{
    private const int RefreshIntervalFrames = 4;
    private static volatile UniversalDevUiPresentationSnapshot current = UniversalDevUiPresentationSnapshot.Empty;
    private static Page lastPage;
    private static int lastCaptureFrame = int.MinValue / 2;

    /// <summary>
    /// The RWImGui frontend reads this once per frame. Use that read as the page-agnostic pump for
    /// queued generic edits and snapshot refreshes, avoiding a second DevUI.Update hook and keeping
    /// all third-party interaction behind the same semantic bridge.
    /// </summary>
    public static UniversalDevUiPresentationSnapshot Current
    {
        get
        {
            EditorSession session = DevToolSessionHub.Current;
            if (session?.Owner == null || !DevToolSessionHub.IsCurrentSessionLive)
            {
                if (current.Available) Clear();
                return current;
            }

            UniversalDevUiCommandQueue.Process(session);
            Publish(session.Owner);
            return current;
        }
    }

    internal static void Publish(global::DevInterface.DevUI owner)
    {
        Page page = owner?.activePage;
        if (page == null)
        {
            Clear();
            return;
        }

        int frame = Time.frameCount;
        bool pageChanged = !ReferenceEquals(page, lastPage);
        if (!pageChanged && frame - lastCaptureFrame < RefreshIntervalFrames)
            return;

        lastPage = page;
        lastCaptureFrame = frame;

        try
        {
            LegacyControlSnapshot[] controls = LegacyDevInterfaceBridge.CaptureRoot(page);
            DevUiMigrationCoverageSnapshot coverage = DevUiMigrationCoverage.CurrentPage;
            string pageType = page.GetType().FullName ?? page.GetType().Name;
            int unmapped = coverage != null && string.Equals(coverage.PageType, pageType, StringComparison.Ordinal)
                ? coverage.UnmappedTypeCount
                : 0;

            current = new UniversalDevUiPresentationSnapshot
            {
                Available = true,
                PageType = pageType,
                PageId = page.IDstring ?? string.Empty,
                Controls = controls ?? Array.Empty<LegacyControlSnapshot>(),
                UnmappedProtocolCount = unmapped
            };
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool universal DevUI mirror capture failed: " + error.Message);
            current = new UniversalDevUiPresentationSnapshot
            {
                Available = true,
                PageType = page.GetType().FullName ?? page.GetType().Name,
                PageId = page.IDstring ?? string.Empty,
                Controls = Array.Empty<LegacyControlSnapshot>(),
                UnmappedProtocolCount = DevUiMigrationCoverage.CurrentPage?.UnmappedTypeCount ?? 0
            };
        }
    }

    internal static void Invalidate() => lastCaptureFrame = int.MinValue / 2;

    internal static void Clear()
    {
        current = UniversalDevUiPresentationSnapshot.Empty;
        lastPage = null;
        lastCaptureFrame = int.MinValue / 2;
    }
}

public enum UniversalDevUiCommandKind
{
    Click,
    SetBoolean,
    SetSlider,
    ResetSlider,
    SetChoice,
    IncrementInteger,
    SetText,
    SetDirection,
    SetColor
}

public readonly struct UniversalDevUiCommand
{
    public UniversalDevUiCommand(
        UniversalDevUiCommandKind kind,
        string path,
        int integer = 0,
        float x = 0f,
        float y = 0f,
        float z = 0f,
        float w = 0f,
        bool boolean = false,
        string text = null)
    {
        Kind = kind;
        Path = path ?? string.Empty;
        Integer = integer;
        X = x;
        Y = y;
        Z = z;
        W = w;
        Boolean = boolean;
        Text = text ?? string.Empty;
    }

    public UniversalDevUiCommandKind Kind { get; }
    public string Path { get; }
    public int Integer { get; }
    public float X { get; }
    public float Y { get; }
    public float Z { get; }
    public float W { get; }
    public bool Boolean { get; }
    public string Text { get; }
}

/// <summary>
/// Page-agnostic mutation queue used by the RWImGui frontend. Every command resolves its target
/// against the active DevInterface tree at execution time, so dynamic panels can appear/disappear
/// without the frontend retaining raw DevUINode references.
/// </summary>
public static class UniversalDevUiCommandQueue
{
    private static readonly ConcurrentQueue<UniversalDevUiCommand> Queue = new();

    public static void Enqueue(UniversalDevUiCommand command) => Queue.Enqueue(command);

    internal static void Process(EditorSession session)
    {
        if (session?.Owner == null) return;

        while (Queue.TryDequeue(out UniversalDevUiCommand command))
        {
            try
            {
                ExecuteWithHistory(session, command);
            }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning(
                    "DevTool universal DevUI command failed (" + command.Kind + " @ " + command.Path + "): " +
                    error.Message);
            }
        }
    }

    internal static void Clear()
    {
        while (Queue.TryDequeue(out _)) { }
        UniversalDevUiPresentationHub.Invalidate();
    }

    private static bool ExecuteWithHistory(EditorSession session, UniversalDevUiCommand command)
    {
        IEditorStateSnapshot before = LegacySnapshotFactory.CaptureForPointer(session);
        bool changed = Execute(session.Owner, command);
        if (!changed) return false;

        if (before != null)
        {
            IEditorStateSnapshot after = before.CaptureCurrent(session);
            if (SnapshotHistoryEntry.TryCreate(Label(command), before, after, out SnapshotHistoryEntry entry))
                session.History.Push(entry);
        }

        UniversalDevUiPresentationHub.Invalidate();
        return true;
    }

    private static bool Execute(global::DevInterface.DevUI owner, UniversalDevUiCommand command)
    {
        return command.Kind switch
        {
            UniversalDevUiCommandKind.Click => UniversalDevUiActionBridge.Click(owner, command.Path),
            UniversalDevUiCommandKind.SetBoolean => UniversalDevUiActionBridge.SetBoolean(owner, command.Path, command.Boolean),
            UniversalDevUiCommandKind.SetSlider => UniversalDevUiActionBridge.SetSlider(owner, command.Path, command.X),
            UniversalDevUiCommandKind.ResetSlider => UniversalDevUiActionBridge.ResetSlider(owner, command.Path),
            UniversalDevUiCommandKind.SetChoice => UniversalDevUiActionBridge.SetChoice(owner, command.Path, command.Integer),
            UniversalDevUiCommandKind.IncrementInteger => UniversalDevUiActionBridge.IncrementInteger(owner, command.Path, command.Integer),
            UniversalDevUiCommandKind.SetText => UniversalDevUiActionBridge.SetText(owner, command.Path, command.Text),
            UniversalDevUiCommandKind.SetDirection => UniversalDevUiActionBridge.SetDirection(owner, command.Path, command.X, command.Y),
            UniversalDevUiCommandKind.SetColor => UniversalDevUiActionBridge.SetColor(owner, command.Path, command.X, command.Y, command.Z, command.W),
            _ => false
        };
    }

    private static string Label(UniversalDevUiCommand command) => command.Kind switch
    {
        UniversalDevUiCommandKind.Click => "DevUI action",
        UniversalDevUiCommandKind.SetBoolean => "DevUI toggle",
        UniversalDevUiCommandKind.SetSlider => "DevUI slider",
        UniversalDevUiCommandKind.ResetSlider => "DevUI slider reset",
        UniversalDevUiCommandKind.SetChoice => "DevUI choice",
        UniversalDevUiCommandKind.IncrementInteger => "DevUI integer",
        UniversalDevUiCommandKind.SetText => "DevUI text",
        UniversalDevUiCommandKind.SetDirection => "DevUI direction",
        UniversalDevUiCommandKind.SetColor => "DevUI color",
        _ => "DevUI edit"
    };
}
