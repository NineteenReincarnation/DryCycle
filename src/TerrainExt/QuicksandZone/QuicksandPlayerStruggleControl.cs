using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

/// <summary>
/// Replaces the old player struggle input with a repeatable reduced quicksand jump.
///
/// Up no longer changes the sink rate. Every native jump opportunity remains usable
/// while the player is in quicksand; there is no per-contact one-jump lock. The
/// baseline sink controller still executes the normal Player.Jump chain and strips
/// its horizontal jump impulse, while this outer hook applies a small vertical launch
/// and a hold-jump boost that falls linearly from 3.25 at zero immersion to 0 at full
/// immersion.
/// </summary>
internal static class QuicksandPlayerStruggleControl
{
    private const float UpperChunkJumpSpeed = 2.00f;
    private const float LowerChunkJumpSpeed = 1.70f;
    private const float ExtraChunkJumpSpeed = 1.85f;
    private const float MaximumJumpBoost = 3.25f;

    private static bool _outerEnabled;

    /// <summary>
    /// Kept for Plugin hook-order compatibility. The reduced-jump controller no longer
    /// needs an inner native-state capture hook.
    /// </summary>
    internal static void EnableNativeCapture()
    {
    }

    /// <summary>
    /// Installed after QuicksandSinkRateLimiter so this hook can replace the limiter's
    /// legacy 1.15 Y struggle impulse with the reduced jump profile.
    /// </summary>
    internal static void Enable()
    {
        if (_outerEnabled)
        {
            return;
        }

        _outerEnabled = true;
        On.Player.Jump += Player_Jump;
    }

    internal static void Disable()
    {
        if (!_outerEnabled)
        {
            return;
        }

        _outerEnabled = false;
        On.Player.Jump -= Player_Jump;
    }

    private static void Player_Jump(On.Player.orig_Jump orig, Player self)
    {
        if (!TryGetQuicksandState(self, out float immersion))
        {
            orig(self);
            return;
        }

        // Let the normal Player.Jump chain run first. QuicksandSinkRateLimiter keeps
        // the pre-jump X velocity; only the reduced Y launch and held-jump boost are
        // authored here.
        orig(self);

        if (self?.bodyChunks == null || self.bodyChunks.Length == 0)
        {
            return;
        }

        for (int i = 0; i < self.bodyChunks.Length; i++)
        {
            BodyChunk chunk = self.bodyChunks[i];
            if (chunk == null)
            {
                continue;
            }

            chunk.vel.y = i switch
            {
                0 => UpperChunkJumpSpeed,
                1 => LowerChunkJumpSpeed,
                _ => ExtraChunkJumpSpeed
            };
        }

        self.feetStuckPos = null;
        self.standing = false;
        self.jumpBoost = Mathf.Lerp(
            MaximumJumpBoost,
            0f,
            Mathf.Clamp01(immersion));
        self.canJump = 0;
    }

    private static bool TryGetQuicksandState(Player player, out float immersion)
    {
        immersion = 0f;
        return player != null &&
               QuicksandSinkRateLimiter.TryGetPlayerQuicksandState(
                   player,
                   out _,
                   out immersion);
    }
}
