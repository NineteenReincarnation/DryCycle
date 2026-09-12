using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.History;

internal interface IEditorStateSnapshot
{
    string Kind { get; }
    string Fingerprint { get; }
    IEditorStateSnapshot CaptureCurrent(EditorSession session);
    bool Restore(EditorSession session);
}

internal sealed class SnapshotHistoryEntry : IEditorHistoryEntry
{
    private readonly IEditorStateSnapshot before;
    private readonly IEditorStateSnapshot after;

    internal SnapshotHistoryEntry(string label, IEditorStateSnapshot before, IEditorStateSnapshot after)
    {
        Label = string.IsNullOrEmpty(label) ? "Legacy edit" : label;
        this.before = before;
        this.after = after;
    }

    public string Label { get; }
    public bool Undo(EditorSession session) => before?.Restore(session) ?? false;
    public bool Redo(EditorSession session) => after?.Restore(session) ?? false;

    internal static bool TryCreate(
        string label,
        IEditorStateSnapshot before,
        IEditorStateSnapshot after,
        out SnapshotHistoryEntry entry)
    {
        entry = null;
        if (before == null || after == null ||
            !string.Equals(before.Kind, after.Kind, StringComparison.Ordinal) ||
            string.Equals(before.Fingerprint, after.Fingerprint, StringComparison.Ordinal))
            return false;

        entry = new SnapshotHistoryEntry(label, before, after);
        return true;
    }
}

internal static class LegacySnapshotFactory
{
    internal static IEditorStateSnapshot CaptureForPointer(EditorSession session)
    {
        Page page = session?.Owner?.activePage;
        if (page == null || page is DialogPage) return null;

        try
        {
            if (page is MapPage mapPage) return MapStateSnapshot.Capture(mapPage);
            if (page is RelationshipPage) return RelationshipStateSnapshot.Capture();
            if (page is ObjectsPage) return PlacedObjectsStateSnapshot.Capture(session.RoomSettings);
            return RoomSettingsStateSnapshot.Capture(session.RoomSettings);
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool legacy pointer snapshot failed: " + error.Message);
            return null;
        }
    }

    internal static IEditorStateSnapshot CaptureForNode(EditorSession session, DevUINode origin)
    {
        if (session?.Owner == null || origin == null) return null;

        PlacedObjectRepresentation representation = FindPlacedObjectAncestor(origin);
        if (representation?.pObj != null)
            return SinglePlacedObjectStateSnapshot.Capture(session.RoomSettings, representation.pObj);

        Page page = session.Owner.activePage;
        if (page == null || page is DialogPage) return null;

        try
        {
            if (page is MapPage mapPage) return MapStateSnapshot.Capture(mapPage);
            if (page is RelationshipPage) return RelationshipStateSnapshot.Capture();
            return RoomSettingsStateSnapshot.Capture(session.RoomSettings);
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool legacy node snapshot failed: " + error.Message);
            return null;
        }
    }

    private static PlacedObjectRepresentation FindPlacedObjectAncestor(DevUINode node)
    {
        DevUINode current = node;
        while (current != null)
        {
            if (current is PlacedObjectRepresentation representation) return representation;
            current = current.parentNode;
        }
        return null;
    }
}

internal sealed class SinglePlacedObjectStateSnapshot : IEditorStateSnapshot
{
    private readonly PlacedObjectState state;

    private SinglePlacedObjectStateSnapshot(PlacedObjectState state)
    {
        this.state = state;
    }

    public string Kind => "PlacedObject:" + (state?.Target == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(state.Target));
    public string Fingerprint => state?.Fingerprint ?? string.Empty;

    internal static SinglePlacedObjectStateSnapshot Capture(RoomSettings settings, PlacedObject target)
    {
        PlacedObjectState captured = PlacedObjectState.Capture(settings, target);
        return captured == null ? null : new SinglePlacedObjectStateSnapshot(captured);
    }

    public IEditorStateSnapshot CaptureCurrent(EditorSession session) =>
        session != null && state?.Target != null ? Capture(session.RoomSettings, state.Target) : null;

    public bool Restore(EditorSession session) => state?.Restore(session) ?? false;
}

internal sealed class PlacedObjectsStateSnapshot : IEditorStateSnapshot
{
    private readonly RoomSettings settings;
    private readonly List<PlacedObjectState> states;
    private readonly string fingerprint;

    private PlacedObjectsStateSnapshot(RoomSettings settings, List<PlacedObjectState> states, string fingerprint)
    {
        this.settings = settings;
        this.states = states;
        this.fingerprint = fingerprint;
    }

