using System;
using System.Runtime.CompilerServices;
using DryCycle.HUD;
using Menu;

namespace DryCycle.Thirst;

internal static class ThirstHooks
{
    private sealed class MeatHydrationState
    {
        public MeatHydrationState()
        {
        }

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

        // Shortcut travel can leave the previous room's submersion values on a
        // realized Player for a short time. Explicitly requiring a realized room
        // and !inShortcut prevents drinking/wave animation from continuing while
        // the player is actually inside a room-transition pipe.
        state.IsDrinking = wantsToDrink;

        if (wantsToDrink)
        {
            // Jolly story co-op uses one shared hydration pool, matching the
            // single vanilla food meter shown by the shared story HUD. Any human
            // player may drink and the common water amount updates immediately.
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

        // Vanilla uses nourishment == -1 for special/invalid food interactions
        // that return before actually feeding the player. Do not grant hydration
        // for those cases just because ObjectEaten was entered.
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

        if (game != null && game.IsStorySession && ModManager.CoopAvailable &&
            game.PlayersToProgressOrWin != null && game.PlayersToProgressOrWin.Count > 1)
        {
            if (TryGetJollySleepState(self, out bool normalAttempt, out bool starvationAttempt) &&
                normalAttempt &&
                !starvationAttempt &&
                ThirstStore.GetRuntimeWater(game, game.GetStorySession.saveState) + 0.0001f <
                    ThirstConstants.HibernateRequirement)
            {
                RejectJollyHibernate(self);
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
        float currentWater = GetCurrentWater(game, self);
        bool specialWarpSave = self != null && self.sessionEndingFromSpinningTopEncounter;

        if (survived)
        {
            // Rain World v1.11.8 also calls SessionEnded(survived: true) for
            // Watcher spinning-top/warp transitions. Vanilla deliberately skips
            // its food hibernation drain in that path, so hydration must likewise
            // be preserved instead of charging the normal 3-point sleep cost.
            // Jolly co-op uses the same shared hydration pool, so the sleep cost
            // is charged exactly once for the party, not once per player.
            float nextCycleWater = specialWarpSave
                ? currentWater
                : (newMalnourished
                    ? 0f
                    : Math.Max(0f, currentWater - ThirstConstants.HibernateCost));

            ThirstStore.SetSaved(self, nextCycleWater);
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

        if (ThirstStore.AddRuntime(player, amount))
        {
            // Food can restore water while the vanilla food meter itself does
            // not change (for example when the stomach is already full). Reveal
            // the shared lower-left cluster so the hydration gain animation is
            // visible regardless of which Jolly player ate the item.
            ThirstMeter.ShowHydrationGain(player);
        }
    }

    private static float GetCurrentWater(RainWorldGame game, SaveState saveState)
    {
        return ThirstStore.GetRuntimeWater(game, saveState);
    }

    private static bool TryGetJollySleepState(
        ShelterDoor door,
        out bool normalAttempt,
        out bool starvationAttempt)
    {
        normalAttempt = true;
        starvationAttempt = false;

        RainWorldGame game = door?.room?.game;
        if (game?.PlayersToProgressOrWin == null || game.PlayersToProgressOrWin.Count <= 1)
        {
            return false;
        }

        bool anyLivingPlayer = false;

        foreach (AbstractCreature abstractPlayer in game.PlayersToProgressOrWin)
        {
            if (abstractPlayer?.state == null || abstractPlayer.state.dead)
            {
                continue;
            }

            anyLivingPlayer = true;
            Player player = abstractPlayer.realizedCreature as Player;

            if (player == null || player.room != door.room || player.isNPC)
            {
                normalAttempt = false;
                continue;
            }

            if (player.ReadyForStarveJolly ||
                player.sleepCounter < 0 ||
                player.forceSleepCounter > 260)
            {
                starvationAttempt = true;
            }

            if (!player.ReadyForWinJolly)
            {
                normalAttempt = false;
            }
        }

        return anyLivingPlayer;
    }

    private static void RejectJollyHibernate(ShelterDoor door)
    {
        RainWorldGame game = door?.room?.game;
        if (game?.Players == null)
        {
            return;
        }

        foreach (AbstractCreature abstractPlayer in game.Players)
        {
            if (abstractPlayer?.realizedCreature is not Player player ||
                player.dead ||
                player.isNPC ||
                player.room != door.room)
            {
                continue;
            }

            player.readyForWin = false;
            player.ReadyForWinJolly = false;
            player.touchedNoInputCounter = 0;
            ThirstMeter.TryReject(player);
        }
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
