using System.Runtime.CompilerServices;
using DevInterface;
using DryCycle.Items.DewPod;
using RWCustom;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

internal static class QuicksandZoneHooks
{
    private const string PlacedTypeName = "QuicksandZone";
    private const float VisualEdgeSampleSpacing = 3f;
    private const int MaxVisualEdgeSamples = 48;
    private const float InitialVisiblePenetration = 0.10f;
    private const float VisualContactEpsilon = 0.02f;
    private const float MaximumSolidSpriteSpan = 96f;

    private sealed class SinkRenderState
    {
        internal bool Active;
        internal PhysicalObject TrackedObject;
        internal QuicksandZone Zone;
        internal bool EntryAlignmentInitialized;
        internal float EntryAlignmentY;
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
            !QuicksandSinkRateLimiter.TryGetVisualSink(
                physicalObject,
                out Vector2 visualOffset,
                out QuicksandZone zone,
                out _))
        {
            RestoreDefaultContainer(self, rCam, renderState);
            ResetRenderTracking(renderState);
            return;
        }

        FContainer sand = rCam.ReturnFContainer("Sand");
        if (sand == null)
        {
            return;
        }

        bool shapeAwareLooseObject =
            physicalObject is not Player &&
            physicalObject is not Creature &&
            (physicalObject.grabbedBy == null || physicalObject.grabbedBy.Count == 0);

        if (!shapeAwareLooseObject)
        {
            if (renderState.TrackedObject != physicalObject || renderState.Zone != zone)
            {
                RestoreDefaultContainer(self, rCam, renderState);
                ResetRenderTracking(renderState);
                renderState.TrackedObject = physicalObject;
                renderState.Zone = zone;
            }

            if (!renderState.Active)
            {
                self.AddSpritesToContainer(sand, rCam);
                renderState.Active = true;
            }

            MoveDrawableBehindTerrain(self, sand);
            ApplyVisualSinkOffset(self, visualOffset);
            return;
        }

        if (renderState.TrackedObject != physicalObject || renderState.Zone != zone)
        {
            RestoreDefaultContainer(self, rCam, renderState);
            ResetRenderTracking(renderState);
            renderState.TrackedObject = physicalObject;
            renderState.Zone = zone;
        }

        if (!renderState.EntryAlignmentInitialized)
        {
            float entryPenetration = MeasureDeepestSurfacePenetration(
                self,
                zone,
                camPos);

            // Match the same principle used by the player locomotion presentation:
            // preserve the object's current pose, then apply one common translation
            // rather than forcing an individual sprite/chunk into a new orientation.
            // This aligns the actual rendered footprint with first contact and leaves
            // horizontal, diagonal and vertical items in their native pose.
            renderState.EntryAlignmentY =
                entryPenetration > float.NegativeInfinity
                    ? entryPenetration - InitialVisiblePenetration
                    : 0f;
            renderState.EntryAlignmentInitialized = true;
        }

        Vector2 alignedOffset = visualOffset + Vector2.up * renderState.EntryAlignmentY;
        ApplyVisualSinkOffset(self, alignedOffset);

        float visualPenetration = MeasureDeepestSurfacePenetration(
            self,
            zone,
            camPos);

        // Sample the current transformed geometry against the curved quicksand
        // surface. Once it actually crosses the curve, the Sand container performs
        // the final curved clipping, so only the portion below the local surface is
        // hidden instead of switching the whole item based on BodyChunk radius.
        bool touchesVisualSurface = visualPenetration >= VisualContactEpsilon;
        if (!touchesVisualSurface)
        {
            RestoreDefaultContainer(self, rCam, renderState);
            return;
        }

        if (!renderState.Active)
        {
            self.AddSpritesToContainer(sand, rCam);
            renderState.Active = true;
        }

