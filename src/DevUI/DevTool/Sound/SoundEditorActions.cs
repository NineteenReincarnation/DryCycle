using System;
using System.Collections.Generic;
using DevInterface;
using DryCycle.DevUI.DevTool.Compatibility;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Factories;
using DryCycle.DevUI.DevTool.History;
using DryCycle.DevUI.DevTool.Objects;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Sound;

public static class SoundEditorKeys
{
    public const string BackgroundDroneVolume = "backgroundDroneVolume";
    public const string NoThreatDroneVolume = "noThreatDroneVolume";
    public const string Volume = "volume";
    public const string Pitch = "pitch";
    public const string Doppler = "doppler";
    public const string Taper = "taper";
    public const string Position = "position";
    public const string Radius = "radius";
    public const string Direction = "direction";
}

internal static class SoundEditorActions
{
    internal static void Select(EditorSession session, int index)
    {
        SoundEditorState state = SoundEditorStateHub.Get(session);
        if (state == null) return;
        int count = session?.RoomSettings?.ambientSounds?.Count ?? 0;
        state.SetSelectedIndex(index >= 0 && index < count ? index : -1);
    }

    internal static bool Create(EditorSession session, string sample, int soundType)
    {
        if (session?.RoomSettings?.ambientSounds == null || string.IsNullOrEmpty(sample)) return false;
        if (session.ToolMode != EditorToolMode.Sound) session.SetToolMode(EditorToolMode.Sound);
        if (soundType < 0 || soundType > 2) return false;

        RoomSettings settings = session.RoomSettings;
        IEditorStateSnapshot before = AmbientSoundCollectionStateSnapshot.Capture(settings);
        if (before == null) return false;

        if (!NativeSoundFactory.TryCreate(
                session,
                sample,
                soundType,
                out AmbientSound created,
                out int selectedIndex))
        {
            if (selectedIndex >= 0)
                SoundEditorStateHub.Get(session)?.SetSelectedIndex(selectedIndex);
            return false;
        }

        IEditorStateSnapshot after = AmbientSoundCollectionStateSnapshot.Capture(settings);
        if (SnapshotHistoryEntry.TryCreate(
                "Create sound " + sample,
                before,
                after,
                out SnapshotHistoryEntry entry))
            session.History.Push(entry);

        SynchronizeNativeMutation(session, collectionChanged: true);
        SoundEditorStateHub.Get(session)?.SetSelectedIndex(selectedIndex);
        return created != null;
    }

    internal static bool CreateFromLibrary(
        EditorSession session,
        string sample,
        int soundType,
        string groupId,
        int destination)
    {
        if (destination < 0 || destination > 2 || string.IsNullOrWhiteSpace(sample)) return false;

        bool toScene = destination != 1;
        bool toGroup = destination != 0;
        if (toGroup && !TryGetWritableGroup(groupId, out _)) return false;

        if (!toScene)
        {
            SoundGroupSoundDefinition definition = CreateDefaultDefinition(sample, soundType);
            return definition != null && SoundGroupLibrary.AddSoundToLocalGroup(groupId, definition);
        }

        bool created = Create(session, sample, soundType);
        SoundEditorState state = SoundEditorStateHub.Get(session);
        if (!created && (state == null || state.SelectedIndex < 0)) return false;
        if (!toGroup) return created;

        return state != null && state.SelectedIndex >= 0 && AddSoundToGroup(session, state.SelectedIndex, groupId);
    }

    internal static bool Delete(EditorSession session, int index)
    {
        if (!TryGetSound(session, index, out AmbientSound sound) || sound.inherited) return false;

        RoomSettings settings = session.RoomSettings;
        IEditorStateSnapshot before = SingleAmbientSoundStateSnapshot.Capture(settings, sound);
        if (before == null) return false;

        settings.ambientSounds.RemoveAt(index);
        IEditorStateSnapshot after = SingleAmbientSoundStateSnapshot.Capture(settings, sound);
        if (!SnapshotHistoryEntry.TryCreate(
                "Delete sound " + (sound.sample ?? string.Empty),
                before,
                after,
                out SnapshotHistoryEntry entry))
            return false;

        SynchronizeNativeMutation(session, collectionChanged: true);
        session.History.Push(entry);

        SoundEditorState state = SoundEditorStateHub.Get(session);
        if (state != null)
        {
            int count = settings.ambientSounds.Count;
            state.SetSelectedIndex(count == 0 ? -1 : Math.Min(index, count - 1));
        }
        return true;
    }

