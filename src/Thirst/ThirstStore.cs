using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace DryCycle.Thirst;

internal static class ThirstStore
{
    private sealed class SaveHydration
    {
        public SaveHydration()
        {
        }

        public float Water = ThirstConstants.MaxWater;
    }

    private static readonly ConditionalWeakTable<Player, ThirstState> PlayerStates = new();
    private static readonly ConditionalWeakTable<SaveState, SaveHydration> SaveStates = new();

    public static ThirstState For(Player player)
    {
        if (PlayerStates.TryGetValue(player, out ThirstState existing))
        {
            return existing;
        }

        float initial = ThirstConstants.MaxWater;
        if (player.room?.game != null && player.room.game.IsStorySession)
        {
            initial = GetSaved(player.room.game.GetStorySession.saveState);
        }

        ThirstState created = new();
        created.Set(initial);
        created.LastWater = created.Water;
        PlayerStates.Add(player, created);
        return created;
    }

    public static float GetSaved(SaveState saveState)
    {
        if (saveState == null)
        {
            return ThirstConstants.MaxWater;
        }

        return SaveStates.GetOrCreateValue(saveState).Water;
    }

    public static void SetSaved(SaveState saveState, float water)
    {
        if (saveState == null)
        {
            return;
        }

        SaveStates.GetOrCreateValue(saveState).Water = Clamp(water);
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
            return ReadValueFromEntries(progression.currentSaveState.unrecognizedSaveStrings);
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

            return ReadValueFromSaveText(parts[1]);
        }

        return ThirstConstants.MaxWater;
    }

    public static void ReadFromUnrecognizedData(SaveState saveState)
    {
        if (saveState == null)
        {
            return;
        }

        SetSaved(saveState, ReadValueFromEntries(saveState.unrecognizedSaveStrings));
    }

    public static void WriteToUnrecognizedData(SaveState saveState)
    {
        if (saveState?.unrecognizedSaveStrings == null)
        {
            return;
        }

        string prefix = ThirstConstants.SaveKey + "<svB>";
        string legacyPrefix = ThirstConstants.LegacySaveKey + "<svB>";

        saveState.unrecognizedSaveStrings.RemoveAll(s =>
            s != null &&
            (s.StartsWith(prefix, StringComparison.Ordinal) ||
             s.StartsWith(legacyPrefix, StringComparison.Ordinal)));

        saveState.unrecognizedSaveStrings.Add(
            prefix + GetSaved(saveState).ToString("0.###", CultureInfo.InvariantCulture));
    }

    private static float ReadValueFromEntries(System.Collections.Generic.List<string> entries)
    {
        if (entries == null)
        {
            return ThirstConstants.MaxWater;
        }

        string prefix = ThirstConstants.SaveKey + "<svB>";
        float result = ThirstConstants.MaxWater;

        foreach (string entry in entries)
        {
            if (entry == null || !entry.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            string value = entry.Substring(prefix.Length);
            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
            {
                result = Clamp(parsed);
            }
        }

        return result;
    }

    private static float ReadValueFromSaveText(string saveText)
    {
        if (string.IsNullOrEmpty(saveText))
        {
            return ThirstConstants.MaxWater;
        }

        string prefix = ThirstConstants.SaveKey + "<svB>";
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

        string value = saveText.Substring(start, end - start);
        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
        {
            return Clamp(parsed);
        }

        return ThirstConstants.MaxWater;
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
