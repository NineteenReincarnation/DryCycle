using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace DryCycle.Thirst;

internal static class ThirstStore
{
    private sealed class SaveHydration
    {
        public readonly Dictionary<int, float> WaterByPlayer = new();
    }

    private sealed class RuntimeHydration
    {
        public readonly Dictionary<int, float> WaterByPlayer = new();
    }

    private static readonly ConditionalWeakTable<Player, ThirstState> PlayerStates = new();
    private static readonly ConditionalWeakTable<SaveState, SaveHydration> SaveStates = new();
    private static readonly ConditionalWeakTable<RainWorldGame, RuntimeHydration> RuntimeStates = new();

    public static ThirstState For(Player player)
    {
        if (player == null)
        {
            return new ThirstState();
        }

        int playerNumber = GetPlayerNumber(player);
        float runtimeWater = GetRuntimeWater(player);

        if (PlayerStates.TryGetValue(player, out ThirstState existing))
        {
            if (Math.Abs(existing.Water - runtimeWater) > 0.0001f)
            {
                existing.Set(runtimeWater);
            }

            return existing;
        }

        ThirstState created = new();
        created.Set(runtimeWater);
        created.LastWater = created.Water;
        PlayerStates.Add(player, created);
        return created;
    }

    public static bool AddRuntime(Player player, float amount)
    {
        if (player == null || amount <= 0f)
        {
            return false;
        }

        RainWorldGame game = player.room?.game ?? player.abstractCreature?.world?.game;
        if (game == null || !game.IsStorySession)
        {
            ThirstState local = For(player);
            float beforeLocal = local.Water;
            local.Add(amount);
            return local.Water > beforeLocal + 0.0001f;
        }

        int playerNumber = GetPlayerNumber(player);
        RuntimeHydration runtime = RuntimeStates.GetOrCreateValue(game);
        float previous = GetOrInitializeRuntimeSlot(
            runtime,
            game.GetStorySession?.saveState,
            playerNumber);
        float next = Clamp(previous + amount);

        if (next <= previous + 0.0001f)
        {
            return false;
        }

        runtime.WaterByPlayer[playerNumber] = next;

        if (PlayerStates.TryGetValue(player, out ThirstState state))
        {
            state.Set(next);
        }

        return true;
    }

    public static bool RemoveRuntime(Player player, float amount)
    {
        if (player == null || amount <= 0f)
        {
            return false;
        }

        RainWorldGame game = player.room?.game ?? player.abstractCreature?.world?.game;
        if (game == null || !game.IsStorySession)
        {
            ThirstState local = For(player);
            float beforeLocal = local.Water;
            local.Set(beforeLocal - amount);
            return local.Water < beforeLocal - 0.0001f;
        }

        int playerNumber = GetPlayerNumber(player);
        RuntimeHydration runtime = RuntimeStates.GetOrCreateValue(game);
        float previous = GetOrInitializeRuntimeSlot(
            runtime,
            game.GetStorySession?.saveState,
            playerNumber);
        float next = Clamp(previous - amount);

        if (next >= previous - 0.0001f)
        {
            return false;
        }

        runtime.WaterByPlayer[playerNumber] = next;

        if (PlayerStates.TryGetValue(player, out ThirstState state))
        {
            state.Set(next);
        }

        return true;
    }

    public static float GetRuntimeWater(Player player)
    {
        if (player == null)
        {
            return ThirstConstants.MaxWater;
        }

        RainWorldGame game = player.room?.game ?? player.abstractCreature?.world?.game;
        if (game == null || !game.IsStorySession)
        {
            if (PlayerStates.TryGetValue(player, out ThirstState localState))
            {
                return localState.Water;
            }

            return ThirstConstants.MaxWater;
        }

        return GetRuntimeWater(
            game,
            game.GetStorySession?.saveState,
            GetPlayerNumber(player));
    }

