namespace DryCycle.Thirst;

internal static class ThirstConstants
{
    // The hydration meter follows the vanilla food-meter convention:
    // five pips laid out as 3 | 2, with the three pips on the left being
    // the amount consumed by a normal hibernation.
    public const int MaxPips = 5;
    public const int DividerAfterPip = 3;
    public const float MaxWater = MaxPips;
    public const float HibernateRequirement = 3f;
    public const float HibernateCost = 3f;

    // Rain World runs gameplay at 40 simulation ticks per second.
    // 0.5 water per second = 0.0125 water per Player.Update.
    public const float DrinkPerTick = 0.0125f;

    // V2 is the five-pip save format. The legacy key is removed on the
    // next save so old four-pip test data cannot cap a new game at four.
    public const string SaveKey = "DRYCYCLETHIRSTV2";
    public const string LegacySaveKey = "DRYCYCLETHIRST";
}
