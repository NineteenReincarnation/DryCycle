using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

/// <summary>
/// Makes Up affect quicksand sink rate without replacing the player's normal jump.
///
/// The inner update capture records the Player state produced by native Player.Update
/// before QuicksandSinkRateLimiter applies its post-update support state. The outer
/// update hook restores that native state while Up is being used as a struggle, so
/// Up changes sink rate only and never locks the player into Stand/Crawl/Default.
///
/// Jump is deliberately different: it remains Rain World's normal jump. A second
/// inner capture hook records the native Player.Jump result before the legacy sink
/// hook can replace its velocity; the outer Jump hook restores that native result so
/// both jump height and horizontal jump impulse remain intact in quicksand.
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

    private sealed class NativeJumpState
    {
        internal bool HasValue;
        internal Vector2[] Velocities;
        internal bool Standing;
        internal int CanJump;
        internal float JumpBoost;
    }

    private static readonly ConditionalWeakTable<Player, NativeState> NativeStates = new();
    private static readonly ConditionalWeakTable<Player, NativeJumpState> NativeJumpStates = new();
    private static bool _captureEnabled;
    private static bool _outerEnabled;

    /// <summary>
    /// Must be enabled before QuicksandSinkRateLimiter so these hooks sit inside it
    /// and can see the state produced directly by native Player.Update / Player.Jump.
    /// </summary>
    internal static void EnableNativeCapture()
    {
        if (_captureEnabled)
        {
            return;
        }

        _captureEnabled = true;
        On.Player.Update += Player_Update_CaptureNativeState;
        On.Player.Jump += Player_Jump_CaptureNativeState;
    }

    /// <summary>
    /// Must be enabled after the quicksand player support hooks so these hooks are
    /// the final owners of Up-struggle displacement and restored native jump state.
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
            On.Player.Jump -= Player_Jump_CaptureNativeState;
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

    private static void Player_Jump_CaptureNativeState(
        On.Player.orig_Jump orig,
        Player self)
    {
        orig(self);

        if (self == null || self.bodyChunks == null)
        {
            return;
        }

        NativeJumpState state = NativeJumpStates.GetValue(
            self,
            _ => new NativeJumpState());

        if (state.Velocities == null || state.Velocities.Length != self.bodyChunks.Length)
        {
            state.Velocities = new Vector2[self.bodyChunks.Length];
        }

        for (int i = 0; i < self.bodyChunks.Length; i++)
        {
            BodyChunk chunk = self.bodyChunks[i];
            state.Velocities[i] = chunk != null ? chunk.vel : Vector2.zero;
        }

        state.Standing = self.standing;
        state.CanJump = self.canJump;
        state.JumpBoost = self.jumpBoost;
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
        // This means Up changes motion only; it does not choose a body mode.
        if (NativeStates.TryGetValue(self, out NativeState nativeState) &&
            nativeState.HasValue)
        {
            self.standing = nativeState.Standing;
            self.bodyMode = nativeState.BodyMode;
            self.canJump = nativeState.CanJump;
        }

        // Whatever the native update and baseline sink controller did internally,
        // the final whole-player motion for an Up-struggle frame is still a small,
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
        bool inQuicksand = IsInQuicksand(self);
        NativeJumpState nativeJumpState = self != null
            ? NativeJumpStates.GetValue(self, _ => new NativeJumpState())
            : null;

        if (nativeJumpState != null)
        {
            nativeJumpState.HasValue = false;
        }

        orig(self);

        if (!inQuicksand ||
            self == null ||
            self.bodyChunks == null ||
            nativeJumpState == null ||
            !nativeJumpState.HasValue ||
            nativeJumpState.Velocities == null)
        {
            return;
        }

        int count = Mathf.Min(self.bodyChunks.Length, nativeJumpState.Velocities.Length);
        for (int i = 0; i < count; i++)
        {
            BodyChunk chunk = self.bodyChunks[i];
            if (chunk != null)
            {
                // Restore the complete native jump impulse. In particular, do not
                // shorten horizontal distance and do not replace normal jump height
                // with the old fixed quicksand struggle velocity.
                chunk.vel = nativeJumpState.Velocities[i];
            }
        }

        self.standing = nativeJumpState.Standing;
        self.canJump = nativeJumpState.CanJump;
        self.jumpBoost = nativeJumpState.JumpBoost;
        self.feetStuckPos = null;
    }

    private static bool HasStruggleInput(Player player)
    {
        // Up remains the deliberate slow-sink struggle. Jump is now fully native.
        return player?.input != null &&
               player.input.Length > 0 &&
               player.input[0].y > 0;
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
