using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using DevInterface;
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
    public bool ResourceAvailable { get; init; }
    public EditorSoundSourceKind ResourceSourceKind { get; init; }
    public string ResourceSourceName { get; init; } = string.Empty;
    public string ResourceSourceId { get; init; } = string.Empty;
}

public sealed class EditorSoundPresentationSnapshot
{
    public static readonly EditorSoundPresentationSnapshot Empty = new();

    public bool Available { get; init; }
    public string RoomKey { get; init; } = string.Empty;
    public float BackgroundDroneVolume { get; init; }
    public float NoThreatDroneVolume { get; init; }
    public string[] Samples { get; init; } = Array.Empty<string>();
    public EditorSoundSampleSnapshot[] SampleEntries { get; init; } = Array.Empty<EditorSoundSampleSnapshot>();
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

    internal static void SynchronizeFromLegacyNode(EditorSession session, DevUINode node)
    {
        if (session?.ToolMode != EditorToolMode.Sound || session.RoomSettings?.ambientSounds == null || node == null)
            return;

        DevUINode current = node;
        while (current != null)
        {
            if (current is AmbientSoundPanel panel && panel.sound != null)
            {
                int index = session.RoomSettings.ambientSounds.IndexOf(panel.sound);
                if (index >= 0) Get(session).SelectedIndex = index;
                return;
            }
            current = current.parentNode;
        }
    }

    internal static void Reset() => states = new ConditionalWeakTable<EditorSession, SoundEditorState>();
}

public static class SoundEditorPresentationHub
{
    private static volatile EditorSoundPresentationSnapshot current = EditorSoundPresentationSnapshot.Empty;
    public static EditorSoundPresentationSnapshot Current => current;

    internal static void Publish(EditorSession session)
    {
        if (session?.ToolMode != EditorToolMode.Sound || session.RoomSettings?.ambientSounds == null ||
            session.Owner?.activePage is not SoundPage page)
        {
            current = EditorSoundPresentationSnapshot.Empty;
            return;
        }

        SoundEditorStateHub.SynchronizeFromLegacyNode(session, session.Owner.draggedNode ?? page.draggedObject);
        SoundEditorState state = SoundEditorStateHub.Get(session);
        int count = session.RoomSettings.ambientSounds.Count;
        if (state.SelectedIndex >= count) state.SelectedIndex = count - 1;
        if (state.SelectedIndex < -1) state.SelectedIndex = -1;

        // Resource discovery belongs to the Rain World / DevUI thread. RWImGui receives only the
        // immutable presentation snapshots below and never touches AssetManager or the mutable
        // source catalog from its render thread.
        EditorSoundSampleSnapshot[] sampleEntries = SoundSampleCatalog.Refresh(page);
        SoundGroupLibrary.EnsureLoaded();

        EditorSoundSnapshot[] sounds = new EditorSoundSnapshot[count];
        for (int i = 0; i < count; i++)
        {
            AmbientSound sound = session.RoomSettings.ambientSounds[i];
            float doppler = sound is DopplerAffectedSound dopplerSound ? dopplerSound.dopplerFac : 0f;
            float taper = sound is SpotSound spotForTaper ? spotForTaper.taper : 0f;
            Vector2 pos = sound is SpotSound spot ? spot.pos : Vector2.zero;
            float radius = sound is SpotSound spotForRadius ? spotForRadius.rad : 0f;
            Vector2 direction = sound is DirectionalSound directional ? directional.direction : Vector2.zero;
            EditorSoundSampleSnapshot resource = SoundSampleCatalog.Resolve(sound?.sample ?? string.Empty);

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
                Selected = i == state.SelectedIndex,
                ResourceAvailable = resource.Available,
                ResourceSourceKind = resource.SourceKind,
                ResourceSourceName = resource.SourceName,
                ResourceSourceId = resource.SourceId
            };
        }

        string[] samples = new string[sampleEntries.Length];
        for (int i = 0; i < sampleEntries.Length; i++) samples[i] = sampleEntries[i].Sample;

        current = new EditorSoundPresentationSnapshot
        {
            Available = true,
            RoomKey = session.Room?.abstractRoom?.name ?? string.Empty,
            BackgroundDroneVolume = session.RoomSettings.BkgDroneVolume,
            NoThreatDroneVolume = session.RoomSettings.BkgDroneNoThreatVolume,
            Samples = samples,
            SampleEntries = sampleEntries,
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
    CreateFromLibrary,
    Delete,
    SetRoomValue,
    SetSoundValue,
    ReloadGroups,
    SetGroupDirectory,
    ResetGroupDirectory,
    CreateGroup,
    DeleteGroup,
    AddSoundToGroup,
    AddSoundsToGroup,
    CreateGroupFromSounds,
    ApplyGroup
}

public readonly struct SoundEditorCommand
{
    public SoundEditorCommand(
        SoundEditorCommandKind kind,
        int index = -1,
        string key = null,
        string text = null,
        int secondaryIndex = -1,
        EditorPropertyValue value = default,
        int[] indices = null)
    {
        Kind = kind;
        Index = index;
        Key = key;
        Text = text;
        SecondaryIndex = secondaryIndex;
        Value = value;
        Indices = indices ?? Array.Empty<int>();
    }

    public SoundEditorCommandKind Kind { get; }
    public int Index { get; }
    public string Key { get; }
    public string Text { get; }
    public int SecondaryIndex { get; }
    public EditorPropertyValue Value { get; }
    public int[] Indices { get; }
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
                    case SoundEditorCommandKind.CreateFromLibrary:
                        SoundEditorActions.CreateFromLibrary(
                            session,
                            command.Text,
                            command.SecondaryIndex,
                            command.Key,
                            command.Index);
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
                    case SoundEditorCommandKind.ReloadGroups:
                        SoundGroupLibrary.Reload();
                        break;
                    case SoundEditorCommandKind.SetGroupDirectory:
                        SoundGroupLibrary.SetLocalDirectory(command.Text);
                        break;
                    case SoundEditorCommandKind.ResetGroupDirectory:
                        SoundGroupLibrary.ResetLocalDirectory();
                        break;
                    case SoundEditorCommandKind.CreateGroup:
                        SoundGroupLibrary.CreateLocalGroup(command.Key, command.Text);
                        break;
                    case SoundEditorCommandKind.DeleteGroup:
                        SoundGroupLibrary.DeleteLocalGroup(command.Key);
                        break;
                    case SoundEditorCommandKind.AddSoundToGroup:
                        SoundEditorActions.AddSoundToGroup(session, command.Index, command.Key);
                        break;
                    case SoundEditorCommandKind.AddSoundsToGroup:
                        SoundEditorActions.AddSoundsToGroup(session, command.Indices, command.Key);
                        break;
                    case SoundEditorCommandKind.CreateGroupFromSounds:
                        SoundEditorActions.CreateGroupFromSounds(
                            session,
                            command.Indices,
                            command.Key,
                            command.Text);
                        break;
                    case SoundEditorCommandKind.ApplyGroup:
                        SoundEditorActions.ApplyGroup(session, command.Key);
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
