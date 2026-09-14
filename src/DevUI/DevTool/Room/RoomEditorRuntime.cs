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

    internal static void Publish(EditorSession session)
    {
        if (session?.ToolMode != EditorToolMode.Room || session.RoomSettings == null)
        {
            Clear();
            return;
        }

        if (EditorRevisionHub.RequiresLiveWorkspaceRefresh(session))
            EditorRevisionHub.Mark(session, EditorRevisionKind.Room);

        long revision = EditorRevisionHub.Get(session, EditorRevisionKind.Room);
        if (ReferenceEquals(observedSession, session) &&
            ReferenceEquals(observedSettings, session.RoomSettings) &&
            observedRevision == revision &&
            current.Available)
            return;

        current = RoomSettingsPresentation.Capture(session);
        observedSession = session;
        observedSettings = session.RoomSettings;
        observedRevision = revision;
    }

    internal static void Clear()
    {
        current = EditorRoomSettingsSnapshot.Empty;
        observedSession = null;
        observedSettings = null;
        observedRevision = 0L;
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
        while (queue.TryDequeue(out RoomEditorCommand command))
        {
            try
            {
                switch (command.Kind)
                {
                    case RoomEditorCommandKind.SetSetting:
                        RoomEditorActions.SetRoomSetting(session, command.Key, command.Value);
                        break;
                    case RoomEditorCommandKind.ResetSetting:
                        RoomEditorActions.ResetRoomSetting(session, command.Key);
                        break;
                    case RoomEditorCommandKind.SetPaletteFade:
                        RoomEditorActions.SetPaletteFade(session, command.Index, command.Value.X);
                        break;
                    case RoomEditorCommandKind.SetTerrainPaletteFade:
                        RoomEditorActions.SetTerrainPaletteFade(session, command.Index, command.Value.X);
                        break;
                    case RoomEditorCommandKind.SetTemplate:
                        if (RoomEditorActions.SetRoomTemplate(session, command.Key))
                            RoomEffectLiveCompatibility.Reconcile(session);
                        break;
                    case RoomEditorCommandKind.SaveAsTemplate:
                        RoomEditorActions.SaveRoomAsTemplate(session, command.Key);
                        break;
                    case RoomEditorCommandKind.AddEffect:
                        // A browser hover may currently own a temporary RoomEffect/controller.
                        // Tear that transaction down before the persistent edit so its rollback can
                        // never remove or overwrite the newly committed runtime state.
                        EffectPreviewRuntime.EndForPersistentOperation("add room effect");
                        if (RoomEditorActions.AddRoomEffect(session, command.Key))
                            RoomEffectLiveCompatibility.Reconcile(session);
                        break;
                    case RoomEditorCommandKind.DeleteEffect:
                        EffectPreviewRuntime.EndForPersistentOperation("delete room effect");
                        if (RoomEditorActions.DeleteRoomEffect(session, command.Index))
                            RoomEffectLiveCompatibility.Reconcile(session);
                        break;
                    case RoomEditorCommandKind.SetEffectAmount:
                        EffectPreviewRuntime.EndForPersistentOperation("change room effect");
                        if (RoomEditorActions.SetRoomEffectAmount(session, command.Index, command.SecondaryIndex, command.Value.X))
                            RoomEffectLiveCompatibility.Reconcile(session);
                        break;
                }

                // Room commands are sparse user actions. Invalidating after a processed command is
                // substantially cheaper and more deterministic than rebuilding the entire settings
                // snapshot every frame, while harmless no-op commands merely cause one extra build.
                EditorRevisionHub.Mark(session, EditorRevisionKind.Room);
                EditorRevisionHub.Mark(session, EditorRevisionKind.Shell);
            }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("DevTool room command failed: " + error.Message);
            }
        }
    }

    internal static void Clear()
    {
        while (queue.TryDequeue(out _)) { }
    }
}
