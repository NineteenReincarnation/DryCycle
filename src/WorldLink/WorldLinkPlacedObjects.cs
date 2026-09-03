using System;
using DevInterface;
using RWCustom;
using UnityEngine;

namespace DryCycle.WorldLink;

internal static class WorldLinkPlacedObjects
{
    private const string ControllerTypeName = "MultiGateController";
    private const string PortTypeName = "MultiGatePort";

    private static bool _enabled;

    internal static PlacedObject.Type ControllerType { get; private set; }
    internal static PlacedObject.Type PortType { get; private set; }
    internal static ObjectsPage.DevObjectCategories Category { get; private set; }

    internal static void Enable()
    {
        if (_enabled) return;
        _enabled = true;

        ControllerType = new PlacedObject.Type(ControllerTypeName, true);
        PortType = new PlacedObject.Type(PortTypeName, true);
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

        Category?.Unregister();
        Category = null;
        ControllerType?.Unregister();
        ControllerType = null;
        PortType?.Unregister();
        PortType = null;
        WorldLinkRoomRegistry.Clear();
    }

    private static void GenerateEmptyData(On.PlacedObject.orig_GenerateEmptyData orig, PlacedObject self)
    {
        // Do not call vanilla first for WorldLink types. In a multi-mod hook chain an
        // outer/inner GenerateEmptyData hook can otherwise replace our custom data again
        // after construction, which makes the representation start with pObj.data of the
        // wrong runtime type and fail during its constructor.
        if (IsControllerType(self?.type))
        {
            self.data = new MultiGateControllerData(self);
            return;
        }
        if (IsPortType(self?.type))
        {
            self.data = new MultiGatePortData(self);
            return;
        }
        orig(self);
    }

    private static ObjectsPage.DevObjectCategories CategoryFor(
        On.DevInterface.ObjectsPage.orig_DevObjectGetCategoryFromPlacedType orig,
        ObjectsPage self,
        PlacedObject.Type type)
    {
        return IsWorldLinkType(type) ? Category : orig(self, type);
    }

    private static void CreateRepresentation(
        On.DevInterface.ObjectsPage.orig_CreateObjRep orig,
        ObjectsPage self,
        PlacedObject.Type type,
        PlacedObject placed)
    {
        if (!IsWorldLinkType(type))
        {
            orig(self, type, placed);
            return;
        }

        if (self?.owner?.room?.game?.cameras == null || self.owner.room.game.cameras.Length == 0)
        {
            Plugin.Logger?.LogError($"WorldLink DevUI: cannot create '{type}' because the Objects page has no active room camera.");
            return;
        }

        bool authoredNow = placed == null;
        if (authoredNow)
        {
            placed = new PlacedObject(type, null)
            {
                pos = VanillaSpawnPosition(self)
            };
            self.RoomSettings.placedObjects.Add(placed);
        }

        EnsureWorldLinkData(placed);

        try
        {
            // The custom representation constructors are deliberately lightweight.
            // Their handles/panels are built on their first Update, after this root has
            // been attached to the ObjectsPage and vanilla has established screen-space
            // placement. This prevents partially-constructed Futile labels leaking at
            // the lower-left corner if an editor widget ever fails to initialize.
            PlacedObjectRepresentation rep = IsControllerType(placed.type)
                ? new MultiGateControllerRepresentation(self.owner, self, placed)
                : new MultiGatePortRepresentation(self.owner, self, placed);

            self.tempNodes.Add(rep);
            self.subNodes.Add(rep);
        }
        catch (Exception ex)
        {
            // Never call orig for a WorldLink object after our base representation has
            // started constructing: doing so leaves leaked custom sprites and then adds
            // a second vanilla representation. Keep the authored object/data intact and
            // log the full exception instead.
            Plugin.Logger?.LogError($"WorldLink DevUI: root representation creation failed for '{type}': {ex}");
            if (authoredNow)
            {
                self.RoomSettings.placedObjects.Remove(placed);
            }
            return;
        }

        try
        {
            WorldLinkRoomRegistry.BuildForRoom(self.owner.room);
        }
        catch (Exception ex)
        {
            // Runtime construction must never break the mapper representation.
            Plugin.Logger?.LogError($"WorldLink: runtime build failed after placing '{type}': {ex}");
        }
    }

    internal static void EnsureWorldLinkData(PlacedObject placed)
    {
        if (placed == null) return;
        if (IsControllerType(placed.type))
        {
            if (placed.data is not MultiGateControllerData)
            {
                Plugin.Logger?.LogWarning("WorldLink: repairing MultiGateController data created by another GenerateEmptyData hook.");
                placed.data = new MultiGateControllerData(placed);
            }
            return;
        }

        if (IsPortType(placed.type) && placed.data is not MultiGatePortData)
        {
            Plugin.Logger?.LogWarning("WorldLink: repairing MultiGatePort data created by another GenerateEmptyData hook.");
            placed.data = new MultiGatePortData(placed);
        }
    }

    private static Vector2 VanillaSpawnPosition(ObjectsPage self)
    {
        return self.owner.room.game.cameras[0].pos
            + Vector2.Lerp(self.owner.mousePos, new Vector2(-683f, 384f), 0.25f)
            + Custom.DegToVec(UnityEngine.Random.value * 360f) * 0.2f;
    }

    internal static bool IsWorldLinkType(PlacedObject.Type type) => IsControllerType(type) || IsPortType(type);

    internal static bool IsControllerType(PlacedObject.Type type) =>
        type != null && (type == ControllerType || string.Equals(type.value, ControllerTypeName, StringComparison.Ordinal));

    internal static bool IsPortType(PlacedObject.Type type) =>
        type != null && (type == PortType || string.Equals(type.value, PortTypeName, StringComparison.Ordinal));

    private static void RoomLoaded(On.Room.orig_Loaded orig, Room self)
    {
        orig(self);
        if (self?.roomSettings?.placedObjects == null) return;
        WorldLinkRoomRegistry.BuildForRoom(self);
    }
}