    public static float GetRuntimeWater(
        RainWorldGame game,
        SaveState fallbackSaveState,
        int playerNumber)
    {
        if (game == null || !game.IsStorySession)
        {
            return GetSaved(fallbackSaveState, playerNumber);
        }

        RuntimeHydration runtime = RuntimeStates.GetOrCreateValue(game);
        SaveState saveState = game.GetStorySession?.saveState ?? fallbackSaveState;
        return GetOrInitializeRuntimeSlot(runtime, saveState, playerNumber);
    }

    public static float GetSaved(SaveState saveState)
    {
        return GetSaved(saveState, 0);
    }

    public static float GetSaved(SaveState saveState, int playerNumber)
    {
        if (saveState == null)
        {
            return ThirstConstants.MaxWater;
        }

        SaveHydration hydration = SaveStates.GetOrCreateValue(saveState);
        return hydration.WaterByPlayer.TryGetValue(playerNumber, out float water)
            ? water
            : ThirstConstants.MaxWater;
    }

    public static void SetSaved(SaveState saveState, float water)
    {
        SetSaved(saveState, 0, water);
    }

    public static void SetSaved(SaveState saveState, int playerNumber, float water)
    {
        if (saveState == null || playerNumber < 0)
        {
            return;
        }

        SaveStates.GetOrCreateValue(saveState).WaterByPlayer[playerNumber] = Clamp(water);
    }

    public static float GetForCharacterSelect(PlayerProgression progression, SlugcatStats.Name slugcat)
    {
        if (progression == null || slugcat == null)
        {
            return ThirstConstants.MaxWater;
        }

        if (progression.currentSaveState != null &&
            progression.currentSaveState.saveStateNumber == slugcat)
        {
            return ReadValueFromEntries(progression.currentSaveState.unrecognizedSaveStrings, 0);
        }

        if (!progression.HasSaveData)
        {
            return ThirstConstants.MaxWater;
        }

        string[] progressionLines = progression.GetProgLinesFromMemory();
        if (progressionLines == null)
        {
            return ThirstConstants.MaxWater;
        }

        foreach (string line in progressionLines)
        {
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            string[] parts = Regex.Split(line, "<progDivB>");
            if (parts.Length != 2 ||
                parts[0] != "SAVE STATE" ||
                BackwardsCompatibilityRemix.ParseSaveNumber(parts[1]) != slugcat)
            {
                continue;
            }

            return ReadValueFromSaveText(parts[1], 0);
        }

        return ThirstConstants.MaxWater;
    }

    public static void ReadFromUnrecognizedData(SaveState saveState)
    {
        if (saveState == null)
        {
            return;
        }

        SaveHydration hydration = SaveStates.GetOrCreateValue(saveState);
        hydration.WaterByPlayer.Clear();

        if (saveState.unrecognizedSaveStrings == null)
        {
            return;
        }

        foreach (string entry in saveState.unrecognizedSaveStrings)
        {
            if (TryReadEntry(entry, out int playerNumber, out float water))
            {
                hydration.WaterByPlayer[playerNumber] = water;
            }
        }
    }

    public static void WriteToUnrecognizedData(SaveState saveState)
    {
        if (saveState?.unrecognizedSaveStrings == null)
        {
            return;
        }

        string mainPrefix = GetSavePrefix(0);
        string legacyPrefix = ThirstConstants.LegacySaveKey + "<svB>";
        string coopPrefix = ThirstConstants.SaveKey + "P";

        saveState.unrecognizedSaveStrings.RemoveAll(s =>
            s != null &&
            (s.StartsWith(mainPrefix, StringComparison.Ordinal) ||
             s.StartsWith(legacyPrefix, StringComparison.Ordinal) ||
             s.StartsWith(coopPrefix, StringComparison.Ordinal)));

        SaveHydration hydration = SaveStates.GetOrCreateValue(saveState);
        if (!hydration.WaterByPlayer.ContainsKey(0))
        {
            hydration.WaterByPlayer[0] = ThirstConstants.MaxWater;
        }

        List<int> playerNumbers = new(hydration.WaterByPlayer.Keys);
        playerNumbers.Sort();

        foreach (int playerNumber in playerNumbers)
        {
            if (playerNumber < 0)
            {
                continue;
            }

            saveState.unrecognizedSaveStrings.Add(
                GetSavePrefix(playerNumber) +
                Clamp(hydration.WaterByPlayer[playerNumber])
                    .ToString("0.###", CultureInfo.InvariantCulture));
        }
    }

