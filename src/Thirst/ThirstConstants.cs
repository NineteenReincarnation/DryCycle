namespace DryCycle.Thirst;

internal static class ThirstConstants
{
    public const float MaxWater = 4f;
    public const float HibernateRequirement = 2f;

    // Rain World runs gameplay at 40 simulation ticks per second.
    // 0.5 water per second = 0.0125 water per Player.Update.
    public const float DrinkPerTick = 0.0125f;

    public const string SaveKey = "DRYCYCLETHIRST";
}
