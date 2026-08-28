using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

/// <summary>
/// Restores normal slugcat walking/body animation while Scheme-D quicksand keeps
/// BodyChunk.ContactPoint free of fake hard-ground collision during player physics.
///
/// Player.Update resets bodyMode from real terrain contact before calling
/// UpdateAnimation/UpdateBodyMode. These hooks restore only the high-level Stand
/// semantics at the exact moments the native locomotion code needs them, so the
/// native run cycle, animationFrame progression and body bobbing still execute.
///
/// PlayerGraphics normally anchors the legs only when the lower BodyChunk reports a
/// floor contact. A temporary visual-only floor contact is supplied exclusively
/// during PlayerGraphics.Update and restored immediately afterwards. Player physics
/// never sees it, so feetStuckPos cannot be created from quicksand.
/// </summary>
internal static class QuicksandPlayerLocomotionSupport
{
    private const float UpwardMotionThreshold = 0.015f;
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.Player.UpdateAnimation += Player_UpdateAnimation;
        On.Player.UpdateBodyMode += Player_UpdateBodyMode;
        On.PlayerGraphics.Update += PlayerGraphics_Update;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;
        On.Player.UpdateAnimation -= Player_UpdateAnimation;
        On.Player.UpdateBodyMode -= Player_UpdateBodyMode;
        On.PlayerGraphics.Update -= PlayerGraphics_Update;
    }

    private static void Player_UpdateAnimation(
        On.Player.orig_UpdateAnimation orig,
        Player self)
    {
        PrepareNativeStandingLocomotion(self);
        orig(self);
        ClearHardGroundState(self);
    }

    private static void Player_UpdateBodyMode(
        On.Player.orig_UpdateBodyMode orig,
        Player self)
    {
        PrepareNativeStandingLocomotion(self);
        orig(self);
        ClearHardGroundState(self);
    }

    private static void PlayerGraphics_Update(
        On.PlayerGraphics.orig_Update orig,
        PlayerGraphics self)
    {
        Player player = self?.player;
        if (!ShouldProvideVisualFootSupport(player) ||
            player.bodyChunks == null ||
            player.bodyChunks.Length < 2 ||
            player.bodyChunks[1] == null)
        {
            orig(self);
            return;
        }

        BodyChunk lowerBody = player.bodyChunks[1];
        var originalContactPoint = lowerBody.contactPoint;

        // Visual-only support. This exists only while PlayerGraphics.Update runs,
        // allowing its native grounded-leg branch to anchor and animate the feet.
        lowerBody.contactPoint.y = -1;

        try
        {
            orig(self);
        }
        finally
        {
            lowerBody.contactPoint = originalContactPoint;
            player.feetStuckPos = null;
        }
    }

    private static void PrepareNativeStandingLocomotion(Player player)
    {
        if (!ShouldUseNativeStandMode(player))
        {
            return;
        }

        // Set the state immediately before the native locomotion methods execute.
        // Unlike the old fake ContactPoint approach, this does not claim that a solid
        // tile exists below the player.
        player.feetStuckPos = null;
        player.standing = true;
        player.bodyMode = Player.BodyModeIndex.Stand;
        player.canJump = Mathf.Max(player.canJump, 2);
    }

    private static void ClearHardGroundState(Player player)
    {
        if (IsInQuicksand(player))
        {
            player.feetStuckPos = null;
        }
    }

    private static bool ShouldUseNativeStandMode(Player player)
    {
        if (!IsInQuicksand(player) ||
            player.dead ||
            !player.Consious ||
            IsMovingUp(player) ||
            player.animation != Player.AnimationIndex.None)
        {
            return false;
        }

        return player.bodyMode == Player.BodyModeIndex.Default ||
               player.bodyMode == Player.BodyModeIndex.Stand ||
               player.bodyMode == Player.BodyModeIndex.Crawl;
    }

    private static bool ShouldProvideVisualFootSupport(Player player)
    {
        return IsInQuicksand(player) &&
               !player.dead &&
               player.Consious &&
               !IsMovingUp(player) &&
               player.animation == Player.AnimationIndex.None &&
               player.bodyMode == Player.BodyModeIndex.Stand;
    }

    private static bool IsInQuicksand(Player player)
    {
        return player != null &&
               QuicksandSinkRateLimiter.TryGetVisualSink(
                   player,
                   out _,
                   out _,
                   out float immersion) &&
               immersion > 0.005f;
    }

    private static bool IsMovingUp(Player player)
    {
        if (player?.bodyChunks == null || player.bodyChunks.Length == 0)
        {
            return false;
        }

        float totalVelocityY = 0f;
        int count = 0;

        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            BodyChunk chunk = player.bodyChunks[i];
            if (chunk == null)
            {
                continue;
            }

            totalVelocityY += chunk.vel.y;
            count++;
        }

        return count > 0 &&
               totalVelocityY / count > UpwardMotionThreshold;
    }
}
