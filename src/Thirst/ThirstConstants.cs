namespace DryCycle.Thirst;

internal static class ThirstConstants
{
    // Hydration is a global 0..5 resource. Water is rendered inside the first
    // five vanilla food pips, with each pip showing empty / half / full water.
    public const int MaxPips = 5;
    public const float MaxWater = 5f;

    // Normal hibernation requires and consumes 3 hydration.
    public const float HibernateRequirement = 3f;
    public const float HibernateCost = 3f;

    // Rain World runs gameplay at 40 simulation ticks per second.
    // 0.5 water per second = 0.0125 water per Player.Update.
    public const float DrinkPerTick = 0.0125f;

    // While actively drinking underwater, keep the vanilla lower-left HUD
    // reveal trigger alive. When drinking stops, the remaining countdown lets
    // the karma / food / rain-meter cluster fade away with vanilla timing.
    public const int UnderwaterHudHoldFrames = 20;

    // V2 remains the five-unit save format. UI-only changes do not alter save
    // semantics, so earlier five-unit hydration saves remain compatible.
    public const string SaveKey = "DRYCYCLETHIRSTV2";
    public const string LegacySaveKey = "DRYCYCLETHIRST";
}
