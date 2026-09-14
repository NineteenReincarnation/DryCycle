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
    internal long Revision = 1L;

    internal bool SetSelectedIndex(int value)
    {
        if (SelectedIndex == value) return false;
        SelectedIndex = value;
        Revision = Revision >= long.MaxValue ? 1L : Revision + 1L;
        return true;
    }
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
                if (index >= 0) Get(session)?.SetSelectedIndex(index);
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
    private static EditorSession observedSession;
    private static global::RoomSettings observedSettings;
    private static SoundPage observedPage;
    private static string[] observedFileNames;
    private static long observedRevision;
    private static long observedSelectionRevision;
    private static int observedSoundCount = -1;
    private static int observedSelectedIndex = int.MinValue;

    public static EditorSoundPresentationSnapshot Current => current;

    internal static void Publish(EditorSession session)
    {
        if (session?.ToolMode != EditorToolMode.Sound || session.RoomSettings?.ambientSounds == null ||
            session.Owner?.activePage is not SoundPage page)
        {
            Clear();
            return;
        }

        SoundEditorStateHub.SynchronizeFromLegacyNode(session, session.Owner.draggedNode ?? page.draggedObject);
        SoundEditorState state = SoundEditorStateHub.Get(session);
        int count = session.RoomSettings.ambientSounds.Count;
        if (state.SelectedIndex >= count) state.SetSelectedIndex(count - 1);
        if (state.SelectedIndex < -1) state.SetSelectedIndex(-1);

        // Legacy handle drags are model writes that bypass command history. Selection itself has a
        // separate state revision and therefore no longer dirties the Sound model channel.
        if (EditorRevisionHub.RequiresLiveWorkspaceRefresh(session) ||
            session.Owner.draggedNode != null || page.draggedObject != null)
            EditorRevisionHub.Mark(session, EditorRevisionKind.Sound);

        long revision = EditorRevisionHub.Get(session, EditorRevisionKind.Sound);
        long selectionRevision = state.Revision;
        string[] fileNames = page.fileNames ?? Array.Empty<string>();

        bool sameIdentity =
            ReferenceEquals(observedSession, session) &&
            ReferenceEquals(observedSettings, session.RoomSettings) &&
            ReferenceEquals(observedPage, page) &&
            current.Available;
        bool modelStable =
            sameIdentity &&
            observedRevision == revision &&
            observedSoundCount == count &&
            ReferenceEquals(observedFileNames, fileNames);

        if (modelStable &&
            observedSelectionRevision == selectionRevision &&
            observedSelectedIndex == state.SelectedIndex)
        {
            DevToolPerformanceMonitor.RecordPresentation(
                DevToolPresentationChannel.Sound,
                DevToolPresentationOutcome.CacheHit);
            return;
        }

        // Selection-only changes never touch resource discovery or model capture. Clone the retained
        // immutable array and replace only the old/new selected rows; every other row and all sample
        // metadata keep reference identity.
        if (modelStable)
        {
            PublishSelectionOnly(state.SelectedIndex, selectionRevision);
            DevToolPerformanceMonitor.RecordPresentation(
                DevToolPresentationChannel.Sound,
                DevToolPresentationOutcome.PartialRebuild);
            return;
        }

        // Resource discovery is independent from mutable room sound values. A volume/position edit
        // must not rescan the sample catalog when SoundPage.fileNames is still the same authoritative
        // resource set.
        bool resourceCatalogStable =
            sameIdentity &&
            ReferenceEquals(observedFileNames, fileNames) &&
            current.SampleEntries != null &&
            current.Samples != null;

        EditorSoundSampleSnapshot[] sampleEntries;
        string[] samples;
        if (resourceCatalogStable)
        {
            sampleEntries = current.SampleEntries;
            samples = current.Samples;
        }
        else
        {
            sampleEntries = SoundSampleCatalog.Refresh(page);
            samples = new string[sampleEntries.Length];
            for (int i = 0; i < sampleEntries.Length; i++)
                samples[i] = sampleEntries[i].Sample;
        }

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

        observedSession = session;
        observedSettings = session.RoomSettings;
        observedPage = page;
        observedFileNames = fileNames;
        observedRevision = revision;
        observedSelectionRevision = selectionRevision;
        observedSoundCount = count;
        observedSelectedIndex = state.SelectedIndex;

        DevToolPerformanceMonitor.RecordPresentation(
            DevToolPresentationChannel.Sound,
            resourceCatalogStable
                ? DevToolPresentationOutcome.PartialRebuild
                : DevToolPresentationOutcome.FullRebuild);
    }

    private static void PublishSelectionOnly(int selectedIndex, long selectionRevision)
    {
        EditorSoundSnapshot[] source = current.Sounds ?? Array.Empty<EditorSoundSnapshot>();
        EditorSoundSnapshot[] next = null;

        for (int i = 0; i < source.Length; i++)
        {
            EditorSoundSnapshot sound = source[i];
            bool selected = i == selectedIndex;
            if (sound.Selected == selected) continue;

            next ??= (EditorSoundSnapshot[])source.Clone();
            next[i] = CloneWithSelection(sound, selected);
        }

        current = new EditorSoundPresentationSnapshot
        {
            Available = current.Available,
            RoomKey = current.RoomKey,
            BackgroundDroneVolume = current.BackgroundDroneVolume,
            NoThreatDroneVolume = current.NoThreatDroneVolume,
            Samples = current.Samples,
            SampleEntries = current.SampleEntries,
            Sounds = next ?? source,
            SelectedIndex = selectedIndex
        };

        observedSelectionRevision = selectionRevision;
        observedSelectedIndex = selectedIndex;
    }

    private static EditorSoundSnapshot CloneWithSelection(EditorSoundSnapshot sound, bool selected) => new()
    {
        Index = sound.Index,
        Type = sound.Type,
        Sample = sound.Sample,
        Inherited = sound.Inherited,
        OverWrite = sound.OverWrite,
        Volume = sound.Volume,
        Pitch = sound.Pitch,
        Doppler = sound.Doppler,
        Taper = sound.Taper,
        X = sound.X,
        Y = sound.Y,
        Radius = sound.Radius,
        DirectionX = sound.DirectionX,
        DirectionY = sound.DirectionY,
        Selected = selected,
        ResourceAvailable = sound.ResourceAvailable,
        ResourceSourceKind = sound.ResourceSourceKind,
        ResourceSourceName = sound.ResourceSourceName,
        ResourceSourceId = sound.ResourceSourceId
    };

    internal static void Clear()
    {
        current = EditorSoundPresentationSnapshot.Empty;
        observedSession = null;
        observedSettings = null;
        observedPage = null;
        observedFileNames = null;
        observedRevision = 0L;
        observedSelectionRevision = 0L;
        observedSoundCount = -1;
        observedSelectedIndex = int.MinValue;
    }
}

