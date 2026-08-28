using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

/// <summary>
/// Makes quicksand struggle input affect sink rate only.
///
/// The inner capture hook records the Player state produced by native Player.Update
/// before QuicksandSinkRateLimiter applies its post-update support state. The outer
/// hook restores that native state whenever Up or Jump is being used as a struggle,
/// so struggle never locks the player into Stand/Crawl/Default.
///
/// Jump itself is swallowed while the player is already in quicksand. Holding Up or
/// Jump simply changes whole-player descent from the normal sink rate to a slower
/// world-Y descent. No horizontal impulse is generated.
/// </summary>
internal static class QuicksandPlayerStruggleControl
{
    private const float StruggleSinkSpeed = 0.035f;
    private const float Epsilon = 0.000001f;

    private sealed class NativeState
    {
        internal bool HasValue;
        internal bool Standing;
        internal Player.BodyModeIndex BodyMode;
        internal int CanJump;
    }

    private static readonly ConditionalWeakTable<Player, NativeState> NativeStates = new();
    private static bool _captureEnabled;
    private static bool _outerEnabled;

    /// <summary>
    /// Must be enabled before QuicksandSinkRateLimiter so this hook sits inside it
    /// and can see the state produced directly by native Player.Update.
    /// </summary>
    internal static void EnableNativeCapture()
    {
        if (_captureEnabled)
        {
            return;
        }

        _captureEnabled = true;
        On.Player.Update += Player_Update_CaptureNativeState;
    }

    /// <summary>
    /// Must be enabled after the quicksand player support hooks so this hook is the
    /// final owner of struggle displacement/state for the frame.
    /// </summary>
    internal static void Enable()
    {
        if (_outerEnabled)
        {
            return;
        }

        _outerEnabled = true;
        On.Player.Update += Player_Update_StruggleOverride;
        On.Player.Jump += Player_Jump;
    }

    internal static void Disable()
    {
        if (_outerEnabled)
        {
            _outerEnabled = false;
            On.Player.Update -= Player_Update_StruggleOverride;
            On.Player.Jump -= Player_Jump;
        }

        if (_captureEnabled)
        {
            _captureEnabled = false;
            On.Player.Update -= Player_Update_CaptureNativeState;
        }
    }

    private static void Player_Update_CaptureNativeState(
        On.Player.orig_Update orig,
        Player self,
        bool eu)
    {
        orig(self, eu);

        if (self == null)
        {
            return;
        }

        NativeState state = NativeStates.GetValue(self, _ => new NativeState());
        state.Standing = self.standing;
        state.BodyMode = self.bodyMode;
        state.CanJump = self.canJump;
        state.HasValue = true;
    }

    private static void Player_Update_StruggleOverride(
        On.Player.orig_Update orig,
        Player self,
        bool eu)
    {
        if (!CanTrack(self))
        {
            orig(self, eu);
            return;
        }

        bool wasInQuicksand = IsInQuicksand(self);
        bool struggleRequested = HasStruggleInput(self);
        float startAverageY = AverageChunkY(self);

        orig(self, eu);

        if (!wasInQuicksand ||
            !struggleRequested ||
            !CanTrack(self) ||
            !IsInQuicksand(self))
        {
            return;
        }

        // Restore exactly the high-level state native Player.Update produced before
        // the baseline quicksand controller applied its supported-Stand fallback.
        // This means struggle changes motion only; it does not choose a body mode.
        if (NativeStates.TryGetValue(self, out NativeState nativeState) &&
            nativeState.HasValue)
        {
            self.standing = nativeState.Standing;
            self.bodyMode = nativeState.BodyMode;
            self.canJump = nativeState.CanJump;
        }

        // Whatever the native update and baseline sink controller did internally,
        // the final whole-player motion for a struggle frame is still a small,
        // strictly downward world-Y step. Relative body-chunk pose is preserved.
        float rawAverageDisplacement = AverageChunkY(self) - startAverageY;
        float positionCorrectionY = -StruggleSinkSpeed - rawAverageDisplacement;
        TranslatePlayerY(self, positionCorrectionY);

        float velocityCorrectionY = -StruggleSinkSpeed - AverageChunkVelocityY(self);
        AddPlayerVelocityY(self, velocityCorrectionY);

        self.feetStuckPos = null;
    }

    private static void Player_Jump(On.Player.orig_Jump orig, Player self)
    {
        if (IsInQuicksand(self))
        {
            // Jump is only a struggle input in quicksand. Player_Update sees the held
            // input and applies the reduced sink rate; no normal jump state/impulse is
            // allowed to leak into the sand controller.
            return;
        }

        orig(self);
    }

    private static bool HasStruggleInput(Player player)
    {
        return player?.input != null &&
               player.input.Length > 0 &&
               (player.input[0].y > 0 || player.input[0].jmp);
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

    private static bool CanTrack(Player player)
    {
        return player != null &&
               player.room != null &&
               player.bodyChunks != null &&
               player.bodyChunks.Length > 0;
    }

    private static float AverageChunkY(Player player)
    {
        float total = 0f;
        int count = 0;

        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            BodyChunk chunk = player.bodyChunks[i];
            if (chunk == null)
            {
                continue;
            }

            total += chunk.pos.y;
            count++;
        }

        return count > 0 ? total / count : 0f;
    }

    private static float AverageChunkVelocityY(Player player)
    {
        float total = 0f;
        int count = 0;

        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            BodyChunk chunk = player.bodyChunks[i];
            if (chunk == null)
            {
                continue;
            }

            total += chunk.vel.y;
            count++;
        }

        return count > 0 ? total / count : 0f;
    }

    private static void TranslatePlayerY(Player player, float deltaY)
    {
        if (Mathf.Abs(deltaY) <= Epsilon)
        {
            return;
        }

        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            BodyChunk chunk = player.bodyChunks[i];
            if (chunk != null)
            {
                chunk.pos.y += deltaY;
            }
        }
    }

    private static void AddPlayerVelocityY(Player player, float deltaY)
    {
        if (Mathf.Abs(deltaY) <= Epsilon)
        {
            return;
        }

        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            BodyChunk chunk = player.bodyChunks[i];
            if (chunk != null)
            {
                chunk.vel.y += deltaY;
            }
        }
    }
}