    internal static bool SetRoomValue(EditorSession session, string key, EditorPropertyValue value)
    {
        if (session?.RoomSettings == null || value.Kind != EditorPropertyKind.Float) return false;

        return MutateRoomVolumes(session, "Change sound room setting", settings =>
        {
            switch (key)
            {
                case SoundEditorKeys.BackgroundDroneVolume:
                {
                    float next = Mathf.Clamp01(value.X);
                    if (Mathf.Approximately(settings.BkgDroneVolume, next)) return false;
                    settings.BkgDroneVolume = next;
                    return true;
                }
                case SoundEditorKeys.NoThreatDroneVolume:
                {
                    float next = Mathf.Clamp01(value.X);
                    if (Mathf.Approximately(settings.BkgDroneNoThreatVolume, next)) return false;
                    settings.BkgDroneNoThreatVolume = next;
                    return true;
                }
                default:
                    return false;
            }
        });
    }

    internal static bool SetSoundValue(
        EditorSession session,
        int index,
        string key,
        EditorPropertyValue value)
    {
        if (!TryGetSound(session, index, out AmbientSound sound) || sound.inherited || string.IsNullOrEmpty(key))
            return false;

        return MutateSound(session, sound, "Change " + key + " on " + (sound.sample ?? "sound"), () =>
        {
            switch (key)
            {
                case SoundEditorKeys.Volume:
                {
                    if (value.Kind != EditorPropertyKind.Float) return false;
                    float next = Mathf.Clamp01(value.X);
                    if (Mathf.Approximately(sound.volume, next)) return false;
                    sound.volume = next;
                    return true;
                }
                case SoundEditorKeys.Pitch:
                {
                    if (value.Kind != EditorPropertyKind.Float) return false;
                    float next = Mathf.Clamp(value.X, 0.1f, 1.9f);
                    if (Mathf.Approximately(sound.pitch, next)) return false;
                    sound.pitch = next;
                    return true;
                }
                case SoundEditorKeys.Doppler:
                {
                    if (value.Kind != EditorPropertyKind.Float || sound is not DopplerAffectedSound doppler) return false;
                    float next = Mathf.Clamp01(value.X);
                    if (Mathf.Approximately(doppler.dopplerFac, next)) return false;
                    doppler.dopplerFac = next;
                    return true;
                }
                case SoundEditorKeys.Taper:
                {
                    if (value.Kind != EditorPropertyKind.Float || sound is not SpotSound spotTaper) return false;
                    float next = Mathf.Clamp01(value.X);
                    if (Mathf.Approximately(spotTaper.taper, next)) return false;
                    spotTaper.taper = next;
                    return true;
                }
                case SoundEditorKeys.Position:
                {
                    if (value.Kind != EditorPropertyKind.Vector2 || sound is not SpotSound spotPosition) return false;
                    Vector2 next = new(value.X, value.Y);
                    if ((spotPosition.pos - next).sqrMagnitude <= 0.000001f) return false;
                    spotPosition.pos = next;
                    return true;
                }
                case SoundEditorKeys.Radius:
                {
                    if (value.Kind != EditorPropertyKind.Float || sound is not SpotSound spotRadius) return false;
                    float radius = Mathf.Max(0f, value.X);
                    Vector2 direction = spotRadius.radHandlePosition.sqrMagnitude > 0.0001f
                        ? spotRadius.radHandlePosition.normalized
                        : Vector2.up;
                    Vector2 nextHandle = direction * radius;
                    if (Mathf.Approximately(spotRadius.rad, radius) &&
                        (spotRadius.radHandlePosition - nextHandle).sqrMagnitude <= 0.000001f)
                        return false;
                    spotRadius.rad = radius;
                    spotRadius.radHandlePosition = nextHandle;
                    return true;
                }
                case SoundEditorKeys.Direction:
                {
                    if (value.Kind != EditorPropertyKind.Vector2 || sound is not DirectionalSound directional) return false;
                    Vector2 raw = new(value.X, value.Y);
                    Vector2 next = raw.sqrMagnitude > 0.0001f ? raw.normalized : Vector2.down;
                    if ((directional.direction - next).sqrMagnitude <= 0.000001f) return false;
                    directional.direction = next;
                    return true;
                }
                default:
                    return false;
            }
        });
    }

    internal static bool AddSoundToGroup(EditorSession session, int index, string groupId)
    {
        if (!TryGetSound(session, index, out AmbientSound sound) ||
            !TryGetWritableGroup(groupId, out _))
            return false;

        SoundGroupSoundDefinition definition = CaptureDefinition(session, sound);
        return definition != null && SoundGroupLibrary.AddSoundToLocalGroup(groupId, definition);
    }

