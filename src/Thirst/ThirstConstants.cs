namespace DryCycle.Thirst;

internal static class ThirstConstants
{
    // Hydration is a global 0..5 resource. The HUD no longer draws five
    // separate hydration pips; this value is rendered as a liquid level inside
    // every vanilla food circle instead.
    public const float MaxWater = 5f;

    // Normal hibernation requires and consumes 3 hydration.
    public const float HibernateRequirement = 3f;
    public const float HibernateCost = 3f;

    // Rain World runs gameplay at 40 simulation ticks per second.
    // 0.5 water per second = 0.0125 water per Player.Update.
    public const float DrinkPerTick = 0.0125f;

    // V2 remains the five-unit save format. This UI redesign does not change
    // save semantics, so existing 0.2.5/0.2.6 hydration values stay valid.
    public const string SaveKey = "DRYCYCLETHIRSTV2";
    public const string LegacySaveKey = "DRYCYCLETHIRST";
}
