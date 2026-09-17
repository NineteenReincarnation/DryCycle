using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Sound;

/// <summary>
/// Keeps the real Rain World ambient-audio runtime aligned with RoomSettings without involving
/// SoundPage, AmbientSoundPanel or any DevInterface Handle.
///
/// AmbientSoundPlayer retains the AmbientSound model by reference, so ordinary volume/pitch/
/// position/radius/direction edits need no rebuild. Reconciliation is only required when collection
/// membership changes or when legacy code has already slated a player for deletion.
/// </summary>
internal static class NativeSoundRuntimeReconciler
{
    internal static void Reconcile(EditorSession session)
    {
        if (session?.RoomSettings?.ambientSounds == null)
            return;

        RoomCamera[] cameras = session.Owner?.game?.cameras;
        RoomCamera camera = cameras != null && cameras.Length > 0 ? cameras[0] : null;
        Reconcile(session.RoomSettings, camera);
    }

    internal static void Reconcile(RoomSettings settings, RoomCamera camera)
    {
        List<AmbientSound> sounds = settings?.ambientSounds;
        VirtualMicrophone microphone = camera?.virtualMicrophone;
        if (sounds == null || microphone?.ambientSoundPlayers == null)
            return;

        List<AmbientSoundPlayer> players = microphone.ambientSoundPlayers;

        for (int i = players.Count - 1; i >= 0; i--)
        {
            AmbientSoundPlayer player = players[i];
            if (player == null)
            {
                players.RemoveAt(i);
                continue;
            }

            if (!ContainsReference(sounds, player.aSound))
            {
                player.slatedForDeletion = true;
                continue;
            }

            if (player.slatedForDeletion)
                continue;

            // Keep one live runtime player per model member. If vanilla/legacy code accidentally
            // produced duplicates, retire the older copies without restarting the newest instance.
            for (int j = i - 1; j >= 0; j--)
            {
                AmbientSoundPlayer older = players[j];
                if (older != null && !older.slatedForDeletion && ReferenceEquals(older.aSound, player.aSound))
                    older.slatedForDeletion = true;
            }
        }

        for (int i = 0; i < sounds.Count; i++)
        {
            AmbientSound sound = sounds[i];
            if (sound == null) continue;

            bool found = false;
            for (int j = 0; j < players.Count; j++)
            {
                AmbientSoundPlayer player = players[j];
                if (player != null && !player.slatedForDeletion && ReferenceEquals(player.aSound, sound))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
                players.Add(new AmbientSoundPlayer(microphone, sound));
        }
    }

    private static bool ContainsReference(List<AmbientSound> values, AmbientSound target)
    {
        if (values == null || target == null) return false;
        for (int i = 0; i < values.Count; i++)
            if (ReferenceEquals(values[i], target)) return true;
        return false;
    }
}
