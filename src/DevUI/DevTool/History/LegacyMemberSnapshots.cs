using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using DevInterface;
using DryCycle.DevUI.DevTool.Compatibility;
using DryCycle.DevUI.DevTool.Core;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.History;

/// <summary>
/// Resolves legacy controls whose complete edit domain is one small model member. These snapshots
/// sit in front of the document-level compatibility fallback: known room/sound/trigger controls get
/// bounded transactions, while unknown third-party controls retain the safe whole-document path.
/// </summary>
internal static class LegacyMemberSnapshotFactory
{
    internal static IEditorStateSnapshot CaptureForNode(EditorSession session, DevUINode origin)
    {
        if (session?.Owner == null || origin == null)
            return null;

        if (session.Owner.activePage is MapPage mapPage)
        {
            RoomPanel roomPanel = FindAncestor<RoomPanel>(origin);
            if (roomPanel?.roomRep?.room != null)
                return SingleMapRoomStateSnapshot.Capture(mapPage, roomPanel);
        }

        if (session.RoomSettings == null)
            return null;

        AmbientSoundPanel soundPanel = FindAncestor<AmbientSoundPanel>(origin);
        if (soundPanel?.sound != null)
            return SingleAmbientSoundStateSnapshot.Capture(session.RoomSettings, soundPanel.sound);

        TriggerPanel triggerPanel = FindAncestor<TriggerPanel>(origin);
        if (triggerPanel?.trigger != null)
            return SingleTriggerStateSnapshot.Capture(session.RoomSettings, triggerPanel.trigger);

        return null;
    }

    private static T FindAncestor<T>(DevUINode node) where T : DevUINode
    {
        DevUINode current = node;
        while (current != null)
        {
            if (current is T typed)
                return typed;
            current = current.parentNode;
        }
        return null;
    }
}

/// <summary>
/// One-room Map transaction. Position/layer/subregion and room-local map metadata are copied, but
/// unrelated rooms, global attraction defaults and map materials are deliberately excluded. This is
/// the normal history unit for room moves and room-panel legacy edits.
/// </summary>
internal sealed class SingleMapRoomStateSnapshot : IEditorStateSnapshot
{
    private readonly global::World world;
    private readonly string roomName;
    private readonly Vector2 pos;
    private readonly Vector2 devPos;
    private readonly int layer;
    private readonly string subregion;
    private readonly Vector2[] nodePositions;
    private readonly int[] exitDirections;
    private readonly AbstractRoom.CreatureRoomAttraction[] attractions;
    private readonly Dictionary<string, AbstractRoom.CreatureRoomAttraction> namedAttractions;

    private SingleMapRoomStateSnapshot(
        global::World world,
        string roomName,
        Vector2 pos,
        Vector2 devPos,
        int layer,
        string subregion,
        Vector2[] nodePositions,
        int[] exitDirections,
        AbstractRoom.CreatureRoomAttraction[] attractions,
        Dictionary<string, AbstractRoom.CreatureRoomAttraction> namedAttractions)
    {
        this.world = world;
        this.roomName = roomName ?? string.Empty;
        this.pos = pos;
        this.devPos = devPos;
        this.layer = layer;
        this.subregion = subregion;
        this.nodePositions = nodePositions;
        this.exitDirections = exitDirections;
        this.attractions = attractions;
        this.namedAttractions = namedAttractions ?? new Dictionary<string, AbstractRoom.CreatureRoomAttraction>(StringComparer.Ordinal);
        Fingerprint = BuildFingerprint();
    }

    public string Kind =>
        "MapRoom:" + (world == null ? 0 : RuntimeHelpers.GetHashCode(world)) + ":" + roomName;

    public string Fingerprint { get; }

    internal static SingleMapRoomStateSnapshot Capture(MapPage page, RoomPanel panel)
    {
        AbstractRoom room = panel?.roomRep?.room;
        if (page?.world == null || room == null)
            return null;

        return new SingleMapRoomStateSnapshot(
            page.world,
            room.name,
            panel.pos,
            panel.devPos,
            panel.layer,
            room.subregionName,
            panel.roomRep.nodePositions == null ? null : (Vector2[])panel.roomRep.nodePositions.Clone(),
            panel.roomRep.exitDirections == null ? null : (int[])panel.roomRep.exitDirections.Clone(),
            room.roomAttractions == null ? null : (AbstractRoom.CreatureRoomAttraction[])room.roomAttractions.Clone(),
            room.namedRoomAttractions == null
                ? new Dictionary<string, AbstractRoom.CreatureRoomAttraction>(StringComparer.Ordinal)
                : new Dictionary<string, AbstractRoom.CreatureRoomAttraction>(room.namedRoomAttractions, StringComparer.Ordinal));
    }

    internal static SingleMapRoomStateSnapshot Capture(MapPage page, int roomIndex)
    {
        RoomPanel panel = FindRoomPanel(page, roomIndex);
        return panel == null ? null : Capture(page, panel);
    }

