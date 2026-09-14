using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using DevInterface;
using DryCycle.DevUI.DevTool.Compatibility;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Sound;

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
        SoundPresentationChangeHintHub.MarkRoomValues(session);

        // The rebuilt Sound frontend reads RoomSettings directly through its revisioned snapshot.
        // Refresh hidden vanilla controls only when they actually own presentation.
        if (!LegacyDevUiQuiescenceController.IsQuiescent(session.Owner) &&
            session.Owner?.activePage is SoundPage page)
            page.Refresh();

        return true;
    }
}

/// <summary>
/// In-memory snapshot of AmbientSound collection membership/order. ApplyGroup only replaces members;
/// it never mutates the sound objects that it removes. Retaining object references is therefore both
/// more exact and safer than round-tripping every sound through ToString/FromString, especially for
/// third-party AmbientSound subclasses whose serialization contract is unknown to DryCycle.
/// </summary>
internal sealed class AmbientSoundCollectionStateSnapshot : IEditorStateSnapshot
{
    private readonly RoomSettings settings;
    private readonly AmbientSound[] members;
    private readonly string fingerprint;

    private AmbientSoundCollectionStateSnapshot(RoomSettings settings, AmbientSound[] members, string fingerprint)
    {
        this.settings = settings;
        this.members = members;
        this.fingerprint = fingerprint;
    }

    public string Kind =>
        "AmbientSoundCollection:" + (settings == null ? 0 : RuntimeHelpers.GetHashCode(settings));

    public string Fingerprint => fingerprint;

    internal static AmbientSoundCollectionStateSnapshot Capture(RoomSettings settings)
    {
        if (settings?.ambientSounds == null)
            return null;

        AmbientSound[] members = settings.ambientSounds.ToArray();
        string[] ids = new string[members.Length];
        for (int i = 0; i < members.Length; i++)
            ids[i] = members[i] == null ? "null" : RuntimeHelpers.GetHashCode(members[i]).ToString(CultureInfo.InvariantCulture);

        return new AmbientSoundCollectionStateSnapshot(settings, members, string.Join("|", ids));
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
            for (int i = 0; i < members.Length; i++)
            {
                AmbientSound sound = members[i];
                if (sound != null)
                    settings.ambientSounds.Add(sound);
            }

            SoundPresentationChangeHintHub.MarkCollection(session);

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