    internal static bool AddSoundsToGroup(EditorSession session, int[] indices, string groupId)
    {
        if (indices == null || indices.Length == 0 || !TryGetWritableGroup(groupId, out _))
            return false;

        HashSet<int> unique = new();
        List<SoundGroupSoundDefinition> definitions = new(indices.Length);
        for (int i = 0; i < indices.Length; i++)
        {
            int index = indices[i];
            if (!unique.Add(index) || !TryGetSound(session, index, out AmbientSound sound)) continue;
            SoundGroupSoundDefinition definition = CaptureDefinition(session, sound);
            if (definition != null) definitions.Add(definition);
        }
        if (definitions.Count == 0) return false;

        bool changed = false;
        for (int i = 0; i < definitions.Count; i++)
            changed |= SoundGroupLibrary.AddSoundToLocalGroup(groupId, definitions[i]);
        return changed;
    }

    internal static bool CreateGroupFromSounds(
        EditorSession session,
        int[] indices,
        string groupId,
        string groupName)
    {
        if (indices == null || indices.Length == 0 ||
            string.IsNullOrWhiteSpace(groupId) || string.IsNullOrWhiteSpace(groupName))
            return false;

        if (!SoundGroupLibrary.CreateLocalGroup(groupId, groupName)) return false;
        if (AddSoundsToGroup(session, indices, groupId)) return true;

        SoundGroupLibrary.DeleteLocalGroup(groupId);
        return false;
    }

    internal static bool ApplyGroup(EditorSession session, string groupId)
    {
        if (session?.RoomSettings?.ambientSounds == null || session.Room == null ||
            string.IsNullOrWhiteSpace(groupId) ||
            !SoundGroupLibrary.TryGetGroup(groupId, out SoundGroupDefinition group))
            return false;

        RoomSettings settings = session.RoomSettings;
        IEditorStateSnapshot before = AmbientSoundCollectionStateSnapshot.Capture(settings);
        if (before == null) return false;

        int changed = 0;
        int lastIndex = -1;
        for (int i = 0; i < group.Sounds.Count; i++)
        {
            SoundGroupSoundDefinition definition = group.Sounds[i];
            if (!SoundSampleCatalog.Resolve(definition.Sample).Available)
                continue;

            AmbientSound sound = CreateFromDefinition(session.Room, definition, i);
            if (sound == null) continue;

            if (sound.type?.Index != AmbientSound.Type.Spot.Index)
            {
                bool overrideExisting = false;
                for (int existingIndex = settings.ambientSounds.Count - 1; existingIndex >= 0; existingIndex--)
                {
                    AmbientSound existing = settings.ambientSounds[existingIndex];
                    if (existing?.type == sound.type &&
                        string.Equals(existing.sample, sound.sample, StringComparison.Ordinal))
                    {
                        settings.ambientSounds.RemoveAt(existingIndex);
                        overrideExisting = true;
                    }
                }
                sound.overWrite = overrideExisting;
            }

            settings.ambientSounds.Add(sound);
            lastIndex = settings.ambientSounds.Count - 1;
            changed++;
        }

        if (changed == 0) return false;

        IEditorStateSnapshot after = AmbientSoundCollectionStateSnapshot.Capture(settings);
        if (!SnapshotHistoryEntry.TryCreate(
                "Apply sound group " + (group.Name ?? group.Id),
                before,
                after,
                out SnapshotHistoryEntry entry))
            return false;

        SynchronizeNativeMutation(session, collectionChanged: true);
        session.History.Push(entry);
        SoundEditorStateHub.Get(session)?.SetSelectedIndex(lastIndex);
        return true;
    }

    private static bool TryGetWritableGroup(string groupId, out SoundGroupDefinition group)
    {
        group = null;
        return !string.IsNullOrWhiteSpace(groupId) &&
               SoundGroupLibrary.TryGetGroup(groupId, out group) &&
               group.IsLocal;
    }

    private static SoundGroupSoundDefinition CaptureDefinition(EditorSession session, AmbientSound sound)
    {
        if (sound == null || string.IsNullOrWhiteSpace(sound.sample)) return null;

        global::Room room = session?.Room;
        SoundGroupSoundDefinition definition = new()
        {
            Type = sound.type?.value ?? "Omnidirectional",
            Sample = sound.sample ?? string.Empty,
            Volume = sound.volume,
            Pitch = sound.pitch
        };

        if (sound is DopplerAffectedSound doppler)
            definition.Doppler = doppler.dopplerFac;

        if (sound is DirectionalSound directional)
        {
            definition.DirectionX = directional.direction.x;
            definition.DirectionY = directional.direction.y;
        }
        else if (sound is SpotSound spot)
        {
            definition.X = room != null && room.PixelWidth > 0.01f
                ? Mathf.Clamp01(spot.pos.x / room.PixelWidth)
                : 0.5f;
            definition.Y = room != null && room.PixelHeight > 0.01f
                ? Mathf.Clamp01(spot.pos.y / room.PixelHeight)
                : 0.5f;
            definition.Radius = spot.rad;
            definition.Taper = spot.taper;
        }

        return definition;
    }

