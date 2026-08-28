using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

/// <summary>
/// Removes carryable items after they are completely below a quicksand surface.
/// Creature death/cleanup is owned by QuicksandCreatureEscape so a creature first
/// receives the same complete-submersion death condition as the player and is only
/// removed after its short submerged-death cleanup delay.
///
/// Realized deletion and render deletion are handled together. Rain World's
/// UpdatableAndDeletable.Destroy() only marks the realized object for removal, so
/// every matching RoomCamera.SpriteLeaser is cleaned and removed immediately before
/// the object is destroyed.
/// </summary>
internal static class QuicksandSubmersionCleanup
{
    private const float FullSubmergeClearance = 1.5f;
    private const float SpearTopClearance = 35f;

    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.Room.Update += Room_Update;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;
        On.Room.Update -= Room_Update;
    }

    internal static void DeleteCreatureAfterSubmersion(Creature creature)
    {
        if (creature == null || creature is Player || creature.slatedForDeletetion)
        {
            return;
        }

        DeleteSubmergedObject(creature);
    }

    private static void Room_Update(On.Room.orig_Update orig, Room self)
    {
        orig(self);
        CleanupFullySubmerged(self);
    }

    private static void CleanupFullySubmerged(Room room)
    {
        if (room?.physicalObjects == null || room.updateList == null)
        {
            return;
        }

        for (int layer = 0; layer < room.physicalObjects.Length; layer++)
        {
            var objects = room.physicalObjects[layer];
            if (objects == null)
            {
                continue;
            }

            for (int i = objects.Count - 1; i >= 0; i--)
            {
                PhysicalObject physicalObject = objects[i];
                if (!ShouldCleanupItem(physicalObject) ||
                    !IsFullySubmergedInAnyQuicksand(physicalObject, room))
                {
                    continue;
                }

                DeleteSubmergedObject(physicalObject);
            }
        }
    }

    private static bool ShouldCleanupItem(PhysicalObject physicalObject)
    {
        if (physicalObject == null ||
            physicalObject.slatedForDeletetion ||
            physicalObject.room == null ||
            physicalObject.bodyChunks == null ||
            physicalObject.bodyChunks.Length == 0)
        {
            return false;
        }

        // Creature lifetime is deliberately not handled here anymore. A living
        // creature must not be Destroy()ed on its first fully submerged frame.
        return physicalObject is PlayerCarryableItem && physicalObject is not Creature;
    }

    private static bool IsFullySubmergedInAnyQuicksand(
        PhysicalObject physicalObject,
        Room room)
    {
        for (int i = 0; i < room.updateList.Count; i++)
        {
            if (room.updateList[i] is QuicksandZone zone &&
                IsUsableZone(zone) &&
                IsFullySubmergedInZone(physicalObject, zone))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsFullySubmergedInZone(
        PhysicalObject physicalObject,
        QuicksandZone zone)
    {
        float zoneBottomY = zone.PlacedObject.pos.y - zone.Data.BottomDepth;
        float extraTopClearance = physicalObject is Spear ? SpearTopClearance : 0f;

        for (int i = 0; i < physicalObject.bodyChunks.Length; i++)
        {
            BodyChunk chunk = physicalObject.bodyChunks[i];
            if (chunk == null)
            {
                return false;
            }

            float radius = Mathf.Max(1f, chunk.rad);
            if (chunk.pos.x < zone.startX || chunk.pos.x > zone.endX)
            {
                return false;
            }

            float u = zone.MaterialUAtWorldX(chunk.pos.x);
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

            float topY = chunk.pos.y + radius + extraTopClearance;
            if (topY > surfacePoint.y - FullSubmergeClearance)
            {
                return false;
            }

            if (chunk.pos.y < zoneBottomY - radius)
            {
                return false;
            }
        }

        return true;
    }

    private static void DeleteSubmergedObject(PhysicalObject physicalObject)
    {
        Room room = physicalObject?.room;

        while (physicalObject?.grabbedBy != null && physicalObject.grabbedBy.Count > 0)
        {
            Creature.Grasp grasp = physicalObject.grabbedBy[physicalObject.grabbedBy.Count - 1];
            if (grasp == null)
            {
                physicalObject.grabbedBy.RemoveAt(physicalObject.grabbedBy.Count - 1);
            }
            else
            {
                grasp.Release();
            }
        }

        if (physicalObject is Creature creature)
        {
            creature.LoseAllGrasps();
            if (!creature.dead)
            {
                creature.Die();
            }
        }

        RemoveRenderedObject(room, physicalObject);
        physicalObject?.Destroy();
        physicalObject?.abstractPhysicalObject?.Destroy();
    }

    private static void RemoveRenderedObject(Room room, PhysicalObject physicalObject)
    {
        if (room?.game?.cameras == null || physicalObject == null)
        {
            return;
        }

        for (int cameraIndex = 0; cameraIndex < room.game.cameras.Length; cameraIndex++)
        {
            RoomCamera camera = room.game.cameras[cameraIndex];
            if (camera == null || camera.spriteLeasers == null)
            {
                continue;
            }

            for (int i = camera.spriteLeasers.Count - 1; i >= 0; i--)
            {
                RoomCamera.SpriteLeaser leaser = camera.spriteLeasers[i];
                if (leaser == null || ResolvePhysicalObject(leaser.drawableObject) != physicalObject)
                {
                    continue;
                }

                leaser.CleanSpritesAndRemove();
                camera.spriteLeasers.RemoveAt(i);
            }
        }
    }

    private static PhysicalObject ResolvePhysicalObject(IDrawable drawable)
    {
        if (drawable is GraphicsModule graphicsModule)
        {
            return graphicsModule.owner;
        }

        return drawable as PhysicalObject;
    }

    private static bool IsUsableZone(QuicksandZone zone)
    {
        return zone != null &&
               !zone.slatedForDeletetion &&
               zone.PlacedObject != null &&
               zone.PlacedObject.active &&
               zone.Data != null;
    }
}
