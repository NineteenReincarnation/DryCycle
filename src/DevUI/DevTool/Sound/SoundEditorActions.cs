using System;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
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
        state.SelectedIndex = index >= 0 && index < count ? index : -1;
    }

    internal static bool Create(EditorSession session, string sample, int soundType)
    {
        if (session?.RoomSettings?.ambientSounds == null || string.IsNullOrEmpty(sample)) return false;
        if (session.ToolMode != EditorToolMode.Sound) session.SetToolMode(EditorToolMode.Sound);
        if (session.Owner?.activePage is not SoundPage page) return false;
        if (soundType < 0 || soundType > 2) return false;

        if (soundType != AmbientSound.Type.Spot.Index)
        {
            for (int i = 0; i < session.RoomSettings.ambientSounds.Count; i++)
            {
                AmbientSound existing = session.RoomSettings.ambientSounds[i];
                if (existing != null && !existing.inherited && existing.type?.Index == soundType &&
                    string.Equals(existing.sample, sample, StringComparison.Ordinal))
                {
                    SoundEditorStateHub.Get(session).SelectedIndex = i;
                    return true;
                }
            }
        }

        RoomSettingsStateSnapshot before = RoomSettingsStateSnapshot.Capture(session.RoomSettings);
        int beforeCount = session.RoomSettings.ambientSounds.Count;

        page.soundType = soundType;
        page.CreateSoundRep(sample);

        RoomSettingsStateSnapshot after = RoomSettingsStateSnapshot.Capture(session.RoomSettings);
        if (SnapshotHistoryEntry.TryCreate(
                "Create sound " + sample,
                before,
                after,
                out SnapshotHistoryEntry entry))
            session.History.Push(entry);

        SoundEditorState state = SoundEditorStateHub.Get(session);
        if (state != null)
        {
            if (session.RoomSettings.ambientSounds.Count > beforeCount)
                state.SelectedIndex = session.RoomSettings.ambientSounds.Count - 1;
            else
                state.SelectedIndex = FindLast(session, sample, soundType);
        }
        return true;
    }

    internal static bool Delete(EditorSession session, int index)
    {
        if (!TryGetSound(session, index, out AmbientSound sound) || sound.inherited) return false;

        RoomSettingsStateSnapshot before = RoomSettingsStateSnapshot.Capture(session.RoomSettings);
        session.RoomSettings.ambientSounds.RemoveAt(index);
        RefreshSoundPage(session);
        RoomSettingsStateSnapshot after = RoomSettingsStateSnapshot.Capture(session.RoomSettings);

        if (SnapshotHistoryEntry.TryCreate(
                "Delete sound " + (sound.sample ?? string.Empty),
                before,
                after,
                out SnapshotHistoryEntry entry))
            session.History.Push(entry);

        SoundEditorState state = SoundEditorStateHub.Get(session);
        if (state != null)
        {
            int count = session.RoomSettings.ambientSounds.Count;
            state.SelectedIndex = count == 0 ? -1 : Math.Min(index, count - 1);
        }
        return true;
    }

    internal static bool SetRoomValue(EditorSession session, string key, EditorPropertyValue value)
    {
        if (session?.RoomSettings == null || value.Kind != EditorPropertyKind.Float) return false;

        return Mutate(session, "Change sound room setting", settings =>
        {
            switch (key)
            {
                case SoundEditorKeys.BackgroundDroneVolume:
                    settings.BkgDroneVolume = Mathf.Clamp01(value.X);
                    return true;
                case SoundEditorKeys.NoThreatDroneVolume:
                    settings.BkgDroneNoThreatVolume = Mathf.Clamp01(value.X);
                    return true;
                default:
                    return false;
            }
        }, refreshSoundPage: false);
    }

    internal static bool SetSoundValue(
        EditorSession session,
        int index,
        string key,
        EditorPropertyValue value)
    {
        if (!TryGetSound(session, index, out AmbientSound sound) || sound.inherited || string.IsNullOrEmpty(key))
            return false;

        return Mutate(session, "Change " + key + " on " + (sound.sample ?? "sound"), _ =>
        {
            switch (key)
            {
                case SoundEditorKeys.Volume:
                    if (value.Kind != EditorPropertyKind.Float) return false;
                    sound.volume = Mathf.Clamp01(value.X);
                    return true;
                case SoundEditorKeys.Pitch:
                    if (value.Kind != EditorPropertyKind.Float) return false;
                    sound.pitch = Mathf.Clamp(value.X, 0.1f, 1.9f);
                    return true;
                case SoundEditorKeys.Doppler:
                    if (value.Kind != EditorPropertyKind.Float || sound is not DopplerAffectedSound doppler) return false;
                    doppler.dopplerFac = Mathf.Clamp01(value.X);
                    return true;
                case SoundEditorKeys.Taper:
                    if (value.Kind != EditorPropertyKind.Float || sound is not SpotSound spotTaper) return false;
                    spotTaper.taper = Mathf.Clamp01(value.X);
                    return true;
                case SoundEditorKeys.Position:
                    if (value.Kind != EditorPropertyKind.Vector2 || sound is not SpotSound spotPosition) return false;
                    spotPosition.pos = new Vector2(value.X, value.Y);
                    return true;
                case SoundEditorKeys.Radius:
                    if (value.Kind != EditorPropertyKind.Float || sound is not SpotSound spotRadius) return false;
                    float radius = Mathf.Max(0f, value.X);
                    Vector2 direction = spotRadius.radHandlePosition.sqrMagnitude > 0.0001f
                        ? spotRadius.radHandlePosition.normalized
                        : Vector2.up;
                    spotRadius.rad = radius;
                    spotRadius.radHandlePosition = direction * radius;
                    return true;
                case SoundEditorKeys.Direction:
                    if (value.Kind != EditorPropertyKind.Vector2 || sound is not DirectionalSound directional) return false;
                    Vector2 next = new(value.X, value.Y);
                    directional.direction = next.sqrMagnitude > 0.0001f ? next.normalized : Vector2.down;
                    return true;
                default:
                    return false;
            }
        });
    }

    internal static bool AddSoundToGroup(EditorSession session, int index, string groupId)
    {
        if (!TryGetSound(session, index, out AmbientSound sound) || string.IsNullOrWhiteSpace(groupId))
            return false;

        global::Room room = session.Room;
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

        return SoundGroupLibrary.AddSoundToLocalGroup(groupId, definition);
    }

    internal static bool ApplyGroup(EditorSession session, string groupId)
    {
        if (session?.RoomSettings?.ambientSounds == null || session.Room == null ||
            string.IsNullOrWhiteSpace(groupId) ||
            !SoundGroupLibrary.TryGetGroup(groupId, out SoundGroupDefinition group))
            return false;

        RoomSettings settings = session.RoomSettings;
        RoomSettingsStateSnapshot before = RoomSettingsStateSnapshot.Capture(settings);
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

        RefreshSoundPage(session);
        RoomSettingsStateSnapshot after = RoomSettingsStateSnapshot.Capture(settings);
        if (SnapshotHistoryEntry.TryCreate(
                "Apply sound group " + (group.Name ?? group.Id),
                before,
                after,
                out SnapshotHistoryEntry entry))
            session.History.Push(entry);

        SoundEditorState state = SoundEditorStateHub.Get(session);
        if (state != null) state.SelectedIndex = lastIndex;
        return true;
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

    private static bool Mutate(
        EditorSession session,
        string label,
        Func<RoomSettings, bool> mutation,
        bool refreshSoundPage = true)
    {
        RoomSettings settings = session?.RoomSettings;
        if (settings == null || mutation == null) return false;

        RoomSettingsStateSnapshot before = RoomSettingsStateSnapshot.Capture(settings);
        if (before == null || !mutation(settings)) return false;

        if (refreshSoundPage) RefreshSoundPage(session);
        RoomSettingsStateSnapshot after = RoomSettingsStateSnapshot.Capture(settings);
        if (SnapshotHistoryEntry.TryCreate(label, before, after, out SnapshotHistoryEntry entry))
            session.History.Push(entry);
        return true;
    }

    private static void RefreshSoundPage(EditorSession session)
    {
        try { session?.Owner?.activePage?.Refresh(); }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool sound refresh failed: " + error.Message);
        }
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