    private static SoundGroupSoundDefinition CreateDefaultDefinition(string sample, int soundType)
    {
        string type = soundType switch
        {
            0 => "Omnidirectional",
            1 => "Directional",
            2 => "Spot",
            _ => null
        };
        if (type == null) return null;

        return new SoundGroupSoundDefinition
        {
            Type = type,
            Sample = sample ?? string.Empty
        };
    }

    private static AmbientSound CreateFromDefinition(
        global::Room room,
        SoundGroupSoundDefinition definition,
        int ordinal)
    {
        AmbientSound sound;
        if (string.Equals(definition.Type, "Directional", StringComparison.Ordinal))
        {
            DirectionalSound directional = new(definition.Sample, inherited: false)
            {
                direction = NormalizeDirection(definition.DirectionX, definition.DirectionY)
            };
            directional.dopplerFac = Mathf.Clamp01(definition.Doppler);
            sound = directional;
        }
        else if (string.Equals(definition.Type, "Spot", StringComparison.Ordinal))
        {
            SpotSound spot = new(definition.Sample, inherited: false)
            {
                pos = new Vector2(
                    Mathf.Clamp01(definition.X) * room.PixelWidth,
                    Mathf.Clamp01(definition.Y) * room.PixelHeight),
                rad = Mathf.Max(0f, definition.Radius),
                taper = Mathf.Clamp01(definition.Taper)
            };
            spot.dopplerFac = Mathf.Clamp01(definition.Doppler);
            spot.radHandlePosition = Vector2.right * spot.rad;
            sound = spot;
        }
        else
        {
            sound = new OmniDirectionalSound(definition.Sample, inherited: false);
        }

        sound.volume = Mathf.Clamp01(definition.Volume);
        sound.pitch = Mathf.Clamp(definition.Pitch, 0.1f, 1.9f);
        sound.panelPosition = new Vector2(52f + (ordinal % 8) * 14f, 52f + (ordinal / 8) * 14f);
        return sound;
    }

    private static Vector2 NormalizeDirection(float x, float y)
    {
        Vector2 value = new(x, y);
        return value.sqrMagnitude > 0.0001f ? value.normalized : Vector2.down;
    }

    private static bool MutateSound(
        EditorSession session,
        AmbientSound target,
        string label,
        Func<bool> mutation,
        bool syncLegacyPresentation = true)
    {
        RoomSettings settings = session?.RoomSettings;
        if (settings == null || target == null || mutation == null) return false;

        IEditorStateSnapshot before = SingleAmbientSoundStateSnapshot.Capture(settings, target);
        if (before == null || !mutation()) return false;

        IEditorStateSnapshot after = SingleAmbientSoundStateSnapshot.Capture(settings, target);
        if (!SnapshotHistoryEntry.TryCreate(label, before, after, out SnapshotHistoryEntry entry))
            return false;

        if (syncLegacyPresentation)
            SynchronizeNativeMutation(session, collectionChanged: false);
        session.History.Push(entry);
        return true;
    }

    private static bool MutateRoomVolumes(
        EditorSession session,
        string label,
        Func<RoomSettings, bool> mutation)
    {
        RoomSettings settings = session?.RoomSettings;
        if (settings == null || mutation == null) return false;

        IEditorStateSnapshot before = SoundRoomVolumeStateSnapshot.Capture(settings);
        if (before == null || !mutation(settings)) return false;
        IEditorStateSnapshot after = SoundRoomVolumeStateSnapshot.Capture(settings);
        if (!SnapshotHistoryEntry.TryCreate(label, before, after, out SnapshotHistoryEntry entry))
            return false;

        SynchronizeNativeMutation(session, collectionChanged: false);
        session.History.Push(entry);
        return true;
    }

    private static void SynchronizeNativeMutation(EditorSession session, bool collectionChanged)
    {
        NativeLegacyPresentationInvalidation.InvalidateCurrentSoundOrTriggerPage(session);
        if (collectionChanged)
            NativeSoundRuntimeReconciler.Reconcile(session);
    }

    private static bool TryGetSound(EditorSession session, int index, out AmbientSound sound)
    {
        sound = null;
        if (session?.RoomSettings?.ambientSounds == null || index < 0 || index >= session.RoomSettings.ambientSounds.Count)
            return false;
        sound = session.RoomSettings.ambientSounds[index];
        return sound != null;
    }

    private static int FindLast(EditorSession session, string sample, int soundType)
    {
        if (session?.RoomSettings?.ambientSounds == null) return -1;
        for (int i = session.RoomSettings.ambientSounds.Count - 1; i >= 0; i--)
        {
            AmbientSound sound = session.RoomSettings.ambientSounds[i];
            if (sound != null && sound.type?.Index == soundType && string.Equals(sound.sample, sample, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }
}
