using RWCustom;
using UnityEngine;

namespace DryCycle.Items.ScavengerLance;

internal sealed partial class ScavengerLance
{
    internal const float PlayerLevelSpeed = 4.5f;

    internal Vector2 PlayerCarryDirection(Player player, Vector2 handDirection)
    {
        if (_thrustFrames > 0) return _thrustDirection;
        if (!player.Consious || (player.bodyMode != Player.BodyModeIndex.Stand &&
            player.bodyMode != Player.BodyModeIndex.Default && player.bodyMode != Player.BodyModeIndex.Crawl))
            return handDirection;
        float speed = (player.mainBodyChunk.vel.x + player.bodyChunks[1].vel.x) * 0.5f;
        float level = Mathf.InverseLerp(3f, PlayerLevelSpeed, Mathf.Abs(speed));
        if (level == 0f) return handDirection;
        // Match actual movement, not left/right input or ThrowDirection. Passing
        // through zero speed restores the normal hand pose before changing sides.
        return Custom.DegToVec(Mathf.LerpAngle(Custom.VecToDeg(handDirection), speed > 0f ? 90f : -90f, level));
    }

    internal void SynchronizePlayerThrust(bool eu)
    {
        if (_thrustFrames <= 0 || Holder is not Player) return;
        rotation = _thrustDirection;
        setRotation = rotation;
        // The vanilla graphics callback just placed this chunk in the carrying
        // hand. Extend from that position without changing either of the hands.
        firstChunk.MoveFromOutsideMyUpdate(eu, firstChunk.pos + rotation *
            (Mathf.Sin((12 - _thrustFrames) / 12f * Mathf.PI) * 17f));
    }
}
