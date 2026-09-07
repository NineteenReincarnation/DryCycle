using System;
using System.Reflection;
using MonoMod.RuntimeDetour;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Applies Task13 soft environmental modifiers to Task10 without touching Task10's
/// reservation, partner or MicroFlock state model.
/// </summary>
internal static class DesertBatflyEnvironmentalSocialBridge
{
    private delegate void SocialUpdateOrig(DesertBatfly bat);
    private delegate void SocialUpdateDetour(SocialUpdateOrig orig, DesertBatfly bat);
    private delegate float SocialDriveOrig(DesertBatflyPersonality personality);
    private delegate float SocialDriveDetour(SocialDriveOrig orig, DesertBatflyPersonality personality);
    private delegate float GroupJoinOrig(float conformity, float socialDrive);
    private delegate float GroupJoinDetour(GroupJoinOrig orig, float conformity, float socialDrive);
    private delegate bool CanPlayChaseOrig(DesertBatfly bat);
    private delegate bool CanPlayChaseDetour(CanPlayChaseOrig orig, DesertBatfly bat);

    [ThreadStatic]
    private static DesertBatfly currentBat;

    private static Hook updateHook;
    private static Hook driveHook;
    private static Hook groupJoinHook;
    private static Hook chaseHook;

    internal static bool Installed => updateHook != null;

    internal static void Enable()
    {
        if (Installed) return;
        Disable();
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
            if (update == null || drive == null || groupJoin == null || chase == null) return;

            updateHook = new Hook(update, (SocialUpdateDetour)UpdateHook);
            driveHook = new Hook(drive, (SocialDriveDetour)DriveHook);
            groupJoinHook = new Hook(groupJoin, (GroupJoinDetour)GroupJoinHook);
            chaseHook = new Hook(chase, (CanPlayChaseDetour)ChaseHook);
        }
        catch
        {
            Disable();
        }
    }

    internal static void Disable()
    {
        try { chaseHook?.Dispose(); } catch { }
        try { groupJoinHook?.Dispose(); } catch { }
        try { driveHook?.Dispose(); } catch { }
        try { updateHook?.Dispose(); } catch { }
        chaseHook = null;
        groupJoinHook = null;
        driveHook = null;
        updateHook = null;
        currentBat = null;
    }

    private static void UpdateHook(SocialUpdateOrig orig, DesertBatfly bat)
    {
        DesertBatfly previous = currentBat;
        currentBat = bat;
        try
        {
            if (bat != null && DesertBatflyEnvironmentalBehavior.SuppressNeutralSocial(bat))
            {
                DesertBatflySocialLife.CancelForPriority(bat, "Task13 suppresses neutral social activity");
                return;
            }
            orig(bat);
        }
        finally
        {
            currentBat = previous;
        }
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