public enum SoundEditorCommandKind
{
    Select = 0,
    Create = 1,
    Delete = 2,
    SetRoomValue = 3,
    SetSoundValue = 4,
    ReloadGroups = 5,
    SetGroupDirectory = 6,
    ResetGroupDirectory = 7,
    CreateGroup = 8,
    DeleteGroup = 9,
    AddSoundToGroup = 10,
    ApplyGroup = 11,
    CreateFromLibrary = 12,
    AddSoundsToGroup = 13,
    CreateGroupFromSounds = 14
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
        Indices = Array.Empty<int>();
    }

    public SoundEditorCommand(
        SoundEditorCommandKind kind,
        int[] indices,
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
        long historyBeforeBatch = session?.History.Revision ?? 0L;
        bool nonHistorySceneDirty = false;

        while (queue.TryDequeue(out SoundEditorCommand command))
        {
            try
            {
                long historyBeforeCommand = session?.History.Revision ?? 0L;
                SoundEditorState state = SoundEditorStateHub.Get(session);
                int selectedBefore = state?.SelectedIndex ?? -1;
                int soundCountBefore = session?.RoomSettings?.ambientSounds?.Count ?? 0;
                bool changed = false;
                bool sceneCommand = false;

                switch (command.Kind)
                {
                    case SoundEditorCommandKind.Select:
                        SoundEditorActions.Select(session, command.Index);
                        changed = (state?.SelectedIndex ?? -1) != selectedBefore;
                        break;
                    case SoundEditorCommandKind.Create:
                        changed = SoundEditorActions.Create(session, command.Text, command.SecondaryIndex);
                        sceneCommand = true;
                        break;
                    case SoundEditorCommandKind.CreateFromLibrary:
                        changed = SoundEditorActions.CreateFromLibrary(
                            session,
                            command.Text,
                            command.SecondaryIndex,
                            command.Key,
                            command.Index);
                        sceneCommand = command.Index != 1;
                        break;
                    case SoundEditorCommandKind.Delete:
                        changed = SoundEditorActions.Delete(session, command.Index);
                        sceneCommand = true;
                        break;
                    case SoundEditorCommandKind.SetRoomValue:
                        changed = SoundEditorActions.SetRoomValue(session, command.Key, command.Value);
                        sceneCommand = true;
                        break;
                    case SoundEditorCommandKind.SetSoundValue:
                        changed = SoundEditorActions.SetSoundValue(session, command.Index, command.Key, command.Value);
                        sceneCommand = true;
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
                        changed = SoundEditorActions.ApplyGroup(session, command.Key);
                        sceneCommand = true;
                        break;
                }

                if (!sceneCommand)
                    continue;

                int soundCountAfter = session?.RoomSettings?.ambientSounds?.Count ?? 0;
                bool observableModelChange = changed || soundCountAfter != soundCountBefore;

                // Pure selection is carried by SoundEditorState.Revision and never dirties the model.
                // Compatibility-success paths that change room sound data without creating history
                // still retain one direct workspace invalidation as a safety fallback.
                if (observableModelChange && (session?.History.Revision ?? 0L) == historyBeforeCommand)
                    nonHistorySceneDirty = true;
            }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("DevTool sound command failed: " + error.Message);
            }
        }

        if (nonHistorySceneDirty && (session?.History.Revision ?? 0L) == historyBeforeBatch)
            EditorRevisionHub.Mark(session, EditorRevisionKind.Sound);
    }

    internal static void Clear()
    {
        while (queue.TryDequeue(out _)) { }
    }
}
