using System;
using System.Collections.Concurrent;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Objects;

namespace DryCycle.DevUI.DevTool.Room;

public static class RoomEditorPresentationHub
{
    private static volatile EditorRoomSettingsSnapshot current = EditorRoomSettingsSnapshot.Empty;

    public static EditorRoomSettingsSnapshot Current => current;

    internal static void Publish(EditorSession session)
    {
        current = session?.ToolMode == EditorToolMode.Room
            ? RoomSettingsPresentation.Capture(session)
            : EditorRoomSettingsSnapshot.Empty;
    }

    internal static void Clear() => current = EditorRoomSettingsSnapshot.Empty;
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
                        RoomEditorActions.SetRoomTemplate(session, command.Key);
                        break;
                    case RoomEditorCommandKind.SaveAsTemplate:
                        RoomEditorActions.SaveRoomAsTemplate(session, command.Key);
                        break;
                    case RoomEditorCommandKind.AddEffect:
                        RoomEditorActions.AddRoomEffect(session, command.Key);
                        break;
                    case RoomEditorCommandKind.DeleteEffect:
                        RoomEditorActions.DeleteRoomEffect(session, command.Index);
                        break;
                    case RoomEditorCommandKind.SetEffectAmount:
                        RoomEditorActions.SetRoomEffectAmount(session, command.Index, command.SecondaryIndex, command.Value.X);
                        break;
                }
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
