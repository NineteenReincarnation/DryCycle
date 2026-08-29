using System.Globalization;
using DryCycle.Items.KingVultureSpear;
using DryCycle.Thirst;

namespace DryCycle.TemperatureSystem;

/// <summary>
/// Builds the developer-facing water-loss breakdown.
///
/// This is deliberately separate from the HUD renderer. As temperature, activity,
/// equipment and other hydration-loss factors are implemented, their individual
/// terms can be added here without turning the HUD code into gameplay logic.
/// </summary>
internal static class WaterLossRateDebug
{
    // Placeholder for the temperature contribution. It is intentionally neutral
    // until the temperature-to-water-loss rules are authored.
    private const float TemperatureLossMultiplierPlaceholder = 1f;

    internal static string BuildLine(Player player)
    {
        if (player == null)
        {
            return "WV Lost = 0.000/s = no player";
        }

        float baseRate = SlugBaseHydrationFeatures.GetWaterLossRate(player);
        float statusMultiplier = KingVultureSpearCombat.GetWaterLossMultiplier(player);
        float roomHeat = RoomHeatFactor.GetRoomHeat(player.room);
        float temperatureMultiplier = TemperatureLossMultiplierPlaceholder;

        // Keep this equal to the real loss currently applied by ThirstHooks. The
        // temperature multiplier is 1.0 for now, so displaying it as a placeholder
        // does not change the effective result.
        float effectiveRate = baseRate * statusMultiplier * temperatureMultiplier;

        return string.Format(
            CultureInfo.InvariantCulture,
            "WV Lost = {0:0.000}/s = Base {1:0.000} x Status {2:0.000} x Temp {3:0.000} [Room {4:+0.00;-0.00;0.00}]",
            effectiveRate,
            baseRate,
            statusMultiplier,
            temperatureMultiplier,
            roomHeat);
    }
}
