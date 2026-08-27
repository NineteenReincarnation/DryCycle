using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DevInterface;
using RWCustom;
using UnityEngine;

namespace DryCycle.Items.DewPod;

internal static class DewPodPlantHooks
{
    private const string PlacedTypeName = "DewPodPlant";
    private const int HarvestFramesRequired = 20;
    private const float HarvestRange = 42f;

    internal sealed class PlantRuntimeState
    {
        public int CycleNumber;
        public int InitialMask;
        public int HarvestedMask;
        public bool ConsumptionReported;
        public bool Dormant;
    }

    private sealed class GamePlantState
    {
        public readonly Dictionary<long, PlantRuntimeState> Plants = new();
    }

    private sealed class PlayerHarvestState
    {
        public DewPodPlant Plant;
        public int Slot = -1;
        public int Hand = -1;
        public int Progress;
        public bool Active;
        public bool RequiresRelease;
    }

    private readonly struct PodCandidate
    {
        public PodCandidate(DewPodPlant plant, int slot, float distance)
        {
            Plant = plant;
            Slot = slot;
            Distance = distance;
        }

        public DewPodPlant Plant { get; }
        public int Slot { get; }
        public float Distance { get; }
    }

    private static readonly ConditionalWeakTable<RainWorldGame, GamePlantState> RuntimeStates = new();
    private static readonly ConditionalWeakTable<Player, PlayerHarvestState> HarvestStates = new();

    private static bool _enabled;

    public static PlacedObject.Type PlacedType { get; private set; }

    public static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        PlacedType = new PlacedObject.Type(PlacedTypeName, register: true);

