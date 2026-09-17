using System;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Factories;

/// <summary>
/// Pure model construction for Rain World's three ambient-sound kinds. This replaces the old
/// SoundPage.CreateSoundRep dependency: no panel, file-list button or SoundPage state is required to
/// add a sound to RoomSettings. Spatial runtime/legacy-handle reconciliation remains a separate
/// compatibility concern until the Native Gizmo Engine lands.
/// </summary>
internal static class NativeSoundFactory
{
    internal static bool TryCreate(
        EditorSession session,
        string sample,
        int soundType,
        out AmbientSound created,
        out int selectedIndex)
    {
        created = null;
        selectedIndex = -1;
        if (session?.RoomSettings?.ambientSounds == null || string.IsNullOrWhiteSpace(sample))
            return false;
        if (soundType < AmbientSound.Type.Omnidirectional.Index || soundType > AmbientSound.Type.Spot.Index)
            return false;

        RoomSettings settings = session.RoomSettings;

        // Omni and Directional are unique by (type, sample). Preserve the current rebuilt-editor
        // behavior: selecting an already-local entry is a no-op rather than vanilla's old toggle-off.
        if (soundType != AmbientSound.Type.Spot.Index)
        {
            for (int i = 0; i < settings.ambientSounds.Count; i++)
            {
                AmbientSound existing = settings.ambientSounds[i];
                if (existing != null && !existing.inherited && existing.type?.Index == soundType &&
                    string.Equals(existing.sample, sample, StringComparison.Ordinal))
                {
                    selectedIndex = i;
                    return false;
                }
            }
        }

        if (soundType == AmbientSound.Type.Omnidirectional.Index)
            created = new OmniDirectionalSound(sample, inherited: false);
        else if (soundType == AmbientSound.Type.Directional.Index)
            created = new DirectionalSound(sample, inherited: false);
        else
            created = new SpotSound(sample, inherited: false)
            {
                pos = NativeFactoryPlacement.WorldCursor(session)
            };

        created.panelPosition = NativeFactoryPlacement.LegacyPanelSlot(settings.ambientSounds.Count);

        // A local non-spatial sound shadows inherited entries with the same identity. Preserve the
        // serialized overWrite bit and legacy panel position without asking SoundPage to rebuild.
        if (soundType != AmbientSound.Type.Spot.Index)
        {
            bool overWrite = false;
            for (int i = settings.ambientSounds.Count - 1; i >= 0; i--)
            {
                AmbientSound existing = settings.ambientSounds[i];
                if (existing?.type?.Index != soundType ||
                    !string.Equals(existing.sample, sample, StringComparison.Ordinal))
                    continue;

                created.panelPosition = existing.panelPosition;
                settings.ambientSounds.RemoveAt(i);
                overWrite = true;
            }
            created.overWrite = overWrite;
        }

        settings.ambientSounds.Add(created);
        selectedIndex = settings.ambientSounds.Count - 1;
        return true;
    }
}
