using System;
using System.Collections.Generic;
using DevInterface;
using RWCustom;
using UnityEngine;

namespace DryCycle.WorldLink;

internal static class WorldLinkPlacedObjects
{
    private const string ControllerTypeName = "MultiGateController";
    private const string PortTypeName = "MultiGatePort";

    private static bool _enabled;
    private static bool _loggedFallbackRepair;

    internal static PlacedObject.Type ControllerType { get; private set; }
    internal static PlacedObject.Type PortType { get; private set; }
    internal static ObjectsPage.DevObjectCategories Category { get; private set; }

    internal static void Enable()
    {
        if (_enabled) return;
        _enabled = true;
        _loggedFallbackRepair = false;
        ControllerType = new PlacedObject.Type(ControllerTypeName, true);
        PortType = new PlacedObject.Type(PortTypeName, true);
        Category = new ObjectsPage.DevObjectCategories("DryCycle-WorldLink", true);
        WorldLinkRoomRegistry.SetEnabled(true);
        On.PlacedObject.GenerateEmptyData += GenerateEmptyData;
        On.DevInterface.ObjectsPage.DevObjectGetCategoryFromPlacedType += CategoryFor;
        On.DevInterface.ObjectsPage.CreateObjRep += CreateRepresentation;
        On.DevInterface.ObjectsPage.Refresh += RefreshObjectsPage;
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
        On.DevInterface.ObjectsPage.Refresh -= RefreshObjectsPage;
        On.Room.Loaded -= RoomLoaded;
        Category?.Unregister(); Category = null;
        ControllerType?.Unregister(); ControllerType = null;
        PortType?.Unregister(); PortType = null;
        WorldLinkRoomRegistry.Clear();
    }

    private static void GenerateEmptyData(On.PlacedObject.orig_GenerateEmptyData orig, PlacedObject self)
    {
        orig(self);
        if (IsControllerType(self?.type)) self.data = new MultiGateControllerData(self);
        else if (IsPortType(self?.type)) self.data = new MultiGatePortData(self);
    }

    private static ObjectsPage.DevObjectCategories CategoryFor(On.DevInterface.ObjectsPage.orig_DevObjectGetCategoryFromPlacedType orig, ObjectsPage self, PlacedObject.Type type)
    {
        return IsWorldLinkType(type) ? Category : orig(self, type);
    }

