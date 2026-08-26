namespace DryCycle.Thirst;

internal static class ThirstConstants
{
    public const float MaxWater = 4f;
    public const float HibernateRequirement = 2f;

    // Rain World simulation updates at 40 ticks per second.
    // 0.5 water / second therefore equals 0.5 / 40 each Player.Update.
    public const float DrinkPerTick = 0.0125f;

    public const string SaveKey = "DRYCYCLETHIRST";
}
