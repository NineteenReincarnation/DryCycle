using System.Runtime.CompilerServices;

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

    internal static float GetWaterLossMultiplier(Player player)
    {
        if (player == null ||
            player.isNPC ||
            !WeaknessStates.TryGetValue(player, out PlayerWeaknessState state) ||
            state.RemainingFrames <= 0)
        {
            return 1f;
        }

        return WeaknessWaterLossMultiplier;
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
}
