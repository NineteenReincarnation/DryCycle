using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DevInterface;
using DryCycle.HUD;
using DryCycle.Thirst;
using UnityEngine;

namespace DryCycle.Items.DewPod;

internal static class DewPodHooks
{
    private const string ObjectTypeName = "DewPod";
    private const string PlacedTypeName = "DewPod";
    private const string DevCategoryName = "DryCycle";
    private const string WaterPrefix = "DRYCYCLE_DEWPOD_WATER=";
    private const string BrokenPrefix = "DRYCYCLE_DEWPOD_BROKEN=";

    private sealed class DrinkVisualState
    {
        public int Hand = -1;
        public int Frames;
    }

    private static readonly ConditionalWeakTable<Player, DrinkVisualState> DrinkVisualStates = new();
    private static bool _enabled;

    public static AbstractPhysicalObject.AbstractObjectType ObjectType { get; private set; }
    public static PlacedObject.Type PlacedType { get; private set; }
    public static ObjectsPage.DevObjectCategories DevCategory { get; private set; }

    public static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;

        ObjectType = new AbstractPhysicalObject.AbstractObjectType(ObjectTypeName, register: true);
        PlacedType = new PlacedObject.Type(PlacedTypeName, register: true);
        DevCategory = new ObjectsPage.DevObjectCategories(DevCategoryName, register: true);

