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

    private sealed class FullFoodEatState
    {
        public bool Active;
        public int OriginalFood;
        public int OriginalQuarterFood;
        public bool OverflowVisualShownThisHold;
    }

    private static readonly ConditionalWeakTable<Creature, MeatHydrationState> MeatStates = new();
    private static readonly ConditionalWeakTable<Player, FullFoodEatState> FullFoodEatStates = new();
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
        On.Player.GrabUpdate += Player_GrabUpdate;
        On.Player.AddFood += Player_AddFood;
        On.Player.AddQuarterFood += Player_AddQuarterFood;
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
        On.Player.GrabUpdate -= Player_GrabUpdate;
        On.Player.AddFood -= Player_AddFood;
        On.Player.AddQuarterFood -= Player_AddQuarterFood;
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

        // WaterLossRate is WV/second. Each player loses water independently,
        // including while travelling through shortcuts. Dead players stop losing.
        if (!self.dead)
        {
            float passiveLoss = SlugBaseHydrationFeatures.GetWaterLossPerTick(self);
            if (passiveLoss > 0f)
            {
                float beforeWater = state.Water;

                if (ThirstStore.RemoveRuntime(self, passiveLoss))
                {
                    float afterWater = state.Water;

                    // Half a hydration pip is exactly 200 WV. Reveal the vanilla
                    // lower-left HUD each time passive loss crosses another 0.5-pip
                    // boundary. Math.Ceiling prevents a full pip from notifying on
                    // the first tiny WV loss; the notification happens only after
                    // a complete half-pip segment has actually been consumed.
                    if (CrossedHalfPipLossBoundary(beforeWater, afterWater))
                    {
                        self.showKarmaFoodRainTime = Math.Max(
                            self.showKarmaFoodRainTime,
                            ThirstConstants.HydrationLossHudHoldFrames);
                    }
                }
            }
        }

        bool fullySubmerged = self.bodyChunks != null &&
                              self.bodyChunks.Length >= 2 &&
                              self.bodyChunks[0].submersion > 0.9f &&
                              self.bodyChunks[1].submersion > 0.9f;

        if (self.dead || !self.Consious || self.input == null || self.input.Length == 0)
        {
            state.IsDrinking = false;
            return;
        }

        float maxWater = ThirstStore.GetMaxWaterPips(self);
        bool breathBarActive = self.airInLungs < 0.999f;
        bool wantsToDrink = self.room != null &&
                            !self.inShortcut &&
                            self.input[0].pckp &&
                            fullySubmerged &&
                            breathBarActive &&
                            state.Water < maxWater - 0.0001f;

        state.IsDrinking = wantsToDrink;

        if (wantsToDrink)
        {
            ThirstMeter.ShowDrinking(self);
            ThirstStore.AddRuntime(self, ThirstConstants.DrinkPerTick);
        }
    }

    private static void Player_GrabUpdate(On.Player.orig_GrabUpdate orig, Player self, bool eu)
    {
        bool fullHydratingEat = BeginFullHydratingEat(self);

        try
        {
            orig(self, eu);
        }
        finally
        {
            EndFullHydratingEat(self, fullHydratingEat);
        }
    }

    private static void Player_AddFood(On.Player.orig_AddFood orig, Player self, int add)
    {
        if (IsFullHydratingEatActive(self))
        {
            return;
        }

        orig(self, add);
    }

    private static void Player_AddQuarterFood(On.Player.orig_AddQuarterFood orig, Player self)
    {
        if (IsFullHydratingEatActive(self))
        {
            return;
        }

        orig(self);
    }

    private static void Player_ObjectEaten(On.Player.orig_ObjectEaten orig, Player self, IPlayerEdible edible)
    {
        float water = FoodWaterTable.ForEdible(edible);
        bool nourishmentAllowed = edible != null &&
                                  SlugcatStats.NourishmentOfObjectEaten(self.SlugCatClass, edible) != -1;
        bool overflowEat = IsFullHydratingEatActive(self);

        orig(self, edible);

        if (water > 0f && nourishmentAllowed)
        {
            AddHydration(self, water);

            if (overflowEat)
            {
                ThirstMeter.ShowOverflowFoodEat(self);
            }
        }
    }

    private static void Player_EatMeatUpdate(On.Player.orig_EatMeatUpdate orig, Player self, int graspIndex)
    {
        Creature creature = null;
        int before = 0;
        bool overflowEat = IsFullHydratingEatActive(self);

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

        if (overflowEat)
        {
            ThirstMeter.ShowOverflowFoodEat(self);
        }
    }

    private static bool BeginFullHydratingEat(Player player)
    {
        if (player == null || player.playerState == null)
        {
            return false;
        }

        FullFoodEatState state = FullFoodEatStates.GetOrCreateValue(player);
        bool pickupHeld = player.input != null &&
                          player.input.Length > 0 &&
                          player.input[0].pckp;

        if (!pickupHeld)
        {
            state.OverflowVisualShownThisHold = false;
        }

        if (!pickupHeld ||
            !IsStoryPlayer(player) ||
            player.dead ||
            !player.Consious ||
            player.FoodInStomach < player.MaxFoodInStomach ||
            !HasHydratingFoodInVanillaEatSlot(player))
        {
            return false;
        }

        state.Active = true;
        state.OriginalFood = player.playerState.foodInStomach;
        state.OriginalQuarterFood = player.playerState.quarterFoodPoints;

        player.playerState.foodInStomach = Math.Max(0, state.OriginalFood - 1);

        if (!state.OverflowVisualShownThisHold)
        {
            state.OverflowVisualShownThisHold = true;
            ThirstMeter.ShowOverflowFoodEat(player);
        }

        return true;
    }

    private static void EndFullHydratingEat(Player player, bool wasActive)
    {
        if (!wasActive || player?.playerState == null)
        {
            return;
        }

        if (!FullFoodEatStates.TryGetValue(player, out FullFoodEatState state))
        {
            return;
        }

        player.playerState.foodInStomach = state.OriginalFood;
        player.playerState.quarterFoodPoints = state.OriginalQuarterFood;
        state.Active = false;
    }

    private static bool IsFullHydratingEatActive(Player player)
    {
        return player != null &&
               FullFoodEatStates.TryGetValue(player, out FullFoodEatState state) &&
               state.Active;
    }

    private static bool HasHydratingFoodInVanillaEatSlot(Player player)
    {
        if (player?.grasps == null)
        {
            return false;
        }

        if (!ModManager.MSC || player.SlugCatClass != MoreSlugcats.MoreSlugcatsEnums.SlugcatStatsName.Spear)
        {
            int limit = Math.Min(2, player.grasps.Length);
            for (int i = 0; i < limit; i++)
            {
                if (player.grasps[i]?.grabbed is not IPlayerEdible edible || !edible.Edible)
                {
                    continue;
                }

                return FoodWaterTable.ForEdible(edible) > 0f &&
                       SlugcatStats.NourishmentOfObjectEaten(player.SlugCatClass, edible) != -1;
            }
        }

        int meatIndex = 0;
        if (ModManager.MMF &&
            player.grasps.Length > 1 &&
            (player.grasps[0] == null || player.grasps[0].grabbed is not Creature) &&
            player.grasps[1]?.grabbed is Creature)
        {
            meatIndex = 1;
        }

        if (meatIndex >= player.grasps.Length ||
            player.grasps[meatIndex]?.grabbed is not Creature creature ||
            creature.State == null ||
            creature.State.meatLeft <= 0 ||
            creature.Template == null ||
            creature.Template.meatPoints <= 0 ||
            !player.CanEatMeat(creature))
        {
            return false;
        }

        return FoodWaterTable.ForCreature(creature) > 0f;
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
                int requiredPips = SlugBaseHydrationFeatures.GetWaterPips(player);
                bool waterEnough = ThirstStore.For(player).Water + 0.0001f >= requiredPips;

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
                int hibernateCost = SlugBaseHydrationFeatures.GetWaterPips(playerState.slugcatCharacter);
                float currentWater = ThirstStore.GetRuntimeWater(game, saveState, playerNumber);
                float nextWater = specialWarpSave
                    ? currentWater
                    : (newMalnourished
                        ? 0f
                        : Math.Max(0f, currentWater - hibernateCost));

                ThirstStore.SetSaved(saveState, playerNumber, nextWater);
                wrotePlayer = true;
            }
        }

        if (!wrotePlayer)
        {
            int hibernateCost = SlugBaseHydrationFeatures.GetWaterPips(saveState.saveStateNumber);
            float currentWater = ThirstStore.GetSaved(saveState, 0);
            float nextWater = specialWarpSave
                ? currentWater
                : (newMalnourished
                    ? 0f
                    : Math.Max(0f, currentWater - hibernateCost));

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

        if (!anyLivingPlayer || anyStarvationAttempt || !allNormalReady)
        {
            return false;
        }

        bool rejected = false;

        foreach (Player player in livingPlayers)
        {
            int requiredPips = SlugBaseHydrationFeatures.GetWaterPips(player);
            if (ThirstStore.For(player).Water + 0.0001f >= requiredPips)
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
