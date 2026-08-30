using DevInterface;
using RWCustom;
using UnityEngine;

namespace DryCycle.TemperatureSystem;

/// <summary>
/// Registers the unified local Environment Zone in DevInterface.
/// The underlying PlacedObject type name stays DryCycleShadeZone for save
/// compatibility with existing rooms.
/// </summary>
internal static class SolarShadeZoneHooks
{
    private const string PlacedTypeName = "DryCycleShadeZone";
    private const string DevCategoryName = "DryCycle-Temperature";

    private static bool _enabled;

    internal static PlacedObject.Type PlacedType { get; private set; }
    internal static ObjectsPage.DevObjectCategories DevCategory { get; private set; }

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        PlacedType = new PlacedObject.Type(PlacedTypeName, register: true);
        DevCategory = new ObjectsPage.DevObjectCategories(DevCategoryName, register: true);

        On.PlacedObject.GenerateEmptyData += PlacedObject_GenerateEmptyData;
        On.DevInterface.ObjectsPage.DevObjectGetCategoryFromPlacedType +=
            ObjectsPage_DevObjectGetCategoryFromPlacedType;
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
        On.DevInterface.ObjectsPage.DevObjectGetCategoryFromPlacedType -=
            ObjectsPage_DevObjectGetCategoryFromPlacedType;
        On.DevInterface.ObjectsPage.CreateObjRep -= ObjectsPage_CreateObjRep;

        DevCategory?.Unregister();
        DevCategory = null;

        PlacedType?.Unregister();
        PlacedType = null;
    }

    private static void PlacedObject_GenerateEmptyData(
        On.PlacedObject.orig_GenerateEmptyData orig,
        PlacedObject self)
    {
        orig(self);

        if (self != null && self.type == PlacedType)
        {
            self.data = new SolarShadeZoneData(self);
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

        if (newlyPlaced)
        {
            // New Environment Zones start from the authored values of this room.
            // This makes the panel immediately reflect the room configuration before
            // the designer types a local override.
            Room room = self.owner?.room;
            data.SetDefaultsFromRoom(
                SolarEnvironment.GetRoomShade(room),
                HumidityEnvironment.GetRoomHumidity(room));
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
