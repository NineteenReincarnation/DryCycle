using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.PlayerAbility.SlugCatKarmicArmor;

/// <summary>
/// Gives story-mode players the Watcher scavenger karmic shield when reinforced
/// karma is consumed. All runtime state belongs to the realized Player instance,
/// so split-screen and Jolly co-op players cannot overwrite one another.
/// </summary>
internal static class SlugCatKarmicArmorRuntime
{
    private static ConditionalWeakTable<Player, PlayerKarmicArmorState> _armorStates = new();
    private static readonly HashSet<SlugCatKarmicArmorVisual> ActiveVisuals = new();
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.Player.Update += Player_Update;
        On.Creature.Violence += Creature_Violence;
        On.Player.SpearStick += Player_SpearStick;
        On.Player.Die += Player_Die;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;
        On.Player.Update -= Player_Update;
        On.Creature.Violence -= Creature_Violence;
        On.Player.SpearStick -= Player_SpearStick;
        On.Player.Die -= Player_Die;

        List<SlugCatKarmicArmorVisual> visuals = new(ActiveVisuals);
        ActiveVisuals.Clear();
        foreach (SlugCatKarmicArmorVisual visual in visuals)
        {
            visual.DestroyFromRuntime();
        }

        _armorStates = new ConditionalWeakTable<Player, PlayerKarmicArmorState>();
    }

    private static void Player_Update(On.Player.orig_Update orig, Player self, bool eu)
    {
        orig(self, eu);

        if (self.room == null)
        {
            return;
        }

        // Player-specific adaptation from the original standalone mod: a dangerous
        // grab can wake the shield before a creature's lethal bite reaches Violence.
        if (self.dangerGrasp != null)
        {
            PlayerKarmicArmorState dangerState = TryAcquireArmor(self);
            if (dangerState != null && dangerState.LastDangerGrasp != self.dangerGrasp)
            {
                EnsureArmorVisual(self, dangerState);
                TriggerArmor(dangerState, resetTime: 15);
            }

            if (dangerState != null)
            {
                dangerState.LastDangerGrasp = self.dangerGrasp;
            }
        }

        if (!_armorStates.TryGetValue(self, out PlayerKarmicArmorState state))
        {
            return;
        }

        if (self.dangerGrasp == null)
        {
            state.LastDangerGrasp = null;
        }

        TickArmor(state);
        MaintainArmorVisual(self, state);
    }

    private static void Creature_Violence(
        On.Creature.orig_Violence orig,
        Creature self,
        BodyChunk source,
        Vector2? directionAndMomentum,
        BodyChunk hitChunk,
        PhysicalObject.Appendage.Pos hitAppendage,
        Creature.DamageType type,
        float damage,
        float stunBonus)
    {
        if (self is Player player && source?.owner != null)
        {
            // Matches Scavenger.Violence: thrown weapons use the 45-frame cadence;
            // direct creature attacks use the faster 15-frame cadence.
            if (source.owner is Weapon weapon && weapon.mode == Weapon.Mode.Thrown)
            {
                PlayerKarmicArmorState state = TryAcquireArmor(player);
                if (state?.IsProtected == true)
                {
                    DeflectWeapon(player, state, weapon);
                    return;
                }
            }
            else if (source.owner is Creature)
            {
                PlayerKarmicArmorState state = TryAcquireArmor(player);
                if (state?.IsProtected == true)
                {
                    EnsureArmorVisual(player, state);
                    TriggerArmor(state, resetTime: 15);
                    return;
                }
            }
        }

        orig(self, source, directionAndMomentum, hitChunk, hitAppendage, type, damage, stunBonus);
    }

    private static bool Player_SpearStick(
        On.Player.orig_SpearStick orig,
        Player self,
        Weapon source,
        float damage,
        BodyChunk chunk,
        PhysicalObject.Appendage.Pos onAppendagePos,
        Vector2 direction)
    {
        PlayerKarmicArmorState state = TryAcquireArmor(self);
        if (state?.IsProtected == true)
        {
            DeflectWeapon(self, state, source);
            return false;
        }

        return orig(self, source, damage, chunk, onAppendagePos, direction);
    }

    private static void Player_Die(On.Player.orig_Die orig, Player self)
    {
        PlayerKarmicArmorState state = TryAcquireArmor(self);
        if (state?.IsProtected == true)
        {
            EnsureArmorVisual(self, state);

            bool newDangerGrasp = self.dangerGrasp != null &&
                                  state.LastDangerGrasp != self.dangerGrasp;
            if (!state.Triggered || newDangerGrasp)
            {
                TriggerArmor(state, self.dangerGrasp != null ? 15 : 45);
            }

            return;
        }

        orig(self);
    }

    private static PlayerKarmicArmorState TryAcquireArmor(Player player)
    {
        RainWorldGame game = player?.abstractCreature?.world?.game;
        if (!ModManager.Watcher || game == null)
        {
            return null;
        }

        if (_armorStates.TryGetValue(player, out PlayerKarmicArmorState existingState))
        {
            if (existingState.IsProtected)
            {
                return existingState;
            }

            // Preserve the original depleted-shield buildup. A newly gained karma
            // reinforcement cannot replace it until that visual has exploded or left.
            if (existingState.Armor != null && !existingState.Armor.slatedForDeletetion)
            {
                return null;
            }
        }

        if (player.room == null || game.session is not StoryGameSession storySession)
        {
            return null;
        }

        DeathPersistentSaveData saveData = storySession.saveState.deathPersistentSaveData;
        if (!saveData.reinforcedKarma)
        {
            return null;
        }

        PlayerKarmicArmorState state = existingState ?? _armorStates.GetOrCreateValue(player);
        state.Reset(Mathf.Clamp(saveData.karma + 1, 1, 10));

        // Reinforced karma is shared by the story save in co-op. The first player
        // whose shield actually triggers consumes it and owns this shield instance.
        saveData.reinforcedKarma = false;
        UpdateKarmaMeters(game);

        Plugin.Logger?.LogInfo(
            $"Player {player.playerState?.playerNumber.ToString() ?? "?"} acquired " +
            $"karmic armor with {state.KarmaLevels} level(s).");

        return state;
    }

    private static void UpdateKarmaMeters(RainWorldGame game)
    {
        if (game?.cameras == null)
        {
            return;
        }

        foreach (RoomCamera camera in game.cameras)
        {
            if (camera?.hud?.karmaMeter == null)
            {
                continue;
            }

            camera.hud.karmaMeter.blinkRedCounter = 30;
            camera.hud.karmaMeter.showAsReinforced = false;
        }
    }

    private static void TriggerArmor(PlayerKarmicArmorState state, int resetTime)
    {
        state.Triggered = true;
        state.Timer = 0;
        state.ResetTime = resetTime;
    }

    private static void TickArmor(PlayerKarmicArmorState state)
    {
        // Exact Scavenger.Update behavior: after the first trigger, continually spend
        // one shield level at the configured cadence until no levels remain.
        if (!state.IsProtected || !state.Triggered)
        {
            return;
        }

        state.Timer--;
        if (state.Timer <= 0)
        {
            state.Timer = state.ResetTime;
            state.KarmaLevels--;
        }
    }

    private static void DeflectWeapon(
        Player player,
        PlayerKarmicArmorState state,
        Weapon weapon)
    {
        EnsureArmorVisual(player, state);

        // Spear.HitSomething queries Player.SpearStick twice in one collision. Fold
        // both calls into one effect/countdown transition for this player and weapon.
        int gameClock = player.room?.game?.clock ?? -1;
        if (!state.TryMarkWeaponDeflection(weapon, gameClock))
        {
            return;
        }

        TriggerArmor(state, resetTime: 45);
        state.Armor?.DeflectedProjectile(weapon.firstChunk.pos);
    }

    private static void MaintainArmorVisual(Player player, PlayerKarmicArmorState state)
    {
        if (state.Armor != null &&
            (state.Armor.slatedForDeletetion || state.Armor.room != player.room))
        {
            state.Armor.DestroyFromRuntime();
        }

        if (state.IsProtected)
        {
            EnsureArmorVisual(player, state);
        }
    }

    private static void EnsureArmorVisual(Player player, PlayerKarmicArmorState state)
    {
        if (player.room == null || state.Armor != null || !state.IsProtected)
        {
            return;
        }

        state.Armor = new SlugCatKarmicArmorVisual(player, state);
        ActiveVisuals.Add(state.Armor);
        player.room.AddObject(state.Armor);
    }

    internal static void NotifyVisualDestroyed(SlugCatKarmicArmorVisual visual)
    {
        ActiveVisuals.Remove(visual);
    }
}