        On.AbstractPhysicalObject.Realize += AbstractPhysicalObject_Realize;
        On.AbstractConsumable.IsTypeConsumable += AbstractConsumable_IsTypeConsumable;
        On.SaveState.AbstractPhysicalObjectFromString += SaveState_AbstractPhysicalObjectFromString;
        On.PlacedObject.GenerateEmptyData += PlacedObject_GenerateEmptyData;
        On.Room.Loaded += Room_Loaded;
        On.DevInterface.ObjectsPage.DevObjectGetCategoryFromPlacedType += ObjectsPage_DevObjectGetCategoryFromPlacedType;
        On.Player.Grabability += Player_Grabability;
        On.Player.GrabUpdate += Player_GrabUpdate;
        On.PlayerGraphics.Update += PlayerGraphics_Update;
    }

    public static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;

        On.AbstractPhysicalObject.Realize -= AbstractPhysicalObject_Realize;
        On.AbstractConsumable.IsTypeConsumable -= AbstractConsumable_IsTypeConsumable;
        On.SaveState.AbstractPhysicalObjectFromString -= SaveState_AbstractPhysicalObjectFromString;
        On.PlacedObject.GenerateEmptyData -= PlacedObject_GenerateEmptyData;
        On.Room.Loaded -= Room_Loaded;
        On.DevInterface.ObjectsPage.DevObjectGetCategoryFromPlacedType -= ObjectsPage_DevObjectGetCategoryFromPlacedType;
        On.Player.Grabability -= Player_Grabability;
        On.Player.GrabUpdate -= Player_GrabUpdate;
        On.PlayerGraphics.Update -= PlayerGraphics_Update;

        DevCategory?.Unregister();
        DevCategory = null;

        PlacedType?.Unregister();
        PlacedType = null;

        ObjectType?.Unregister();
        ObjectType = null;
    }

    private static void AbstractPhysicalObject_Realize(
        On.AbstractPhysicalObject.orig_Realize orig,
        AbstractPhysicalObject self)
    {
        orig(self);

        if (self is AbstractDewPod &&
            self.type == ObjectType &&
            self.realizedObject == null)
        {
            self.realizedObject = new DewPod(self);
        }
    }

    private static bool AbstractConsumable_IsTypeConsumable(
        On.AbstractConsumable.orig_IsTypeConsumable orig,
        AbstractPhysicalObject.AbstractObjectType type)
    {
        return type == ObjectType || orig(type);
    }

    private static void PlacedObject_GenerateEmptyData(
        On.PlacedObject.orig_GenerateEmptyData orig,
        PlacedObject self)
    {
        orig(self);

        if (self != null && self.type == PlacedType && self.data == null)
        {
            self.data = new PlacedObject.ConsumableObjectData(self);
        }
    }

    private static ObjectsPage.DevObjectCategories ObjectsPage_DevObjectGetCategoryFromPlacedType(
        On.DevInterface.ObjectsPage.orig_DevObjectGetCategoryFromPlacedType orig,
        ObjectsPage self,
        PlacedObject.Type type)
    {
        if (type == PlacedType)
        {
            return DevCategory;
        }

        return orig(self, type);
    }

    private static void Room_Loaded(On.Room.orig_Loaded orig, Room self)
    {
        bool firstRealization = self?.abstractRoom?.firstTimeRealized ?? false;
        orig(self);

        if (!firstRealization ||
            self?.abstractRoom == null ||
            self.roomSettings?.placedObjects == null ||
            self.world == null ||
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

            if (self.game.session is StoryGameSession story &&
                story.saveState.ItemConsumed(
                    self.world,
                    karmaFlower: false,
                    self.abstractRoom.index,
                    i))
            {
                continue;
            }

            AbstractDewPod abstractPod = new(
                self.world,
                self.GetWorldCoordinate(placed.pos),
                self.game.GetNewID(),
                self.abstractRoom.index,
                i,
                placed.data as PlacedObject.ConsumableObjectData,
                AbstractDewPod.MaxWaterWV,
                broken: false)
            {
                isConsumed = false
            };

            self.abstractRoom.AddEntity(abstractPod);
            abstractPod.placedObjectOrigin = self.SetAbstractRoomAndPlacedObjectNumber(
                self.abstractRoom.name,
                i);
            abstractPod.RealizeInRoom();
        }
    }

    private static Player.ObjectGrabability Player_Grabability(
        On.Player.orig_Grabability orig,
        Player self,
        PhysicalObject obj)
    {
        if (obj is DewPod)
        {
            return Player.ObjectGrabability.OneHand;
        }

        return orig(self, obj);
    }

    private static void Player_GrabUpdate(
        On.Player.orig_GrabUpdate orig,
        Player self,
        bool eu)
    {
        orig(self, eu);

        if (self == null)
        {
            return;
        }

        DrinkVisualState visual = DrinkVisualStates.GetOrCreateValue(self);
        visual.Hand = -1;
        visual.Frames = 0;

        if (!CanDrinkFromPod(self) ||
            !TryGetHeldPod(self, out DewPod pod, out int hand))
        {
            return;
        }

        ThirstState thirst = ThirstStore.For(self);
        float maxWaterPips = ThirstStore.GetMaxWaterPips(self);
        float missingWV = Mathf.Max(
            0f,
            (maxWaterPips - thirst.Water) * ThirstConstants.WaterValuePerPip);

        if (missingWV <= 0.0001f || pod.WaterWV <= 0.0001f)
        {
            return;
        }

        float requestedWV = Mathf.Min(
            DewPod.DrinkRateWVPerSecond / 40f,
            Mathf.Min(missingWV, pod.WaterWV));

        if (requestedWV <= 0f ||
            !ThirstStore.AddRuntime(
                self,
                requestedWV / ThirstConstants.WaterValuePerPip))
        {
            return;
        }

        pod.RemoveWater(requestedWV);
        thirst.IsDrinking = true;
        ThirstMeter.ShowDrinking(self);

        Vector2 mouth = self.mainBodyChunk.pos + new Vector2(self.flipDirection * 2f, 5f);
        if (self.graphicsModule is PlayerGraphics graphics && graphics.head != null)
        {
            mouth = graphics.head.pos;
        }

        pod.MarkDrinking(mouth);
        visual.Hand = hand;
        visual.Frames = 2;
    }

    private static bool CanDrinkFromPod(Player player)
    {
        if (player == null ||
            player.isNPC ||
            player.room?.game == null ||
            !player.room.game.IsStorySession ||
            player.dead ||
            !player.Consious ||
            player.inShortcut ||
            player.input == null ||
            player.input.Length == 0 ||
            !player.input[0].pckp)
        {
            return false;
        }

        bool fullySubmerged = player.bodyChunks != null &&
                              player.bodyChunks.Length >= 2 &&
                              player.bodyChunks[0].submersion > 0.9f &&
                              player.bodyChunks[1].submersion > 0.9f;

        // When the slugcat is already using DryCycle's normal submerged-drinking
        // path, do not also drain a Dew Pod in the same input hold.
        if (fullySubmerged && player.airInLungs < 0.999f)
        {
            return false;
        }

        return true;
    }

    private static bool TryGetHeldPod(Player player, out DewPod pod, out int hand)
    {
        pod = null;
        hand = -1;

        if (player?.grasps == null)
        {
            return false;
        }

        int limit = Math.Min(2, player.grasps.Length);
        for (int i = 0; i < limit; i++)
        {
            if (player.grasps[i]?.grabbed is DewPod candidate && candidate.WaterWV > 0f)
            {
                pod = candidate;
                hand = i;
                return true;
            }
        }

        return false;
    }

    private static void PlayerGraphics_Update(
        On.PlayerGraphics.orig_Update orig,
        PlayerGraphics self)
    {
        orig(self);

        Player player = self?.player;
        if (player == null ||
            self.hands == null ||
            self.head == null ||
            !DrinkVisualStates.TryGetValue(player, out DrinkVisualState state) ||
            state.Frames <= 0 ||
            state.Hand < 0 ||
            state.Hand >= self.hands.Length ||
            player.grasps == null ||
            state.Hand >= player.grasps.Length ||
            player.grasps[state.Hand]?.grabbed is not DewPod pod)
        {
            return;
        }

        self.LookAtObject(pod);
        Vector2 target = Vector2.Lerp(self.head.pos, player.mainBodyChunk.pos, 0.24f);
        self.hands[state.Hand].pos = Vector2.Lerp(
            self.hands[state.Hand].pos,
            target,
            0.72f);
        state.Frames--;
    }

    private static AbstractPhysicalObject SaveState_AbstractPhysicalObjectFromString(
        On.SaveState.orig_AbstractPhysicalObjectFromString orig,
        World world,
        string objString)
    {
        string[] parts = Regex.Split(objString ?? string.Empty, "<oA>");
        if (parts.Length < 5 || parts[1] != ObjectTypeName)
        {
            return orig(world, objString);
        }

        try
        {
            int rippleLayer = 0;
            EntityID id;

            if (parts[0].Contains("<oB>"))
            {
                string[] idParts = Regex.Split(parts[0], "<oB>");
                id = EntityID.FromString(idParts[0]);
                rippleLayer = int.Parse(
                    idParts[1],
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture);
            }
            else
            {
                id = EntityID.FromString(parts[0]);
            }

            WorldCoordinate pos = WorldCoordinate.FromString(parts[2]);
            int originRoom = int.Parse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture);
            int placedObjectIndex = int.Parse(parts[4], NumberStyles.Any, CultureInfo.InvariantCulture);
            float waterWV = AbstractDewPod.MaxWaterWV;
            bool broken = false;
            List<string> unrecognized = new();

            for (int i = 5; i < parts.Length; i++)
            {
                string attribute = parts[i];

                if (attribute.StartsWith(WaterPrefix, StringComparison.Ordinal))
                {
                    if (float.TryParse(
                        attribute.Substring(WaterPrefix.Length),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out float parsedWater))
                    {
                        waterWV = parsedWater;
                    }
                }
                else if (attribute.StartsWith(BrokenPrefix, StringComparison.Ordinal))
                {
                    string value = attribute.Substring(BrokenPrefix.Length);
                    broken = value == "1" ||
                             value.Equals("true", StringComparison.OrdinalIgnoreCase);
                }
                else if (!string.IsNullOrEmpty(attribute))
                {
                    unrecognized.Add(attribute);
                }
            }

            AbstractDewPod result = new(
                world,
                pos,
                id,
                originRoom,
                placedObjectIndex,
                null,
                waterWV,
                broken)
            {
                rippleLayer = rippleLayer,
                unrecognizedAttributes = unrecognized.ToArray()
            };

            return result;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"Failed to parse DewPod save data: {ex.Message}");
            return orig(world, objString);
        }
    }
}
