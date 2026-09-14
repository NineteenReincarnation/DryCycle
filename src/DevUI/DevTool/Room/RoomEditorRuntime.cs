using System;
using System.Collections.Concurrent;
using DryCycle.DevUI.DevTool.Compatibility;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Objects;
using DryCycle.DevUI.DevTool.Preview;

namespace DryCycle.DevUI.DevTool.Room;

public static class RoomEditorPresentationHub
{
    private static volatile EditorRoomSettingsSnapshot current = EditorRoomSettingsSnapshot.Empty;
    private static EditorSession observedSession;
    private static global::RoomSettings observedSettings;
    private static long observedRevision;

    public static EditorRoomSettingsSnapshot Current => current;
    internal static DevToolPresentationOutcome LastOutcome { get; private set; } = DevToolPresentationOutcome.FullRebuild;

    internal static void Publish(EditorSession session)
    {
        if (session?.ToolMode != EditorToolMode.Room || session.RoomSettings == null)
        {
            Clear();
            return;
        }

        // Opaque legacy/third-party writers deliberately do not provide semantic hints. Mark their
        // Room revision and force the safe full-capture path rather than assuming what they changed.
        if (EditorRevisionHub.RequiresLiveWorkspaceRefresh(session))
        {
            EditorRevisionHub.Mark(session, EditorRevisionKind.Room);
            RoomPresentationChangeHintHub.MarkFull(session);
        }

        long revision = EditorRevisionHub.Get(session, EditorRevisionKind.Room);
        bool sameContext =
            ReferenceEquals(observedSession, session) &&
            ReferenceEquals(observedSettings, session.RoomSettings) &&
            current.Available;

        if (sameContext && observedRevision == revision)
        {
            LastOutcome = DevToolPresentationOutcome.CacheHit;
            return;
        }

        RoomPresentationChangeHint hint = RoomPresentationChangeHintHub.Consume(session);
        bool partial = sameContext && hint.HasChanges && !hint.Full;
        current = partial
            ? RoomSettingsPresentation.Capture(session, current, hint)
            : RoomSettingsPresentation.Capture(session);
        LastOutcome = partial
            ? DevToolPresentationOutcome.PartialRebuild
            : DevToolPresentationOutcome.FullRebuild;

        observedSession = session;
        observedSettings = session.RoomSettings;
        observedRevision = revision;
    }

    internal static void Clear()
    {
        RoomPresentationChangeHintHub.Clear(observedSession);
        current = EditorRoomSettingsSnapshot.Empty;
        observedSession = null;
        observedSettings = null;
        observedRevision = 0L;
        LastOutcome = DevToolPresentationOutcome.FullRebuild;
    }
}

public enum RoomEditorCommandKind
{
    SetSetting,
    ResetSetting,
    SetPaletteFade,
    SetTerrainPaletteFade,
    SetTemplate,
    SaveAsTemplate,
    AddEffect,
    DeleteEffect,
    SetEffectAmount
}

public readonly struct RoomEditorCommand
{
    public RoomEditorCommand(
        RoomEditorCommandKind kind,
        string key = null,
        int index = -1,
        int secondaryIndex = -1,
        EditorPropertyValue value = default)
    {
        Kind = kind;
        Key = key;
        Index = index;
        SecondaryIndex = secondaryIndex;
        Value = value;
    }

    public RoomEditorCommandKind Kind { get; }
    public string Key { get; }
    public int Index { get; }
    public int SecondaryIndex { get; }
    public EditorPropertyValue Value { get; }
}

public static class RoomEditorCommandQueue
{
    private static readonly ConcurrentQueue<RoomEditorCommand> queue = new();

    public static void Enqueue(RoomEditorCommand command) => queue.Enqueue(command);

