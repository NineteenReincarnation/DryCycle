using UnityEngine;

namespace DryCycle.Items.ScavengerLance;

/// <summary>The weapon knows a grip and impact recipient, never a creature AI state machine.</summary>
internal interface ILanceWielder
{
    bool TryGetLanceGrip(ScavengerLance lance, out LanceGrip grip);
    void LanceImpact(bool wall, float speed, float retainedSpeed);
}

internal readonly struct LanceGrip
{
    internal LanceGrip(Vector2 position, Vector2 direction, bool braced, bool charging, float runUp,
        bool counterSweep = false, bool aimTracking = false)
    {
        Position = position;
        Direction = direction;
        Braced = braced;
        Charging = charging;
        RunUp = runUp;
        CounterSweep = counterSweep;
        AimTracking = aimTracking;
    }

    internal Vector2 Position { get; }
    internal Vector2 Direction { get; }
    internal bool Braced { get; }
    internal bool Charging { get; }
    internal float RunUp { get; }
    /// <summary>
    /// True only during the one-shot evasive counter-sweep inside an airborne charge.
    /// This is still the same charge attack, not a separate thrust.
    /// </summary>
    internal bool CounterSweep { get; }
    /// <summary>Visible pre-launch aim should turn gradually rather than snap to each solver refresh.</summary>
    internal bool AimTracking { get; }
}