    public IEditorStateSnapshot CaptureCurrent(EditorSession session)
    {
        if (session?.Owner?.activePage is not MapPage page || !ReferenceEquals(page.world, world))
            return null;

        RoomPanel panel = FindRoomPanel(page, roomName);
        return panel == null ? null : Capture(page, panel);
    }

    public bool Restore(EditorSession session)
    {
        if (session?.Owner?.activePage is not MapPage page || !ReferenceEquals(page.world, world))
            return false;

        RoomPanel panel = FindRoomPanel(page, roomName);
        AbstractRoom room = panel?.roomRep?.room;
        if (panel == null || room == null)
            return false;

        try
        {
            panel.pos = pos;
            panel.devPos = devPos;
            panel.layer = layer;
            room.subregionName = subregion;
            panel.roomRep.nodePositions = nodePositions == null ? null : (Vector2[])nodePositions.Clone();
            panel.roomRep.exitDirections = exitDirections == null ? null : (int[])exitDirections.Clone();

            if (attractions != null)
                room.roomAttractions = (AbstractRoom.CreatureRoomAttraction[])attractions.Clone();

            room.namedRoomAttractions ??= new Dictionary<string, AbstractRoom.CreatureRoomAttraction>();
            room.namedRoomAttractions.Clear();
            foreach (KeyValuePair<string, AbstractRoom.CreatureRoomAttraction> pair in namedAttractions)
                room.namedRoomAttractions[pair.Key] = pair.Value;

            // RoomPanel.Refresh creates/rebinds the vanilla map texture. The rebuilt World Workspace
            // reads model state directly, so hidden vanilla refresh work is skipped during undo/redo
            // just as it is during the original edit. Legacy/vanilla mode keeps the old behavior.
            if (!LegacyDevUiQuiescenceController.IsQuiescent(session.Owner))
            {
                panel.Refresh();
                page.Refresh();
            }
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool single-map-room restore failed: " + error.Message);
            return false;
        }
    }

    private string BuildFingerprint()
    {
        StringBuilder builder = new();
        builder.Append(roomName).Append('|');
        AppendVector(builder, pos);
        AppendVector(builder, devPos);
        builder.Append(layer).Append('|').Append(subregion ?? string.Empty).Append('|');

        if (nodePositions != null)
            for (int i = 0; i < nodePositions.Length; i++) AppendVector(builder, nodePositions[i]);
        builder.Append('|');

        if (exitDirections != null)
            for (int i = 0; i < exitDirections.Length; i++) builder.Append(exitDirections[i]).Append(',');
        builder.Append('|');

        if (attractions != null)
            for (int i = 0; i < attractions.Length; i++) builder.Append(attractions[i]?.value ?? string.Empty).Append(',');
        builder.Append('|');

        List<string> keys = new(namedAttractions.Keys);
        keys.Sort(StringComparer.Ordinal);
        for (int i = 0; i < keys.Count; i++)
        {
            string key = keys[i];
            builder.Append(key).Append('=').Append(namedAttractions[key]?.value ?? string.Empty).Append(',');
        }
        return builder.ToString();
    }

    private static void AppendVector(StringBuilder builder, Vector2 value)
    {
        builder.Append(value.x.ToString("R", CultureInfo.InvariantCulture))
            .Append(',')
            .Append(value.y.ToString("R", CultureInfo.InvariantCulture))
            .Append('|');
    }

    private static RoomPanel FindRoomPanel(MapPage page, int roomIndex)
    {
        if (page?.subNodes == null) return null;
        for (int i = 0; i < page.subNodes.Count; i++)
        {
            if (page.subNodes[i] is RoomPanel panel && panel.roomRep?.room?.index == roomIndex)
                return panel;
        }
        return null;
    }

    private static RoomPanel FindRoomPanel(MapPage page, string name)
    {
        if (page?.subNodes == null || string.IsNullOrEmpty(name)) return null;
        for (int i = 0; i < page.subNodes.Count; i++)
        {
            if (page.subNodes[i] is RoomPanel panel &&
                string.Equals(panel.roomRep?.room?.name, name, StringComparison.OrdinalIgnoreCase))
                return panel;
        }
        return null;
    }
}

/// <summary>
/// Lossless transaction for one AmbientSound. Presence and list position are part of the snapshot,
/// so dragging an AmbientSoundPanel into vanilla's TrashBin remains undoable without serializing the
/// complete RoomSettings document.
/// </summary>
internal sealed class SingleAmbientSoundStateSnapshot : IEditorStateSnapshot
{
    private static readonly string[] SoundSeparator = { "><" };

    private readonly RoomSettings settings;
    private readonly AmbientSound target;
    private readonly int index;
    private readonly bool present;
    private readonly string serialized;
    private readonly bool inherited;
    private readonly bool overWrite;

