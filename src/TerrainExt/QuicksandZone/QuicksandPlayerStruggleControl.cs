using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

/// <summary>
/// Temporarily replaces the old player struggle input with one reduced quicksand jump.
///
/// Up no longer changes the sink rate. Jump is allowed only while the player is still
/// shallowly immersed, and only once during the same quicksand contact. The baseline
/// sink controller still executes the native Player.Jump path and strips its horizontal
/// impulse; this outer hook then replaces only the vertical launch with a small jump.
/// The one-jump lock resets only after the player has remained outside quicksand for a
/// short time, preventing repeated hopping across the surface.
/// </summary>
internal static class QuicksandPlayerStruggleControl
{
    private const float ShallowJumpMaxImmersion = 0.20f;
    private const float UpperChunkJumpSpeed = 2.00f;
    private const float LowerChunkJumpSpeed = 1.70f;
    private const float ExtraChunkJumpSpeed = 1.85f;
    private const int ClearTicksToRearm = 12;

    private sealed class JumpState
    {
        internal bool LowJumpUsed;
        internal int ClearTicks;
    }

    private static readonly ConditionalWeakTable<Player, JumpState> JumpStates = new();
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
    /// legacy 1.15 Y struggle impulse with the reduced shallow jump profile.
    /// </summary>
    internal static void Enable()
    {
        if (_outerEnabled)
        {
            return;
        }

        _outerEnabled = true;
        On.Player.Update += Player_Update;
        On.Player.Jump += Player_Jump;
    }

    internal static void Disable()
    {
        if (!_outerEnabled)
        {
            return;
        }

        _outerEnabled = false;
        On.Player.Update -= Player_Update;
        On.Player.Jump -= Player_Jump;
    }

    private static void Player_Update(
        On.Player.orig_Update orig,
        Player self,
        bool eu)
    {
        orig(self, eu);

        if (self == null)
        {
            return;
        }

        JumpState state = JumpStates.GetValue(self, _ => new JumpState());
        if (TryGetQuicksandState(self, out _))
        {
            state.ClearTicks = 0;
            return;
        }

        if (!state.LowJumpUsed)
        {
            state.ClearTicks = 0;
            return;
        }

        state.ClearTicks++;
        if (state.ClearTicks >= ClearTicksToRearm)
        {
            state.LowJumpUsed = false;
            state.ClearTicks = 0;
        }
    }

    private static void Player_Jump(On.Player.orig_Jump orig, Player self)
    {
        if (!TryGetQuicksandState(self, out float immersion))
        {
            orig(self);
            return;
        }

        JumpState state = JumpStates.GetValue(self, _ => new JumpState());
        if (state.LowJumpUsed || immersion > ShallowJumpMaxImmersion)
        {
            // No struggle fallback for now: deeper/repeated jump presses simply do
            // nothing until the player genuinely leaves this quicksand contact.
            return;
        }

        state.LowJumpUsed = true;
        state.ClearTicks = 0;

        // Let the normal Player.Jump chain run first. QuicksandSinkRateLimiter keeps
        // the pre-jump X velocity and removes jumpBoost; only Y is replaced below.
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
        self.jumpBoost = 0f;
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
