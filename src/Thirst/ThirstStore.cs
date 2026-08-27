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

    public static float GetMaxWaterPips(Player player)
    {
        return player == null ? 0f : Math.Max(0, player.MaxFoodInStomach);
    }

    public static float GetMaxWaterPips(SlugcatStats.Name slugcat)
    {
        return slugcat == null ? 0f : Math.Max(0, SlugcatStats.SlugcatFoodMeter(slugcat).x);
    }

    public static float GetMaxWaterValue(Player player)
    {
        return GetMaxWaterPips(player) * ThirstConstants.WaterValuePerPip;
    }

    public static float GetMaxWaterValue(SlugcatStats.Name slugcat)
    {
        return GetMaxWaterPips(slugcat) * ThirstConstants.WaterValuePerPip;
    }

    public static ThirstState For(Player player)
    {
        if (player == null)
        {
            return new ThirstState();
        }

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

        float maxWater = GetMaxWaterPips(player);
        RainWorldGame game = player.room?.game ?? player.abstractCreature?.world?.game;

        if (game == null || !game.IsStorySession)
        {
            ThirstState local = For(player);
            float beforeLocal = local.Water;
            float nextLocal = Clamp(beforeLocal + amount, maxWater);
            local.Set(nextLocal);
            return nextLocal > beforeLocal + 0.0001f;
        }

        int playerNumber = GetPlayerNumber(player);
        RuntimeHydration runtime = RuntimeStates.GetOrCreateValue(game);
        float previous = GetOrInitializeRuntimeSlot(
            runtime,
            game.GetStorySession?.saveState,
            playerNumber,
            maxWater);
        float next = Clamp(previous + amount, maxWater);

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

        float maxWater = GetMaxWaterPips(player);
        RainWorldGame game = player.room?.game ?? player.abstractCreature?.world?.game;

        if (game == null || !game.IsStorySession)
        {
            ThirstState local = For(player);
            float beforeLocal = local.Water;
            float nextLocal = Clamp(beforeLocal - amount, maxWater);
            local.Set(nextLocal);
            return nextLocal < beforeLocal - 0.0001f;
        }

        int playerNumber = GetPlayerNumber(player);
        RuntimeHydration runtime = RuntimeStates.GetOrCreateValue(game);
        float previous = GetOrInitializeRuntimeSlot(
            runtime,
            game.GetStorySession?.saveState,
            playerNumber,
            maxWater);
        float next = Clamp(previous - amount, maxWater);

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
            return 0f;
        }

        float maxWater = GetMaxWaterPips(player);
        RainWorldGame game = player.room?.game ?? player.abstractCreature?.world?.game;

        if (game == null || !game.IsStorySession)
        {
            if (PlayerStates.TryGetValue(player, out ThirstState localState))
            {
                return Clamp(localState.Water, maxWater);
            }

            return maxWater;
        }

        return GetRuntimeWater(
            game,
            game.GetStorySession?.saveState,
            GetPlayerNumber(player),
            maxWater);
    }

    public static float GetRuntimeWater(
        RainWorldGame game,
        SaveState fallbackSaveState,
        int playerNumber)
    {
        float maxWater = ResolveMaxWaterPips(game, fallbackSaveState, playerNumber);
        return GetRuntimeWater(game, fallbackSaveState, playerNumber, maxWater);
    }

    private static float GetRuntimeWater(
        RainWorldGame game,
        SaveState fallbackSaveState,
        int playerNumber,
        float maxWater)
    {
        if (game == null || !game.IsStorySession)
        {
            return Clamp(GetSaved(fallbackSaveState, playerNumber), maxWater);
        }

        RuntimeHydration runtime = RuntimeStates.GetOrCreateValue(game);
        SaveState saveState = game.GetStorySession?.saveState ?? fallbackSaveState;
        return GetOrInitializeRuntimeSlot(runtime, saveState, playerNumber, maxWater);
    }

    public static float GetSaved(SaveState saveState)
    {
        return GetSaved(saveState, 0);
    }

    public static float GetSaved(SaveState saveState, int playerNumber)
    {
        if (saveState == null)
        {
            return 0f;
        }

        SaveHydration hydration = SaveStates.GetOrCreateValue(saveState);
        if (hydration.WaterByPlayer.TryGetValue(playerNumber, out float water))
        {
            return ClampNonNegative(water);
        }

        return GetMaxWaterPips(saveState.saveStateNumber);
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

        SaveStates.GetOrCreateValue(saveState).WaterByPlayer[playerNumber] = ClampNonNegative(water);
    }

    public static float GetForCharacterSelect(PlayerProgression progression, SlugcatStats.Name slugcat)
    {
        float fullWater = GetMaxWaterPips(slugcat);
        if (progression == null || slugcat == null)
        {
            return fullWater;
        }

        if (progression.currentSaveState != null &&
            progression.currentSaveState.saveStateNumber == slugcat)
        {
            return ReadValueFromEntries(
                progression.currentSaveState.unrecognizedSaveStrings,
                0,
                fullWater);
        }

        if (!progression.HasSaveData)
        {
            return fullWater;
        }

        string[] progressionLines = progression.GetProgLinesFromMemory();
        if (progressionLines == null)
        {
            return fullWater;
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

            return ReadValueFromSaveText(parts[1], 0, fullWater);
        }

        return fullWater;
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
            hydration.WaterByPlayer[0] = GetMaxWaterPips(saveState.saveStateNumber);
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
                ClampNonNegative(hydration.WaterByPlayer[playerNumber])
                    .ToString("0.###", CultureInfo.InvariantCulture));
        }
    }

    private static int GetPlayerNumber(Player player)
    {
        return player?.playerState?.playerNumber ?? 0;
    }

    private static float ResolveMaxWaterPips(
        RainWorldGame game,
        SaveState saveState,
        int playerNumber)
    {
        if (game?.Players != null)
        {
            foreach (AbstractCreature abstractPlayer in game.Players)
            {
                if (abstractPlayer?.state is not PlayerState playerState ||
                    playerState.playerNumber != playerNumber)
                {
                    continue;
                }

                if (abstractPlayer.realizedCreature is Player player)
                {
                    return GetMaxWaterPips(player);
                }

                return GetMaxWaterPips(playerState.slugcatCharacter);
            }
        }

        return GetMaxWaterPips(saveState?.saveStateNumber);
    }

    private static float GetOrInitializeRuntimeSlot(
        RuntimeHydration runtime,
        SaveState saveState,
        int playerNumber,
        float maxWater)
    {
        if (runtime.WaterByPlayer.TryGetValue(playerNumber, out float water))
        {
            float clamped = Clamp(water, maxWater);
            runtime.WaterByPlayer[playerNumber] = clamped;
            return clamped;
        }

        SaveHydration saved = saveState == null ? null : SaveStates.GetOrCreateValue(saveState);
        water = saved != null && saved.WaterByPlayer.TryGetValue(playerNumber, out float existing)
            ? Clamp(existing, maxWater)
            : maxWater;

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
        water = 0f;

        if (string.IsNullOrEmpty(entry))
        {
            return false;
        }

        string mainPrefix = GetSavePrefix(0);
        if (entry.StartsWith(mainPrefix, StringComparison.Ordinal))
        {
            return TryParseWater(entry.Substring(mainPrefix.Length), out water);
        }

        string legacyPrefix = ThirstConstants.LegacySaveKey + "<svB>";
        if (entry.StartsWith(legacyPrefix, StringComparison.Ordinal))
        {
            return TryParseWater(entry.Substring(legacyPrefix.Length), out water);
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
        int playerNumber,
        float fallback)
    {
        if (entries == null)
        {
            return fallback;
        }

        float result = fallback;
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

    private static float ReadValueFromSaveText(
        string saveText,
        int playerNumber,
        float fallback)
    {
        if (string.IsNullOrEmpty(saveText))
        {
            return fallback;
        }

        string prefix = GetSavePrefix(playerNumber);
        int start = saveText.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return fallback;
        }

        start += prefix.Length;
        int end = saveText.IndexOf("<svA>", start, StringComparison.Ordinal);
        if (end < 0)
        {
            end = saveText.Length;
        }

        return TryParseWater(saveText.Substring(start, end - start), out float parsed)
            ? parsed
            : fallback;
    }

    private static bool TryParseWater(string value, out float water)
    {
        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
        {
            water = ClampNonNegative(parsed);
            return true;
        }

        water = 0f;
        return false;
    }

    private static float Clamp(float value, float maxWater)
    {
        if (value < 0f)
        {
            return 0f;
        }

        if (value > maxWater)
        {
            return maxWater;
        }

        return value;
    }

    private static float ClampNonNegative(float value)
    {
        return value < 0f ? 0f : value;
    }
}
