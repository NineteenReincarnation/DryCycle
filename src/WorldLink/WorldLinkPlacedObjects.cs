using DevInterface;
using RWCustom;
using UnityEngine;

namespace DryCycle.WorldLink;

internal static class WorldLinkPlacedObjects
{
    private static bool _enabled;
    internal static PlacedObject.Type ControllerType { get; private set; }
    internal static PlacedObject.Type PortType { get; private set; }
    internal static ObjectsPage.DevObjectCategories Category { get; private set; }

    internal static void Enable()
    {
        if (_enabled) return;
        _enabled = true;
        ControllerType = new PlacedObject.Type("MultiGateController", true);
        PortType = new PlacedObject.Type("MultiGatePort", true);
        Category = new ObjectsPage.DevObjectCategories("DryCycle-WorldLink", true);
        WorldLinkRoomRegistry.SetEnabled(true);
        On.PlacedObject.GenerateEmptyData += GenerateEmptyData;
        On.DevInterface.ObjectsPage.DevObjectGetCategoryFromPlacedType += CategoryFor;
        On.DevInterface.ObjectsPage.CreateObjRep += CreateRepresentation;
        On.Room.Loaded += RoomLoaded;
    }

    internal static void Disable()
    {
        if (!_enabled) return;
        _enabled = false;
        WorldLinkRoomRegistry.SetEnabled(false);
        On.PlacedObject.GenerateEmptyData -= GenerateEmptyData;
        On.DevInterface.ObjectsPage.DevObjectGetCategoryFromPlacedType -= CategoryFor;
        On.DevInterface.ObjectsPage.CreateObjRep -= CreateRepresentation;
        On.Room.Loaded -= RoomLoaded;
        Category?.Unregister(); Category = null;
        ControllerType?.Unregister(); ControllerType = null;
        PortType?.Unregister(); PortType = null;
        WorldLinkRoomRegistry.Clear();
    }

    private static void GenerateEmptyData(On.PlacedObject.orig_GenerateEmptyData orig, PlacedObject self)
    {
        orig(self);
        if (self.type == ControllerType) self.data = new MultiGateControllerData(self);
        else if (self.type == PortType) self.data = new MultiGatePortData(self);
    }

    private static ObjectsPage.DevObjectCategories CategoryFor(On.DevInterface.ObjectsPage.orig_DevObjectGetCategoryFromPlacedType orig, ObjectsPage self, PlacedObject.Type type)
    {
        return type == ControllerType || type == PortType ? Category : orig(self, type);
    }

    private static void CreateRepresentation(On.DevInterface.ObjectsPage.orig_CreateObjRep orig, ObjectsPage self, PlacedObject.Type type, PlacedObject placed)
    {
        if (type != ControllerType && type != PortType)
        {
            orig(self, type, placed);
            return;
        }

        if (placed == null)
        {
            placed = new PlacedObject(type, null)
            {
                pos = self.owner.room.game.cameras[0].pos + Vector2.Lerp(self.owner.mousePos, new Vector2(-683f, 384f), 0.25f) + Custom.DegToVec(Random.value * 360f) * 0.2f
            };
            self.RoomSettings.placedObjects.Add(placed);
        }

        if (type == ControllerType && placed.data is not MultiGateControllerData) placed.data = new MultiGateControllerData(placed);
        if (type == PortType && placed.data is not MultiGatePortData) placed.data = new MultiGatePortData(placed);

        PlacedObjectRepresentation rep = type == ControllerType
            ? new MultiGateControllerRepresentation(self.owner, self, placed)
            : new MultiGatePortRepresentation(self.owner, self, placed);
        self.tempNodes.Add(rep);
        self.subNodes.Add(rep);
        WorldLinkRoomRegistry.BuildForRoom(self.owner.room);
    }

    private static void RoomLoaded(On.Room.orig_Loaded orig, Room self)
    {
        orig(self);
        if (self?.roomSettings?.placedObjects == null) return;
        WorldLinkRoomRegistry.BuildForRoom(self);
    }
}
