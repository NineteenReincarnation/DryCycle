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

        Check(WalkableDynamicSurfaceRuntime.IsGroundingNormal(Vector2.up),
            "Upward dynamic-curve normal must retain ordinary ground semantics");
        Vector2 steepNormal = new(.8f, .6f);
        Check(!WalkableDynamicSurfaceRuntime.IsGroundingNormal(steepNormal) &&
              WalkableDynamicSurfaceRuntime.IsSurfaceContactNormal(steepNormal),
            "Steep dynamic curve must remain a sliding contact without granting ground state");
        Check(!WalkableDynamicSurfaceRuntime.IsSurfaceContactNormal(Vector2.right),
            "Horizontal endpoint normal must release the rider instead of wrapping under the surface");

        Vector2 shallowNormal = new(-.5f, .8660254f);
        Vector2 restingSlopeVelocity = WalkableDynamicSurfaceRuntime.ResolveGroundContactVelocity(
            new Vector2(0f, -.9f), shallowNormal, .5f, .05f, .9f);
        Check(Mathf.Abs(restingSlopeVelocity.x) < .001f && Mathf.Abs(restingSlopeVelocity.y) < .001f,
            "Gravity-only velocity on a shallow dynamic curve must settle instead of becoming downhill motion");

        Vector2 climbingSlopeVelocity = WalkableDynamicSurfaceRuntime.ResolveGroundContactVelocity(
            new Vector2(2f, 0f), shallowNormal, .5f, .05f, .9f);
        Check(climbingSlopeVelocity.x > 0f && climbingSlopeVelocity.y > 0f,
            "Grounded horizontal locomotion must follow the shallow curve instead of being flattened");

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

        Check(WalkableDynamicSurfaceRuntime.ShouldAcquireDuringCooldown(4f, -3f, -6f),
            "Jump cooldown must not suppress a genuine downward crossing back onto the dynamic surface");
        Check(!WalkableDynamicSurfaceRuntime.ShouldAcquireDuringCooldown(4f, -3f, 2f),
            "Jump cooldown must still reject an upward crossing through the one-way surface");
        Check(!WalkableDynamicSurfaceRuntime.ShouldAcquireDuringCooldown(.2f, .1f, -1f),
            "Cooldown must not reattach from mere near-surface proximity without a fresh crossing");

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

        platformRuntimeCases += 25;
    }
}
