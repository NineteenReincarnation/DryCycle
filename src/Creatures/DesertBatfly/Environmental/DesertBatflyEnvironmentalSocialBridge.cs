using System;
using System.Reflection;
using MonoMod.RuntimeDetour;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Applies Task13 soft environmental modifiers to Task10 without touching Task10's
/// reservation, partner or MicroFlock state model. This bridge also owns the lifecycle
/// of the narrow Task13 survival/Task09 bridges so the whole environmental layer still
/// has a single Enable/Disable entry through DesertBatflyEnvironmentalIntegration.
/// </summary>
internal static class DesertBatflyEnvironmentalSocialBridge
{
    private const float Task10BaseSocialRange = 240f;
    private const float MinimumActivityRangeScale = 0.28f;

    private delegate void SocialUpdateOrig(DesertBatfly bat);
    private delegate void SocialUpdateDetour(SocialUpdateOrig orig, DesertBatfly bat);
    private delegate float SocialDriveOrig(DesertBatflyPersonality personality);
    private delegate float SocialDriveDetour(SocialDriveOrig orig, DesertBatflyPersonality personality);
    private delegate float GroupJoinOrig(float conformity, float socialDrive);
    private delegate float GroupJoinDetour(GroupJoinOrig orig, float conformity, float socialDrive);
    private delegate bool CanPlayChaseOrig(DesertBatfly bat);
    private delegate bool CanPlayChaseDetour(CanPlayChaseOrig orig, DesertBatfly bat);
    private delegate bool CanBePartnerOrig(
        DesertBatfly source,
        DesertBatfly candidate,
        DesertBatflySocialRoomRuntime.RoomState roomState);
    private delegate bool CanBePartnerDetour(
        CanBePartnerOrig orig,
        DesertBatfly source,
        DesertBatfly candidate,
        DesertBatflySocialRoomRuntime.RoomState roomState);

    [ThreadStatic]
    private static DesertBatfly currentBat;

    private static Hook updateHook;
    private static Hook driveHook;
    private static Hook groupJoinHook;
    private static Hook chaseHook;
    private static Hook partnerHook;

    internal static bool Installed => updateHook != null;

    internal static void Enable()
    {
        // These bridges are independent of the Task10 reflection hooks. Enable them first
        // so a future Task10 rename cannot silently disable Task13 Home/Burrow/Task09 policy.
        DesertBatflyEnvironmentalTask09Bridge.Enable();
        DesertBatflyEnvironmentalSurvivalBridge.Enable();

        if (Installed) return;
        DisposeTask10Hooks();
        try
        {
            BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            MethodInfo update = typeof(DesertBatflySocialLife).GetMethod(
                "Update", flags, null, new[] { typeof(DesertBatfly) }, null);
            MethodInfo drive = typeof(DesertBatflySocialLife).GetMethod(
                "SocialDrivePerTick", flags, null, new[] { typeof(DesertBatflyPersonality) }, null);
            MethodInfo groupJoin = typeof(DesertBatflySocialLife).GetMethod(
                "GroupJoinPreference", flags, null, new[] { typeof(float), typeof(float) }, null);
            MethodInfo chase = typeof(DesertBatflySocialLife).GetMethod(
                "CanPlayChase", flags, null, new[] { typeof(DesertBatfly) }, null);
            MethodInfo partner = typeof(DesertBatflySocialLife).GetMethod(
                "CanBePartner", flags, null,
                new[]
                {
                    typeof(DesertBatfly), typeof(DesertBatfly),
                    typeof(DesertBatflySocialRoomRuntime.RoomState)
                },
                null);
            if (update == null || drive == null || groupJoin == null || chase == null || partner == null)
                return;

            updateHook = new Hook(update, (SocialUpdateDetour)UpdateHook);
            driveHook = new Hook(drive, (SocialDriveDetour)DriveHook);
            groupJoinHook = new Hook(groupJoin, (GroupJoinDetour)GroupJoinHook);
            chaseHook = new Hook(chase, (CanPlayChaseDetour)ChaseHook);
            partnerHook = new Hook(partner, (CanBePartnerDetour)CanBePartnerHook);
        }
        catch
        {
            DisposeTask10Hooks();
        }
    }

