using System;
using System.Collections.Generic;
using DevInterface;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Keeps only the live world/backend part of migrated Objects/Sound/Trigger pages synchronized while
/// the rebuilt frontend owns presentation. Vanilla page Refresh rebuilds every hidden panel and, for
/// SoundPage, tears down every AmbientSoundPlayer even when one scalar changed. The rebuilt editor
/// only needs object/spatial handles plus the actual ambient-audio runtime to stay live.
///
/// This class never runs before the page's first ordinary materialization and never owns legacy UI
/// presentation. Callers must fall back to the original page Refresh whenever the page is visible,
/// opaque third-party nodes are present, or a legacy transaction is in flight.
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
            ReconcileSoundPanels(page);
            ReconcileAmbientPlayers(page);
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool minimal Sound backend refresh failed: " + error.Message);
            return false;
        }
    }

    internal static bool TryRefreshTriggers(TriggersPage page)
    {
        if (page?.owner?.room == null || page.RoomSettings?.triggers == null || page.initRefresh)
            return false;

        try
        {
            ReconcileTriggerPanels(page);
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool minimal Trigger backend refresh failed: " + error.Message);
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

    private static void ReconcileSoundPanels(SoundPage page)
    {
        List<AmbientSound> sounds = page.RoomSettings.ambientSounds;
        HashSet<AmbientSound> represented = new(ReferenceComparer<AmbientSound>.Instance);

        // Remove only vanilla AmbientSoundPanel nodes whose model member disappeared (or a duplicate
        // representation). Static page controls and foreign nodes are deliberately untouched.
        for (int i = page.subNodes.Count - 1; i >= 0; i--)
        {
            if (page.subNodes[i] is not AmbientSoundPanel panel)
                continue;

            AmbientSound sound = panel.sound;
            if (sound == null || !ContainsReference(sounds, sound) || !represented.Add(sound))
            {
                RemoveDynamicNode(page, panel, i);
                continue;
            }

            SynchronizeSoundHandle(page, panel, sound);
        }

        // A newly-created spatial sound needs its world handle immediately. Omnidirectional sounds
        // have no world-space backend, so do not construct their hidden vanilla panel just to keep a
        // screen UI that is not currently visible. The deferred full Refresh will create it if the
        // developer explicitly returns to legacy presentation.
        for (int i = 0; i < sounds.Count; i++)
        {
            AmbientSound sound = sounds[i];
            if (sound == null || represented.Contains(sound) || !NeedsSpatialSoundHandle(sound))
                continue;

            AmbientSoundPanel panel = new(page.owner, page, sound.panelPosition, sound);
            panel.Move(sound.panelPosition);
            AddDynamicNode(page, panel);
            represented.Add(sound);
            SynchronizeSoundHandle(page, panel, sound);
        }
    }

    private static void SynchronizeSoundHandle(
        SoundPage page,
        AmbientSoundPanel panel,
        AmbientSound sound)
    {
        RoomCamera camera = PrimaryCamera(page.owner);
        if (camera == null) return;

        if (sound is SpotSound spot)
        {
            SpotSoundHandle handle = FindDirectChild<SpotSoundHandle>(panel);
            if (handle == null)
            {
                handle = new SpotSoundHandle(
                    page.owner,
                    "Spot_Sound_Handle",
                    panel,
                    spot,
                    panel.pos + new Vector2(-50f, -100f),
                    "Spot_Sound_Handle_" + (spot.sample ?? string.Empty));
                panel.subNodes.Add(handle);
            }

            handle.absPos = spot.pos - camera.pos;
            if (handle.subNodes.Count > 0 && handle.subNodes[0] is Handle radiusHandle)
            {
                radiusHandle.pos = spot.radHandlePosition;
                radiusHandle.initRefresh = false;
            }
            handle.Refresh();
            handle.initRefresh = false;
            return;
        }

        if (sound is DirectionalSound directional)
        {
            DirectionalSoundHandle handle = FindDirectChild<DirectionalSoundHandle>(panel);
            if (handle == null)
            {
                handle = new DirectionalSoundHandle(
                    page.owner,
                    "Directional_Sound_Handle",
                    panel,
                    directional,
                    panel.pos,
                    "Directional_Sound_Handle_" + (directional.sample ?? string.Empty));
                panel.subNodes.Add(handle);
            }

            handle.absPos = handle.OnCirclePos(directional.direction);
            handle.Refresh();
            handle.initRefresh = false;
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

        // Existing players hold the AmbientSound object by reference and already read volume, pitch,
        // radius, position, direction and doppler live in DrawUpdate. Reusing them avoids restarting
        // every ambient clip on each slider edit. Only removed/duplicate memberships are retired.
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

        // Add a player only for a model member that has no surviving runtime player. A player that
        // was already slated by vanilla is intentionally not resurrected; replacing it mirrors the
        // next-frame result of SoundPage.Refresh without reloading unrelated clips.
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

    private static void ReconcileTriggerPanels(TriggersPage page)
    {
        List<EventTrigger> triggers = page.RoomSettings.triggers;
        HashSet<EventTrigger> represented = new(ReferenceComparer<EventTrigger>.Instance);

        for (int i = page.subNodes.Count - 1; i >= 0; i--)
        {
            if (page.subNodes[i] is not TriggerPanel panel)
                continue;

            EventTrigger trigger = panel.trigger;
            if (trigger == null || !ContainsReference(triggers, trigger) || !represented.Add(trigger))
            {
                RemoveDynamicNode(page, panel, i);
                continue;
            }

            SynchronizeTriggerHandle(page, panel, trigger);
        }

        // Only SpotTrigger owns a retained world-space handle. Non-spatial trigger panels can stay
        // unmaterialized until legacy UI is explicitly requested.
        for (int i = 0; i < triggers.Count; i++)
        {
            EventTrigger trigger = triggers[i];
            if (trigger is not SpotTrigger || represented.Contains(trigger))
                continue;

            TriggerPanel panel = new(page.owner, page, trigger.panelPosition, trigger);
            panel.Move(trigger.panelPosition);
            AddDynamicNode(page, panel);
            represented.Add(trigger);
            SynchronizeTriggerHandle(page, panel, trigger);
        }
    }

    private static void SynchronizeTriggerHandle(
        TriggersPage page,
        TriggerPanel panel,
        EventTrigger trigger)
    {
        if (trigger is not SpotTrigger spot)
            return;

        RoomCamera camera = PrimaryCamera(page.owner);
        if (camera == null) return;

        SpotTriggerHandle handle = FindDirectChild<SpotTriggerHandle>(panel);
        if (handle == null)
        {
            handle = new SpotTriggerHandle(
                page.owner,
                "Spot_Trigger_Handle",
                panel,
                spot,
                panel.pos + new Vector2(-50f, -100f));
            panel.subNodes.Add(handle);
        }

        handle.absPos = spot.pos - camera.pos;
        if (handle.subNodes.Count > 0 && handle.subNodes[0] is Handle radiusHandle)
        {
            radiusHandle.pos = spot.radHandlePosition;
            radiusHandle.initRefresh = false;
        }
        handle.Refresh();
        handle.initRefresh = false;
    }

    private static void AddDynamicNode(Page page, DevUINode node)
    {
        page.tempNodes ??= new List<DevUINode>();
        page.tempNodes.Add(node);
        page.subNodes.Add(node);
    }

    private static void RemoveDynamicNode(Page page, DevUINode node, int subNodeIndex)
    {
        node.ClearSprites();
        page.subNodes.RemoveAt(subNodeIndex);
        page.tempNodes?.Remove(node);

        switch (page)
        {
            case ObjectsPage objects when ReferenceEquals(objects.draggedObject, node):
                objects.draggedObject = null;
                break;
            case SoundPage sound when ReferenceEquals(sound.draggedObject, node):
                sound.draggedObject = null;
                break;
            case TriggersPage triggers when ReferenceEquals(triggers.draggedObject, node):
                triggers.draggedObject = null;
                break;
        }
    }

    private static T FindDirectChild<T>(DevUINode parent) where T : DevUINode
    {
        if (parent?.subNodes == null) return null;
        for (int i = 0; i < parent.subNodes.Count; i++)
            if (parent.subNodes[i] is T typed) return typed;
        return null;
    }

    private static bool NeedsSpatialSoundHandle(AmbientSound sound) =>
        sound is SpotSound || sound is DirectionalSound;

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