    private SingleAmbientSoundStateSnapshot(
        RoomSettings settings,
        AmbientSound target,
        int index,
        bool present,
        string serialized,
        bool inherited,
        bool overWrite)
    {
        this.settings = settings;
        this.target = target;
        this.index = index;
        this.present = present;
        this.serialized = serialized ?? string.Empty;
        this.inherited = inherited;
        this.overWrite = overWrite;

        Fingerprint = present
            ? "1|" + index + "|" + (inherited ? "1" : "0") + "|" + (overWrite ? "1" : "0") + "|" + this.serialized
            : "0";
    }

    public string Kind =>
        "AmbientSound:" + (target == null ? 0 : RuntimeHelpers.GetHashCode(target));

    public string Fingerprint { get; }

    internal static SingleAmbientSoundStateSnapshot Capture(RoomSettings settings, AmbientSound target)
    {
        if (settings?.ambientSounds == null || target == null)
            return null;

        int index = settings.ambientSounds.IndexOf(target);
        bool present = index >= 0;
        string serialized = present ? target.ToString() : string.Empty;
        return new SingleAmbientSoundStateSnapshot(
            settings,
            target,
            index,
            present,
            serialized,
            target.inherited,
            target.overWrite);
    }

    public IEditorStateSnapshot CaptureCurrent(EditorSession session) =>
        ReferenceEquals(session?.RoomSettings, settings)
            ? Capture(settings, target)
            : null;

    public bool Restore(EditorSession session)
    {
        if (!ReferenceEquals(session?.RoomSettings, settings) || settings?.ambientSounds == null || target == null)
            return false;

        try
        {
            if (!present)
            {
                settings.ambientSounds.Remove(target);
            }
            else
            {
                int currentIndex = settings.ambientSounds.IndexOf(target);
                if (currentIndex >= 0)
                    settings.ambientSounds.RemoveAt(currentIndex);

                int insertIndex = Math.Max(0, Math.Min(index, settings.ambientSounds.Count));
                settings.ambientSounds.Insert(insertIndex, target);

                target.FromString(serialized.Split(SoundSeparator, StringSplitOptions.None));
                target.inherited = inherited;
                target.overWrite = overWrite;
            }

            if (session.Owner?.activePage is SoundPage soundPage)
                soundPage.Refresh();
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool single-sound restore failed: " + error.Message);
            return false;
        }
    }
}

/// <summary>
/// Lossless transaction for one EventTrigger. The serialized trigger format already preserves
/// subtype-specific data (Spot position/radius, SeeCreature type, event data and unknown attrs).
/// Presence/list position are captured separately so vanilla TrashBin delete/undo remains exact.
/// </summary>
internal sealed class SingleTriggerStateSnapshot : IEditorStateSnapshot
{
    private static readonly string[] TriggerSeparator = { "<tA>" };

    private readonly RoomSettings settings;
    private readonly EventTrigger target;
    private readonly int index;
    private readonly bool present;
    private readonly string serialized;

    private SingleTriggerStateSnapshot(
        RoomSettings settings,
        EventTrigger target,
        int index,
        bool present,
        string serialized)
    {
        this.settings = settings;
        this.target = target;
        this.index = index;
        this.present = present;
        this.serialized = serialized ?? string.Empty;
        Fingerprint = present ? "1|" + index + "|" + this.serialized : "0";
    }

    public string Kind =>
        "EventTrigger:" + (target == null ? 0 : RuntimeHelpers.GetHashCode(target));

    public string Fingerprint { get; }

    internal static SingleTriggerStateSnapshot Capture(RoomSettings settings, EventTrigger target)
    {
        if (settings?.triggers == null || target == null)
            return null;

        int index = settings.triggers.IndexOf(target);
        bool present = index >= 0;
        return new SingleTriggerStateSnapshot(
            settings,
            target,
            index,
            present,
            present ? target.ToString() : string.Empty);
    }

    public IEditorStateSnapshot CaptureCurrent(EditorSession session) =>
        ReferenceEquals(session?.RoomSettings, settings)
            ? Capture(settings, target)
            : null;

    public bool Restore(EditorSession session)
    {
        if (!ReferenceEquals(session?.RoomSettings, settings) || settings?.triggers == null || target == null)
            return false;

        try
        {
            if (!present)
            {
                settings.triggers.Remove(target);
            }
            else
            {
                int currentIndex = settings.triggers.IndexOf(target);
                if (currentIndex >= 0)
                    settings.triggers.RemoveAt(currentIndex);

                int insertIndex = Math.Max(0, Math.Min(index, settings.triggers.Count));
                settings.triggers.Insert(insertIndex, target);

                // EventTrigger.FromString only creates an event when the serialized value is not
                // NONE. Clear first so undoing an "add event" operation can faithfully restore NONE.
                target.tEvent = null;
                target.FromString(serialized.Split(TriggerSeparator, StringSplitOptions.None));
            }

            if (session.Owner?.activePage is TriggersPage triggersPage)
                triggersPage.Refresh();
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool single-trigger restore failed: " + error.Message);
            return false;
        }
    }
}