internal sealed class PlayerKarmicArmorState
{
    internal int KarmaLevels;
    internal int Timer = 45;
    internal int ResetTime = 45;
    internal bool Triggered;
    internal Creature.Grasp LastDangerGrasp;
    internal Weapon LastDeflectedWeapon;
    internal int LastDeflectedWeaponClock = -1;
    internal SlugCatKarmicArmorVisual Armor;

    internal bool IsProtected => KarmaLevels > 0;

    internal void Reset(int karmaLevels)
    {
        KarmaLevels = karmaLevels;
        Timer = 45;
        ResetTime = 45;
        Triggered = false;
        LastDangerGrasp = null;
        LastDeflectedWeapon = null;
        LastDeflectedWeaponClock = -1;
        Armor = null;
    }

    internal bool TryMarkWeaponDeflection(Weapon weapon, int gameClock)
    {
        if (ReferenceEquals(LastDeflectedWeapon, weapon) &&
            LastDeflectedWeaponClock == gameClock)
        {
            return false;
        }

        LastDeflectedWeapon = weapon;
        LastDeflectedWeaponClock = gameClock;
        return true;
    }

    internal void DetachArmor(SlugCatKarmicArmorVisual armor)
    {
        if (ReferenceEquals(Armor, armor))
        {
            Armor = null;
        }
    }
}
