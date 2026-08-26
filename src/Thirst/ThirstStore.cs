using System;
using System.Globalization;
using System.Runtime.CompilerServices;

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
        return SaveStates.GetOrCreateValue(saveState).Water;
    }

    public static void SetSaved(SaveState saveState, float water)
    {
        SaveStates.GetOrCreateValue(saveState).Water = Clamp(water);
    }

    public static void ReadFromUnrecognizedData(SaveState saveState)
    {
        float result = ThirstConstants.MaxWater;
        string prefix = ThirstConstants.SaveKey + "<svB>";

        foreach (string entry in saveState.unrecognizedSaveStrings)
        {
            if (!entry.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            string value = entry.Substring(prefix.Length);
            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
            {
                result = Clamp(parsed);
            }
        }

        SetSaved(saveState, result);
    }

    public static void WriteToUnrecognizedData(SaveState saveState)
    {
        string prefix = ThirstConstants.SaveKey + "<svB>";
        saveState.unrecognizedSaveStrings.RemoveAll(s => s.StartsWith(prefix, StringComparison.Ordinal));
        saveState.unrecognizedSaveStrings.Add(prefix + GetSaved(saveState).ToString("0.###", CultureInfo.InvariantCulture));
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
