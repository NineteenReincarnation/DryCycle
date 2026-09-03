using DevInterface;
using RWCustom;
using UnityEngine;

namespace DryCycle.TemperatureSystem;

internal static class SolarShadeZoneHooks
{
    private const string PlacedTypeName = "DryCycleEnvironmentZone";
    private const string LegacyPlacedTypeName = "DryCycleShadeZone";
    private const string DevCategoryName = "DryCycle-Temperature";

    private static bool _enabled;

    internal static PlacedObject.Type PlacedType { get; private set; }
    internal static PlacedObject.Type LegacyPlacedType { get; private set; }
    internal static ObjectsPage.DevObjectCategories DevCategory { get; private set; }

    internal static bool IsEnvironmentZoneType(PlacedObject.Type type)
    {
        return type != null && (type == PlacedType || type == LegacyPlacedType);
    }

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        PlacedType = new PlacedObject.Type(PlacedTypeName, register: true);
        LegacyPlacedType = new PlacedObject.Type(LegacyPlacedTypeName, register: false);
        DevCategory = new ObjectsPage.DevObjectCategories(DevCategoryName, register: true);

        On.PlacedObject.GenerateEmptyData += PlacedObject_GenerateEmptyData;
        On.DevInterface.ObjectsPage.DevObjectGetCategoryFromPlacedType += ObjectsPage_DevObjectGetCategoryFromPlacedType;
        On.DevInterface.ObjectsPage.CreateObjRep += ObjectsPage_CreateObjRep;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;
        On.PlacedObject.GenerateEmptyData -= PlacedObject_GenerateEmptyData;
        On.DevInterface.ObjectsPage.DevObjectGetCategoryFromPlacedType -= ObjectsPage_DevObjectGetCategoryFromPlacedType;
        On.DevInterface.ObjectsPage.CreateObjRep -= ObjectsPage_CreateObjRep;

        DevCategory?.Unregister();
        DevCategory = null;
        PlacedType?.Unregister();
        PlacedType = null;
        LegacyPlacedType = null;
    }

    private static void PlacedObject_GenerateEmptyData(On.PlacedObject.orig_GenerateEmptyData orig, PlacedObject self)
    {
        orig(self);
        if (self != null && IsEnvironmentZoneType(self.type))
        {
            self.data = new SolarShadeZoneData(self);
        }
    }

    private static ObjectsPage.DevObjectCategories ObjectsPage_DevObjectGetCategoryFromPlacedType(
        On.DevInterface.ObjectsPage.orig_DevObjectGetCategoryFromPlacedType orig,
        ObjectsPage self,
        PlacedObject.Type type)
    {
        return IsEnvironmentZoneType(type) ? DevCategory : orig(self, type);
    }

    private static void ObjectsPage_CreateObjRep(
        On.DevInterface.ObjectsPage.orig_CreateObjRep orig,
        ObjectsPage self,
        PlacedObject.Type type,
        PlacedObject placedObject)
    {
        if (!IsEnvironmentZoneType(type))
        {
            orig(self, type, placedObject);
            return;
        }

        bool newlyPlaced = placedObject == null;
        if (newlyPlaced)
        {
            placedObject = new PlacedObject(type, null)
            {
                pos = self.owner.room.game.cameras[0].pos +
                      Vector2.Lerp(self.owner.mousePos, new Vector2(-683f, 384f), 0.25f) +
                      Custom.DegToVec(UnityEngine.Random.value * 360f) * 0.2f
            };
            self.RoomSettings.placedObjects.Add(placedObject);
        }

        if (placedObject.data is not SolarShadeZoneData data)
        {
            data = new SolarShadeZoneData(placedObject);
            placedObject.data = data;
        }

        Room room = self.owner?.room;
        if (newlyPlaced)
        {
            data.SetDefaultsFromRoom(
                RoomHeatFactor.GetAuthoredRoomHeat(room),
                SolarEnvironment.GetRoomShade(room),
                HumidityEnvironment.GetRoomHumidity(room));
        }
        else if (!data.HasRoomHeat)
        {
            data.SetInheritedRoomHeatPreview(RoomHeatFactor.GetAuthoredRoomHeat(room));
        }

        PlacedObjectRepresentation representation = new SolarShadeZoneRepresentation(
            self.owner,
            type + "_Rep",
            self,
            placedObject,
            "Environment Zone");

        self.tempNodes.Add(representation);
        self.subNodes.Add(representation);
    }
}