    internal static void Disable()
    {
        DisposeTask10Hooks();
        DesertBatflyEnvironmentalSurvivalBridge.Disable();
        DesertBatflyEnvironmentalTask09Bridge.Disable();
        currentBat = null;
    }

    private static void DisposeTask10Hooks()
    {
        try { partnerHook?.Dispose(); } catch { }
        try { chaseHook?.Dispose(); } catch { }
        try { groupJoinHook?.Dispose(); } catch { }
        try { driveHook?.Dispose(); } catch { }
        try { updateHook?.Dispose(); } catch { }
        partnerHook = null;
        chaseHook = null;
        groupJoinHook = null;
        driveHook = null;
        updateHook = null;
    }

    private static void UpdateHook(SocialUpdateOrig orig, DesertBatfly bat)
    {
        DesertBatfly previous = currentBat;
        currentBat = bat;
        try
        {
            if (bat != null && ShouldYieldToEnvironment(bat))
            {
                DesertBatflySocialLife.CancelForPriority(bat, "Task13 environmental survival priority");
                return;
            }
            orig(bat);
        }
        finally
        {
            currentBat = previous;
        }
    }

    private static bool ShouldYieldToEnvironment(DesertBatfly bat)
    {
        if (DesertBatflyEnvironmentalBehavior.SuppressNeutralSocial(bat)) return true;
        if (!DesertBatflyEnvironmentalBehavior.TryGetInfluence(
                bat, out DesertBatflyEnvironmentalInfluence influence))
            return false;
        return DesertBatflyEnvironmentalSurvivalBridge.ShouldSeekHome(influence) ||
               DesertBatflyEnvironmentalSurvivalBridge.ShouldBurrow(influence);
    }

    private static float DriveHook(SocialDriveOrig orig, DesertBatflyPersonality personality)
    {
        float value = orig(personality);
        if (currentBat == null) return value;
        return value * DesertBatflyEnvironmentalBehavior.SocialScale(currentBat);
    }

    private static float GroupJoinHook(GroupJoinOrig orig, float conformity, float socialDrive)
    {
        float value = orig(conformity, socialDrive);
        if (currentBat == null) return value;
        return UnityEngine.Mathf.Clamp01(
            value * DesertBatflyEnvironmentalBehavior.GroupCohesionScale(currentBat));
    }

    private static bool ChaseHook(CanPlayChaseOrig orig, DesertBatfly bat)
    {
        if (!orig(bat)) return false;
        float scale = DesertBatflyEnvironmentalBehavior.PlayScale(bat);
        if (scale >= 0.999f) return true;
        if (scale <= 0.001f) return false;
        int bucket = (bat.room?.game?.clock ?? 0) / 90;
        return Stable01(bat.Personality.VisualSeed ^ bucket * 0x632BE5AB) <= scale;
    }

    private static bool CanBePartnerHook(
        CanBePartnerOrig orig,
        DesertBatfly source,
        DesertBatfly candidate,
        DesertBatflySocialRoomRuntime.RoomState roomState)
    {
        if (!orig(source, candidate, roomState)) return false;
        if (source?.mainBodyChunk == null || candidate?.mainBodyChunk == null) return false;
        if (!DesertBatflyEnvironmentalBehavior.TryGetInfluence(
                source, out DesertBatflyEnvironmentalInfluence influence))
            return true;

        float scale = UnityEngine.Mathf.Clamp(
            influence.ActivityRadiusMultiplier,
            MinimumActivityRangeScale,
            1f);
        if (scale >= 0.999f) return true;

        float effectiveRange = Task10BaseSocialRange * scale;
        return UnityEngine.Vector2.Distance(
            source.mainBodyChunk.pos,
            candidate.mainBodyChunk.pos) <= effectiveRange;
    }

    private static float Stable01(int seed)
    {
        unchecked
        {
            uint x = (uint)seed;
            x ^= x >> 16;
            x *= 0x7feb352d;
            x ^= x >> 15;
            x *= 0x846ca68b;
            x ^= x >> 16;
            return (x & 0x00ffffffu) / 16777215f;
        }
    }
}
