namespace DryCycle.Thirst;

internal static class ThirstConstants
{
    // Legacy renderer bounds kept only so older code paths do not impose a
    // five-pip cap. Real capacity is always resolved from the character's food
    // meter through ThirstStore.GetMaxWaterPips(...).
    public const int MaxPips = int.MaxValue;
    public const float MaxWater = float.MaxValue;

    public const int WaterValuePerPip = 400;
    public const int HalfPipWaterValue = WaterValuePerPip / 2;
    public const int WeaknessWaterValueThreshold = HalfPipWaterValue;

    public const float SimulationTicksPerSecond = 40f;
    public const float DrinkPerTick = 0.0125f;

    public const int UnderwaterHudHoldFrames = 20;
    public const int HydrationGainHudHoldFrames = 60;
    public const int RejectHudHoldFrames = 55;

    public const string SaveKey = "DRYCYCLETHIRSTV2";
    public const string LegacySaveKey = "DRYCYCLETHIRST";
}
