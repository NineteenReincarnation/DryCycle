using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text;
using DevInterface;
using UnityEngine;

namespace DryCycle.OptimizedVanilla;

/// <summary>
/// Adds desktop-style shortcuts to Rain World's vanilla H-mode DevUI.
/// Ctrl+S forwards to the active page's native Save button. Ctrl+Z/Ctrl+Y provide
/// a small in-session undo/redo history for the vanilla editable pages.
/// </summary>
internal static class VanillaDevUIShortcutRuntime
{
    private const int MaxHistory = 64;
    private const string WeatherEditorId = "DryCycle_WeatherSpatial";

    private static ConditionalWeakTable<DevUI, SessionState> _states = new();
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        On.DevInterface.DevUI.Update += DevUI_Update;
        _enabled = true;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.DevInterface.DevUI.Update -= DevUI_Update;
        _states = new ConditionalWeakTable<DevUI, SessionState>();
        _enabled = false;
    }

    private static void DevUI_Update(On.DevInterface.DevUI.orig_Update orig, DevUI self)
    {
        if (self == null)
        {
            orig(self);
            return;
        }

        SessionState state = _states.GetOrCreateValue(self);
        state.SyncScope(self);

        bool weatherEditorOwnsShortcuts = IsWeatherEditorActive(self.activePage);
        bool control = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        bool pointerDownBefore = Input.GetMouseButton(0);

        if (!weatherEditorOwnsShortcuts && control && !pointerDownBefore)
        {
            if (Input.GetKeyDown(KeyCode.S))
            {
                TriggerNativeSave(self);
            }
            else if (Input.GetKeyDown(KeyCode.Z))
            {
                state.Undo(self);
            }
            else if (Input.GetKeyDown(KeyCode.Y))
            {
                state.Redo(self);
            }
        }

        if (!weatherEditorOwnsShortcuts && pointerDownBefore && !state.PointerDown)
        {
            state.BeginPointerEdit(self);
        }

        orig(self);

        // A page switch or room transition invalidates page-local history. Vanilla
        // rebuilds those editors from their own backing data, so carrying snapshots
        // across that boundary would restore into stale UI nodes.
        if (!state.IsSameScope(self))
        {
            state.ResetScope(self);
            state.PointerDown = Input.GetMouseButton(0);
            return;
        }

        bool pointerDownAfter = Input.GetMouseButton(0);
        if (state.HasPendingPointerEdit && !pointerDownAfter)
        {
            state.CommitPointerEdit(self);
        }

        state.PointerDown = pointerDownAfter;
    }

    private static bool TriggerNativeSave(DevUI ui)
    {
        Page page = ui?.activePage;
        if (page == null)
        {
            return false;
        }

        for (int i = 0; i < page.subNodes.Count; i++)
        {
            if (page.subNodes[i] is Button button &&
                string.Equals(button.IDstring, "Save_Settings", StringComparison.Ordinal))
            {
                // Use the original button path rather than duplicating per-page save
                // logic. Map, room settings, objects, sound, triggers and relationships
                // therefore keep exactly their vanilla Save behavior.
                button.Clicked();
                return true;
            }
        }

        return false;
    }

    private static bool IsWeatherEditorActive(Page page)
    {
        if (page == null)
        {
            return false;
        }

        DevUINode editor = FindNode(page, WeatherEditorId);
        return editor is Panel panel && !panel.collapsed;
    }

    private static DevUINode FindNode(DevUINode root, string id)
    {
        if (root == null)
        {
            return null;
        }

        if (string.Equals(root.IDstring, id, StringComparison.Ordinal))
        {
            return root;
        }

        for (int i = 0; i < root.subNodes.Count; i++)
        {
            DevUINode found = FindNode(root.subNodes[i], id);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private sealed class SessionState
    {
        private readonly List<HistoryEntry> _undo = new();
        private readonly List<HistoryEntry> _redo = new();

        private Page _page;
        private RoomSettings _roomSettings;
        private World _world;
        private DevSnapshot _pointerStart;

        internal bool PointerDown;
        internal bool HasPendingPointerEdit => _pointerStart != null;

        internal void SyncScope(DevUI ui)
        {
            if (!IsSameScope(ui))
            {
                ResetScope(ui);
            }
        }

        internal bool IsSameScope(DevUI ui)
        {
            return ReferenceEquals(_page, ui?.activePage) &&
                   ReferenceEquals(_roomSettings, ui?.room?.roomSettings) &&
                   ReferenceEquals(_world, ResolveWorld(ui));
        }

        internal void ResetScope(DevUI ui)
        {
            _page = ui?.activePage;
            _roomSettings = ui?.room?.roomSettings;
            _world = ResolveWorld(ui);
            _pointerStart = null;
            _undo.Clear();
            _redo.Clear();
        }

        internal void BeginPointerEdit(DevUI ui)
        {
            _pointerStart = DevSnapshot.Capture(ui);
        }

        internal void CommitPointerEdit(DevUI ui)
        {
            DevSnapshot before = _pointerStart;
            _pointerStart = null;
            if (before == null)
            {
                return;
            }

            DevSnapshot after = DevSnapshot.Capture(ui);
            if (after == null || !before.IsCompatibleWith(after) || before.SameState(after))
            {
                return;
            }

            _undo.Add(new HistoryEntry(before, after));
            if (_undo.Count > MaxHistory)
            {
                _undo.RemoveAt(0);
            }
            _redo.Clear();
        }

        internal void Undo(DevUI ui)
        {
            if (_pointerStart != null || _undo.Count == 0)
            {
                return;
            }

            HistoryEntry entry = _undo[_undo.Count - 1];
            if (!entry.Before.Restore(ui))
            {
                ResetScope(ui);
                return;
            }

            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add(entry);
        }

        internal void Redo(DevUI ui)
        {
            if (_pointerStart != null || _redo.Count == 0)
            {
                return;
            }

            HistoryEntry entry = _redo[_redo.Count - 1];
            if (!entry.After.Restore(ui))
            {
                ResetScope(ui);
                return;
            }

            _redo.RemoveAt(_redo.Count - 1);
            _undo.Add(entry);
        }

        private static World ResolveWorld(DevUI ui)
        {
            if (ui?.activePage is MapPage mapPage)
            {
                return mapPage.world;
            }
            return ui?.room?.world;
        }
    }

    private readonly struct HistoryEntry
    {
        internal readonly DevSnapshot Before;
        internal readonly DevSnapshot After;

        internal HistoryEntry(DevSnapshot before, DevSnapshot after)
        {
            Before = before;
            After = after;
        }
    }

    private abstract class DevSnapshot
    {
        protected DevSnapshot(string fingerprint)
        {
            Fingerprint = fingerprint ?? string.Empty;
        }

        protected string Fingerprint { get; }
        protected abstract string Kind { get; }

        internal bool IsCompatibleWith(DevSnapshot other) =>
            other != null && string.Equals(Kind, other.Kind, StringComparison.Ordinal);

        internal bool SameState(DevSnapshot other) =>
            IsCompatibleWith(other) && string.Equals(Fingerprint, other.Fingerprint, StringComparison.Ordinal);

        internal abstract bool Restore(DevUI ui);

        internal static DevSnapshot Capture(DevUI ui)
        {
            Page page = ui?.activePage;
            if (page == null)
            {
                return null;
            }

            try
            {
                if (page is MapPage mapPage)
                {
                    return MapSnapshot.Capture(mapPage);
                }

                if (page is RelationshipPage)
                {
                    return RelationshipSnapshot.Capture();
                }

                // Dialog is a read-only browser. The remaining vanilla editor pages
                // all edit the current RoomSettings object.
                if (page is DialogPage)
                {
                    return null;
                }

                return RoomSettingsSnapshot.Capture(ui.room?.roomSettings);
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning("DryCycle vanilla DevUI history capture failed: " + ex.Message);
                return null;
            }
        }
    }

    private sealed class RoomSettingsSnapshot : DevSnapshot
    {
        private static readonly HashSet<string> IdentityFields = new(StringComparer.Ordinal)
        {
            "isAncestor",
            "isTemplate",
            "isFirstTemplate",
            "parent",
            "game",
            "room",
            "name",
            "filePath"
        };

        private readonly RoomSettings _target;
        private readonly string _serialized;

        private RoomSettingsSnapshot(RoomSettings target, string serialized)
            : base(serialized)
        {
            _target = target;
            _serialized = serialized;
        }

        protected override string Kind => "RoomSettings";

        internal static RoomSettingsSnapshot Capture(RoomSettings settings)
        {
            if (settings == null)
            {
                return null;
            }

            string serialized = Serialize(settings);
            return serialized == null ? null : new RoomSettingsSnapshot(settings, serialized);
        }

        internal override bool Restore(DevUI ui)
        {
            RoomSettings current = ui?.room?.roomSettings;
            if (current == null || !ReferenceEquals(current, _target))
            {
                return false;
            }

            string tempPath = NewTempPath();
            try
            {
                File.WriteAllText(tempPath, _serialized);

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
                if (!parsed.Load(timeline))
                {
                    return false;
                }

                CopyEditableFields(parsed, current);
                ui.activePage?.Refresh();
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning("DryCycle vanilla DevUI RoomSettings restore failed: " + ex.Message);
                return false;
            }
            finally
            {
                TryDelete(tempPath);
            }
        }

        private static string Serialize(RoomSettings settings)
        {
            string tempPath = NewTempPath();
            try
            {
                settings.Save(tempPath, saveAsTemplate: false);
                return File.ReadAllText(tempPath);
            }
            finally
            {
                TryDelete(tempPath);
            }
        }

        private static void CopyEditableFields(RoomSettings source, RoomSettings destination)
        {
            FieldInfo[] fields = typeof(RoomSettings).GetFields(BindingFlags.Instance | BindingFlags.Public);
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                if (field.IsStatic || IdentityFields.Contains(field.Name))
                {
                    continue;
                }
                field.SetValue(destination, field.GetValue(source));
            }
        }

        private static string NewTempPath() =>
            Path.Combine(Path.GetTempPath(), "DryCycle_DevUI_" + Guid.NewGuid().ToString("N") + ".txt");

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }

    private sealed class RelationshipSnapshot : DevSnapshot
    {
        private readonly Dictionary<CreatureTemplate.Type, Dictionary<CreatureTemplate.Type, CreatureTemplate.Relationship>> _data;

        private RelationshipSnapshot(
            Dictionary<CreatureTemplate.Type, Dictionary<CreatureTemplate.Type, CreatureTemplate.Relationship>> data,
            string fingerprint)
            : base(fingerprint)
        {
            _data = data;
        }

        protected override string Kind => "Relationships";

        internal static RelationshipSnapshot Capture()
        {
            Dictionary<CreatureTemplate.Type, Dictionary<CreatureTemplate.Type, CreatureTemplate.Relationship>> copy = new();
            foreach (KeyValuePair<CreatureTemplate.Type, Dictionary<CreatureTemplate.Type, CreatureTemplate.Relationship>> outer in RelationshipPage.changedRelationships)
            {
                Dictionary<CreatureTemplate.Type, CreatureTemplate.Relationship> inner = new();
                foreach (KeyValuePair<CreatureTemplate.Type, CreatureTemplate.Relationship> pair in outer.Value)
                {
                    inner[pair.Key] = pair.Value;
                }
                copy[outer.Key] = inner;
            }

            return new RelationshipSnapshot(copy, RelationshipFingerprint(copy));
        }

        internal override bool Restore(DevUI ui)
        {
            if (ui?.activePage is not RelationshipPage page)
            {
                return false;
            }

            RelationshipPage.changedRelationships.Clear();
            foreach (KeyValuePair<CreatureTemplate.Type, Dictionary<CreatureTemplate.Type, CreatureTemplate.Relationship>> outer in _data)
            {
                Dictionary<CreatureTemplate.Type, CreatureTemplate.Relationship> inner = new();
                foreach (KeyValuePair<CreatureTemplate.Type, CreatureTemplate.Relationship> pair in outer.Value)
                {
                    inner[pair.Key] = pair.Value;
                }
                RelationshipPage.changedRelationships[outer.Key] = inner;
            }

            page.refresh = true;
            page.Refresh();
            return true;
        }

        private static string RelationshipFingerprint(
            Dictionary<CreatureTemplate.Type, Dictionary<CreatureTemplate.Type, CreatureTemplate.Relationship>> data)
        {
            List<CreatureTemplate.Type> outerKeys = new(data.Keys);
            outerKeys.Sort((a, b) => string.Compare(a?.value, b?.value, StringComparison.Ordinal));
            StringBuilder builder = new();
            for (int i = 0; i < outerKeys.Count; i++)
            {
                CreatureTemplate.Type source = outerKeys[i];
                List<CreatureTemplate.Type> innerKeys = new(data[source].Keys);
                innerKeys.Sort((a, b) => string.Compare(a?.value, b?.value, StringComparison.Ordinal));
                for (int j = 0; j < innerKeys.Count; j++)
                {
                    CreatureTemplate.Type target = innerKeys[j];
                    CreatureTemplate.Relationship relationship = data[source][target];
                    builder.Append(source?.value).Append('>')
                        .Append(target?.value).Append('=')
                        .Append(relationship.type?.value).Append(':')
                        .Append(relationship.intensity.ToString("R", CultureInfo.InvariantCulture)).Append(';');
                }
            }
            return builder.ToString();
        }
    }

    private sealed class MapSnapshot : DevSnapshot
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

        private readonly World _world;
        private readonly List<RoomState> _rooms;
        private readonly Dictionary<CreatureTemplate.Type, string> _defaultAttractions;
        private readonly Dictionary<string, string> _defaultNamedAttractions;
        private readonly List<MaterialState> _materials;

        private MapSnapshot(
            World world,
            List<RoomState> rooms,
            Dictionary<CreatureTemplate.Type, string> defaultAttractions,
            Dictionary<string, string> defaultNamedAttractions,
            List<MaterialState> materials,
            string fingerprint)
            : base(fingerprint)
        {
            _world = world;
            _rooms = rooms;
            _defaultAttractions = defaultAttractions;
            _defaultNamedAttractions = defaultNamedAttractions;
            _materials = materials;
        }

        protected override string Kind => "Map";

        internal static MapSnapshot Capture(MapPage page)
        {
            if (page?.world == null)
            {
                return null;
            }

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
                        NodePositions = roomPanel.roomRep.nodePositions == null
                            ? null
                            : (Vector2[])roomPanel.roomRep.nodePositions.Clone(),
                        ExitDirections = roomPanel.roomRep.exitDirections == null
                            ? null
                            : (int[])roomPanel.roomRep.exitDirections.Clone(),
                        Attractions = room.roomAttractions == null
                            ? null
                            : (AbstractRoom.CreatureRoomAttraction[])room.roomAttractions.Clone(),
                        NamedAttractions = room.namedRoomAttractions == null
                            ? new Dictionary<string, AbstractRoom.CreatureRoomAttraction>(StringComparer.Ordinal)
                            : new Dictionary<string, AbstractRoom.CreatureRoomAttraction>(room.namedRoomAttractions, StringComparer.Ordinal)
                    });
                }
                else if (page.subNodes[i] is MapRenderDefaultMaterial material)
                {
                    Panel materialPanel = material.handleA.subNodes.Count > 0
                        ? material.handleA.subNodes[0] as Panel
                        : null;
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
            Dictionary<CreatureTemplate.Type, string> defaultAttractions =
                page.world.defaultRoomAttractions == null
                    ? new Dictionary<CreatureTemplate.Type, string>()
                    : new Dictionary<CreatureTemplate.Type, string>(page.world.defaultRoomAttractions);
            Dictionary<string, string> defaultNamed =
                page.world.defaultNamedAttractions == null
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : new Dictionary<string, string>(page.world.defaultNamedAttractions, StringComparer.Ordinal);

            string fingerprint = MapFingerprint(rooms, defaultAttractions, defaultNamed, materials);
            return new MapSnapshot(page.world, rooms, defaultAttractions, defaultNamed, materials, fingerprint);
        }

        internal override bool Restore(DevUI ui)
        {
            if (ui?.activePage is not MapPage page || !ReferenceEquals(page.world, _world))
            {
                return false;
            }

            Dictionary<string, RoomState> byName = new(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _rooms.Count; i++)
            {
                byName[_rooms[i].Name] = _rooms[i];
            }

            for (int i = 0; i < page.subNodes.Count; i++)
            {
                if (page.subNodes[i] is not RoomPanel roomPanel ||
                    roomPanel.roomRep?.room == null ||
                    !byName.TryGetValue(roomPanel.roomRep.room.name, out RoomState state))
                {
                    continue;
                }

                roomPanel.pos = state.Pos;
                roomPanel.devPos = state.DevPos;
                roomPanel.layer = state.Layer;
                roomPanel.roomRep.room.subregionName = state.Subregion;
                roomPanel.roomRep.nodePositions = state.NodePositions == null
                    ? null
                    : (Vector2[])state.NodePositions.Clone();
                roomPanel.roomRep.exitDirections = state.ExitDirections == null
                    ? null
                    : (int[])state.ExitDirections.Clone();

                if (state.Attractions != null)
                {
                    roomPanel.roomRep.room.roomAttractions =
                        (AbstractRoom.CreatureRoomAttraction[])state.Attractions.Clone();
                }

                roomPanel.roomRep.room.namedRoomAttractions.Clear();
                foreach (KeyValuePair<string, AbstractRoom.CreatureRoomAttraction> pair in state.NamedAttractions)
                {
                    roomPanel.roomRep.room.namedRoomAttractions[pair.Key] = pair.Value;
                }
                roomPanel.Refresh();
            }

            page.world.defaultRoomAttractions.Clear();
            foreach (KeyValuePair<CreatureTemplate.Type, string> pair in _defaultAttractions)
            {
                page.world.defaultRoomAttractions[pair.Key] = pair.Value;
            }

            page.world.defaultNamedAttractions.Clear();
            foreach (KeyValuePair<string, string> pair in _defaultNamedAttractions)
            {
                page.world.defaultNamedAttractions[pair.Key] = pair.Value;
            }

            RestoreMaterials(page);
            page.Refresh();
            return true;
        }

        private void RestoreMaterials(MapPage page)
        {
            for (int i = page.subNodes.Count - 1; i >= 0; i--)
            {
                if (page.subNodes[i] is not MapRenderDefaultMaterial material)
                {
                    continue;
                }

                page.modeSpecificNodes?.Remove(material);
                material.ClearSprites();
                page.subNodes.RemoveAt(i);
            }

            for (int i = 0; i < _materials.Count; i++)
            {
                MaterialState state = _materials[i];
                MapRenderDefaultMaterial material = new(
                    page.owner,
                    "Def_Mat",
                    page,
                    state.A);
                material.handleA.pos = state.A;
                material.handleB.pos = state.B;
                if (material.handleA.subNodes.Count > 0 && material.handleA.subNodes[0] is Panel panel)
                {
                    panel.pos = state.Panel;
                }
                material.materialIsAir = state.IsAir;
                page.subNodes.Add(material);
                page.modeSpecificNodes?.Add(material);
                material.Refresh();
            }
        }

        private static string MapFingerprint(
            List<RoomState> rooms,
            Dictionary<CreatureTemplate.Type, string> defaultAttractions,
            Dictionary<string, string> defaultNamed,
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
                {
                    for (int j = 0; j < room.NodePositions.Length; j++)
                    {
                        AppendVector(builder, room.NodePositions[j]);
                    }
                }
                builder.Append('|');
                if (room.ExitDirections != null)
                {
                    for (int j = 0; j < room.ExitDirections.Length; j++)
                    {
                        builder.Append(room.ExitDirections[j]).Append(',');
                    }
                }
                builder.Append('|');
                if (room.Attractions != null)
                {
                    for (int j = 0; j < room.Attractions.Length; j++)
                    {
                        builder.Append(room.Attractions[j]?.value ?? string.Empty).Append(',');
                    }
                }
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

            List<CreatureTemplate.Type> defaultKeys = new(defaultAttractions.Keys);
            defaultKeys.Sort((a, b) => string.Compare(a?.value, b?.value, StringComparison.Ordinal));
            for (int i = 0; i < defaultKeys.Count; i++)
            {
                CreatureTemplate.Type key = defaultKeys[i];
                builder.Append("D:").Append(key?.value).Append('=').Append(defaultAttractions[key]).Append(';');
            }

            List<string> defaultNamedKeys = new(defaultNamed.Keys);
            defaultNamedKeys.Sort(StringComparer.Ordinal);
            for (int i = 0; i < defaultNamedKeys.Count; i++)
            {
                string key = defaultNamedKeys[i];
                builder.Append("N:").Append(key).Append('=').Append(defaultNamed[key]).Append(';');
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
}