    internal static void Process(EditorSession session)
    {
        if (session == null) return;

        long historyBeforeBatch = session.History.Revision;
        bool nonHistoryDirty = false;
        while (queue.TryDequeue(out RoomEditorCommand command))
        {
            try
            {
                long historyBeforeCommand = session.History.Revision;
                bool changed = false;
                switch (command.Kind)
                {
                    case RoomEditorCommandKind.SetSetting:
                        changed = RoomEditorActions.SetRoomSetting(session, command.Key, command.Value);
                        break;
                    case RoomEditorCommandKind.ResetSetting:
                        changed = RoomEditorActions.ResetRoomSetting(session, command.Key);
                        break;
                    case RoomEditorCommandKind.SetPaletteFade:
                        changed = RoomEditorActions.SetPaletteFade(session, command.Index, command.Value.X);
                        break;
                    case RoomEditorCommandKind.SetTerrainPaletteFade:
                        changed = RoomEditorActions.SetTerrainPaletteFade(session, command.Index, command.Value.X);
                        break;
                    case RoomEditorCommandKind.SetTemplate:
                        changed = RoomEditorActions.SetRoomTemplate(session, command.Key);
                        if (changed)
                            RoomEffectLiveCompatibility.Reconcile(session);
                        break;
                    case RoomEditorCommandKind.SaveAsTemplate:
                        changed = RoomEditorActions.SaveRoomAsTemplate(session, command.Key);
                        break;
                    case RoomEditorCommandKind.AddEffect:
                        EffectPreviewRuntime.EndForPersistentOperation("add room effect");
                        changed = RoomEditorActions.AddRoomEffect(session, command.Key);
                        if (changed)
                            RoomEffectLiveCompatibility.Reconcile(session);
                        break;
                    case RoomEditorCommandKind.DeleteEffect:
                        EffectPreviewRuntime.EndForPersistentOperation("delete room effect");
                        changed = RoomEditorActions.DeleteRoomEffect(session, command.Index);
                        if (changed)
                            RoomEffectLiveCompatibility.Reconcile(session);
                        break;
                    case RoomEditorCommandKind.SetEffectAmount:
                        EffectPreviewRuntime.EndForPersistentOperation("change room effect");
                        changed = RoomEditorActions.SetRoomEffectAmount(session, command.Index, command.SecondaryIndex, command.Value.X);
                        if (changed)
                            RoomEffectLiveCompatibility.Reconcile(session);
                        break;
                }

                if (!changed) continue;

                MarkPresentationChange(session, command);
                if (session.History.Revision == historyBeforeCommand)
                    nonHistoryDirty = true;
            }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("DevTool room command failed: " + error.Message);
            }
        }

        // Normal model edits push document history; CorePresentation converts that one history edge
        // into Shell + active-workspace invalidation for the entire batch. Operations such as saving
        // a template can update visible Room catalogs without history, so preserve one direct Room
        // revision only when the whole batch stayed outside history.
        if (nonHistoryDirty && session.History.Revision == historyBeforeBatch)
            EditorRevisionHub.Mark(session, EditorRevisionKind.Room);
    }

    private static void MarkPresentationChange(EditorSession session, RoomEditorCommand command)
    {
        switch (command.Kind)
        {
            case RoomEditorCommandKind.SetSetting:
            case RoomEditorCommandKind.ResetSetting:
                RoomPresentationChangeHintHub.MarkSetting(session, command.Key);
                break;
            case RoomEditorCommandKind.SetPaletteFade:
                RoomPresentationChangeHintHub.MarkPaletteFade(session, terrain: false);
                break;
            case RoomEditorCommandKind.SetTerrainPaletteFade:
                RoomPresentationChangeHintHub.MarkPaletteFade(session, terrain: true);
                break;
            case RoomEditorCommandKind.SetEffectAmount:
                RoomPresentationChangeHintHub.MarkEffectAmount(session, command.Index);
                break;
            case RoomEditorCommandKind.AddEffect:
            case RoomEditorCommandKind.DeleteEffect:
                RoomPresentationChangeHintHub.MarkEffects(session);
                break;
            case RoomEditorCommandKind.SetTemplate:
            case RoomEditorCommandKind.SaveAsTemplate:
                RoomPresentationChangeHintHub.MarkFull(session);
                break;
        }
    }

    internal static void Clear()
    {
        while (queue.TryDequeue(out _)) { }
    }
}
