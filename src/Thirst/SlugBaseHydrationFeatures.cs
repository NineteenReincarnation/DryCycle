using System;
using SlugBase;
using SlugBase.Features;
using static SlugBase.Features.FeatureTypes;

namespace DryCycle.Thirst;

/// <summary>
/// SlugBase-facing character configuration for DryCycle hydration.
///
/// Custom SlugBase slugcats can set these exact keys in their character JSON:
/// "WaterLossRate" (WV per second) and "WaterPips" (whole hydration pips required
/// and consumed by normal hibernation). Values are resolved on demand so SlugBase
/// JSON hot-reload is reflected without caching character-specific settings here.
/// </summary>
internal static class SlugBaseHydrationFeatures
{
    public static readonly PlayerFeature<float> WaterLossRate = PlayerFloat("WaterLossRate");
    public static readonly PlayerFeature<int> WaterPips = PlayerInt("WaterPips");

    public const float DefaultWaterLossRate = 5f;
    public const int DefaultWaterPips = 2;

    /// <summary>
    /// Touching this method during plugin initialization guarantees the static
    /// PlayerFeature fields are constructed and registered before SlugBase scans
    /// character JSON files.
    /// </summary>
    public static void Initialize()
    {
    }

    public static float GetWaterLossRate(Player player)
    {
        if (player != null && WaterLossRate.TryGet(player, out float configured))
        {
            return Math.Max(0f, configured);
        }

        return DefaultWaterLossRate;
    }

    public static float GetWaterLossRate(SlugcatStats.Name slugcat)
    {
        SlugBaseCharacter character = SlugBaseCharacter.Get(slugcat);
        if (WaterLossRate.TryGet(character, out float configured))
        {
            return Math.Max(0f, configured);
        }

        return DefaultWaterLossRate;
    }

    public static int GetWaterPips(Player player)
    {
        if (player != null && WaterPips.TryGet(player, out int configured))
        {
            return Math.Max(0, configured);
        }

        return GetDefaultWaterPips(player?.SlugCatClass);
    }

    public static int GetWaterPips(SlugcatStats.Name slugcat)
    {
        SlugBaseCharacter character = SlugBaseCharacter.Get(slugcat);
        if (WaterPips.TryGet(character, out int configured))
        {
            return Math.Max(0, configured);
        }

        return GetDefaultWaterPips(slugcat);
    }

    public static float GetWaterLossPerTick(Player player)
    {
        float waterValuePerSecond = GetWaterLossRate(player);
        return waterValuePerSecond /
               ThirstConstants.WaterValuePerPip /
               ThirstConstants.SimulationTicksPerSecond;
    }

    private static int GetDefaultWaterPips(SlugcatStats.Name slugcat)
    {
        string id = slugcat?.value;

        return id switch
        {
            // Monk
            "Yellow" => 1,

            // Survivor
            "White" => 2,

            // Hunter
            "Red" => 3,

            // More Slugcats
            "Gourmand" => 4,
            "Artificer" => 3,
            "Rivulet" => 3,
            "Saint" => 2,
            "Inv" => 6,

            // Spearmaster, Watcher, Night and any other character without an
            // explicit SlugBase WaterPips feature use the neutral fallback.
            _ => DefaultWaterPips
        };
    }
}