    public string Kind => "PlacedObjects";
    public string Fingerprint => fingerprint;

    internal static PlacedObjectsStateSnapshot Capture(RoomSettings settings)
    {
        if (settings?.placedObjects == null) return null;

        List<PlacedObjectState> states = new(settings.placedObjects.Count);
        StringBuilder fp = new();
        for (int i = 0; i < settings.placedObjects.Count; i++)
        {
            PlacedObjectState state = PlacedObjectState.Capture(settings, settings.placedObjects[i]);
            if (state == null) continue;
            states.Add(state);
            fp.Append(i).Append(':').Append(state.Fingerprint).Append('\u001e');
        }
        return new PlacedObjectsStateSnapshot(settings, states, fp.ToString());
    }

    public IEditorStateSnapshot CaptureCurrent(EditorSession session) =>
        ReferenceEquals(session?.RoomSettings, settings) ? Capture(settings) : null;

    public bool Restore(EditorSession session)
    {
        RoomSettings current = session?.RoomSettings;
        if (!ReferenceEquals(current, settings) || current?.placedObjects == null) return false;

        try
        {
            current.placedObjects.Clear();
            for (int i = 0; i < states.Count; i++)
            {
                if (!states[i].RestoreDetachedFields(current)) return false;
                current.placedObjects.Add(states[i].Target);
            }

            session.Selection.RemoveMissing(current.placedObjects);
            session.Owner?.activePage?.Refresh();
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool placed-object collection restore failed: " + error.Message);
            return false;
        }
    }
}

internal sealed class RoomSettingsStateSnapshot : IEditorStateSnapshot
{
    private static readonly HashSet<string> IdentityFields = new(StringComparer.Ordinal)
    {
        "isAncestor", "isTemplate", "isFirstTemplate", "parent", "game", "room", "name", "filePath", "placedObjects"
    };

    private readonly RoomSettings target;
    private readonly string serialized;
    private readonly PlacedObjectsStateSnapshot placedObjects;

    private RoomSettingsStateSnapshot(RoomSettings target, string serialized, PlacedObjectsStateSnapshot placedObjects)
    {
        this.target = target;
        this.serialized = serialized;
        this.placedObjects = placedObjects;
        Fingerprint = serialized + "\n@DevToolPlacedObjects=" + (placedObjects?.Fingerprint ?? string.Empty);
    }

    public string Kind => "RoomSettings";
    public string Fingerprint { get; }

    internal static RoomSettingsStateSnapshot Capture(RoomSettings settings)
    {
        if (settings == null) return null;
        string serialized = Serialize(settings);
        return serialized == null ? null : new RoomSettingsStateSnapshot(settings, serialized, PlacedObjectsStateSnapshot.Capture(settings));
    }

    public IEditorStateSnapshot CaptureCurrent(EditorSession session) =>
        ReferenceEquals(session?.RoomSettings, target) ? Capture(target) : null;

