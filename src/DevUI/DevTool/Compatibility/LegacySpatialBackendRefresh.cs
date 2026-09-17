using System;
using System.Collections.Generic;
using DevInterface;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Keeps only the backend state that still has real runtime value while the rebuilt frontend owns
/// presentation.
///
/// Objects temporarily retain vanilla/custom representations because their native gizmo coverage is
/// not complete yet. Sound is different: built-in spatial editing is now owned by Native Gizmo, so
/// its only required legacy-side runtime is the actual AmbientSoundPlayer set. Trigger has no
/// equivalent runtime object to retain, therefore a built-in Trigger page needs no hidden Panel or
/// Handle backend at all.
///
/// This class intentionally never constructs AmbientSoundPanel, TriggerPanel, SpotSoundHandle,
/// DirectionalSoundHandle or SpotTriggerHandle on the native path. Returning to explicit Vanilla /
/// Legacy presentation still performs the deferred ordinary page Refresh and recreates the original
/// DevInterface UI losslessly.
/// </summary>
internal static class LegacySpatialBackendRefresh
{
    internal static bool TryRefreshObjects(ObjectsPage page)
    {
        if (page?.owner?.room == null || page.RoomSettings?.placedObjects == null || page.initRefresh)
            return false;

        try
        {
            ReconcileObjectRepresentations(page);
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool minimal Objects backend refresh failed: " + error.Message);
            return false;
        }
    }

    internal static bool TryRefreshSound(SoundPage page)
    {
        if (page?.owner?.room == null || page.RoomSettings?.ambientSounds == null || page.initRefresh)
            return false;

        try
        {
            // Native gizmos own every built-in Sound spatial semantic. Any vanilla panels left from
            // the one-time compatibility materialization are dead presentation state and must not be
            // recreated by an otherwise harmless model Refresh.
            NativeLegacySpatialHandleRetirement.PruneBuiltinSoundNodes(page);
            ReconcileAmbientPlayers(page);
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool native Sound backend refresh failed: " + error.Message);
            return false;
        }
    }

    internal static bool TryRefreshTriggers(TriggersPage page)
    {
        if (page?.owner?.room == null || page.RoomSettings?.triggers == null || page.initRefresh)
            return false;

        try
        {
            // Trigger authoring has no separate runtime player that must be kept in sync. Built-in
            // TriggerPanel/SpotTriggerHandle nodes are therefore pure legacy presentation and can be
            // absent for the complete lifetime of the native workspace after compatibility probing.
            NativeLegacySpatialHandleRetirement.PruneBuiltinTriggerNodes(page);
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool native Trigger backend refresh failed: " + error.Message);
            return false;
        }
    }

    private static void ReconcileObjectRepresentations(ObjectsPage page)
    {
        List<PlacedObject> objects = page.RoomSettings.placedObjects;
        HashSet<PlacedObject> represented = new(ReferenceComparer<PlacedObject>.Instance);

        // ObjectsPage.CreateObjRep registers each representation as a direct tempNode. Retain the
        // representation instance for every still-live model member so POM/RegionKit/vanilla handle
        // state, drag state and sprite allocations are not churned by unrelated edits.
        for (int i = page.subNodes.Count - 1; i >= 0; i--)
        {
            if (page.subNodes[i] is not PlacedObjectRepresentation representation)
                continue;

            PlacedObject placedObject = representation.pObj;
            if (placedObject == null ||
                !ContainsReference(objects, placedObject) ||
                !represented.Add(placedObject))
            {
                RemoveDynamicNode(page, representation, i);
            }
        }

        // Only missing model members need a compatibility representation. Use vanilla's public
        // factory path rather than constructing base representations ourselves so ordinary hooks and
        // custom object factories continue to participate exactly as they do in ObjectsPage.Refresh.
        for (int i = 0; i < objects.Count; i++)
        {
            PlacedObject placedObject = objects[i];
            if (placedObject?.type == null || represented.Contains(placedObject))
                continue;

            page.CreateObjRep(placedObject.type, placedObject);
            represented.Add(placedObject);
        }
    }

    private static void ReconcileAmbientPlayers(SoundPage page)
    {
        RoomCamera camera = PrimaryCamera(page.owner);
        VirtualMicrophone microphone = camera?.virtualMicrophone;
        List<AmbientSound> sounds = page.RoomSettings.ambientSounds;
        if (microphone?.ambientSoundPlayers == null || sounds == null)
            return;

        List<AmbientSoundPlayer> players = microphone.ambientSoundPlayers;

        // Existing players hold AmbientSound by reference and read volume, pitch, position, radius,
        // direction and doppler live in DrawUpdate. Scalar/native-gizmo edits therefore need no
        // player rebuild. Only collection membership is reconciled here.
        for (int i = players.Count - 1; i >= 0; i--)
        {
            AmbientSoundPlayer player = players[i];
            if (player == null)
            {
                players.RemoveAt(i);
                continue;
            }

            if (!ContainsReference(sounds, player.aSound))
            {
                player.slatedForDeletion = true;
                continue;
            }

            if (player.slatedForDeletion)
                continue;

            for (int j = i - 1; j >= 0; j--)
            {
                AmbientSoundPlayer older = players[j];
                if (older != null && !older.slatedForDeletion && ReferenceEquals(older.aSound, player.aSound))
                    older.slatedForDeletion = true;
            }
        }

        // A player already slated by vanilla is intentionally not resurrected. Add one fresh player
        // for any model member that has no surviving runtime instance, without touching unrelated
        // clips and without materializing SoundPage UI.
        for (int i = 0; i < sounds.Count; i++)
        {
            AmbientSound sound = sounds[i];
            if (sound == null) continue;

            bool found = false;
            for (int j = 0; j < players.Count; j++)
            {
                AmbientSoundPlayer player = players[j];
                if (player != null && !player.slatedForDeletion && ReferenceEquals(player.aSound, sound))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
                players.Add(new AmbientSoundPlayer(microphone, sound));
        }
    }

    private static void RemoveDynamicNode(Page page, DevUINode node, int subNodeIndex)
    {
        node.ClearSprites();
        page.subNodes.RemoveAt(subNodeIndex);
        page.tempNodes?.Remove(node);

        if (page is ObjectsPage objects && ReferenceEquals(objects.draggedObject, node))
            objects.draggedObject = null;
    }

    private static RoomCamera PrimaryCamera(global::DevInterface.DevUI owner)
    {
        RoomCamera[] cameras = owner?.room?.game?.cameras;
        return cameras != null && cameras.Length > 0 ? cameras[0] : null;
    }

    private static bool ContainsReference<T>(List<T> values, T target) where T : class
    {
        if (values == null || target == null) return false;
        for (int i = 0; i < values.Count; i++)
            if (ReferenceEquals(values[i], target)) return true;
        return false;
    }

    private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
    {
        internal static readonly ReferenceComparer<T> Instance = new();

        public bool Equals(T x, T y) => ReferenceEquals(x, y);
        public int GetHashCode(T obj) =>
            obj == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
