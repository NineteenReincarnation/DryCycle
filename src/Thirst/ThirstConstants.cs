namespace DryCycle.Thirst;

internal static class ThirstConstants
{
    public const int WaterValuePerPip = 400;

    public const float SimulationTicksPerSecond = 40f;
    public const float DrinkPerTick = 0.0125f;

    public const int UnderwaterHudHoldFrames = 20;
    public const int HydrationGainHudHoldFrames = 60;
    public const int HydrationLossHudHoldFrames = 60;
    public const int RejectHudHoldFrames = 55;

    public const string SaveKey = "DRYCYCLETHIRSTV2";
    public const string LegacySaveKey = "DRYCYCLETHIRST";
}
