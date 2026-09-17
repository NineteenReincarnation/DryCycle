using System;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Factories;

/// <summary>
/// Pure model construction for ambient sounds. Registered native providers get first refusal;
/// Rain World's three built-in kinds remain the default. No SoundPage/Panel is required to create
/// the model. Spatial runtime/legacy-handle reconciliation is a separate transitional concern.
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

        RoomSettings settings = session.RoomSettings;

        // Non-spot sounds are unique by semantic identity. This check precedes providers so every
        // native implementation observes the same document-level rule instead of duplicating it.
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

        if (!NativeAuthoringFactoryRegistry.TryCreateSound(session, sample, soundType, out created))
        {
            if (soundType < AmbientSound.Type.Omnidirectional.Index || soundType > AmbientSound.Type.Spot.Index)
                return false;

            if (soundType == AmbientSound.Type.Omnidirectional.Index)
                created = new OmniDirectionalSound(sample, inherited: false);
            else if (soundType == AmbientSound.Type.Directional.Index)
                created = new DirectionalSound(sample, inherited: false);
            else
                created = new SpotSound(sample, inherited: false)
                {
                    pos = NativeFactoryPlacement.WorldCursor(session)
                };
        }

        if (created == null) return false;
        created.panelPosition = NativeFactoryPlacement.LegacyPanelSlot(settings.ambientSounds.Count);

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

        if (!ContainsReference(settings.ambientSounds, created))
            settings.ambientSounds.Add(created);
        selectedIndex = settings.ambientSounds.IndexOf(created);
        return selectedIndex >= 0;
    }

    private static bool ContainsReference(System.Collections.Generic.List<AmbientSound> values, AmbientSound target)
    {
        for (int i = 0; i < values.Count; i++)
            if (ReferenceEquals(values[i], target)) return true;
        return false;
    }
}
