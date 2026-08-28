using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

/// <summary>
/// Restores the horizontal support semantics that are lost when quicksand disables
/// hard terrain collision.
///
/// Rain World's ordinary ground braking only runs while a BodyChunk has a real
/// floor ContactPoint. Scheme-D intentionally keeps ContactPoint at zero so
/// feetStuckPos and other hard-ground behaviour cannot activate. The consequence is
/// that pre-existing horizontal momentum is otherwise left to behave like air
/// movement and the player visibly drifts across the quicksand.
///
/// This hook removes only whole-player passive X translation while there is no
/// horizontal input. Relative X motion between the two body chunks is preserved, so
/// body posture remains native. Active left/right input is never modified.
/// </summary>
internal static class QuicksandPlayerHorizontalStability
{
    private const float Epsilon = 0.000001f;
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.Player.Update += Player_Update;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;
        On.Player.Update -= Player_Update;
    }

    private static void Player_Update(On.Player.orig_Update orig, Player self, bool eu)
    {
        if (!CanTrack(self))
        {
            orig(self, eu);
            return;
        }

        float startAverageX = AverageChunkX(self);
        bool hadHorizontalInput = HasHorizontalInput(self);

        orig(self, eu);

        if (!CanTrack(self) ||
            hadHorizontalInput ||
            HasHorizontalInput(self) ||
            (self.grabbedBy != null && self.grabbedBy.Count > 0) ||
            !QuicksandSinkRateLimiter.TryGetVisualSink(
                self,
                out _,
                out _,
                out float immersion) ||
            immersion <= 0.005f)
        {
            return;
        }

        // Undo only center-of-mass X travel generated while the player supplied no
        // left/right input. Applying one common correction preserves chunk spacing
        // and therefore does not flatten the normal body pose.
        float averageDisplacementX = AverageChunkX(self) - startAverageX;
        if (Mathf.Abs(averageDisplacementX) > Epsilon)
        {
            TranslatePlayerX(self, -averageDisplacementX);
        }

        // Remove residual whole-player horizontal momentum as well. Relative chunk
        // velocity is retained, so internal posture/connection motion still works.
        float averageVelocityX = AverageChunkVelocityX(self);
        if (Mathf.Abs(averageVelocityX) > Epsilon)
        {
            AddPlayerVelocityX(self, -averageVelocityX);
        }
    }

    private static bool CanTrack(Player player)
    {
        return player != null &&
               player.room != null &&
               player.bodyChunks != null &&
               player.bodyChunks.Length > 0;
    }

    private static bool HasHorizontalInput(Player player)
    {
        return player?.input != null &&
               player.input.Length > 0 &&
               player.input[0].x != 0;
    }

    private static float AverageChunkX(Player player)
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

            total += chunk.pos.x;
            count++;
        }

        return count > 0 ? total / count : 0f;
    }

    private static float AverageChunkVelocityX(Player player)
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

            total += chunk.vel.x;
            count++;
        }

        return count > 0 ? total / count : 0f;
    }

    private static void TranslatePlayerX(Player player, float deltaX)
    {
        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            BodyChunk chunk = player.bodyChunks[i];
            if (chunk != null)
            {
                chunk.pos.x += deltaX;
            }
        }
    }

    private static void AddPlayerVelocityX(Player player, float deltaX)
    {
        for (int i = 0; i < player.bodyChunks.Length; i++)
        {
            BodyChunk chunk = player.bodyChunks[i];
            if (chunk != null)
            {
                chunk.vel.x += deltaX;
            }
        }
    }
}