    public bool Restore(EditorSession session)
    {
        RoomSettings current = session?.RoomSettings;
        if (!ReferenceEquals(current, target)) return false;

        string tempPath = NewTempPath();
        try
        {
            File.WriteAllText(tempPath, serialized);
#pragma warning disable SYSLIB0050
            RoomSettings parsed = (RoomSettings)FormatterServices.GetUninitializedObject(typeof(RoomSettings));
#pragma warning restore SYSLIB0050
            parsed.isAncestor = current.isAncestor;
            parsed.isTemplate = current.isTemplate;
            parsed.isFirstTemplate = current.isFirstTemplate;
            parsed.parent = current.parent;
            parsed.game = current.game;
            parsed.room = current.room;
            parsed.name = current.name;
            parsed.filePath = tempPath;
            parsed.wetTerrain = true;
            parsed.Reset();

            SlugcatStats.Timeline timeline = current.game != null && current.game.IsStorySession
                ? current.game.TimelinePoint
                : null;
            if (!parsed.Load(timeline)) return false;

            CopyEditableFields(parsed, current);
            if (placedObjects != null && !placedObjects.Restore(session)) return false;
            session.Owner?.activePage?.Refresh();
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool RoomSettings restore failed: " + error.Message);
            return false;
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static string Serialize(RoomSettings settings)
    {
        string path = NewTempPath();
        try
        {
            settings.Save(path, saveAsTemplate: false);
            return File.ReadAllText(path);
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static void CopyEditableFields(RoomSettings source, RoomSettings destination)
    {
        FieldInfo[] fields = typeof(RoomSettings).GetFields(BindingFlags.Instance | BindingFlags.Public);
        for (int i = 0; i < fields.Length; i++)
        {
            FieldInfo field = fields[i];
            if (field.IsStatic || IdentityFields.Contains(field.Name)) continue;
            field.SetValue(destination, field.GetValue(source));
        }
    }

    private static string NewTempPath() =>
        Path.Combine(Path.GetTempPath(), "DryCycle_DevTool_" + Guid.NewGuid().ToString("N") + ".txt");

    private static void TryDelete(string path)
    {
        try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
        catch { }
    }
}

internal sealed class RelationshipStateSnapshot : IEditorStateSnapshot
{
    private readonly Dictionary<CreatureTemplate.Type, Dictionary<CreatureTemplate.Type, CreatureTemplate.Relationship>> data;

    private RelationshipStateSnapshot(
        Dictionary<CreatureTemplate.Type, Dictionary<CreatureTemplate.Type, CreatureTemplate.Relationship>> data,
        string fingerprint)
    {
        this.data = data;
        Fingerprint = fingerprint;
    }

    public string Kind => "Relationships";
    public string Fingerprint { get; }

    internal static RelationshipStateSnapshot Capture()
    {
        Dictionary<CreatureTemplate.Type, Dictionary<CreatureTemplate.Type, CreatureTemplate.Relationship>> copy = new();
        foreach (KeyValuePair<CreatureTemplate.Type, Dictionary<CreatureTemplate.Type, CreatureTemplate.Relationship>> outer in RelationshipPage.changedRelationships)
        {
            Dictionary<CreatureTemplate.Type, CreatureTemplate.Relationship> inner = new();
            foreach (KeyValuePair<CreatureTemplate.Type, CreatureTemplate.Relationship> pair in outer.Value)
                inner[pair.Key] = pair.Value;
            copy[outer.Key] = inner;
        }
        return new RelationshipStateSnapshot(copy, FingerprintOf(copy));
    }

    public IEditorStateSnapshot CaptureCurrent(EditorSession session) =>
        session?.Owner?.activePage is RelationshipPage ? Capture() : null;

    public bool Restore(EditorSession session)
    {
        if (session?.Owner?.activePage is not RelationshipPage page) return false;

        RelationshipPage.changedRelationships.Clear();
        foreach (KeyValuePair<CreatureTemplate.Type, Dictionary<CreatureTemplate.Type, CreatureTemplate.Relationship>> outer in data)
        {
            Dictionary<CreatureTemplate.Type, CreatureTemplate.Relationship> inner = new();
            foreach (KeyValuePair<CreatureTemplate.Type, CreatureTemplate.Relationship> pair in outer.Value)
                inner[pair.Key] = pair.Value;
            RelationshipPage.changedRelationships[outer.Key] = inner;
        }
        page.refresh = true;
        page.Refresh();
        return true;
    }

    private static string FingerprintOf(
        Dictionary<CreatureTemplate.Type, Dictionary<CreatureTemplate.Type, CreatureTemplate.Relationship>> source)
    {
        List<CreatureTemplate.Type> outerKeys = new(source.Keys);
        outerKeys.Sort((a, b) => string.Compare(a?.value, b?.value, StringComparison.Ordinal));
        StringBuilder builder = new();
        for (int i = 0; i < outerKeys.Count; i++)
        {
            CreatureTemplate.Type from = outerKeys[i];
            List<CreatureTemplate.Type> innerKeys = new(source[from].Keys);
            innerKeys.Sort((a, b) => string.Compare(a?.value, b?.value, StringComparison.Ordinal));
            for (int j = 0; j < innerKeys.Count; j++)
            {
                CreatureTemplate.Type to = innerKeys[j];
                CreatureTemplate.Relationship relationship = source[from][to];
                builder.Append(from?.value).Append('>')
                    .Append(to?.value).Append('=')
                    .Append(relationship.type?.value).Append(':')
                    .Append(relationship.intensity.ToString("R", CultureInfo.InvariantCulture)).Append(';');
            }
        }
        return builder.ToString();
    }
}

internal sealed class MapStateSnapshot : IEditorStateSnapshot
{
    private sealed class RoomState
    {
        internal string Name;
        internal Vector2 Pos;
        internal Vector2 DevPos;
        internal int Layer;
        internal string Subregion;
        internal Vector2[] NodePositions;
        internal int[] ExitDirections;
        internal AbstractRoom.CreatureRoomAttraction[] Attractions;
        internal Dictionary<string, AbstractRoom.CreatureRoomAttraction> NamedAttractions;
    }

    private sealed class MaterialState
    {
        internal Vector2 A;
        internal Vector2 B;
        internal Vector2 Panel;
        internal bool IsAir;
    }

    private readonly global::World world;
    private readonly List<RoomState> rooms;
    private readonly Dictionary<CreatureTemplate.Type, string> defaultAttractions;
    private readonly Dictionary<string, string> defaultNamedAttractions;
    private readonly List<MaterialState> materials;

    private MapStateSnapshot(
        global::World world,
        List<RoomState> rooms,
        Dictionary<CreatureTemplate.Type, string> defaultAttractions,
        Dictionary<string, string> defaultNamedAttractions,
        List<MaterialState> materials,
        string fingerprint)
    {
        this.world = world;
        this.rooms = rooms;
        this.defaultAttractions = defaultAttractions;
        this.defaultNamedAttractions = defaultNamedAttractions;
        this.materials = materials;
        Fingerprint = fingerprint;
    }

    public string Kind => "Map";
    public string Fingerprint { get; }

    internal static MapStateSnapshot Capture(MapPage page)
    {
        if (page?.world == null) return null;

        List<RoomState> rooms = new();
        List<MaterialState> materials = new();
        for (int i = 0; i < page.subNodes.Count; i++)
        {
            if (page.subNodes[i] is RoomPanel roomPanel && roomPanel.roomRep?.room != null)
            {
                AbstractRoom room = roomPanel.roomRep.room;
                rooms.Add(new RoomState
                {
                    Name = room.name,
                    Pos = roomPanel.pos,
                    DevPos = roomPanel.devPos,
                    Layer = roomPanel.layer,
                    Subregion = room.subregionName,
                    NodePositions = roomPanel.roomRep.nodePositions == null ? null : (Vector2[])roomPanel.roomRep.nodePositions.Clone(),
                    ExitDirections = roomPanel.roomRep.exitDirections == null ? null : (int[])roomPanel.roomRep.exitDirections.Clone(),
                    Attractions = room.roomAttractions == null ? null : (AbstractRoom.CreatureRoomAttraction[])room.roomAttractions.Clone(),
                    NamedAttractions = room.namedRoomAttractions == null
                        ? new Dictionary<string, AbstractRoom.CreatureRoomAttraction>(StringComparer.Ordinal)
                        : new Dictionary<string, AbstractRoom.CreatureRoomAttraction>(room.namedRoomAttractions, StringComparer.Ordinal)
                });
            }
            else if (page.subNodes[i] is MapRenderDefaultMaterial material)
            {
                Panel materialPanel = material.handleA.subNodes.Count > 0 ? material.handleA.subNodes[0] as Panel : null;
                materials.Add(new MaterialState
                {
                    A = material.handleA.pos,
                    B = material.handleB.pos,
                    Panel = materialPanel?.pos ?? Vector2.zero,
                    IsAir = material.materialIsAir
                });
            }
        }

        rooms.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        Dictionary<CreatureTemplate.Type, string> defaults = page.world.defaultRoomAttractions == null
            ? new Dictionary<CreatureTemplate.Type, string>()
            : new Dictionary<CreatureTemplate.Type, string>(page.world.defaultRoomAttractions);
        Dictionary<string, string> named = page.world.defaultNamedAttractions == null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(page.world.defaultNamedAttractions, StringComparer.Ordinal);

        return new MapStateSnapshot(page.world, rooms, defaults, named, materials, BuildFingerprint(rooms, defaults, named, materials));
    }

    public IEditorStateSnapshot CaptureCurrent(EditorSession session) =>
        session?.Owner?.activePage is MapPage page && ReferenceEquals(page.world, world) ? Capture(page) : null;

    public bool Restore(EditorSession session)
    {
        if (session?.Owner?.activePage is not MapPage page || !ReferenceEquals(page.world, world)) return false;

        Dictionary<string, RoomState> byName = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rooms.Count; i++) byName[rooms[i].Name] = rooms[i];

        for (int i = 0; i < page.subNodes.Count; i++)
        {
            if (page.subNodes[i] is not RoomPanel roomPanel || roomPanel.roomRep?.room == null ||
                !byName.TryGetValue(roomPanel.roomRep.room.name, out RoomState state)) continue;

            roomPanel.pos = state.Pos;
            roomPanel.devPos = state.DevPos;
            roomPanel.layer = state.Layer;
            roomPanel.roomRep.room.subregionName = state.Subregion;
            roomPanel.roomRep.nodePositions = state.NodePositions == null ? null : (Vector2[])state.NodePositions.Clone();
            roomPanel.roomRep.exitDirections = state.ExitDirections == null ? null : (int[])state.ExitDirections.Clone();
            if (state.Attractions != null)
                roomPanel.roomRep.room.roomAttractions = (AbstractRoom.CreatureRoomAttraction[])state.Attractions.Clone();

            roomPanel.roomRep.room.namedRoomAttractions.Clear();
            foreach (KeyValuePair<string, AbstractRoom.CreatureRoomAttraction> pair in state.NamedAttractions)
                roomPanel.roomRep.room.namedRoomAttractions[pair.Key] = pair.Value;
            roomPanel.Refresh();
        }

        page.world.defaultRoomAttractions.Clear();
        foreach (KeyValuePair<CreatureTemplate.Type, string> pair in defaultAttractions)
            page.world.defaultRoomAttractions[pair.Key] = pair.Value;

        page.world.defaultNamedAttractions.Clear();
        foreach (KeyValuePair<string, string> pair in defaultNamedAttractions)
            page.world.defaultNamedAttractions[pair.Key] = pair.Value;

        RestoreMaterials(page);
        page.Refresh();
        return true;
    }

    private void RestoreMaterials(MapPage page)
    {
        for (int i = page.subNodes.Count - 1; i >= 0; i--)
        {
            if (page.subNodes[i] is not MapRenderDefaultMaterial material) continue;
            page.modeSpecificNodes?.Remove(material);
            material.ClearSprites();
            page.subNodes.RemoveAt(i);
        }

        for (int i = 0; i < materials.Count; i++)
        {
            MaterialState state = materials[i];
            MapRenderDefaultMaterial material = new(page.owner, "Def_Mat", page, state.A);
            material.handleA.pos = state.A;
            material.handleB.pos = state.B;
            if (material.handleA.subNodes.Count > 0 && material.handleA.subNodes[0] is Panel panel)
                panel.pos = state.Panel;
            material.materialIsAir = state.IsAir;
            page.subNodes.Add(material);
            page.modeSpecificNodes?.Add(material);
            material.Refresh();
        }
    }

    private static string BuildFingerprint(
        List<RoomState> rooms,
        Dictionary<CreatureTemplate.Type, string> defaults,
        Dictionary<string, string> named,
        List<MaterialState> materials)
    {
        StringBuilder builder = new();
        for (int i = 0; i < rooms.Count; i++)
        {
            RoomState room = rooms[i];
            builder.Append(room.Name).Append('|');
            AppendVector(builder, room.Pos);
            AppendVector(builder, room.DevPos);
            builder.Append(room.Layer).Append('|').Append(room.Subregion ?? string.Empty).Append('|');

            if (room.NodePositions != null)
                for (int j = 0; j < room.NodePositions.Length; j++) AppendVector(builder, room.NodePositions[j]);
            builder.Append('|');
            if (room.ExitDirections != null)
                for (int j = 0; j < room.ExitDirections.Length; j++) builder.Append(room.ExitDirections[j]).Append(',');
            builder.Append('|');
            if (room.Attractions != null)
                for (int j = 0; j < room.Attractions.Length; j++) builder.Append(room.Attractions[j]?.value ?? string.Empty).Append(',');
            builder.Append('|');

            List<string> namedKeys = new(room.NamedAttractions.Keys);
            namedKeys.Sort(StringComparer.Ordinal);
            for (int j = 0; j < namedKeys.Count; j++)
            {
                string key = namedKeys[j];
                builder.Append(key).Append('=').Append(room.NamedAttractions[key]?.value ?? string.Empty).Append(',');
            }
            builder.Append(';');
        }

        List<CreatureTemplate.Type> defaultKeys = new(defaults.Keys);
        defaultKeys.Sort((a, b) => string.Compare(a?.value, b?.value, StringComparison.Ordinal));
        for (int i = 0; i < defaultKeys.Count; i++)
        {
            CreatureTemplate.Type key = defaultKeys[i];
            builder.Append("D:").Append(key?.value).Append('=').Append(defaults[key]).Append(';');
        }

        List<string> namedKeys2 = new(named.Keys);
        namedKeys2.Sort(StringComparer.Ordinal);
        for (int i = 0; i < namedKeys2.Count; i++)
        {
            string key = namedKeys2[i];
            builder.Append("N:").Append(key).Append('=').Append(named[key]).Append(';');
        }

        for (int i = 0; i < materials.Count; i++)
        {
            builder.Append("M:");
            AppendVector(builder, materials[i].A);
            AppendVector(builder, materials[i].B);
            AppendVector(builder, materials[i].Panel);
            builder.Append(materials[i].IsAir ? '1' : '0').Append(';');
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
}
