namespace DryCycle.Thirst;

internal static class ThirstConstants
{
    // Hydration remains a global 0..5 resource. Water is rendered inside the
    // first five vanilla food pips, with each pip showing empty / half / full.
    public const int MaxPips = 5;
    public const float MaxWater = 5f;

    // Internal Water Value (WV) scale.
    // 1 full hydration pip = 400 WV, 1 half pip = 200 WV.
    public const int WaterValuePerPip = 400;
    public const int HalfPipWaterValue = WaterValuePerPip / 2;
    public const int MaxWaterValue = MaxPips * WaterValuePerPip;

    // At or below 200 WV (half of one hydration pip), the player receives the
    // same gameplay weakness state as Rain World's malnourished/starving state.
    public const int WeaknessWaterValueThreshold = HalfPipWaterValue;

    // Rain World gameplay simulation runs at 40 ticks per second. SlugBase's
    // WaterLossRate feature is expressed in WV per second and is converted to
    // pip-space only when DryCycle subtracts the value from runtime hydration.
    public const float SimulationTicksPerSecond = 40f;

    // Drinking remains 0.5 hydration pip per second:
    // 0.0125 pip/tick = 5 WV/tick = 200 WV/second.
    public const float DrinkPerTick = 0.0125f;

    // HUD reveal timings. Drinking refreshes a short hold continuously; one-shot
    // hydration gains and failed hibernation attempts need longer holds so their
    // visual feedback remains visible even when vanilla food did not change.
    public const int UnderwaterHudHoldFrames = 20;
    public const int HydrationGainHudHoldFrames = 60;
    public const int RejectHudHoldFrames = 55;

    // V2 remains the five-unit save format. WV and SlugBase character settings
    // are runtime/configuration layers, so no save migration is required.
    public const string SaveKey = "DRYCYCLETHIRSTV2";
    public const string LegacySaveKey = "DRYCYCLETHIRST";
}
