using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Objects;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Sound;

public sealed class EditorSoundSnapshot
{
    public int Index { get; init; }
    public string Type { get; init; } = string.Empty;
    public string Sample { get; init; } = string.Empty;
    public bool Inherited { get; init; }
    public bool OverWrite { get; init; }
    public float Volume { get; init; }
    public float Pitch { get; init; }
    public float Doppler { get; init; }
    public float Taper { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float Radius { get; init; }
    public float DirectionX { get; init; }
    public float DirectionY { get; init; }
    public bool Selected { get; init; }
}

public sealed class EditorSoundPresentationSnapshot
{
    public static readonly EditorSoundPresentationSnapshot Empty = new();

    public bool Available { get; init; }
    public float BackgroundDroneVolume { get; init; }
    public float NoThreatDroneVolume { get; init; }
    public string[] Samples { get; init; } = Array.Empty<string>();
    public EditorSoundSnapshot[] Sounds { get; init; } = Array.Empty<EditorSoundSnapshot>();
    public int SelectedIndex { get; init; } = -1;
}

internal sealed class SoundEditorState
{
    internal int SelectedIndex = -1;
}

internal static class SoundEditorStateHub
{
    private static ConditionalWeakTable<EditorSession, SoundEditorState> states = new();

    internal static SoundEditorState Get(EditorSession session) =>
        session == null ? null : states.GetValue(session, _ => new SoundEditorState());

    internal static void Reset() => states = new ConditionalWeakTable<EditorSession, SoundEditorState>();
}

public static class SoundEditorPresentationHub
{
    private static volatile EditorSoundPresentationSnapshot current = EditorSoundPresentationSnapshot.Empty;
    public static EditorSoundPresentationSnapshot Current => current;

    internal static void Publish(EditorSession session)
    {
        if (session?.ToolMode != EditorToolMode.Sound || session.RoomSettings?.ambientSounds == null ||
            session.Owner?.activePage is not DevInterface.SoundPage page)
        {
            current = EditorSoundPresentationSnapshot.Empty;
            return;
        }

        SoundEditorState state = SoundEditorStateHub.Get(session);
        int count = session.RoomSettings.ambientSounds.Count;
        if (state.SelectedIndex >= count) state.SelectedIndex = count - 1;
        if (state.SelectedIndex < -1) state.SelectedIndex = -1;

        EditorSoundSnapshot[] sounds = new EditorSoundSnapshot[count];
        for (int i = 0; i < count; i++)
        {
            AmbientSound sound = session.RoomSettings.ambientSounds[i];
            float doppler = sound is DopplerAffectedSound dopplerSound ? dopplerSound.dopplerFac : 0f;
            float taper = sound is SpotSound spotForTaper ? spotForTaper.taper : 0f;
            Vector2 pos = sound is SpotSound spot ? spot.pos : Vector2.zero;
            float radius = sound is SpotSound spotForRadius ? spotForRadius.rad : 0f;
            Vector2 direction = sound is DirectionalSound directional ? directional.direction : Vector2.zero;

            sounds[i] = new EditorSoundSnapshot
            {
                Index = i,
                Type = sound?.type?.value ?? string.Empty,
                Sample = sound?.sample ?? string.Empty,
                Inherited = sound?.inherited ?? false,
                OverWrite = sound?.overWrite ?? false,
                Volume = sound?.volume ?? 0f,
                Pitch = sound?.pitch ?? 1f,
                Doppler = doppler,
                Taper = taper,
                X = pos.x,
                Y = pos.y,
                Radius = radius,
                DirectionX = direction.x,
                DirectionY = direction.y,
                Selected = i == state.SelectedIndex
            };
        }

        string[] samples = page.fileNames == null ? Array.Empty<string>() : (string[])page.fileNames.Clone();
        Array.Sort(samples, StringComparer.OrdinalIgnoreCase);

        current = new EditorSoundPresentationSnapshot
        {
            Available = true,
            BackgroundDroneVolume = session.RoomSettings.BkgDroneVolume,
            NoThreatDroneVolume = session.RoomSettings.BkgDroneNoThreatVolume,
            Samples = samples,
            Sounds = sounds,
            SelectedIndex = state.SelectedIndex
        };
    }

    internal static void Clear() => current = EditorSoundPresentationSnapshot.Empty;
}

public enum SoundEditorCommandKind
{
    Select,
    Create,
    Delete,
    SetRoomValue,
    SetSoundValue
}

public readonly struct SoundEditorCommand
{
    public SoundEditorCommand(
        SoundEditorCommandKind kind,
        int index = -1,
        string key = null,
        string text = null,
        int secondaryIndex = -1,
        EditorPropertyValue value = default)
    {
        Kind = kind;
        Index = index;
        Key = key;
        Text = text;
        SecondaryIndex = secondaryIndex;
        Value = value;
    }

    public SoundEditorCommandKind Kind { get; }
    public int Index { get; }
    public string Key { get; }
    public string Text { get; }
    public int SecondaryIndex { get; }
    public EditorPropertyValue Value { get; }
}

public static class SoundEditorCommandQueue
{
    private static readonly ConcurrentQueue<SoundEditorCommand> queue = new();
    public static void Enqueue(SoundEditorCommand command) => queue.Enqueue(command);

    internal static void Process(EditorSession session)
    {
        while (queue.TryDequeue(out SoundEditorCommand command))
        {
            try
            {
                switch (command.Kind)
                {
                    case SoundEditorCommandKind.Select:
                        SoundEditorActions.Select(session, command.Index);
                        break;
                    case SoundEditorCommandKind.Create:
                        SoundEditorActions.Create(session, command.Text, command.SecondaryIndex);
                        break;
                    case SoundEditorCommandKind.Delete:
                        SoundEditorActions.Delete(session, command.Index);
                        break;
                    case SoundEditorCommandKind.SetRoomValue:
                        SoundEditorActions.SetRoomValue(session, command.Key, command.Value);
                        break;
                    case SoundEditorCommandKind.SetSoundValue:
                        SoundEditorActions.SetSoundValue(session, command.Index, command.Key, command.Value);
                        break;
                }
            }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("DevTool sound command failed: " + error.Message);
            }
        }
    }

    internal static void Clear()
    {
        while (queue.TryDequeue(out _)) { }
    }
}
