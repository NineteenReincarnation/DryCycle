using DevInterface;
using DryCycle.Items.DewPod;
using RWCustom;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

internal static class QuicksandZoneHooks
{
    private const string PlacedTypeName = "QuicksandZone";
    private static bool _enabled;

    internal static PlacedObject.Type PlacedType { get; private set; }

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        PlacedType = new PlacedObject.Type(PlacedTypeName, register: true);

        On.PlacedObject.GenerateEmptyData += PlacedObject_GenerateEmptyData;
        On.DevInterface.ObjectsPage.DevObjectGetCategoryFromPlacedType +=
            ObjectsPage_DevObjectGetCategoryFromPlacedType;
        On.DevInterface.ObjectsPage.CreateObjRep += ObjectsPage_CreateObjRep;
        On.Room.Loaded += Room_Loaded;
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
        On.Room.Loaded -= Room_Loaded;

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
            self.data = new QuicksandZoneData(self);
        }
    }

    private static ObjectsPage.DevObjectCategories ObjectsPage_DevObjectGetCategoryFromPlacedType(
        On.DevInterface.ObjectsPage.orig_DevObjectGetCategoryFromPlacedType orig,
        ObjectsPage self,
        PlacedObject.Type type)
    {
        if (type == PlacedType)
        {
            return DewPodHooks.DevCategory ??
                   new ObjectsPage.DevObjectCategories("DryCycle", register: false);
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

        PlacedObjectRepresentation representation = new QuicksandZoneRepresentation(
            self.owner,
            type + "_Rep",
            self,
            placedObject,
            "Quicksand Zone");

        self.tempNodes.Add(representation);
        self.subNodes.Add(representation);
        EnsureRuntimeObject(self.owner.room, placedObject);
    }

    private static void Room_Loaded(On.Room.orig_Loaded orig, Room self)
    {
        orig(self);

        if (self?.roomSettings?.placedObjects == null)
        {
            return;
        }

        for (int i = 0; i < self.roomSettings.placedObjects.Count; i++)
        {
            PlacedObject placedObject = self.roomSettings.placedObjects[i];
            if (placedObject != null && placedObject.type == PlacedType && placedObject.active)
            {
                EnsureRuntimeObject(self, placedObject);
            }
        }
    }

    private static void EnsureRuntimeObject(Room room, PlacedObject placedObject)
    {
        if (room == null || placedObject == null || room.updateList == null)
        {
            return;
        }

        for (int i = 0; i < room.updateList.Count; i++)
        {
            if (room.updateList[i] is QuicksandZone existing &&
                existing.PlacedObject == placedObject)
            {
                return;
            }
        }

        room.AddObject(new QuicksandZone(placedObject));
    }
}
