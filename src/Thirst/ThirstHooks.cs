using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DryCycle.HUD;
using Menu;

namespace DryCycle.Thirst;

internal static class ThirstHooks
{
    private sealed class MeatHydrationState
    {
        public int InitialMeat;
    }

    private static readonly ConditionalWeakTable<Creature, MeatHydrationState> MeatStates = new();
    private static bool _enabled;

    public static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;

        ThirstMeter.Enable();

        On.Player.Update += Player_Update;
        On.Player.ObjectEaten += Player_ObjectEaten;
        On.Player.EatMeatUpdate += Player_EatMeatUpdate;
        On.ShelterDoor.Close += ShelterDoor_Close;
        On.SaveState.LoadGame += SaveState_LoadGame;
        On.SaveState.SaveToString += SaveState_SaveToString;
        On.SaveState.SessionEnded += SaveState_SessionEnded;
        On.Menu.SleepAndDeathScreen.GetDataFromGame += SleepAndDeathScreen_GetDataFromGame;
        On.Menu.SlugcatSelectMenu.SlugcatPageContinue.ctor += SlugcatPageContinue_ctor;
    }

    public static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;

        On.Player.Update -= Player_Update;
        On.Player.ObjectEaten -= Player_ObjectEaten;
        On.Player.EatMeatUpdate -= Player_EatMeatUpdate;
        On.ShelterDoor.Close -= ShelterDoor_Close;
        On.SaveState.LoadGame -= SaveState_LoadGame;
        On.SaveState.SaveToString -= SaveState_SaveToString;
        On.SaveState.SessionEnded -= SaveState_SessionEnded;
        On.Menu.SleepAndDeathScreen.GetDataFromGame -= SleepAndDeathScreen_GetDataFromGame;
        On.Menu.SlugcatSelectMenu.SlugcatPageContinue.ctor -= SlugcatPageContinue_ctor;

        ThirstMeter.Disable();
    }

    private static void Player_Update(On.Player.orig_Update orig, Player self, bool eu)
    {
        orig(self, eu);

        if (!IsStoryPlayer(self))
        {
            return;
        }

        ThirstState state = ThirstStore.For(self);

        bool fullySubmerged = self.bodyChunks != null &&
                              self.bodyChunks.Length >= 2 &&
                              self.bodyChunks[0].submersion > 0.9f &&
                              self.bodyChunks[1].submersion > 0.9f;

        if (self.dead || !self.Consious || self.input == null || self.input.Length == 0)
        {
            state.IsDrinking = false;
            return;
        }

        bool breathBarActive = self.airInLungs < 0.999f;
        bool wantsToDrink = self.room != null &&
                            !self.inShortcut &&
                            self.input[0].pckp &&
                            fullySubmerged &&
                            breathBarActive &&
                            state.Water < ThirstConstants.MaxWater;

        // Shortcut travel can retain stale submersion values for a short time.
        // Require a realized room and !inShortcut so water cannot be gained while
        // the player is actually travelling through a transition pipe.
        state.IsDrinking = wantsToDrink;

        if (wantsToDrink)
        {
            ThirstMeter.ShowDrinking(self);
            ThirstStore.AddRuntime(self, ThirstConstants.DrinkPerTick);
        }
    }

    private static void Player_ObjectEaten(On.Player.orig_ObjectEaten orig, Player self, IPlayerEdible edible)
    {
        float water = FoodWaterTable.ForEdible(edible);
        bool nourishmentAllowed = edible != null &&
                                  SlugcatStats.NourishmentOfObjectEaten(self.SlugCatClass, edible) != -1;

        orig(self, edible);

        // Vanilla uses nourishment == -1 for interactions that return before the
        // player is actually fed. Those interactions must not grant hydration.
        if (water > 0f && nourishmentAllowed)
        {
            AddHydration(self, water);
        }
    }

    private static void Player_EatMeatUpdate(On.Player.orig_EatMeatUpdate orig, Player self, int graspIndex)
    {
        Creature creature = null;
        int before = 0;

        if (self.grasps != null && graspIndex >= 0 && graspIndex < self.grasps.Length)
        {
            creature = self.grasps[graspIndex]?.grabbed as Creature;

            if (creature != null)
            {
                before = creature.State.meatLeft;
                MeatHydrationState meatState = MeatStates.GetOrCreateValue(creature);

                if (meatState.InitialMeat <= 0)
                {
                    meatState.InitialMeat = Math.Max(1, before);
                }
            }
        }

        orig(self, graspIndex);

        if (creature == null || !IsStoryPlayer(self))
        {
            return;
        }

        int after = creature.State.meatLeft;
        int consumed = Math.Max(0, before - after);

        if (consumed <= 0)
        {
            return;
        }

        float totalWater = FoodWaterTable.ForCreature(creature);
        if (totalWater <= 0f)
        {
            return;
        }

        int totalMeat = Math.Max(1, MeatStates.GetOrCreateValue(creature).InitialMeat);
        AddHydration(self, totalWater * consumed / totalMeat);
    }

    private static void ShelterDoor_Close(On.ShelterDoor.orig_Close orig, ShelterDoor self)
    {
        RainWorldGame game = self?.room?.game;

        if (game != null &&
            game.IsStorySession &&
            ModManager.CoopAvailable &&
            game.PlayersToProgressOrWin != null &&
            game.PlayersToProgressOrWin.Count > 1)
        {
            if (RejectJollyHibernateForHydration(self))
            {
                return;
            }
        }
        else
        {
            Player player = FindPrimaryPlayer(self);

            if (player != null && !player.stillInStartShelter && IsStoryPlayer(player))
            {
                bool starvationAttempt = player.sleepCounter < 0 ||
                                         player.forceSleepCounter > 260 ||
                                         player.ReadyForStarveJolly;

                bool normalAttempt = player.readyForWin || player.ReadyForWinJolly;
                bool waterEnough = ThirstStore.For(player).Water + 0.0001f >=
                                   ThirstConstants.HibernateRequirement;

                if (normalAttempt && !starvationAttempt && !waterEnough)
                {
                    player.readyForWin = false;
                    player.touchedNoInputCounter = 0;
                    ThirstMeter.TryReject(player);
                    return;
                }
            }
        }

        orig(self);
    }

    private static void SaveState_LoadGame(On.SaveState.orig_LoadGame orig, SaveState self, string str, RainWorldGame game)
    {
        orig(self, str, game);
        ThirstStore.ReadFromUnrecognizedData(self);
    }

    private static string SaveState_SaveToString(On.SaveState.orig_SaveToString orig, SaveState self)
    {
        ThirstStore.WriteToUnrecognizedData(self);
        return orig(self);
    }

    private static void SaveState_SessionEnded(
        On.SaveState.orig_SessionEnded orig,
        SaveState self,
        RainWorldGame game,
        bool survived,
        bool newMalnourished)
    {
        bool specialWarpSave = self != null && self.sessionEndingFromSpinningTopEncounter;

        if (survived)
        {
            SaveNextCycleHydration(self, game, newMalnourished, specialWarpSave);
            // SaveState.SessionEnded writes progression from inside orig(), so
            // DryCycle must update unrecognized save strings before calling it.
            ThirstStore.WriteToUnrecognizedData(self);
        }

        orig(self, game, survived, newMalnourished);

        if (!survived)
        {
            return;
        }

        if (newMalnourished && !specialWarpSave)
        {
            self.food = 0;
        }

        ThirstStore.WriteToUnrecognizedData(self);
    }

    private static void SaveNextCycleHydration(
        SaveState saveState,
        RainWorldGame game,
        bool newMalnourished,
        bool specialWarpSave)
    {
        if (saveState == null)
        {
            return;
        }

        bool wrotePlayer = false;

        if (game?.Players != null)
        {
            foreach (AbstractCreature abstractPlayer in game.Players)
            {
                if (abstractPlayer?.state is not PlayerState playerState)
                {
                    continue;
                }

                int playerNumber = playerState.playerNumber;
                float currentWater = ThirstStore.GetRuntimeWater(game, saveState, playerNumber);
                float nextWater = specialWarpSave
                    ? currentWater
                    : (newMalnourished
                        ? 0f
                        : Math.Max(0f, currentWater - ThirstConstants.HibernateCost));

                ThirstStore.SetSaved(saveState, playerNumber, nextWater);
                wrotePlayer = true;
            }
        }

        if (!wrotePlayer)
        {
            float currentWater = ThirstStore.GetSaved(saveState, 0);
            float nextWater = specialWarpSave
                ? currentWater
                : (newMalnourished
                    ? 0f
                    : Math.Max(0f, currentWater - ThirstConstants.HibernateCost));

            ThirstStore.SetSaved(saveState, 0, nextWater);
        }
    }

    private static void SleepAndDeathScreen_GetDataFromGame(
        On.Menu.SleepAndDeathScreen.orig_GetDataFromGame orig,
        SleepAndDeathScreen self,
        KarmaLadderScreen.SleepDeathScreenDataPackage package)
    {
        orig(self, package);

        if ((self.IsSleepScreen || self.IsStarveScreen) &&
            self.hud?.foodMeter != null &&
            package?.saveState != null)
        {
            bool animateHibernateCost = self.IsSleepScreen &&
                                        !self.goalMalnourished &&
                                        !package.saveState.sessionEndingFromSpinningTopEncounter;

            // The vanilla sleep screen has one FoodMeter. Keep its existing
            // single-player presentation bound to player 0's saved hydration;
            // gameplay Jolly HUD switches owner with the focused player.
            ThirstMeter.ConfigureSleep(
                self.hud.foodMeter,
                package.saveState,
                self,
                animateHibernateCost);
        }
    }

    private static void SlugcatPageContinue_ctor(
        On.Menu.SlugcatSelectMenu.SlugcatPageContinue.orig_ctor orig,
        SlugcatSelectMenu.SlugcatPageContinue self,
        Menu.Menu menu,
        MenuObject owner,
        int pageIndex,
        SlugcatStats.Name slugcatNumber)
    {
        orig(self, menu, owner, pageIndex, slugcatNumber);

        if (self.hud?.foodMeter == null || menu?.manager?.rainWorld?.progression == null)
        {
            return;
        }

        float water = ThirstStore.GetForCharacterSelect(
            menu.manager.rainWorld.progression,
            slugcatNumber);

        ThirstMeter.ConfigureCharacterSelect(self.hud.foodMeter, water);
    }

    private static void AddHydration(Player player, float amount)
    {
        if (amount <= 0f || !IsStoryPlayer(player))
        {
            return;
        }

        ThirstState state = ThirstStore.For(player);
        float beforeWater = state.Water;

        if (ThirstStore.AddRuntime(player, amount))
        {
            float afterWater = ThirstStore.For(player).Water;

            // Food hydration is applied to gameplay state immediately, but the
            // HUD is explicitly told the pre/post values so it can replay the
            // same continuous rising surface and moving wave used while drinking.
            ThirstMeter.ShowHydrationGain(player, beforeWater, afterWater);
        }
    }

    private static bool RejectJollyHibernateForHydration(ShelterDoor door)
    {
        RainWorldGame game = door?.room?.game;
        if (game?.PlayersToProgressOrWin == null)
        {
            return false;
        }

        bool anyLivingPlayer = false;
        bool anyStarvationAttempt = false;
        bool allNormalReady = true;
        List<Player> livingPlayers = new();

        foreach (AbstractCreature abstractPlayer in game.PlayersToProgressOrWin)
        {
            if (abstractPlayer?.state is not PlayerState playerState ||
                playerState.dead ||
                playerState.permaDead)
            {
                continue;
            }

            anyLivingPlayer = true;
            Player player = abstractPlayer.realizedCreature as Player;

            if (player == null || player.isNPC || player.room != door.room)
            {
                allNormalReady = false;
                continue;
            }

            livingPlayers.Add(player);

            if (player.ReadyForStarveJolly ||
                player.sleepCounter < 0 ||
                player.forceSleepCounter > 260)
            {
                anyStarvationAttempt = true;
            }

            if (!player.ReadyForWinJolly)
            {
                allNormalReady = false;
            }
        }

        // Match vanilla Jolly behavior: a starvation attempt may close the
        // shelter even when normal ready conditions are not met. Hydration does
        // not block that path; successful starvation sleep will zero each saved
        // player's hydration in SessionEnded.
        if (!anyLivingPlayer || anyStarvationAttempt || !allNormalReady)
        {
            return false;
        }

        bool rejected = false;

        foreach (Player player in livingPlayers)
        {
            if (ThirstStore.For(player).Water + 0.0001f >= ThirstConstants.HibernateRequirement)
            {
                continue;
            }

            player.readyForWin = false;
            player.ReadyForWinJolly = false;
            player.touchedNoInputCounter = 0;
            ThirstMeter.TryReject(player);
            rejected = true;
        }

        return rejected;
    }

    private static Player FindPrimaryPlayer(ShelterDoor door)
    {
        if (door?.room?.game?.Players == null)
        {
            return null;
        }

        foreach (AbstractCreature abstractPlayer in door.room.game.Players)
        {
            if (abstractPlayer?.realizedCreature is Player player &&
                player.room == door.room &&
                !player.dead &&
                !player.isNPC)
            {
                return player;
            }
        }

        return null;
    }

    private static bool IsStoryPlayer(Player player)
    {
        if (player == null || player.isNPC)
        {
            return false;
        }

        RainWorldGame game = player.room?.game ?? player.abstractCreature?.world?.game;
        return game != null && game.IsStorySession;
    }
}
