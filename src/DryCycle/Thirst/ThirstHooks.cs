using System;
using System.Runtime.CompilerServices;
using System.Reflection;
using DryCycle.HUD;

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
        On.Player.Update += Player_Update;
        On.Player.ObjectEaten += Player_ObjectEaten;
        On.Player.EatMeatUpdate += Player_EatMeatUpdate;
        On.ShelterDoor.Close += ShelterDoor_Close;
        On.SaveState.LoadGame += SaveState_LoadGame;
        On.SaveState.SaveToString += SaveState_SaveToString;
        On.SaveState.SessionEnded += SaveState_SessionEnded;
        On.HUD.HUD.InitSinglePlayerHud += HUD_InitSinglePlayerHud;
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
        On.HUD.HUD.InitSinglePlayerHud -= HUD_InitSinglePlayerHud;
    }

    private static void Player_Update(On.Player.orig_Update orig, Player self, bool eu)
    {
        orig(self, eu);

        if (!IsStoryPlayer(self) || self.dead || !self.Consious || self.input == null || self.input.Length == 0)
        {
            return;
        }

        ThirstState state = ThirstStore.For(self);
        bool fullySubmerged = self.bodyChunks != null &&
                              self.bodyChunks.Length >= 2 &&
                              self.bodyChunks[0].submersion > 0.9f &&
                              self.bodyChunks[1].submersion > 0.9f;

        // pckp is Rain World's pickup/eat input (Shift on the default keyboard layout).
        bool breathIsBeingUsed = self.airInLungs < 0.999f;
        bool wantsToDrink = self.input[0].pckp && fullySubmerged && breathIsBeingUsed && state.Water < ThirstConstants.MaxWater;

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
            SaveState saveState = player.room.game.GetStorySession.saveState;
            int foodRequirement = saveState.malnourished ? player.slugcatStats.maxFood : player.slugcatStats.foodToHibernate;
            bool foodEnough = player.FoodInRoom(player.room, false) >= foodRequirement;
            bool waterEnough = ThirstStore.For(player).Water + 0.0001f >= ThirstConstants.HibernateRequirement;

            // Starvation hibernation remains legal when food is insufficient.
            // If food is sufficient, hydration must independently satisfy its requirement.
            if (foodEnough && !waterEnough)
            {
                player.readyForWin = false;
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

    private static void SaveState_SessionEnded(On.SaveState.orig_SessionEnded orig, SaveState self, RainWorldGame game, bool survived, bool newMalnourished)
    {
        float currentWater = GetCurrentWater(game, self);
        orig(self, game, survived, newMalnourished);

        if (!survived)
        {
            return;
        }

        if (newMalnourished)
        {
            // DryCycle starvation hibernation consumes every remaining food pip.
            self.food = 0;
        }

        float nextCycleWater = newMalnourished
            ? 0f
            : Math.Max(0f, currentWater - ThirstConstants.HibernateRequirement);

        ThirstStore.SetSaved(self, nextCycleWater);
        ThirstStore.WriteToUnrecognizedData(self);
    }

    private static void HUD_InitSinglePlayerHud(On.HUD.HUD.orig_InitSinglePlayerHud orig, global::HUD.HUD self, RoomCamera cam)
    {
        orig(self, cam);

        Player player = FindHudPlayer(self, cam);
        if (player != null && IsStoryPlayer(player))
        {
            ThirstMeter.Attach(self, player);
        }
    }

    private static Player FindHudPlayer(global::HUD.HUD hud, RoomCamera cam)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // HUD.owner exists in some reference/decompiled builds, but is absent from
        // at least one 1.11.8 runtime build. Reflection avoids a hard field token,
        // so the hook keeps working instead of throwing MissingFieldException.
        FieldInfo ownerField = hud?.GetType().GetField("owner", flags);
        if (ownerField?.GetValue(hud) is Player ownerPlayer)
        {
            return ownerPlayer;
        }

        // FireUpSinglePlayerHUD is invoked for the camera's followed slugcat.
        // Read this field reflectively for the same cross-build reason.
        FieldInfo followedField = cam?.GetType().GetField("followAbstractCreature", flags);
        if (followedField?.GetValue(cam) is AbstractCreature followed && followed.realizedCreature is Player followedPlayer)
        {
            return followedPlayer;
        }

        return null;
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
            if (abstractPlayer?.realizedCreature is Player player && player.room == door.room && !player.dead)
            {
                return player;
            }
        }

        return null;
    }

    private static bool IsStoryPlayer(Player player)
    {
        return player?.room?.game != null && player.room.game.IsStorySession && !player.isSlugpup;
    }
}
