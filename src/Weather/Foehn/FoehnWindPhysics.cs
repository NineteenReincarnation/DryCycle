using UnityEngine;

namespace DryCycle.Weather.Foehn;

/// <summary>
/// Applies the same local Foehn signal used by rendering to Rain World's physical
/// objects after the room finishes its normal update. This timing avoids fighting the
/// player's locomotion code inside Player.Update: the wind changes velocity for the
/// next physics step instead of being immediately overwritten by movement logic.
/// </summary>
internal static class FoehnWindPhysics
{
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        On.Room.Update += Room_Update;
        _enabled = true;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.Room.Update -= Room_Update;
        _enabled = false;
    }

    private static void Room_Update(On.Room.orig_Update orig, Room self)
    {
        orig(self);
        ApplyRoomWind(self);
    }

    private static void ApplyRoomWind(Room room)
    {
        if (!_enabled || room?.physicalObjects == null)
        {
            return;
        }

        for (int layer = 0; layer < room.physicalObjects.Length; layer++)
        {
            var objects = room.physicalObjects[layer];
            if (objects == null)
            {
                continue;
            }

            for (int i = 0; i < objects.Count; i++)
            {
                PhysicalObject physicalObject = objects[i];
                if (physicalObject == null ||
                    physicalObject.slatedForDeletetion ||
                    physicalObject.room != room ||
                    physicalObject.bodyChunks == null ||
                    physicalObject.bodyChunks.Length == 0)
                {
                    continue;
                }

                if (physicalObject is Creature creature &&
                    (creature.inShortcut || creature.enteringShortCut.HasValue))
                {
                    continue;
                }

                Vector2 samplePosition = AveragePosition(physicalObject);
                if (!FoehnWeatherRuntime.TrySampleWind(
                        room,
                        samplePosition,
                        out FoehnWindSample wind))
                {
                    continue;
                }

                Vector2 force = ResolveForce(physicalObject, samplePosition, wind);
                if (force.sqrMagnitude <= 0.000001f)
                {
                    continue;
                }

                for (int chunkIndex = 0;
                     chunkIndex < physicalObject.bodyChunks.Length;
                     chunkIndex++)
                {
                    BodyChunk chunk = physicalObject.bodyChunks[chunkIndex];
                    if (chunk != null)
                    {
                        chunk.vel += force;
                    }
                }
            }
        }
    }

    private static Vector2 ResolveForce(
        PhysicalObject physicalObject,
        Vector2 position,
        in FoehnWindSample wind)
    {
        float drive = Mathf.Pow(Mathf.Clamp01(wind.Intensity), 0.82f);
        if (drive <= 0.0001f)
        {
            return Vector2.zero;
        }

        float exposure = Mathf.Lerp(0.14f, 1f, wind.Exposure);
        float nozzle = Mathf.Lerp(0.90f, 1.43f, wind.Nozzle);
        float pulse = 0.46f + wind.Gust * 0.64f + wind.Front * 1.06f;
        float acceleration = 0.238f * drive * exposure * nozzle * pulse;

        bool grounded = IsGrounded(physicalObject);
        float response;
        float verticalResponse = 1f;
        float maximum;

        if (physicalObject is Player player)
        {
            response = PlayerResponse(player, grounded);
            verticalResponse = grounded ? 0.28f : 1f;
            maximum = 0.62f;
        }
        else if (physicalObject is Creature)
        {
            float lightness = Mathf.InverseLerp(
                6.5f,
                0.28f,
                Mathf.Max(0.01f, physicalObject.TotalMass));
            response = Mathf.Lerp(0.18f, 0.72f, lightness);
            if (grounded)
            {
                response *= 0.48f;
                verticalResponse = 0.36f;
            }
            maximum = 0.48f;
        }
        else
        {
            float lightness = Mathf.InverseLerp(
                4.2f,
                0.06f,
                Mathf.Max(0.01f, physicalObject.TotalMass));
            response = Mathf.Lerp(0.26f, 1.30f, lightness);
            if (grounded)
            {
                response *= 0.43f;
                verticalResponse = 0.30f;
            }

            if (physicalObject.grabbedBy != null && physicalObject.grabbedBy.Count > 0)
            {
                response *= 0.18f;
            }
            maximum = 0.82f;
        }

        response *= Mathf.Lerp(1f, 0.08f, Mathf.Clamp01(physicalObject.Submersion));

        Vector2 direction = wind.Direction;
        direction.y *= verticalResponse;
        if (direction.sqrMagnitude > 0.0001f)
        {
            direction.Normalize();
        }

        Vector2 cross = new(-wind.Direction.y, wind.Direction.x);
        float wakeTurbulence =
            (wind.Wake * 0.090f + wind.Edge * 0.038f) *
            (0.36f + wind.Turbulence * 0.64f) *
            drive;
        float crossWave = Mathf.Sin(
            position.x * 0.029f -
            position.y * 0.041f +
            wind.Gust * 5.7f +
            wind.Front * 2.3f);

        Vector2 force =
            direction * acceleration * response +
            cross * crossWave * wakeTurbulence * response;
        return ClampMagnitude(force, maximum);
    }

    private static float PlayerResponse(Player player, bool grounded)
    {
        if (player == null)
        {
            return 0f;
        }

        if (player.bodyMode == Player.BodyModeIndex.Swimming)
        {
            return 0.08f;
        }

        bool climbing =
            player.bodyMode == Player.BodyModeIndex.CorridorClimb ||
            player.bodyMode == Player.BodyModeIndex.ClimbIntoShortCut ||
            player.bodyMode == Player.BodyModeIndex.WallClimb ||
            player.bodyMode == Player.BodyModeIndex.ClimbingOnBeam ||
            player.animation == Player.AnimationIndex.HangFromBeam ||
            player.animation == Player.AnimationIndex.ClimbOnBeam ||
            player.animation == Player.AnimationIndex.VineGrab ||
            player.animation == Player.AnimationIndex.ZeroGPoleGrab;

        if (climbing)
        {
            return 0.18f;
        }

        if (grounded || player.canJump > 0)
        {
            return 0.34f;
        }

        // Airborne slugcats receive the full readable Foehn push. This is deliberately
        // much stronger than the grounded response so running remains controllable while
        // jumps visibly drift with and against the wind.
        return 0.96f;
    }

    private static bool IsGrounded(PhysicalObject physicalObject)
    {
        if (physicalObject?.bodyChunks == null)
        {
            return false;
        }

        for (int i = 0; i < physicalObject.bodyChunks.Length; i++)
        {
            BodyChunk chunk = physicalObject.bodyChunks[i];
            if (chunk != null && chunk.ContactPoint.y < 0)
            {
                return true;
            }
        }

        return false;
    }

    private static Vector2 AveragePosition(PhysicalObject physicalObject)
    {
        Vector2 total = Vector2.zero;
        int count = 0;
        for (int i = 0; i < physicalObject.bodyChunks.Length; i++)
        {
            BodyChunk chunk = physicalObject.bodyChunks[i];
            if (chunk == null)
            {
                continue;
            }

            total += chunk.pos;
            count++;
        }

        return count > 0 ? total / count : physicalObject.firstChunk.pos;
    }

    private static Vector2 ClampMagnitude(Vector2 value, float maximum)
    {
        float length = value.magnitude;
        if (length <= maximum || length <= 0.0001f)
        {
            return value;
        }

        return value * (maximum / length);
    }
}