    private static int GetPlayerNumber(Player player)
    {
        return player?.playerState?.playerNumber ?? 0;
    }

    private static float GetOrInitializeRuntimeSlot(
        RuntimeHydration runtime,
        SaveState saveState,
        int playerNumber)
    {
        if (runtime.WaterByPlayer.TryGetValue(playerNumber, out float water))
        {
            return water;
        }

        water = GetSaved(saveState, playerNumber);
        runtime.WaterByPlayer[playerNumber] = water;
        return water;
    }

    private static string GetSavePrefix(int playerNumber)
    {
        return playerNumber <= 0
            ? ThirstConstants.SaveKey + "<svB>"
            : ThirstConstants.SaveKey + "P" + playerNumber + "<svB>";
    }

    private static bool TryReadEntry(string entry, out int playerNumber, out float water)
    {
        playerNumber = 0;
        water = ThirstConstants.MaxWater;

        if (string.IsNullOrEmpty(entry))
        {
            return false;
        }

        string mainPrefix = GetSavePrefix(0);
        if (entry.StartsWith(mainPrefix, StringComparison.Ordinal))
        {
            return TryParseWater(entry.Substring(mainPrefix.Length), out water);
        }

        string playerPrefix = ThirstConstants.SaveKey + "P";
        if (!entry.StartsWith(playerPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        int separator = entry.IndexOf("<svB>", playerPrefix.Length, StringComparison.Ordinal);
        if (separator < 0)
        {
            return false;
        }

        string playerText = entry.Substring(
            playerPrefix.Length,
            separator - playerPrefix.Length);

        if (!int.TryParse(playerText, NumberStyles.Integer, CultureInfo.InvariantCulture, out playerNumber) ||
            playerNumber < 1)
        {
            return false;
        }

        return TryParseWater(entry.Substring(separator + 5), out water);
    }

    private static float ReadValueFromEntries(
        List<string> entries,
        int playerNumber)
    {
        if (entries == null)
        {
            return ThirstConstants.MaxWater;
        }

        float result = ThirstConstants.MaxWater;
        foreach (string entry in entries)
        {
            if (TryReadEntry(entry, out int entryPlayer, out float parsed) &&
                entryPlayer == playerNumber)
            {
                result = parsed;
            }
        }

        return result;
    }

    private static float ReadValueFromSaveText(string saveText, int playerNumber)
    {
        if (string.IsNullOrEmpty(saveText))
        {
            return ThirstConstants.MaxWater;
        }

        string prefix = GetSavePrefix(playerNumber);
        int start = saveText.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return ThirstConstants.MaxWater;
        }

        start += prefix.Length;
        int end = saveText.IndexOf("<svA>", start, StringComparison.Ordinal);
        if (end < 0)
        {
            end = saveText.Length;
        }

        return TryParseWater(saveText.Substring(start, end - start), out float parsed)
            ? parsed
            : ThirstConstants.MaxWater;
    }

    private static bool TryParseWater(string value, out float water)
    {
        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
        {
            water = Clamp(parsed);
            return true;
        }

        water = ThirstConstants.MaxWater;
        return false;
    }

    private static float Clamp(float value)
    {
        if (value < 0f)
        {
            return 0f;
        }

        if (value > ThirstConstants.MaxWater)
        {
            return ThirstConstants.MaxWater;
        }

        return value;
    }
}
