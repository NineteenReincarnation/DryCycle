using RWCustom;
using UnityEngine;

namespace DryCycle.Items.ScavengerLance;

internal sealed partial class ScavengerLance
{
    internal const float PlayerLevelSpeed = 4.5f;

    internal bool RequestPlayerAttack(Vector2 direction, float maxDamage, int frames, int cooldown)
    {
        if (_thrustCooldown > 0 || Holder is not Player player || !player.Consious) return false;

        _thrustDirection = direction.sqrMagnitude > 0.01f ? direction.normalized : rotation;
        _thrustFrames = Mathf.Clamp(frames, 6, 14);
        _thrustCooldown = Mathf.Max(_thrustFrames + 4, cooldown);
        _thrustMaxDamage = Mathf.Max(0.12f, maxDamage);
        _hitCreatures.Clear();
        rotation = _thrustDirection;
        setRotation = rotation;
        _previousTip = Tip;
        _previousGrip = firstChunk.pos;
        _havePreviousPose = true;
        ResetCarryRig();
        return true;
    }

    internal void TrackPlayerAttackDirection(Vector2 direction)
    {
        if (_thrustFrames <= 0 || Holder is not Player || direction.sqrMagnitude < 0.001f) return;
        _thrustDirection = direction.normalized;
        rotation = _thrustDirection;
        setRotation = rotation;
    }

    internal Vector2 PlayerCarryDirection(Player player, Vector2 handDirection)
    {
        if (ScavengerLancePlayerController.TryGetPose(player, this, out PlayerLancePose pose))
            return pose.Direction;
        if (_thrustFrames > 0) return _thrustDirection;
        if (!player.Consious || (player.bodyMode != Player.BodyModeIndex.Stand &&
            player.bodyMode != Player.BodyModeIndex.Default && player.bodyMode != Player.BodyModeIndex.Crawl))
            return handDirection;
        float speed = (player.mainBodyChunk.vel.x + player.bodyChunks[1].vel.x) * 0.5f;
        float level = Mathf.InverseLerp(3f, PlayerLevelSpeed, Mathf.Abs(speed));
        if (level == 0f) return handDirection;
        // Normal carry still follows Rain World's hand animation. At running speed the long weapon
        // gradually levels with actual motion, then naturally returns to the hand pose through zero.
        return Custom.DegToVec(Mathf.LerpAngle(Custom.VecToDeg(handDirection), speed > 0f ? 90f : -90f, level));
    }

    internal void SynchronizePlayerPose(Player player, int hand, bool eu)
    {
        if (player == null || Holder != player || hand < 0 || hand >= player.grasps.Length ||
            player.grasps[hand]?.grabbed != this)
            return;

        bool hasPose = ScavengerLancePlayerController.TryGetPose(player, this, out PlayerLancePose pose);
        if (!hasPose && _thrustFrames <= 0) return;

        Vector2 direction = hasPose
            ? pose.Direction
            : _thrustDirection.sqrMagnitude > 0.001f ? _thrustDirection.normalized : rotation;
        Vector2 handOffset = hasPose
            ? pose.PrimaryHandOffset
            : direction * (Mathf.Sin((12 - Mathf.Min(12, _thrustFrames)) / 12f * Mathf.PI) * 17f);

        PlayerGraphics graphics = player.graphicsModule as PlayerGraphics;
        Vector2 anchor = firstChunk.pos + handOffset;
        Vector2 anchorVelocity = player.mainBodyChunk.vel;

        if (graphics != null && graphics.hands != null && hand < graphics.hands.Length)
        {
            anchor = graphics.hands[hand].pos + handOffset;
            graphics.hands[hand].pos = anchor;
            graphics.hands[hand].vel += handOffset * 0.14f;
            anchorVelocity = graphics.hands[hand].vel;

            if (hasPose && pose.UseSupportHand)
            {
                int supportHand = hand == 0 ? 1 : 0;
                if (supportHand >= 0 && supportHand < graphics.hands.Length &&
                    supportHand < player.grasps.Length && player.grasps[supportHand] == null)
                {
                    // The support hand does not teleport into a binary two-hand pose. The controller
                    // grows SupportHandDistance/SupportBlend across the brace, so the second hand
                    // visibly slides along the shaft while the stance firms up.
                    Vector2 supportTarget = anchor + direction * pose.SupportHandDistance + Vector2.down * 0.75f;
                    Vector2 before = graphics.hands[supportHand].pos;
                    graphics.hands[supportHand].pos = Vector2.Lerp(before, supportTarget, pose.SupportBlend);
                    graphics.hands[supportHand].vel += (supportTarget - before) * 0.22f;
                }
            }
        }

        if (hasPose)
        {
            ApplyPlayerBodyPose(player, graphics, direction, pose);
            ApplyPlayerWeaponTension(direction, pose.WeaponTension);
        }

        rotation = direction;
        setRotation = direction;
        rotationSpeed = 0f;
        firstChunk.MoveFromOutsideMyUpdate(eu, anchor);
        firstChunk.vel = anchorVelocity;
    }

    private static void ApplyPlayerBodyPose(Player player, PlayerGraphics graphics,
        Vector2 direction, PlayerLancePose pose)
    {
        if (graphics == null) return;

        float face = Mathf.Abs(direction.x) > 0.12f ? Mathf.Sign(direction.x) : player.flipDirection;
        if (face == 0f) face = 1f;
        Vector2 horizontal = new(face, 0f);

        // Keep this visual-only. The actual player physics remain in Player.Update; draw positions are
        // displaced after vanilla has produced its animation so bracing reads as a compressed,
        // staggered stance without replacing Rain World's locomotion state machine.
        if (graphics.drawPositions != null && graphics.drawPositions.GetLength(0) >= 2)
        {
            graphics.drawPositions[0, 0] += horizontal * (pose.BodyLean * 0.55f) +
                Vector2.down * pose.BodyCompression;
            graphics.drawPositions[1, 0] += -horizontal * (pose.BodyLean * 0.58f) +
                Vector2.down * (pose.BodyCompression * 0.76f);
        }

        float stance = Mathf.Clamp01((pose.BodyCompression + pose.BodyLean) / 7f);
        if (stance > 0f)
        {
            Vector2 legTarget = new(-face * 0.42f, -1f);
            graphics.legsDirection = Vector2.Lerp(graphics.legsDirection,
                legTarget.normalized, stance * 0.42f);
        }

        if (pose.HeadAim > 0f)
        {
            Vector2 look = Vector2.Lerp(graphics.lookDirection, direction, pose.HeadAim);
            if (look.sqrMagnitude > 0.001f)
                graphics.lookDirection = look.normalized;
            if (graphics.head != null)
            {
                graphics.head.vel += direction * (0.22f * pose.HeadAim);
                graphics.head.vel += Vector2.down * (pose.BodyCompression * 0.035f);
            }
        }
    }

    private void ApplyPlayerWeaponTension(Vector2 direction, float tension)
    {
        if (Holder is not Player) return;
        float face = Mathf.Abs(direction.x) > 0.12f ? Mathf.Sign(direction.x) : 1f;

        // The existing lance mesh already supports a small handle bend. During the brace, pull it
        // gently into tension; the attack pose then drives tension back toward zero over the first
        // few frames, making the shaft visibly settle/straighten as stored force is released.
        float targetBend = -face * Mathf.Clamp01(tension) * 1.55f;
        _bend = Mathf.Lerp(_bend, targetBend, 0.32f);
        _bendVelocity *= 0.58f;
    }
}
