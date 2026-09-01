using System.Collections.Generic;
using DevInterface;
using RWCustom;
using UnityEngine;

namespace DryCycle.Weather.HeatWave;

internal static class HeatColumnHooks
{
    private const string PlacedTypeName = "HeatColumn";
    private const string DevCategoryName = "DryCycle-Weather";

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

    internal static void CollectEmitters(Room room, List<HeatColumnEmitterSample> target)
    {
        target.Clear();
        if (room?.roomSettings?.placedObjects == null || PlacedType == null)
        {
            return;
        }

        for (int i = 0; i < room.roomSettings.placedObjects.Count; i++)
        {
            PlacedObject placed = room.roomSettings.placedObjects[i];
            if (placed == null ||
                !placed.active ||
                placed.type != PlacedType ||
                placed.data is not HeatColumnData data)
            {
                continue;
            }

            Vector2 end = placed.pos + data.FlowVector;
            target.Add(new HeatColumnEmitterSample(
                placed.pos,
                end,
                data.Radius,
                data.Strength,
                data.Turbulence,
                data.FlowSpeed,
                data.Expansion,
                data.Pulse));
        }
    }

    private static void PlacedObject_GenerateEmptyData(
        On.PlacedObject.orig_GenerateEmptyData orig,
        PlacedObject self)
    {
        orig(self);
        if (self != null && self.type == PlacedType)
        {
            self.data = new HeatColumnData(self);
        }
    }

    private static ObjectsPage.DevObjectCategories ObjectsPage_DevObjectGetCategoryFromPlacedType(
        On.DevInterface.ObjectsPage.orig_DevObjectGetCategoryFromPlacedType orig,
        ObjectsPage self,
        PlacedObject.Type type)
    {
        return type == PlacedType
            ? DevCategory
            : orig(self, type);
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

        if (placedObject.data is not HeatColumnData)
        {
            placedObject.data = new HeatColumnData(placedObject);
        }

        PlacedObjectRepresentation representation = new HeatColumnRepresentation(
            self.owner,
            type + "_Rep",
            self,
            placedObject);
        self.tempNodes.Add(representation);
        self.subNodes.Add(representation);
    }
}
