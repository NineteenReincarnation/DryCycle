namespace DryCycle.ShelterExts;

/// <summary>
/// Removes Rain World's ordinary shelter entrance-depth requirement while leaving
/// the actual ShelterDoor close/hibernate pipeline intact. Vanilla normally requires
/// the player to be more than six Manhattan tiles from shelter node 0 before the door
/// may close. This runtime supplies the same close trigger after Player.Update without
/// that one distance test.
/// </summary>
internal static class OpenShelterSleepRuntime
{
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        On.Player.Update += Player_Update;
        _enabled = true;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.Player.Update -= Player_Update;
        _enabled = false;
    }

    private static void Player_Update(
        On.Player.orig_Update orig,
        Player self,
        bool eu)
    {
        orig(self, eu);
        TryCloseShelterWithoutEntranceDepth(self);
    }

    private static void TryCloseShelterWithoutEntranceDepth(Player player)
    {
        Room room = player?.room;
        ShelterDoor door = room?.shelterDoor;

        if (player == null ||
            room?.abstractRoom == null ||
            !room.abstractRoom.shelter ||
            room.game == null ||
            !room.game.IsStorySession ||
            player.AI != null ||
            player.dead ||
            player.Sleeping ||
            player.inShortcut ||
            door == null ||
            door.Broken ||
            door.IsClosing ||
            door.Closed > 0.0001f ||
            player.abstractCreature == null)
        {
            return;
        }

        // Keep every non-depth restriction from vanilla. MMF deliberately waits for
        // the player to have left corridor movement, and Ancient Shelters still use
        // their authored rectangular sleep range.
        if ((ModManager.MMF && player.timeSinceInCorridorMode <= 10) ||
            !ShelterDoor.IsTileInsideShelterRange(
                room.abstractRoom,
                player.abstractCreature.pos.Tile))
        {
            return;
        }

        int idleFramesRequired = ModManager.MMF ? 40 : 20;

        if (player.readyForWin && player.touchedNoInputCounter > idleFramesRequired)
        {
            if (ModManager.CoopAvailable)
            {
                player.ReadyForWinJolly = true;
            }

            door.Close();
            return;
        }

        if (player.forceSleepCounter > 260)
        {
            if (ModManager.CoopAvailable)
            {
                player.ReadyForStarveJolly = true;
            }

            player.sleepCounter = -24;
            door.Close();
        }
    }
}
