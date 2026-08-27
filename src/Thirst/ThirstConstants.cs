namespace DryCycle.Thirst;

internal static class ThirstConstants
{
    // Hydration is a global 0..5 resource. Water is rendered inside the first
    // five vanilla food pips, with each pip showing empty / half / full water.
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

    // The cyan divider is drawn after this many hydration pips. The amount to
    // the left of the divider is the normal hibernation requirement and cost.
    // Current layout: 1 pip | 4 pips, therefore normal sleep requires/consumes 1.
    public const int HydrationSleepDividerAfterPip = 1;
    public const float HibernateRequirement = HydrationSleepDividerAfterPip;
    public const float HibernateCost = HydrationSleepDividerAfterPip;

    // Rain World runs gameplay at 40 simulation ticks per second.
    // 0.5 water per second = 0.0125 pip per tick = 5 WV per tick = 200 WV/sec.
    public const float DrinkPerTick = 0.0125f;

    // HUD reveal timings. Drinking refreshes a short hold continuously; one-shot
    // hydration gains and failed hibernation attempts need longer holds so their
    // visual feedback remains visible even when vanilla food did not change.
    public const int UnderwaterHudHoldFrames = 20;
    public const int HydrationGainHudHoldFrames = 60;
    public const int RejectHudHoldFrames = 55;

    // V2 remains the five-unit save format. WV is an internal conversion layer,
    // so existing hydration saves remain compatible without a save migration.
    public const string SaveKey = "DRYCYCLETHIRSTV2";
    public const string LegacySaveKey = "DRYCYCLETHIRST";
}