        On.PlacedObject.GenerateEmptyData += PlacedObject_GenerateEmptyData;
        On.Room.Loaded += Room_Loaded;
        On.DevInterface.ObjectsPage.DevObjectGetCategoryFromPlacedType += ObjectsPage_DevObjectGetCategoryFromPlacedType;
        On.DevInterface.ObjectsPage.CreateObjRep += ObjectsPage_CreateObjRep;
        On.Player.Update += Player_Update;
        On.Player.GrabUpdate += Player_GrabUpdate;
        On.PlayerGraphics.Update += PlayerGraphics_Update;
        On.SlugcatHand.Update += SlugcatHand_Update;
    }

    public static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;

        On.PlacedObject.GenerateEmptyData -= PlacedObject_GenerateEmptyData;
        On.Room.Loaded -= Room_Loaded;
        On.DevInterface.ObjectsPage.DevObjectGetCategoryFromPlacedType -= ObjectsPage_DevObjectGetCategoryFromPlacedType;
        On.DevInterface.ObjectsPage.CreateObjRep -= ObjectsPage_CreateObjRep;
        On.Player.Update -= Player_Update;
        On.Player.GrabUpdate -= Player_GrabUpdate;
        On.PlayerGraphics.Update -= PlayerGraphics_Update;
        On.SlugcatHand.Update -= SlugcatHand_Update;

        PlacedType?.Unregister();
        PlacedType = null;
    }

    private static void PlacedObject_GenerateEmptyData(
        On.PlacedObject.orig_GenerateEmptyData orig,
        PlacedObject self)
    {
        orig(self);

        if (self != null && self.type == PlacedType && self.data is not PlacedObject.ConsumableObjectData)
        {
            self.data = new PlacedObject.ConsumableObjectData(self);
        }
    }

    private static ObjectsPage.DevObjectCategories ObjectsPage_DevObjectGetCategoryFromPlacedType(
        On.DevInterface.ObjectsPage.orig_DevObjectGetCategoryFromPlacedType orig,
        ObjectsPage self,
        PlacedObject.Type type)
    {
        if (type == PlacedType && DewPodHooks.DevCategory != null)
        {
            return DewPodHooks.DevCategory;
        }

        return orig(self, type);
    }

    private static void ObjectsPage_CreateObjRep(
        On.DevInterface.ObjectsPage.orig_CreateObjRep orig,
        ObjectsPage self,
        PlacedObject.Type type,
        PlacedObject placedObject)
    {
        if (type != PlacedType)
        {
            orig(self, type, placedObject);
            return;
        }

        if (placedObject == null)
        {
            placedObject = new PlacedObject(type, null)
            {
                pos = self.owner.room.game.cameras[0].pos +
                      Vector2.Lerp(self.owner.mousePos, new Vector2(-683f, 384f), 0.25f) +
                      Custom.DegToVec(UnityEngine.Random.value * 360f) * 0.2f
            };
            self.RoomSettings.placedObjects.Add(placedObject);
        }

        PlacedObjectRepresentation representation = new ConsumableRepresentation(
            self.owner,
            type + "_Rep",
            self,
            placedObject,
            type.ToString());

        self.tempNodes.Add(representation);
        self.subNodes.Add(representation);
    }

    private static void Room_Loaded(On.Room.orig_Loaded orig, Room self)
    {
        orig(self);

        if (self?.abstractRoom == null ||
            self.roomSettings?.placedObjects == null ||
            self.game == null)
        {
            return;
        }

        for (int i = 0; i < self.roomSettings.placedObjects.Count; i++)
        {
            PlacedObject placed = self.roomSettings.placedObjects[i];
            if (placed == null || placed.type != PlacedType || !placed.active)
            {
                continue;
            }

            if (HasPlantInstance(self, i))
            {
                continue;
            }

            PlantRuntimeState runtime = ResolveRuntimeState(self, i);
            if (runtime == null)
            {
                continue;
            }

            self.AddObject(new DewPodPlant(
                self,
                placed,
                self.abstractRoom.index,
                i,
                runtime));
        }
    }

    private static bool HasPlantInstance(Room room, int placedObjectIndex)
    {
        if (room?.updateList == null)
        {
            return false;
        }

        for (int i = 0; i < room.updateList.Count; i++)
        {
            if (room.updateList[i] is DewPodPlant plant &&
                plant.PlacedObjectIndex == placedObjectIndex)
            {
                return true;
            }
        }

        return false;
    }

    private static PlantRuntimeState ResolveRuntimeState(Room room, int placedObjectIndex)
    {
        RainWorldGame game = room?.game;
        if (game == null)
        {
            return null;
        }

        int cycleNumber = GetCycleNumber(game);
        long key = MakeKey(room.abstractRoom.index, placedObjectIndex);
        GamePlantState gameState = RuntimeStates.GetOrCreateValue(game);

        if (gameState.Plants.TryGetValue(key, out PlantRuntimeState existing) &&
            existing.CycleNumber == cycleNumber)
        {
            return existing;
        }

        bool consumed = game.session is StoryGameSession story &&
                        story.saveState.ItemConsumed(
                            room.world,
                            karmaFlower: false,
                            room.abstractRoom.index,
                            placedObjectIndex);

        PlantRuntimeState created;
        if (consumed)
        {
            created = new PlantRuntimeState
            {
                CycleNumber = cycleNumber,
                InitialMask = (1 << DewPodPlant.SlotCount) - 1,
                HarvestedMask = (1 << DewPodPlant.SlotCount) - 1,
                ConsumptionReported = true,
                Dormant = true
            };
        }
        else
        {
            int initialMask = BuildInitialMask(room.abstractRoom.index, placedObjectIndex, cycleNumber);
            created = new PlantRuntimeState
            {
                CycleNumber = cycleNumber,
                InitialMask = initialMask,
                HarvestedMask = 0,
                ConsumptionReported = false,
                Dormant = false
            };
        }

        gameState.Plants[key] = created;
        return created;
    }

    private static int BuildInitialMask(int roomIndex, int placedObjectIndex, int cycleNumber)
    {
        unchecked
        {
            uint hash = 2166136261u;
            hash = (hash ^ (uint)roomIndex) * 16777619u;
            hash = (hash ^ (uint)placedObjectIndex) * 16777619u;
            hash = (hash ^ (uint)cycleNumber) * 16777619u;
            hash ^= hash >> 13;
            hash *= 0x5bd1e995u;
            hash ^= hash >> 15;

            int all = (1 << DewPodPlant.SlotCount) - 1;
            bool fourMature = (hash & 3u) != 0u;
            if (fourMature)
            {
                return all;
            }

            int missingSlot = (int)((hash >> 3) % DewPodPlant.SlotCount);
            return all & ~(1 << missingSlot);
        }
    }

    private static int GetCycleNumber(RainWorldGame game)
    {
        return game?.GetStorySession?.saveState?.cycleNumber ?? 0;
    }

    private static long MakeKey(int roomIndex, int placedObjectIndex)
    {
        return ((long)roomIndex << 32) | (uint)placedObjectIndex;
    }

    private static void Player_GrabUpdate(
        On.Player.orig_GrabUpdate orig,
        Player self,
        bool eu)
    {
        if (self != null &&
            HarvestStates.TryGetValue(self, out PlayerHarvestState state) &&
            state.Active)
        {
            return;
        }

        orig(self, eu);
    }

    private static void Player_Update(On.Player.orig_Update orig, Player self, bool eu)
    {
        orig(self, eu);

        if (self == null)
        {
            return;
        }

        PlayerHarvestState state = HarvestStates.GetOrCreateValue(self);
        UpdateHarvestState(self, state);
    }

    private static void UpdateHarvestState(Player player, PlayerHarvestState state)
    {
        bool pickupHeld = player.input != null &&
                          player.input.Length > 0 &&
                          player.input[0].pckp;

        if (!pickupHeld)
        {
            ResetHarvestState(state, clearReleaseLatch: true);
            return;
        }

        if (state.RequiresRelease)
        {
            state.Active = false;
            return;
        }

        if (player.room == null ||
            player.dead ||
            !player.Consious ||
            player.isNPC ||
            player.inShortcut ||
            player.FreeHand() < 0)
        {
            ResetHarvestState(state, clearReleaseLatch: false);
            return;
        }

        if (!FindNearestPod(player, out PodCandidate candidate))
        {
            ResetHarvestState(state, clearReleaseLatch: false);
            return;
        }

        if (state.Plant != candidate.Plant || state.Slot != candidate.Slot)
        {
            state.Plant = candidate.Plant;
            state.Slot = candidate.Slot;
            state.Hand = player.FreeHand();
            state.Progress = 0;
        }

        if (state.Hand < 0 ||
            player.grasps == null ||
            state.Hand >= player.grasps.Length ||
            player.grasps[state.Hand] != null)
        {
            ResetHarvestState(state, clearReleaseLatch: false);
            return;
        }

        state.Active = true;
        state.Progress = Mathf.Min(state.Progress + 1, HarvestFramesRequired);

        Vector2 tip = candidate.Plant.GetPodPosition(candidate.Slot);
        Vector2 toward = Custom.DirVec(player.mainBodyChunk.pos, tip);
        if (Mathf.Abs(toward.x) > 0.15f)
        {
            player.flipDirection = toward.x < 0f ? -1 : 1;
        }

        float strain = Mathf.InverseLerp(0f, HarvestFramesRequired, state.Progress);
        Vector2 handTarget = player.mainBodyChunk.pos + toward * Mathf.Lerp(8f, 15f, strain);
        candidate.Plant.SetPullInfluence(candidate.Slot, handTarget, strain);

        if (state.Progress < HarvestFramesRequired)
        {
            return;
        }

        if (candidate.Plant.TryHarvest(player, candidate.Slot, state.Hand))
        {
            state.Active = false;
            state.Progress = 0;
            state.RequiresRelease = true;
        }
        else
        {
            ResetHarvestState(state, clearReleaseLatch: false);
        }
    }

    private static bool FindNearestPod(Player player, out PodCandidate candidate)
    {
        candidate = default;
        float bestDistance = float.MaxValue;
        bool found = false;

        if (player?.room?.updateList == null || player.mainBodyChunk == null)
        {
            return false;
        }

        for (int i = 0; i < player.room.updateList.Count; i++)
        {
            if (player.room.updateList[i] is not DewPodPlant plant)
            {
                continue;
            }

            for (int slot = 0; slot < DewPodPlant.SlotCount; slot++)
            {
                if (!plant.IsMatureSlot(slot))
                {
                    continue;
                }

                float distance = Vector2.Distance(
                    player.mainBodyChunk.pos,
                    plant.GetPodPosition(slot));

                if (distance > HarvestRange || distance >= bestDistance)
                {
                    continue;
                }

                bestDistance = distance;
                candidate = new PodCandidate(plant, slot, distance);
                found = true;
            }
        }

        return found;
    }

    private static void PlayerGraphics_Update(
        On.PlayerGraphics.orig_Update orig,
        PlayerGraphics self)
    {
        orig(self);

        Player player = self?.player;
        if (player == null ||
            player.isNPC ||
            self.drawPositions == null ||
            self.drawPositions.GetLength(0) < 2 ||
            self.drawPositions.GetLength(1) < 1 ||
            !HarvestStates.TryGetValue(player, out PlayerHarvestState state) ||
            !state.Active ||
            state.Plant == null ||
            !state.Plant.IsMatureSlot(state.Slot))
        {
            return;
        }

        Vector2 tip = state.Plant.GetPodPosition(state.Slot);
        Vector2 toward = Custom.DirVec(player.mainBodyChunk.pos, tip);
        if (toward.sqrMagnitude < 0.001f)
        {
            toward = new Vector2(player.flipDirection, 0f);
        }

        float strain = Mathf.InverseLerp(0f, HarvestFramesRequired, state.Progress);
        self.drawPositions[0, 0] += toward * Mathf.Lerp(1.2f, 3.8f, strain);
        self.drawPositions[1, 0] -= toward * Mathf.Lerp(0.4f, 1.5f, strain);

        if (self.head != null)
        {
            self.head.pos += toward * Mathf.Lerp(0.5f, 1.7f, strain);
        }
    }

    private static void SlugcatHand_Update(On.SlugcatHand.orig_Update orig, SlugcatHand self)
    {
        PlayerGraphics graphics = self?.owner as PlayerGraphics;
        Player player = graphics?.player;

        if (player != null &&
            !player.isNPC &&
            HarvestStates.TryGetValue(player, out PlayerHarvestState state) &&
            state.Active &&
            state.Hand == self.limbNumber &&
            state.Plant != null &&
            state.Plant.IsMatureSlot(state.Slot))
        {
            Vector2 tip = state.Plant.GetPodPosition(state.Slot);
            float strain = Mathf.InverseLerp(0f, HarvestFramesRequired, state.Progress);
            Vector2 tremor = Custom.RNV() * (0.15f * strain);

            self.reachingForObject = true;
            self.absoluteHuntPos = tip + tremor;
            self.huntSpeed = 18f;
            self.quickness = 0.95f;
        }

        orig(self);
    }

    private static void ResetHarvestState(PlayerHarvestState state, bool clearReleaseLatch)
    {
        state.Plant = null;
        state.Slot = -1;
        state.Hand = -1;
        state.Progress = 0;
        state.Active = false;

        if (clearReleaseLatch)
        {
            state.RequiresRelease = false;
        }
    }
}
