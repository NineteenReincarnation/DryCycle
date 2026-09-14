using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using DevInterface;
using DryCycle.DevUI.DevTool.Compatibility;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.History;

/// <summary>
/// Lightweight room-level Sound settings transaction. Only the two values owned by SoundPage are
/// captured, so adjusting either drone volume never serializes the complete RoomSettings document.
/// </summary>
internal sealed class SoundRoomVolumeStateSnapshot : IEditorStateSnapshot
{
    private readonly RoomSettings settings;
    private readonly float backgroundDroneVolume;
    private readonly float noThreatDroneVolume;

    private SoundRoomVolumeStateSnapshot(RoomSettings settings)
    {
        this.settings = settings;
        backgroundDroneVolume = settings.BkgDroneVolume;
        noThreatDroneVolume = settings.BkgDroneNoThreatVolume;
        Fingerprint =
            backgroundDroneVolume.ToString("R", CultureInfo.InvariantCulture) + "|" +
            noThreatDroneVolume.ToString("R", CultureInfo.InvariantCulture);
    }

    public string Kind =>
        "SoundRoomVolumes:" + (settings == null ? 0 : RuntimeHelpers.GetHashCode(settings));

    public string Fingerprint { get; }

    internal static SoundRoomVolumeStateSnapshot Capture(RoomSettings settings) =>
        settings == null ? null : new SoundRoomVolumeStateSnapshot(settings);

    public IEditorStateSnapshot CaptureCurrent(EditorSession session) =>
        ReferenceEquals(session?.RoomSettings, settings) ? Capture(settings) : null;

    public bool Restore(EditorSession session)
    {
        if (!ReferenceEquals(session?.RoomSettings, settings))
            return false;

        settings.BkgDroneVolume = backgroundDroneVolume;
        settings.BkgDroneNoThreatVolume = noThreatDroneVolume;

        // The rebuilt Sound frontend reads RoomSettings directly through its revisioned snapshot.
        // Refresh hidden vanilla controls only when they actually own presentation.
        if (!LegacyDevUiQuiescenceController.IsQuiescent(session.Owner) &&
            session.Owner?.activePage is SoundPage page)
            page.Refresh();

        return true;
    }
}

/// <summary>
/// In-memory snapshot of the AmbientSound collection. Used for true batch mutations such as applying
/// a Sound Group, where several existing members may be replaced and several new members inserted in
/// one history transaction. Object identity, list order and each sound's lossless save string are all
/// retained, allowing Undo/Redo without RoomSettings.Save, temporary files or reparsing unrelated
/// room settings.
/// </summary>
internal sealed class AmbientSoundCollectionStateSnapshot : IEditorStateSnapshot
{
    private sealed class Entry
    {
        internal AmbientSound Target;
        internal string Serialized;
        internal bool Inherited;
        internal bool OverWrite;
    }

    private static readonly string[] SoundSeparator = { "><" };

    private readonly RoomSettings settings;
    private readonly Entry[] entries;

    private AmbientSoundCollectionStateSnapshot(RoomSettings settings, Entry[] entries, string fingerprint)
    {
        this.settings = settings;
        this.entries = entries;
        Fingerprint = fingerprint;
    }

    public string Kind =>
        "AmbientSoundCollection:" + (settings == null ? 0 : RuntimeHelpers.GetHashCode(settings));

    public string Fingerprint { get; }

    internal static AmbientSoundCollectionStateSnapshot Capture(RoomSettings settings)
    {
        if (settings?.ambientSounds == null)
            return null;

        Entry[] entries = new Entry[settings.ambientSounds.Count];
        StringBuilder fingerprint = new();

        for (int i = 0; i < settings.ambientSounds.Count; i++)
        {
            AmbientSound sound = settings.ambientSounds[i];
            if (sound == null)
            {
                entries[i] = new Entry();
                fingerprint.Append(i).Append(":null;");
                continue;
            }

            string serialized = sound.ToString() ?? string.Empty;
            entries[i] = new Entry
            {
                Target = sound,
                Serialized = serialized,
                Inherited = sound.inherited,
                OverWrite = sound.overWrite
            };

            fingerprint.Append(i).Append(':')
                .Append(RuntimeHelpers.GetHashCode(sound)).Append(':')
                .Append(sound.inherited ? '1' : '0').Append(':')
                .Append(sound.overWrite ? '1' : '0').Append(':')
                .Append(serialized).Append(';');
        }

        return new AmbientSoundCollectionStateSnapshot(settings, entries, fingerprint.ToString());
    }

    public IEditorStateSnapshot CaptureCurrent(EditorSession session) =>
        ReferenceEquals(session?.RoomSettings, settings) ? Capture(settings) : null;

    public bool Restore(EditorSession session)
    {
        if (!ReferenceEquals(session?.RoomSettings, settings) || settings?.ambientSounds == null)
            return false;

        try
        {
            settings.ambientSounds.Clear();
            for (int i = 0; i < entries.Length; i++)
            {
                Entry entry = entries[i];
                AmbientSound sound = entry?.Target;
                if (sound == null)
                    continue;

                sound.FromString((entry.Serialized ?? string.Empty).Split(SoundSeparator, StringSplitOptions.None));
                sound.inherited = entry.Inherited;
                sound.overWrite = entry.OverWrite;
                settings.ambientSounds.Add(sound);
            }

            // Collection shape changes require the vanilla compatibility backend to recreate its
            // world-space Sound handles even when the screen-space SoundPage is quiescent.
            if (session.Owner?.activePage is SoundPage page)
                page.Refresh();
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool ambient-sound collection restore failed: " + error.Message);
            return false;
        }
    }
}
