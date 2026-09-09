using DryCycle.Creatures.Platforming;
using UnityEngine;

internal static partial class Program
{
    private static void PlatformRuntimeTests()
    {
        Check(WalkableDynamicSurfaceRuntime.IsGroundCompatibleBodyMode(Player.BodyModeIndex.Default),
            "Default player body mode must remain eligible for dynamic ground");
        Check(WalkableDynamicSurfaceRuntime.IsGroundCompatibleBodyMode(Player.BodyModeIndex.Stand),
            "Stand player body mode must remain eligible for dynamic ground");
        Check(WalkableDynamicSurfaceRuntime.IsGroundCompatibleBodyMode(Player.BodyModeIndex.Crawl),
            "Crawl player body mode must remain eligible for dynamic ground");
        Check(!WalkableDynamicSurfaceRuntime.IsGroundCompatibleBodyMode(Player.BodyModeIndex.WallClimb) &&
              !WalkableDynamicSurfaceRuntime.IsGroundCompatibleBodyMode(Player.BodyModeIndex.ClimbingOnBeam) &&
              !WalkableDynamicSurfaceRuntime.IsGroundCompatibleBodyMode(Player.BodyModeIndex.Swimming) &&
              !WalkableDynamicSurfaceRuntime.IsGroundCompatibleBodyMode(Player.BodyModeIndex.ZeroG),
            "Dynamic ground body-mode whitelist admitted a non-ground locomotion mode");

        Check(WalkableDynamicSurfaceRuntime.ShouldAcquireContact(6f, -30f, -20f),
            "Swept high-speed landing from above must acquire the platform");
        Check(WalkableDynamicSurfaceRuntime.ShouldAcquireContact(4f, 2f, -1f),
            "Slow approach immediately above the surface must acquire the platform");
        Check(!WalkableDynamicSurfaceRuntime.ShouldAcquireContact(-5f, 1f, 6f),
            "Rising through the platform from below must not acquire it");
        Check(!WalkableDynamicSurfaceRuntime.ShouldAcquireContact(9f, 9f, -1f),
            "A player well above the platform must not magnetically acquire it");
        Check(!WalkableDynamicSurfaceRuntime.ShouldAcquireContact(3f, -55f, -25f),
            "Implausibly deep endpoint penetration must not be treated as a landing");

        Check(WalkableDynamicSurfaceRuntime.ShouldDetachAfterMovement(.5f, 1.2f, 3f),
            "Upward separation after movement must detach from dynamic ground");
        Check(!WalkableDynamicSurfaceRuntime.ShouldDetachAfterMovement(.5f, .6f, 3f),
            "Upward velocity without actual surface separation must not false-detach on platform motion");
        Check(!WalkableDynamicSurfaceRuntime.ShouldDetachAfterMovement(.5f, 1.2f, -1f),
            "Downward supported velocity must not be mistaken for a jump detach");

        Check(WalkableDynamicSurfaceRuntime.ContactCorrection(-4f) > 0f,
            "Penetrating rider must be corrected outward from dynamic ground");
        Check(WalkableDynamicSurfaceRuntime.ContactCorrection(4f) < 0f,
            "Hovering rider must be corrected back toward dynamic ground");

        Vector2 worldVelocity = new(4f, -3f);
        Vector2 surfaceVelocity = new(2f, 1f);
        Vector2 riderVelocity = WalkableDynamicSurfaceRuntime.ToRiderVelocity(worldVelocity, surfaceVelocity);
        Check(Vector2.Distance(riderVelocity, new Vector2(2f, -4f)) < .001f,
            "Dynamic ground acquisition must convert world velocity into the moving-surface frame");
        Check(Vector2.Distance(WalkableDynamicSurfaceRuntime.ToWorldVelocity(riderVelocity, surfaceVelocity), worldVelocity) < .001f,
            "Dynamic ground detach must restore surface velocity exactly once");

        Check(WalkableDynamicSurfaceRuntime.IsSurfaceSpeedRideable(new Vector2(12f, 0f)) &&
              !WalkableDynamicSurfaceRuntime.IsSurfaceSpeedRideable(new Vector2(12.1f, 0f)),
            "Dynamic ground must reject surface speeds that cannot be restored exactly on detach");

        platformRuntimeCases += 17;
    }
}
