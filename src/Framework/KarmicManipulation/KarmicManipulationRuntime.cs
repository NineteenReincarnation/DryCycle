using UnityEngine;

namespace DryCycle.Framework.KarmicManipulation;

/// <summary>
/// Single authority for spending reinforced karma on DryCycle karmic abilities.
/// Karmic armor and active manipulations such as Karma Spear must never read or
/// mutate DeathPersistentSaveData.reinforcedKarma independently.
/// </summary>
internal static class KarmicManipulationRuntime
{
    internal static bool HasProtection(Player player)
    {
        return TryGetStoryState(player, out _, out DeathPersistentSaveData saveData) &&
               saveData.reinforcedKarma;
    }

    internal static bool TryConsumeProtection(
        Player player,
        KarmicManipulationUse use,
        out KarmicCharge charge)
    {
        charge = default;

        if (!TryGetStoryState(player, out RainWorldGame game, out DeathPersistentSaveData saveData) ||
            !saveData.reinforcedKarma)
        {
            return false;
        }

        int karmaLevel = Mathf.Clamp(saveData.karma + 1, 1, 10);
        saveData.reinforcedKarma = false;
        RefreshKarmaMeters(game, saveData);

        charge = new KarmicCharge(
            karmaLevel,
            player.playerState?.playerNumber ?? -1,
            use);

        Plugin.Logger?.LogInfo(
            $"Karmic Manipulation consumed reinforced karma: " +
            $"player={charge.PlayerNumber}, use={use}, level={karmaLevel}.");
        return true;
    }

    internal static int CurrentKarmaLevel(Player player)
    {
        return TryGetStoryState(player, out _, out DeathPersistentSaveData saveData)
            ? Mathf.Clamp(saveData.karma + 1, 1, 10)
            : 1;
    }

    private static bool TryGetStoryState(
        Player player,
        out RainWorldGame game,
        out DeathPersistentSaveData saveData)
    {
        game = player?.abstractCreature?.world?.game;
        saveData = null;

        if (!ModManager.Watcher ||
            game == null ||
            player.room == null ||
            game.session is not StoryGameSession storySession)
        {
            return false;
        }

        saveData = storySession.saveState?.deathPersistentSaveData;
        return saveData != null;
    }

    private static void RefreshKarmaMeters(
        RainWorldGame game,
        DeathPersistentSaveData saveData)
    {
        if (game?.cameras == null)
        {
            return;
        }

        foreach (RoomCamera camera in game.cameras)
        {
            // Do not spell this as HUD.KarmaMeter here. DryCycle owns a DryCycle.HUD
            // namespace, so qualified lookup from this namespace can bind to the mod
            // namespace instead of Rain World's global HUD namespace. Type inference
            // keeps this independent from that namespace collision.
            var meter = camera?.hud?.karmaMeter;
            if (meter == null)
            {
                continue;
            }

            meter.blinkRedCounter = 30;
            meter.showAsReinforced = false;
            meter.UpdateGraphic(saveData.karma, saveData.karmaCap);
        }
    }
}

internal enum KarmicManipulationUse
{
    Armor,
    KarmaSpear
}

internal readonly struct KarmicCharge
{
    internal KarmicCharge(
        int karmaLevel,
        int playerNumber,
        KarmicManipulationUse use)
    {
        KarmaLevel = karmaLevel;
        PlayerNumber = playerNumber;
        Use = use;
    }

    internal int KarmaLevel { get; }
    internal int PlayerNumber { get; }
    internal KarmicManipulationUse Use { get; }
}
