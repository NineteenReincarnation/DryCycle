using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

/// <summary>
/// Final shallow-surface bounce guard.
/// The quicksand surface is only used as a height test here. Any automatic bounce
/// suppression is world-Y only and never changes X velocity.
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

        // Intentional upward input is handled by the fixed Y-axis struggle/climb
        // rules and must not be cancelled here.
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

                TryAbsorbVerticalSurfaceBounce(chunk, zone);
            }
        }
    }

    private static void TryAbsorbVerticalSurfaceBounce(BodyChunk chunk, QuicksandZone zone)
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
                out _,
                out _))
        {
            return;
        }

        float bottomY = zone.PlacedObject.pos.y - zone.Data.BottomDepth;
        float depthLength = Mathf.Max(4f, surfacePoint.y - bottomY);
        float signedDepth = surfacePoint.y - chunk.pos.y;

        if (signedDepth < -radius * InfluenceMargin ||
            signedDepth > Mathf.Min(depthLength, radius * MaxSurfaceDepthRadius))
        {
            return;
        }

        // Positive world-Y velocity is an upward bounce. Remove only that Y
        // component. X velocity is deliberately untouched.
        if (chunk.vel.y > 0f)
        {
            chunk.vel.y = 0f;
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
