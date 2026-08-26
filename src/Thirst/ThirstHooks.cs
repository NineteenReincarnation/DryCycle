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

        On.Player.Update += Player_Update;
        On.Player.ObjectEaten += Player_ObjectEaten;
        On.Player.EatMeatUpdate += Player_EatMeatUpdate;
        On.ShelterDoor.Close += ShelterDoor_Close;
        On.SaveState.LoadGame += SaveState_LoadGame;
        On.SaveState.SaveToString += SaveState_SaveToString;
        On.SaveState.SessionEnded += SaveState_SessionEnded;
        On.RoomCamera.FireUpSinglePlayerHUD += RoomCamera_FireUpSinglePlayerHUD;
        On.Menu.SleepAndDeathScreen.GetDataFromGame += SleepAndDeathScreen_GetDataFromGame;
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
        On.RoomCamera.FireUpSinglePlayerHUD -= RoomCamera_FireUpSinglePlayerHUD;
        On.Menu.SleepAndDeathScreen.GetDataFromGame -= SleepAndDeathScreen_GetDataFromGame;
    }

    private static void Player_Update(On.Player.orig_Update orig, Player self, bool eu)
    {
        orig(self, eu);

        if (!IsStoryPlayer(self))
        {
            return;
        }

        ThirstState state = ThirstStore.For(self);

        if (self.dead || !self.Consious || self.input == null || self.input.Length == 0)
        {
            state.IsDrinking = false;
            return;
        }

        bool fullySubmerged = self.bodyChunks != null &&
                              self.bodyChunks.Length >= 2 &&
                              self.bodyChunks[0].submersion > 0.9f &&
                              self.bodyChunks[1].submersion > 0.9f;

        bool breathBarActive = self.airInLungs < 0.999f;
        bool wantsToDrink = self.input[0].pckp &&
                            fullySubmerged &&
                            breathBarActive &&
                            state.Water < ThirstConstants.MaxWater;

        state.IsDrinking = wantsToDrink;

        if (wantsToDrink)
        {
            state.Add(ThirstConstants.DrinkPerTick);
        }
    }

    private static void Player_ObjectEaten(On.Player.orig_ObjectEaten orig, Player self, IPlayerEdible edible)
    {
        float water = FoodWaterTable.ForEdible(edible);
        orig(self, edible);

        if (water > 0f && IsStoryPlayer(self))
        {
            ThirstStore.For(self).Add(water);
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
        ThirstStore.For(self).Add(totalWater * consumed / totalMeat);
    }

    private static void ShelterDoor_Close(On.ShelterDoor.orig_Close orig, ShelterDoor self)
    {
        Player player = FindPrimaryPlayer(self);

        if (player != null && !player.stillInStartShelter && IsStoryPlayer(player))
        {
            bool starvationAttempt = player.sleepCounter < 0 ||
                                     player.forceSleepCounter > 260 ||
                                     player.ReadyForStarveJolly;

            bool normalAttempt = player.readyForWin || player.ReadyForWinJolly;
            bool waterEnough = ThirstStore.For(player).Water + 0.0001f >= ThirstConstants.HibernateRequirement;

            if (normalAttempt && !starvationAttempt && !waterEnough)
            {
                player.readyForWin = false;
                player.touchedNoInputCounter = 0;
                ThirstMeter.TryReject(player);
                return;
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

        // SessionEnded can serialize the save from inside vanilla code. Put the
        // next-cycle hydration into the SaveState before orig so that any save
        // performed there already contains the two-pip hibernation cost.
        if (survived)
        {
            float nextCycleWater = newMalnourished
                ? 0f
                : Math.Max(0f, currentWater - ThirstConstants.HibernateCost);

            ThirstStore.SetSaved(self, nextCycleWater);
            ThirstStore.WriteToUnrecognizedData(self);
        }

        orig(self, game, survived, newMalnourished);

        if (!survived)
        {
            return;
        }

        if (newMalnourished)
        {
            self.food = 0;
        }

        // Vanilla may rebuild unrecognizedSaveStrings while ending the session,
        // so write the already-calculated value once more after orig as well.
        ThirstStore.WriteToUnrecognizedData(self);
    }

    private static void RoomCamera_FireUpSinglePlayerHUD(
        On.RoomCamera.orig_FireUpSinglePlayerHUD orig,
        RoomCamera self,
        Player player)
    {
        orig(self, player);

        if (player != null && self.hud != null && IsStoryPlayer(player))
        {
            ThirstMeter.Attach(self.hud, player);
        }
    }

    private static void SleepAndDeathScreen_GetDataFromGame(
        On.Menu.SleepAndDeathScreen.orig_GetDataFromGame orig,
        SleepAndDeathScreen self,
        KarmaLadderScreen.SleepDeathScreenDataPackage package)
    {
        orig(self, package);

        // The sleep/starve screen owns its own HUD. Attach the same hydration
        // meter to that HUD so the save screen shows the post-sleep water value.
        if ((self.IsSleepScreen || self.IsStarveScreen) &&
            self.hud != null &&
            package?.saveState != null)
        {
            ThirstMeter.Attach(self.hud, package.saveState);
        }
    }

    private static float GetCurrentWater(RainWorldGame game, SaveState saveState)
    {
        if (game?.Players != null)
        {
            foreach (AbstractCreature abstractPlayer in game.Players)
            {
                if (abstractPlayer?.realizedCreature is Player player && !player.dead)
                {
                    return ThirstStore.For(player).Water;
                }
            }
        }

        return ThirstStore.GetSaved(saveState);
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
                !player.dead)
            {
                return player;
            }
        }

        return null;
    }

    private static bool IsStoryPlayer(Player player)
    {
        return player?.room?.game != null &&
               player.room.game.IsStorySession &&
               !player.isSlugpup;
    }
}
