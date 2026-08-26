namespace DryCycle.Thirst;

internal static class ThirstConstants
{
    // The hydration meter is five pips laid out as 3 | 2.
    // The two pips after the divider are the normal hibernation cost.
    public const int MaxPips = 5;
    public const int DividerAfterPip = 3;
    public const float MaxWater = MaxPips;
    public const float HibernateRequirement = 2f;
    public const float HibernateCost = 2f;

    // Rain World runs gameplay at 40 simulation ticks per second.
    // 0.5 water per second = 0.0125 water per Player.Update.
    public const float DrinkPerTick = 0.0125f;

    // V2 starts the five-pip save format. The legacy key is removed on the
    // next save so old four-pip test data cannot cap a new game at four.
    public const string SaveKey = "DRYCYCLETHIRSTV2";
    public const string LegacySaveKey = "DRYCYCLETHIRST";
}
