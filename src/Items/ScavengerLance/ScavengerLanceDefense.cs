using UnityEngine;

namespace DryCycle.Items.ScavengerLance;

internal sealed partial class ScavengerLance
{
    private bool _defensiveLiftActive;
    private int _defensiveLiftTotalFrames;
    private Vector2 _defensiveLiftStart = Vector2.right;
    private Vector2 _defensiveLiftEnd = Vector2.right;

    /// <summary>
    /// Close-defense attacks use the same real thrust/swept-blade collision path as every other
    /// lance hit, but with a shorter action and recovery than the generic 44-tick thrust. This keeps
    /// the defensive response physical without changing ordinary player/AI thrust timing.
    /// </summary>
    internal void RequestDefensiveThrust(Vector2 direction, float maxDamage, int frames, int cooldown)
    {
        Creature holder = Holder;
        if (_thrustCooldown > 0 || holder == null || !holder.Consious) return;

        ClearDefensiveLift();
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

    /// <summary>
    /// Starts a short upward/outward pick. The existing thrust frames keep the weapon collision live,
    /// while UpdateDefensiveLiftPose rotates the real lance through the authored arc. Because the
    /// normal SweepBlade path sees the changing direction, this is an actual sweeping attack rather
    /// than a cosmetic animation or a separate fake hit box.
    /// </summary>
    internal void RequestDefensiveLift(Vector2 startDirection, Vector2 endDirection,
        float maxDamage, int frames, int cooldown)
    {
        Creature holder = Holder;
        if (_thrustCooldown > 0 || holder == null || !holder.Consious) return;

        Vector2 fallback = rotation.sqrMagnitude > 0.01f ? rotation.normalized : Vector2.right;
        _defensiveLiftStart = startDirection.sqrMagnitude > 0.01f ? startDirection.normalized : fallback;
        _defensiveLiftEnd = endDirection.sqrMagnitude > 0.01f ? endDirection.normalized : _defensiveLiftStart;
        _defensiveLiftTotalFrames = Mathf.Clamp(frames, 6, 12);
        _defensiveLiftActive = true;

        _thrustDirection = _defensiveLiftStart;
        _thrustFrames = _defensiveLiftTotalFrames;
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

    /// <summary>
    /// Called by the close-defense motor while the attack is active. ScavengerLance.Update owns the
    /// frame countdown; this method only derives the current direction from that countdown, keeping
    /// the weapon's existing thrust/collision lifecycle intact.
    /// </summary>
    internal void UpdateDefensiveLiftPose()
    {
        if (!_defensiveLiftActive) return;
        if (_thrustFrames <= 0 || _defensiveLiftTotalFrames <= 0)
        {
            ClearDefensiveLift();
            return;
        }

        int elapsed = Mathf.Clamp(_defensiveLiftTotalFrames - _thrustFrames + 1, 0, _defensiveLiftTotalFrames);
        float t = Mathf.Clamp01((float)elapsed / _defensiveLiftTotalFrames);
        float eased = t * t * (3f - 2f * t);
        float startAngle = Mathf.Atan2(_defensiveLiftStart.y, _defensiveLiftStart.x) * Mathf.Rad2Deg;
        float endAngle = Mathf.Atan2(_defensiveLiftEnd.y, _defensiveLiftEnd.x) * Mathf.Rad2Deg;
        float angle = Mathf.LerpAngle(startAngle, endAngle, eased);
        float radians = angle * Mathf.Deg2Rad;
        _thrustDirection = new Vector2(Mathf.Cos(radians), Mathf.Sin(radians));
    }

    private void ClearDefensiveLift()
    {
        _defensiveLiftActive = false;
        _defensiveLiftTotalFrames = 0;
        _defensiveLiftStart = Vector2.right;
        _defensiveLiftEnd = Vector2.right;
    }
}