    private static void CreateRepresentation(On.DevInterface.ObjectsPage.orig_CreateObjRep orig, ObjectsPage self, PlacedObject.Type type, PlacedObject placed)
    {
        if (!IsWorldLinkType(type))
        {
            orig(self, type, placed);
            return;
        }

        try
        {
            bool creating = placed == null;
            if (creating)
            {
                placed = new PlacedObject(type, null)
                {
                    pos = VanillaSpawnPosition(self)
                };
                self.RoomSettings.placedObjects.Add(placed);
            }
            else if (placed.pos == Vector2.zero && self.owner?.mouseClick == true)
            {
                // Some CreateObjRep hook chains materialize the PlacedObject before our
                // hook sees it. Vanilla treats (0,0) as an uninitialized placement, so
                // recover the same spawn position instead of letting the representation
                // collapse onto the lower-left screen origin.
                placed.pos = VanillaSpawnPosition(self);
            }

            EnsureWorldLinkData(placed);
            AddCustomRepresentation(self, placed);
            WorldLinkRoomRegistry.BuildForRoom(self.owner.room);
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"WorldLink DevUI: failed to create representation for '{type}': {ex}");
            // Keep the editor usable and preserve the object even if a future UI change
            // breaks our representation. RefreshObjectsPage will try to repair this
            // vanilla fallback on the next Objects-page rebuild.
            orig(self, type, placed);
        }
    }

    private static void RefreshObjectsPage(On.DevInterface.ObjectsPage.orig_Refresh orig, ObjectsPage self)
    {
        orig(self);
        RepairFallbackRepresentations(self);
    }

    private static void RepairFallbackRepresentations(ObjectsPage self)
    {
        if (self?.tempNodes == null || self.subNodes == null) return;

        List<PlacedObjectRepresentation> replace = null;
        for (int i = 0; i < self.tempNodes.Count; i++)
        {
            if (self.tempNodes[i] is not PlacedObjectRepresentation rep || rep.pObj == null || !IsWorldLinkType(rep.pObj.type)) continue;

            bool correct = IsControllerType(rep.pObj.type)
                ? rep is MultiGateControllerRepresentation
                : rep is MultiGatePortRepresentation;
            if (correct) continue;

            replace ??= new List<PlacedObjectRepresentation>();
            replace.Add(rep);
        }

        if (replace == null) return;

        for (int i = 0; i < replace.Count; i++)
        {
            PlacedObjectRepresentation old = replace[i];
            PlacedObject placed = old.pObj;
            old.ClearSprites();
            self.tempNodes.Remove(old);
            self.subNodes.Remove(old);

            // A vanilla fallback representation converts an uninitialized (0,0)
            // PlacedObject into exactly camera.pos, which is the lower-left screen
            // origin seen in the reported bug. If this repair happens on the create
            // click, recover the normal vanilla spawn point before building our UI.
            if (self.owner?.mouseClick == true && IsAtCameraScreenOrigin(self, placed))
            {
                placed.pos = VanillaSpawnPosition(self);
            }

            EnsureWorldLinkData(placed);
            AddCustomRepresentation(self, placed);
        }

        if (!_loggedFallbackRepair)
        {
            _loggedFallbackRepair = true;
            Plugin.Logger?.LogWarning("WorldLink DevUI: replaced a vanilla fallback representation with the WorldLink editor representation. Another CreateObjRep hook may be materializing the object before WorldLink sees it.");
        }

        WorldLinkRoomRegistry.BuildForRoom(self.owner.room);
    }

    private static void AddCustomRepresentation(ObjectsPage self, PlacedObject placed)
    {
        PlacedObjectRepresentation rep = IsControllerType(placed.type)
            ? new MultiGateControllerRepresentation(self.owner, self, placed)
            : new MultiGatePortRepresentation(self.owner, self, placed);

        // PlacedObjectRepresentation's base constructor initially stores world-space
        // pObj.pos in its DevUI-local pos field and normally corrects it on the first
        // Update. WorldLink representations own large child panels/handles, so correct
        // that transform immediately and refresh the complete subtree before the frame
        // is rendered. This prevents the one-frame/off-screen "label only" state.
        rep.AbsMove(placed.pos - self.owner.room.game.cameras[0].pos);
        rep.Refresh();

        self.tempNodes ??= new List<DevUINode>();
        self.tempNodes.Add(rep);
        self.subNodes.Add(rep);
    }

    private static void EnsureWorldLinkData(PlacedObject placed)
    {
        if (placed == null) return;
        if (IsControllerType(placed.type) && placed.data is not MultiGateControllerData)
        {
            placed.data = new MultiGateControllerData(placed);
        }
        else if (IsPortType(placed.type) && placed.data is not MultiGatePortData)
        {
            placed.data = new MultiGatePortData(placed);
        }
    }

    private static bool IsAtCameraScreenOrigin(ObjectsPage self, PlacedObject placed)
    {
        if (self?.owner?.room?.game?.cameras == null || self.owner.room.game.cameras.Length == 0 || placed == null) return false;
        return (placed.pos - self.owner.room.game.cameras[0].pos).sqrMagnitude < 0.01f;
    }

    private static Vector2 VanillaSpawnPosition(ObjectsPage self)
    {
        return self.owner.room.game.cameras[0].pos
            + Vector2.Lerp(self.owner.mousePos, new Vector2(-683f, 384f), 0.25f)
            + Custom.DegToVec(UnityEngine.Random.value * 360f) * 0.2f;
    }

    private static bool IsWorldLinkType(PlacedObject.Type type) => IsControllerType(type) || IsPortType(type);

    private static bool IsControllerType(PlacedObject.Type type) =>
        type != null && (type == ControllerType || string.Equals(type.value, ControllerTypeName, StringComparison.Ordinal));

    private static bool IsPortType(PlacedObject.Type type) =>
        type != null && (type == PortType || string.Equals(type.value, PortTypeName, StringComparison.Ordinal));

    private static void RoomLoaded(On.Room.orig_Loaded orig, Room self)
    {
        orig(self);
        if (self?.roomSettings?.placedObjects == null) return;
        WorldLinkRoomRegistry.BuildForRoom(self);
    }
}
