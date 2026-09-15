using UnityEngine;

namespace DryCycle.Items.ScavengerLance;

internal sealed partial class ScavengerLance
{
    /// <summary>
    /// Close-defense attacks use the same real thrust/collision path as every other lance hit, but
    /// with a shorter action and recovery than the generic 44-tick thrust. This keeps the defensive
    /// response physical without turning ordinary player/AI thrust timing into a global special case.
    /// </summary>
    internal void RequestDefensiveThrust(Vector2 direction, float maxDamage, int frames, int cooldown)
    {
        Creature holder = Holder;
        if (_thrustCooldown > 0 || holder == null || !holder.Consious) return;

        _thrustDirection = direction.sqrMagnitude > 0.01f ? direction.normalized : rotation;
        _thrustFrames = Mathf.Clamp(frames, 6, 12);
        _thrustCooldown = Mathf.Max(_thrustFrames + 4, cooldown);
        _thrustMaxDamage = Mathf.Max(0.12f, maxDamage);
        _hitCreatures.Clear();

        rotation = _thrustDirection;
        setRotation = rotation;
        _previousTip = Tip;
        _previousGrip = firstChunk.pos;
        _havePreviousPose = true;
        ResetCarryRig();
    }
}
