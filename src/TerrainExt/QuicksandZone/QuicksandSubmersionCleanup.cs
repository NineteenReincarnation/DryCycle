using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

/// <summary>
/// Removes non-player creatures and carryable items after they are completely
/// below a quicksand surface. Cleanup runs after Room.Update so no object is
/// destroyed in the middle of its own update.
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

            // Destroy() only marks an object for deletion. Room removes it from the
            // physical-object lists on the next update, so this does not mutate the
            // collection while it is being iterated.
            for (int i = objects.Count - 1; i >= 0; i--)
            {
                PhysicalObject physicalObject = objects[i];
                if (!ShouldCleanup(physicalObject) ||
                    !IsFullySubmergedInAnyQuicksand(physicalObject, room))
                {
                    continue;
                }

                DeleteSubmergedObject(physicalObject);
            }
        }
    }

    private static bool ShouldCleanup(PhysicalObject physicalObject)
    {
        if (physicalObject == null ||
            physicalObject.slatedForDeletetion ||
            physicalObject.room == null ||
            physicalObject.bodyChunks == null ||
            physicalObject.bodyChunks.Length == 0)
        {
            return false;
        }

        // Player death/session handling stays with the existing player quicksand
        // logic. Never remove a Player-derived AbstractCreature outright.
        if (physicalObject is Player)
        {
            return false;
        }

        // Limit cleanup to actual creatures and carryable items, rather than every
        // PhysicalObject in the room (which could include room machinery).
        return physicalObject is Creature || physicalObject is PlayerCarryableItem;
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

            // Sinking is world-Y only. Full submersion therefore means the top of
            // every physical chunk is below the local quicksand surface Y.
            // Spears get extra clearance because their visible sprite extends far
            // beyond their single small physics chunk.
            float topY = chunk.pos.y + radius + extraTopClearance;
            if (topY > surfacePoint.y - FullSubmergeClearance)
            {
                return false;
            }

            // Avoid deleting unrelated objects far below the authored quicksand band
            // merely because they share the same X coordinate.
            if (chunk.pos.y < zoneBottomY - radius)
            {
                return false;
            }
        }

        return true;
    }

    private static void DeleteSubmergedObject(PhysicalObject physicalObject)
    {
        // Release anything holding this object so no creature keeps a stale grasp.
        while (physicalObject.grabbedBy != null && physicalObject.grabbedBy.Count > 0)
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

        // Mark both realized and abstract objects for deletion. Room cleanup removes
        // the realized object from update/physics lists and the abstract object from
        // the room entity lists, so it will not be realized again later.
        physicalObject.Destroy();
        physicalObject.abstractPhysicalObject?.Destroy();
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
