using System.Runtime.CompilerServices;
using DevInterface;
using DryCycle.Items.DewPod;
using RWCustom;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

internal static class QuicksandZoneHooks
{
    private const string PlacedTypeName = "QuicksandZone";
    private const int LayerSampleCount = 64;
    private const float HeadCoverClearance = 8f;

    private sealed class PlayerLayerState
    {
        internal bool SplitActive;
    }

    private sealed class ZoneLayerCache
    {
        internal readonly Vector2[] Surface = new Vector2[LayerSampleCount];
        internal readonly Vector2[] Bottom = new Vector2[LayerSampleCount];
    }

    private static readonly ConditionalWeakTable<PlayerGraphics, PlayerLayerState> PlayerLayerStates = new();
    private static readonly ConditionalWeakTable<QuicksandZone, ZoneLayerCache> ZoneLayerCaches = new();
    private static readonly int[] UpperPlayerSprites = { 0, 3, 5, 6, 7, 8, 9 };
    private static readonly int[] LowerPlayerSprites = { 1, 2, 4 };
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
        On.PlayerGraphics.DrawSprites += PlayerGraphics_DrawSprites;
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
        On.PlayerGraphics.DrawSprites -= PlayerGraphics_DrawSprites;

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

    private static void PlayerGraphics_DrawSprites(
        On.PlayerGraphics.orig_DrawSprites orig,
        PlayerGraphics self,
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        float timeStacker,
        Vector2 camPos)
    {
        orig(self, sLeaser, rCam, timeStacker, camPos);

        if (self?.player == null || sLeaser?.sprites == null || rCam?.room == null)
        {
            return;
        }

        PlayerLayerState state = PlayerLayerStates.GetOrCreateValue(self);
        bool splitActive = IsPlayerTouchingQuicksand(self.player);

        if (!splitActive)
        {
            if (state.SplitActive)
            {
                self.AddToContainer(sLeaser, rCam, null);
                state.SplitActive = false;
            }

            return;
        }

        FContainer sandContainer = rCam.ReturnFContainer("Sand");
        if (sandContainer == null)
        {
            return;
        }

        // Lower body is hidden as soon as the player begins sinking. The upper body
        // stays visible only while the actual PlayerGraphics head is still above the
        // quicksand surface. Once the head itself clears the surface, every slugcat
        // sprite goes behind Sand so the visual state agrees with the death check.
        bool headFullyCovered = IsHeadFullyCovered(self);
        MoveSpritesToSand(sLeaser, sandContainer, LowerPlayerSprites, moveToFront: false);
        MoveSpritesToSand(
            sLeaser,
            sandContainer,
            UpperPlayerSprites,
            moveToFront: !headFullyCovered);

        state.SplitActive = true;
    }

    private static void MoveSpritesToSand(
        RoomCamera.SpriteLeaser sLeaser,
        FContainer sandContainer,
        int[] spriteIndices,
        bool moveToFront)
    {
        for (int i = 0; i < spriteIndices.Length; i++)
        {
            int spriteIndex = spriteIndices[i];
            if (spriteIndex < 0 || spriteIndex >= sLeaser.sprites.Length)
            {
                continue;
            }

            FSprite sprite = sLeaser.sprites[spriteIndex];
            if (sprite == null)
            {
                continue;
            }

            if (sprite.container != sandContainer)
            {
                sandContainer.AddChild(sprite);
            }

            if (moveToFront)
            {
                sprite.MoveToFront();
            }
            else
            {
                sprite.MoveToBack();
            }
        }
    }

    private static bool IsPlayerTouchingQuicksand(Player player)
    {
        Room room = player?.room;
        if (room?.updateList == null)
        {
            return false;
        }

        for (int i = 0; i < room.updateList.Count; i++)
        {
            if (room.updateList[i] is QuicksandZone zone &&
                !zone.slatedForDeletetion &&
                zone.IntersectsPlayerForLayer(player))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHeadFullyCovered(PlayerGraphics graphics)
    {
        Player player = graphics?.player;
        Room room = player?.room;
        if (room?.updateList == null || graphics.head == null)
        {
            return false;
        }

        Vector2 headPosition = graphics.head.pos;

        for (int i = 0; i < room.updateList.Count; i++)
        {
            if (room.updateList[i] is not QuicksandZone zone ||
                zone.slatedForDeletetion ||
                zone.PlacedObject == null ||
                !zone.PlacedObject.active ||
                zone.PlacedObject.data is not QuicksandZoneData data ||
                !zone.IntersectsPlayerForLayer(player))
            {
                continue;
            }

            ZoneLayerCache cache = ZoneLayerCaches.GetValue(zone, _ => new ZoneLayerCache());
            QuicksandSurface.SampleZone(zone.PlacedObject, data, cache.Surface, cache.Bottom);

            if (!QuicksandSurface.TryGetContact(
                    headPosition,
                    HeadCoverClearance + 2f,
                    cache.Surface,
                    cache.Bottom,
                    out QuicksandSurface.Contact contact))
            {
                continue;
            }

            float headDepth = Vector2.Dot(
                headPosition - contact.SurfacePoint,
                contact.Inward);
            if (headDepth >= HeadCoverClearance)
            {
                return true;
            }
        }

        return false;
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
