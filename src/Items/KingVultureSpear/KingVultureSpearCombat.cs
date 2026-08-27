using System;
using System.Runtime.CompilerServices;
using DryCycle.Thirst;

namespace DryCycle.Items.KingVultureSpear;

internal static class KingVultureSpearCombat
{
    private const float DamageMultiplier = 3f;
    private const float WeaknessWaterLossMultiplier = 3f;
    private const int WeaknessFrames = 200;

    private sealed class PlayerWeaknessState
    {
        public int RemainingFrames;
    }

    private static readonly ConditionalWeakTable<Player, PlayerWeaknessState> WeaknessStates = new();
    private static bool _enabled;

    public static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.Player.ThrownSpear += Player_ThrownSpear;
        On.Player.Grabability += Player_Grabability;
        On.Player.SlugcatGrab += Player_SlugcatGrab;
        On.Player.Update += Player_Update;
    }

    public static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;
        On.Player.ThrownSpear -= Player_ThrownSpear;
        On.Player.Grabability -= Player_Grabability;
        On.Player.SlugcatGrab -= Player_SlugcatGrab;
        On.Player.Update -= Player_Update;
    }

    private static void Player_ThrownSpear(
        On.Player.orig_ThrownSpear orig,
        Player self,
        Spear spear)
    {
        orig(self, spear);

        if (spear is not global::DryCycle.Items.KingVultureSpear.KingVultureSpear)
        {
            return;
        }

        // Player.ThrownSpear has already applied the vanilla slugcat-specific
        // spearDamageBonus. Multiplying afterwards makes this weapon exactly
        // three times that slugcat's normal spear damage, including Monk/Hunter/
        // Gourmand and other vanilla throwing-skill differences.
        spear.spearDamageBonus *= DamageMultiplier;

        if (self != null && !self.isNPC)
        {
            WeaknessStates.GetOrCreateValue(self).RemainingFrames = WeaknessFrames;
        }
    }

    private static Player.ObjectGrabability Player_Grabability(
        On.Player.orig_Grabability orig,
        Player self,
        PhysicalObject obj)
    {
        Player.ObjectGrabability result = orig(self, obj);

        if (self == null ||
            self.isNPC ||
            obj is not global::DryCycle.Items.KingVultureSpear.KingVultureSpear spear ||
            IsAlreadyCarriedBy(self, spear))
        {
            return result;
        }

        return HasOtherCarriedKingVultureSpear(self, spear)
            ? Player.ObjectGrabability.CantGrab
            : result;
    }

    private static void Player_SlugcatGrab(
        On.Player.orig_SlugcatGrab orig,
        Player self,
        PhysicalObject obj,
        int graspUsed)
    {
        if (self != null &&
            !self.isNPC &&
            obj is global::DryCycle.Items.KingVultureSpear.KingVultureSpear spear &&
            !IsAlreadyCarriedBy(self, spear) &&
            HasOtherCarriedKingVultureSpear(self, spear))
        {
            return;
        }

        orig(self, obj, graspUsed);
    }

    private static void Player_Update(On.Player.orig_Update orig, Player self, bool eu)
    {
        orig(self, eu);

        if (self == null ||
            self.isNPC ||
            !WeaknessStates.TryGetValue(self, out PlayerWeaknessState state) ||
            state.RemainingFrames <= 0)
        {
            return;
        }

        // ThirstHooks already removes the normal 1x passive loss each player tick.
        // Removing another 2x here makes the total exactly 3x for 200 ticks = 5 s.
        if (!self.dead && IsStoryPlayer(self))
        {
            float normalLoss = SlugBaseHydrationFeatures.GetWaterLossPerTick(self);
            float extraLoss = normalLoss * (WeaknessWaterLossMultiplier - 1f);

            if (extraLoss > 0f)
            {
                ThirstState thirst = ThirstStore.For(self);
                float beforeWater = thirst.Water;

                if (ThirstStore.RemoveRuntime(self, extraLoss))
                {
                    float afterWater = thirst.Water;
                    if (CrossedHalfPipLossBoundary(beforeWater, afterWater))
                    {
                        self.showKarmaFoodRainTime = Math.Max(
                            self.showKarmaFoodRainTime,
                            ThirstConstants.HydrationLossHudHoldFrames);
                    }
                }
            }
        }

        state.RemainingFrames--;
    }

    private static bool HasOtherCarriedKingVultureSpear(
        Player player,
        global::DryCycle.Items.KingVultureSpear.KingVultureSpear ignored)
    {
        if (player?.grasps != null)
        {
            for (int i = 0; i < player.grasps.Length; i++)
            {
                if (player.grasps[i]?.grabbed is global::DryCycle.Items.KingVultureSpear.KingVultureSpear held &&
                    held != ignored)
                {
                    return true;
                }
            }
        }

        return player?.spearOnBack?.spear is global::DryCycle.Items.KingVultureSpear.KingVultureSpear back &&
               back != ignored;
    }

    private static bool IsAlreadyCarriedBy(
        Player player,
        global::DryCycle.Items.KingVultureSpear.KingVultureSpear spear)
    {
        if (player == null || spear == null)
        {
            return false;
        }

        if (player.grasps != null)
        {
            for (int i = 0; i < player.grasps.Length; i++)
            {
                if (player.grasps[i]?.grabbed == spear)
                {
                    return true;
                }
            }
        }

        return player.spearOnBack?.spear == spear;
    }

    private static bool IsStoryPlayer(Player player)
    {
        RainWorldGame game = player?.room?.game ?? player?.abstractCreature?.world?.game;
        return game != null && game.IsStorySession;
    }

    private static bool CrossedHalfPipLossBoundary(float beforeWater, float afterWater)
    {
        if (afterWater >= beforeWater - 0.000001f)
        {
            return false;
        }

        int beforeHalfPips = (int)Math.Ceiling(Math.Max(0f, beforeWater) * 2.0);
        int afterHalfPips = (int)Math.Ceiling(Math.Max(0f, afterWater) * 2.0);
        return afterHalfPips < beforeHalfPips;
    }
}
