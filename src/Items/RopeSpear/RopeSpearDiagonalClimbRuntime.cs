using UnityEngine;

namespace DryCycle.Items.RopeSpear;

/// <summary>
/// Restores RopeSpear's historical diagonal climbing input while keeping the
/// vanilla VineGrab state responsible for body placement, hand posing, swinging,
/// jump release, and vine attachment.
/// </summary>
internal static class RopeSpearDiagonalClimbRuntime
{
    private const float InputDeadZone = 0.05f;
    private const float MinAlongCursor = 12f;
    private const float MaxCursor = 30f;
    private const float PreservedSwingFactor = 0.35f;

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

        On.Player.Update -= Player_Update;
        _enabled = false;
    }

    private static void Player_Update(
        On.Player.orig_Update orig,
        Player self,
        bool eu)
    {
        BiasVineCursorAlongRope(self);
        orig(self, eu);
    }

    private static void BiasVineCursorAlongRope(Player player)
    {
        if (player?.animation != Player.AnimationIndex.VineGrab ||
            player.vinePos?.vine is not RopeSpear ||
            player.room?.climbableVines == null ||
            player.input == null ||
            player.input.Length == 0)
        {
            return;
        }

        Vector2 input = new(player.input[0].x, player.input[0].y);
        if (input.sqrMagnitude < 0.01f)
        {
            return;
        }

        if (input.sqrMagnitude > 1f)
        {
            input.Normalize();
        }

        Vector2 tangent = player.room.climbableVines.VineDir(player.vinePos);
        if (tangent.sqrMagnitude < 0.0001f)
        {
            return;
        }
        tangent.Normalize();

        // This is the important part of the pre-VineGrab implementation: project
        // world-space input directly onto the visible rope tangent. Up, Right, or a
        // diagonal combination can therefore advance on a sloped rope according to
        // the direction actually pressed instead of depending on vanilla's
        // goal-position angle heuristic.
        float alongInput = Vector2.Dot(input, tangent);
        float alongMagnitude = Mathf.Abs(alongInput);
        if (alongMagnitude <= InputDeadZone)
        {
            return;
        }

        Vector2 normal = new(-tangent.y, tangent.x);
        float preservedSwing =
            Vector2.Dot(player.vineClimbCursor, normal) * PreservedSwingFactor;

        // Keep enough tangent authority to make ClimbOnVineSpeed unambiguous even
        // when the body is hanging slightly off the rope. Vanilla Player.Update will
        // still add the ordinary SwimDir contribution and perform the actual climb.
        float tangentCursor = Mathf.Sign(alongInput) *
                              Mathf.Max(MinAlongCursor, MaxCursor * alongMagnitude);

        player.vineClimbCursor = Vector2.ClampMagnitude(
            tangent * tangentCursor + normal * preservedSwing,
            MaxCursor);
    }
}
