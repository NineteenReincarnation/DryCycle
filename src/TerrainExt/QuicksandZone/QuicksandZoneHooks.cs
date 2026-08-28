using System.Runtime.CompilerServices;
using DevInterface;
using DryCycle.Items.DewPod;
using RWCustom;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

internal static class QuicksandZoneHooks
{
    private const string PlacedTypeName = "QuicksandZone";

    private sealed class SinkRenderState
    {
        internal bool Active;
    }

    private static readonly ConditionalWeakTable<RoomCamera.SpriteLeaser, SinkRenderState> SinkRenderStates = new();
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
        On.RoomCamera.SpriteLeaser.Update += SpriteLeaser_Update;
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
        On.RoomCamera.SpriteLeaser.Update -= SpriteLeaser_Update;

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

        EnsureRuntimeObject(self.owner.room, placedObject);

        PlacedObjectRepresentation representation = new QuicksandZoneRepresentation(
            self.owner,
            type + "_Rep",
            self,
            placedObject,
            "Quicksand Zone");

        self.tempNodes.Add(representation);
        self.subNodes.Add(representation);
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

    private static void SpriteLeaser_Update(
        On.RoomCamera.SpriteLeaser.orig_Update orig,
        RoomCamera.SpriteLeaser self,
        float timeStacker,
        RoomCamera rCam,
        Vector2 camPos)
    {
        orig(self, timeStacker, rCam, camPos);

        if (self == null || self.sprites == null || rCam?.room == null)
        {
            return;
        }

        SinkRenderState renderState = SinkRenderStates.GetOrCreateValue(self);
        PhysicalObject physicalObject = ResolvePhysicalObject(self.drawableObject);

        if (physicalObject == null ||
            physicalObject.room != rCam.room ||
            !QuicksandRealisticPhysics.TryGetVisualSink(
                physicalObject,
                out Vector2 visualOffset,
                out _,
                out _))
        {
            if (renderState.Active)
            {
                self.AddSpritesToContainer(null, rCam);
                renderState.Active = false;
            }

            return;
        }

        FContainer sand = rCam.ReturnFContainer("Sand");
        if (sand == null)
        {
            return;
        }

        if (!renderState.Active)
        {
            self.AddSpritesToContainer(sand, rCam);
            renderState.Active = true;
        }

        MoveDrawableBehindTerrain(self, sand);
        ApplyVisualSinkOffset(self, visualOffset);
    }

    private static PhysicalObject ResolvePhysicalObject(IDrawable drawable)
    {
        if (drawable is GraphicsModule graphicsModule)
        {
            return graphicsModule.owner;
        }

        return drawable as PhysicalObject;
    }

    private static void MoveDrawableBehindTerrain(
        RoomCamera.SpriteLeaser sLeaser,
        FContainer sand)
    {
        for (int i = sLeaser.sprites.Length - 1; i >= 0; i--)
        {
            FSprite sprite = sLeaser.sprites[i];
            if (sprite == null)
            {
                continue;
            }

            if (sprite.container != sand)
            {
                sand.AddChild(sprite);
            }

            sprite.MoveToBack();
        }

        if (sLeaser.containers == null)
        {
            return;
        }

        for (int i = sLeaser.containers.Length - 1; i >= 0; i--)
        {
            FContainer container = sLeaser.containers[i];
            if (container == null)
            {
                continue;
            }

            sand.AddChild(container);
            container.MoveToBack();
        }
    }

    private static void ApplyVisualSinkOffset(
        RoomCamera.SpriteLeaser sLeaser,
        Vector2 visualOffset)
    {
        if (visualOffset.sqrMagnitude < 0.0000001f)
        {
            return;
        }

        for (int i = 0; i < sLeaser.sprites.Length; i++)
        {
            FSprite sprite = sLeaser.sprites[i];
            if (sprite == null)
            {
                continue;
            }

            sprite.x += visualOffset.x;
            sprite.y += visualOffset.y;
        }
    }

    private static void EnsureRuntimeObject(Room room, PlacedObject placedObject)
    {
        if (room == null || placedObject == null || room.updateList == null)
        {
            return;
        }

        QuicksandZone zone = null;
        QuicksandFlowOverlay overlay = null;

        for (int i = 0; i < room.updateList.Count; i++)
        {
            if (room.updateList[i] is QuicksandZone existingZone &&
                existingZone.PlacedObject == placedObject)
            {
                zone = existingZone;
            }
            else if (room.updateList[i] is QuicksandFlowOverlay existingOverlay &&
                     existingOverlay.Zone?.PlacedObject == placedObject)
            {
                overlay = existingOverlay;
            }
        }

        if (zone == null)
        {
            zone = new QuicksandZone(room, placedObject);
            room.AddObject(zone);
        }
        else
        {
            zone.RefreshCurve();
        }

        if (overlay == null)
        {
            room.AddObject(new QuicksandFlowOverlay(zone));
        }
    }
}
