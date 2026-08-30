using DevInterface;
using RWCustom;
using UnityEngine;

namespace DryCycle.TemperatureSystem;

/// <summary>
/// Registers the unified local Environment Zone in DevInterface.
/// New rooms use DryCycleEnvironmentZone. The former DryCycleShadeZone identifier
/// is still recognized (without registering it in the add-object menu) so existing
/// room files continue to load.
/// </summary>
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
        return type != null &&
               (type == PlacedType || type == LegacyPlacedType);
    }

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        PlacedType = new PlacedObject.Type(PlacedTypeName, register: true);
        // Do not register the legacy name: registering it would make a second
        // obsolete button appear in DevTools. ExtEnum equality is value-based, so
        // unregistered instances still match old serialized room objects.
        LegacyPlacedType = new PlacedObject.Type(LegacyPlacedTypeName, register: false);
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
        LegacyPlacedType = null;
    }

    private static void PlacedObject_GenerateEmptyData(
        On.PlacedObject.orig_GenerateEmptyData orig,
        PlacedObject self)
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
        if (IsEnvironmentZoneType(type))
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

        if (newlyPlaced)
        {
            // New Environment Zones start from this room's authored values.
            // Example: RoomShade=0.25 and Humidity=-0.40 => the new panel opens
            // with Shade 0.25 and Humidity -0.40 before any manual edit.
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
