using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

/// <summary>
/// Final post-player-update guard for the shallow quicksand surface layer.
/// Rain World's locomotion code can inject a small velocity away from a surface
/// after BodyChunk terrain collision has already been disabled; on quicksand that
/// reads as a short hop/bounce. This removes only that unsolicited outward normal
/// component while preserving explicit jump/upward-climb input.
/// </summary>
internal static class QuicksandEntryBounceGuard
{
    private const float InfluenceMargin = 1.45f;
    private const float MaxSurfaceDepthRadius = 0.55f;

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
        orig(self, eu);
        SuppressSurfaceBounce(self);
    }

    private static void SuppressSurfaceBounce(Player player)
    {
        if (player == null ||
            player.room == null ||
            player.room.updateList == null ||
            player.bodyChunks == null ||
            player.bodyChunks.Length == 0)
        {
            return;
        }

        // Do not interfere with an intentional attempt to leave the sand.
        if (player.jumpBoost > 0f)
        {
            return;
        }

        if (player.input != null && player.input.Length > 0 &&
            (player.input[0].jmp || player.input[0].y > 0))
        {
            return;
        }

        for (int i = 0; i < player.room.updateList.Count; i++)
        {
            if (player.room.updateList[i] is not QuicksandZone zone ||
                !IsUsableZone(zone))
            {
                continue;
            }

            for (int j = 0; j < player.bodyChunks.Length; j++)
            {
                BodyChunk chunk = player.bodyChunks[j];
                if (chunk == null)
                {
                    continue;
                }

                TryAbsorbOutwardSurfaceVelocity(chunk, zone);
            }
        }
    }

    private static void TryAbsorbOutwardSurfaceVelocity(BodyChunk chunk, QuicksandZone zone)
    {
        float radius = Mathf.Max(1f, chunk.rad);
        if (chunk.pos.x < zone.startX - radius * 1.15f ||
            chunk.pos.x > zone.endX + radius * 1.15f)
        {
            return;
        }

        float u = zone.MaterialUAtWorldX(chunk.pos.x);
        if (!zone.Data.IsQuicksand(u) ||
            !zone.TrySampleSurfaceFrame(
                u,
                out Vector2 surfacePoint,
                out _,
                out Vector2 inward,
                out float depthLength))
        {
            return;
        }

        float signedDepth = Vector2.Dot(chunk.pos - surfacePoint, inward);
        if (signedDepth < -radius * InfluenceMargin ||
            signedDepth > Mathf.Min(depthLength, radius * MaxSurfaceDepthRadius))
        {
            return;
        }

        if (inward.sqrMagnitude < 0.0001f)
        {
            return;
        }

        inward.Normalize();
        float inwardSpeed = Vector2.Dot(chunk.vel, inward);
        if (inwardSpeed < 0f)
        {
            // Zero only the velocity normal that points out of the sand. Tangential
            // movement is retained, so this does not create a horizontal conveyor or
            // pin the player to one x-coordinate on a curved surface.
            chunk.vel -= inward * inwardSpeed;
        }
    }

    private static bool IsUsableZone(QuicksandZone zone)
    {
        return zone != null &&
               !zone.slatedForDeletetion &&
               zone.PlacedObject != null &&
               zone.PlacedObject.active &&
               zone.Data != null;
    }
}