        MoveDrawableBehindTerrain(self, sand);
    }

    private static PhysicalObject ResolvePhysicalObject(IDrawable drawable)
    {
        if (drawable is GraphicsModule graphicsModule)
        {
            return graphicsModule.owner;
        }

        return drawable as PhysicalObject;
    }

    private static void RestoreDefaultContainer(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        SinkRenderState renderState)
    {
        if (renderState == null || !renderState.Active)
        {
            return;
        }

        sLeaser.AddSpritesToContainer(null, rCam);
        renderState.Active = false;
    }

    private static void ResetRenderTracking(SinkRenderState renderState)
    {
        if (renderState == null)
        {
            return;
        }

        renderState.TrackedObject = null;
        renderState.Zone = null;
        renderState.EntryAlignmentInitialized = false;
        renderState.EntryAlignmentY = 0f;
    }

    private static float MeasureDeepestSurfacePenetration(
        RoomCamera.SpriteLeaser sLeaser,
        QuicksandZone zone,
        Vector2 camPos)
    {
        if (sLeaser?.sprites == null || !IsUsableZone(zone))
        {
            return float.NegativeInfinity;
        }

        float deepest = float.NegativeInfinity;
        bool sampled = false;

        for (int i = 0; i < sLeaser.sprites.Length; i++)
        {
            FSprite sprite = sLeaser.sprites[i];
            if (!CanUseSpriteForContact(sprite))
            {
                continue;
            }

            if (sprite is TriangleMesh mesh &&
                mesh.vertices != null &&
                mesh.triangles != null &&
                mesh.vertices.Length > 0)
            {
                for (int triangleIndex = 0; triangleIndex < mesh.triangles.Length; triangleIndex++)
                {
                    TriangleMesh.Triangle triangle = mesh.triangles[triangleIndex];
                    SampleVisualEdge(
                        sprite,
                        mesh.vertices[triangle.a],
                        mesh.vertices[triangle.b],
                        zone,
                        camPos,
                        ref deepest,
                        ref sampled);
                    SampleVisualEdge(
                        sprite,
                        mesh.vertices[triangle.b],
                        mesh.vertices[triangle.c],
                        zone,
                        camPos,
                        ref deepest,
                        ref sampled);
                    SampleVisualEdge(
                        sprite,
                        mesh.vertices[triangle.c],
                        mesh.vertices[triangle.a],
                        zone,
                        camPos,
                        ref deepest,
                        ref sampled);
                }

                continue;
            }

            Vector2[] localVertices = sprite._localVertices;
            if (localVertices == null || localVertices.Length < 2)
            {
                continue;
            }

            for (int vertexIndex = 0; vertexIndex < localVertices.Length; vertexIndex++)
            {
                Vector2 start = localVertices[vertexIndex];
                Vector2 end = localVertices[(vertexIndex + 1) % localVertices.Length];
                SampleVisualEdge(
                    sprite,
                    start,
                    end,
                    zone,
                    camPos,
                    ref deepest,
                    ref sampled);
            }
        }

        return sampled ? deepest : float.NegativeInfinity;
    }

    private static bool CanUseSpriteForContact(FSprite sprite)
    {
        if (sprite == null || !sprite.isVisible || sprite.alpha <= 0.001f)
        {
            return false;
        }

        if (sprite is TriangleMesh)
        {
            return true;
        }

        Rect rect = sprite.localRect;
        float width = Mathf.Abs(rect.width * sprite.scaleX);
        float height = Mathf.Abs(rect.height * sprite.scaleY);

        // Large glow/aura sprites should not define where the solid item touches the
        // sand. Normal carryable-item art, including spear sprites, remains below
        // this span and therefore participates in geometric contact sampling.
        return width <= MaximumSolidSpriteSpan && height <= MaximumSolidSpriteSpan;
    }

    private static void SampleVisualEdge(
        FSprite sprite,
        Vector2 localStart,
        Vector2 localEnd,
        QuicksandZone zone,
        Vector2 camPos,
        ref float deepest,
        ref bool sampled)
    {
        Vector2 worldStart = sprite.LocalToStage(localStart) + camPos;
        Vector2 worldEnd = sprite.LocalToStage(localEnd) + camPos;
        float edgeLength = Vector2.Distance(worldStart, worldEnd);
        int sampleCount = Mathf.Clamp(
            Mathf.CeilToInt(edgeLength / VisualEdgeSampleSpacing),
            1,
            MaxVisualEdgeSamples);

        for (int i = 0; i <= sampleCount; i++)
        {
            float t = (float)i / sampleCount;
            Vector2 worldPoint = Vector2.Lerp(worldStart, worldEnd, t);
            if (!TryGetSurfacePenetration(zone, worldPoint, out float penetration))
            {
                continue;
            }

            sampled = true;
            deepest = Mathf.Max(deepest, penetration);
        }
    }

    private static bool TryGetSurfacePenetration(
        QuicksandZone zone,
        Vector2 worldPoint,
        out float penetration)
    {
        penetration = 0f;
        if (!IsUsableZone(zone) ||
            worldPoint.x < zone.startX ||
            worldPoint.x > zone.endX)
        {
            return false;
        }

        float u = zone.MaterialUAtWorldX(worldPoint.x);
        if (!zone.Data.IsQuicksand(u) ||
            !zone.TrySampleSurfaceFrame(
                u,
                out Vector2 surfacePoint,
                out _,
                out _,
                out _))
        {
            return false;
        }

        penetration = surfacePoint.y - worldPoint.y;
        return true;
    }

    private static bool IsUsableZone(QuicksandZone zone)
    {
        return zone != null &&
               !zone.slatedForDeletetion &&
               zone.PlacedObject != null &&
               zone.PlacedObject.active &&
               zone.Data != null;
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
