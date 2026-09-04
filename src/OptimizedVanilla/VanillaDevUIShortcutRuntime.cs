using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text;
using DevInterface;
using DryCycle.DevUI.Controls;
using UnityEngine;

namespace DryCycle.OptimizedVanilla;

/// <summary>
/// Desktop-style DevUI shortcuts with hybrid transactional undo/redo.
///
/// Objects-page edits use in-memory PlacedObject snapshots that preserve object/data
/// identity. Map/relationship editors use their existing domain snapshots. Other
/// vanilla pages retain the RoomSettings serializer as a conservative fallback.
/// DryCycle text controls can explicitly bracket keyboard edits so a whole focus/edit/
/// commit sequence becomes one global history entry.
/// </summary>
internal static class VanillaDevUIShortcutRuntime
{
    private const int MaxHistory = 64;
    private const string WeatherEditorId = "DryCycle_WeatherSpatial";

    private static ConditionalWeakTable<global::DevInterface.DevUI, SessionState> _states = new();
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
        _states = new ConditionalWeakTable<global::DevInterface.DevUI, SessionState>();
        _enabled = false;
    }

    internal static void BeginExternalEdit(global::DevInterface.DevUI ui, DevUINode origin)
    {
        if (!_enabled || ui == null || origin == null || IsWeatherEditorActive(ui.activePage))
        {
            return;
        }

        SessionState state = _states.GetOrCreateValue(ui);
        state.SyncScope(ui);
        state.BeginExternalEdit(ui, origin);
    }

    internal static void CommitExternalEdit(global::DevInterface.DevUI ui, DevUINode origin)
    {
        if (!_enabled || ui == null || origin == null)
        {
            return;
        }

        SessionState state = _states.GetOrCreateValue(ui);
        state.SyncScope(ui);
        state.CommitExternalEdit(ui, origin);
    }

    internal static void CancelExternalEdit(global::DevInterface.DevUI ui, DevUINode origin)
    {
        if (!_enabled || ui == null || origin == null)
        {
            return;
        }

        SessionState state = _states.GetOrCreateValue(ui);
        state.SyncScope(ui);
        state.CancelExternalEdit(ui, origin);
    }

    private static void DevUI_Update(
        On.DevInterface.DevUI.orig_Update orig,
        global::DevInterface.DevUI self)
    {
        if (self == null)
        {
            orig(self);
            return;
        }

        SessionState state = _states.GetOrCreateValue(self);
        state.SyncScope(self);

        bool weatherEditorOwnsShortcuts = IsWeatherEditorActive(self.activePage);
        bool control = IsControlDown();
        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        int pointerMaskBefore = CurrentPointerMask();
        bool localTextEditing = DryCycleInputFocus.Focused != null || state.HasPendingExternalEdit;

        if (!weatherEditorOwnsShortcuts && control && pointerMaskBefore == 0 && !localTextEditing)
        {
            if (Input.GetKeyDown(KeyCode.S))
            {
                TriggerNativeSave(self);
            }
            else if (Input.GetKeyDown(KeyCode.Z))
            {
                if (shift)
                {
                    state.Redo(self);
                }
                else
                {
                    state.Undo(self);
                }
            }
            else if (Input.GetKeyDown(KeyCode.Y))
            {
                state.Redo(self);
            }
        }

        if (!weatherEditorOwnsShortcuts && pointerMaskBefore != 0 && state.PointerMask == 0)
        {
            state.BeginPointerEdit(self);
        }

        orig(self);

        // Page switches and room transitions invalidate page-local nodes. History does
        // not cross this boundary; every snapshot otherwise targets the live backing
        // objects for the current scope only.
        if (!state.IsSameScope(self))
        {
            state.ResetScope(self);
            state.PointerMask = CurrentPointerMask();
            return;
        }

        // Observe DryCycle text focus after controls have updated. A focus transition
        // therefore brackets the model mutation performed by the field's commit event.
        state.SyncExternalEditors(self);

        int pointerMaskAfter = CurrentPointerMask();
        if (state.HasPendingPointerEdit && pointerMaskAfter == 0)
        {
            state.CommitPointerEdit(self);
        }

        state.PointerMask = pointerMaskAfter;
    }

    private static bool IsControlDown()
    {
        return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) ||
               Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand);
    }

    private static int CurrentPointerMask()
    {
        int result = 0;
        if (Input.GetMouseButton(0))
        {
            result |= 1;
        }
        if (Input.GetMouseButton(1))
        {
            result |= 2;
        }
        return result;
    }

    private static bool TriggerNativeSave(global::DevInterface.DevUI ui)
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

    private static DevUINode FindDeepestMouseNode(DevUINode root)
    {
        if (root == null)
        {
            return null;
        }

        // DevUINode updates children in reverse order, so walk in the same order and
        // prefer the first deepest interactive child under the pointer.
        for (int i = root.subNodes.Count - 1; i >= 0; i--)
        {
            DevUINode found = FindDeepestMouseNode(root.subNodes[i]);
            if (found != null)
            {
                return found;
            }
        }

        return IsMouseOver(root) ? root : null;
    }

    private static bool IsMouseOver(DevUINode node)
    {
        if (node is Handle handle)
        {
            return handle.MouseOver;
        }
        if (node is RectangularDevUINode rectangular)
        {
            return rectangular.MouseOver;
        }
        return false;
    }

    private static PlacedObjectRepresentation FindPlacedObjectAncestor(DevUINode node)
    {
        DevUINode current = node;
        while (current != null)
        {
            if (current is PlacedObjectRepresentation representation)
            {
                return representation;
            }
            current = current.parentNode;
        }
        return null;
    }

    private static bool IsLegacyEditorEditing(DevUINode node)
    {
        if (node == null)
        {
            return false;
        }

        Type type = node.GetType();
        string fullName = type.FullName ?? string.Empty;
        if (!fullName.EndsWith("SolarShadeZoneRepresentation+ZoneTextInput", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            FieldInfo field = type.GetField("_editing", BindingFlags.Instance | BindingFlags.NonPublic);
            return field != null && field.FieldType == typeof(bool) && (bool)field.GetValue(node);
        }
        catch
        {
            return false;
        }
    }

    private sealed class SessionState
    {
        private readonly List<HistoryEntry> _undo = new();
        private readonly List<HistoryEntry> _redo = new();

        private Page _page;
        private RoomSettings _roomSettings;
        private World _world;
        private HistoryState _pointerStart;
        private HistoryState _externalStart;
        private DevUINode _externalOrigin;
        private DevUINode _legacyExternalOrigin;

        internal int PointerMask;
        internal bool HasPendingPointerEdit => _pointerStart != null;
        internal bool HasPendingExternalEdit => _externalStart != null;

        internal void SyncScope(global::DevInterface.DevUI ui)
        {
            if (!IsSameScope(ui))
            {
                ResetScope(ui);
            }
        }

        internal bool IsSameScope(global::DevInterface.DevUI ui)
        {
            return ReferenceEquals(_page, ui?.activePage) &&
                   ReferenceEquals(_roomSettings, ui?.room?.roomSettings) &&
                   ReferenceEquals(_world, ResolveWorld(ui));
        }

        internal void ResetScope(global::DevInterface.DevUI ui)
        {
            _page = ui?.activePage;
            _roomSettings = ui?.room?.roomSettings;
            _world = ResolveWorld(ui);
            _pointerStart = null;
            _externalStart = null;
            _externalOrigin = null;
            _legacyExternalOrigin = null;
            _undo.Clear();
            _redo.Clear();
        }

        internal void SyncExternalEditors(global::DevInterface.DevUI ui)
        {
            DryCycleTextField focused = DryCycleInputFocus.Focused;
            if (focused != null &&
                (!ReferenceEquals(focused.owner, ui) || !ReferenceEquals(focused.Page, ui?.activePage)))
            {
                focused = null;
            }

            // Finish the currently tracked keyboard transaction first. This ordering
            // matters when one click commits editor A and focuses editor B in the same
            // DevUI.Update call.
            if (_externalStart != null)
            {
                if (_externalOrigin is DryCycleTextField dryCycleField)
                {
                    if (!ReferenceEquals(dryCycleField, focused))
                    {
                        CommitExternalEdit(ui, dryCycleField);
                    }
                }
                else if (_legacyExternalOrigin != null &&
                         ReferenceEquals(_externalOrigin, _legacyExternalOrigin) &&
                         !IsLegacyEditorEditing(_legacyExternalOrigin))
                {
                    DevUINode finished = _legacyExternalOrigin;
                    _legacyExternalOrigin = null;
                    CommitExternalEdit(ui, finished);
                }
            }

            if (_externalStart != null)
            {
                return;
            }

            // DryCycleTextField uses a central focus manager, so it is the most reliable
            // source of a keyboard transaction boundary.
            if (focused != null)
            {
                BeginExternalEdit(ui, focused);
                return;
            }

            // Environment Zone predates DryCycleTextField and owns a small private
            // numeric editor. Keep it integrated without rewriting that mapper UI: the
            // pointer frame identifies the editor once, then its private _editing flag
            // is observed until commit/cancel.
            DevUINode legacy = _legacyExternalOrigin;
            if (legacy == null || !IsLegacyEditorEditing(legacy))
            {
                legacy = FindDeepestMouseNode(ui?.activePage);
                if (legacy == null || !IsLegacyEditorEditing(legacy))
                {
                    _legacyExternalOrigin = null;
                    return;
                }
                _legacyExternalOrigin = legacy;
            }

            BeginExternalEdit(ui, legacy);
        }

        internal void BeginPointerEdit(global::DevInterface.DevUI ui)
        {
            _pointerStart = HistoryState.CaptureForPointer(ui);
        }

        internal void CommitPointerEdit(global::DevInterface.DevUI ui)
        {
            HistoryState before = _pointerStart;
            _pointerStart = null;
            CommitStatePair(ui, before);
        }

        internal void BeginExternalEdit(global::DevInterface.DevUI ui, DevUINode origin)
        {
            if (_externalStart != null)
            {
                // Focus manager commits the previous DryCycle field before beginning
                // the next one. If a custom control violates that contract, discard the
                // stale boundary rather than merging unrelated edits.
                _externalStart = null;
                _externalOrigin = null;
            }

            _externalStart = HistoryState.CaptureForNode(ui, origin);
            _externalOrigin = origin;
        }

        internal void CommitExternalEdit(global::DevInterface.DevUI ui, DevUINode origin)
        {
            if (_externalStart == null || !ReferenceEquals(_externalOrigin, origin))
            {
                return;
            }

            HistoryState before = _externalStart;
            _externalStart = null;
            _externalOrigin = null;
            CommitStatePair(ui, before);
            RebasePointerAfterExternalMutation(ui);
        }

        internal void CancelExternalEdit(global::DevInterface.DevUI ui, DevUINode origin)
        {
            if (_externalStart == null || !ReferenceEquals(_externalOrigin, origin))
            {
                return;
            }

            _externalStart = null;
            _externalOrigin = null;
            RebasePointerAfterExternalMutation(ui);
        }

        private void RebasePointerAfterExternalMutation(global::DevInterface.DevUI ui)
        {
            // Clicking away from a text field may commit it inside the same mouse-down
            // frame that begins another pointer edit. Rebase the pointer transaction so
            // the text commit is not recorded a second time by that click.
            if (_pointerStart != null)
            {
                _pointerStart = _pointerStart.CaptureCurrent(ui);
            }
        }

        private void CommitStatePair(global::DevInterface.DevUI ui, HistoryState before)
        {
            if (before == null)
            {
                return;
            }

            HistoryState after = before.CaptureCurrent(ui);
            if (after == null || !before.IsCompatibleWith(after) || before.SameState(after))
            {
                return;
            }

            PushUndo(new HistoryEntry(before, after));
            _redo.Clear();
        }

        private void PushUndo(HistoryEntry entry)
        {
            _undo.Add(entry);
            if (_undo.Count > MaxHistory)
            {
                _undo.RemoveAt(0);
            }
        }

        internal void Undo(global::DevInterface.DevUI ui)
        {
            if (_pointerStart != null || _externalStart != null || _undo.Count == 0)
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

        internal void Redo(global::DevInterface.DevUI ui)
        {
            if (_pointerStart != null || _externalStart != null || _redo.Count == 0)
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
            PushUndo(entry);
        }

        private static World ResolveWorld(global::DevInterface.DevUI ui)
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
        internal readonly HistoryState Before;
        internal readonly HistoryState After;

        internal HistoryEntry(HistoryState before, HistoryState after)
        {
            Before = before;
            After = after;
        }
    }

    private abstract class HistoryState
    {
        protected HistoryState(string fingerprint)
        {
            Fingerprint = fingerprint ?? string.Empty;
        }

        internal string Fingerprint { get; }
        protected abstract string Kind { get; }

        internal bool IsCompatibleWith(HistoryState other) =>
            other != null && string.Equals(Kind, other.Kind, StringComparison.Ordinal);

        internal bool SameState(HistoryState other) =>
            IsCompatibleWith(other) && string.Equals(Fingerprint, other.Fingerprint, StringComparison.Ordinal);

        internal abstract HistoryState CaptureCurrent(global::DevInterface.DevUI ui);
        internal abstract bool Restore(global::DevInterface.DevUI ui);

        internal static HistoryState CaptureForNode(global::DevInterface.DevUI ui, DevUINode origin)
        {
            if (ui == null || origin == null)
            {
                return null;
            }

            PlacedObjectRepresentation representation = FindPlacedObjectAncestor(origin);
            if (representation?.pObj != null)
            {
                return PlacedObjectSnapshot.Capture(ui.room?.roomSettings, representation.pObj);
            }

            return CaptureGeneric(ui);
        }

        internal static HistoryState CaptureForPointer(global::DevInterface.DevUI ui)
        {
            Page page = ui?.activePage;
            if (page == null || page is DialogPage)
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

                if (page is ObjectsPage)
                {
                    // The owner mouse position is updated inside DevUI.Update, so the
                    // pre-update hit target can be one frame stale. Capture the complete
                    // placed-object collection in memory for pointer gestures instead of
                    // risking a false target. This is still far cheaper than serializing
                    // the entire RoomSettings to disk and it also covers add/delete and
                    // custom gestures that begin on spline/edge space.
                    return PlacedObjectsSnapshot.Capture(ui.room?.roomSettings);
                }

                return RoomSettingsSnapshot.Capture(ui.room?.roomSettings);
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning("DryCycle DevUI history capture failed: " + ex.Message);
                return null;
            }
        }

        private static HistoryState CaptureGeneric(global::DevInterface.DevUI ui)
        {
            Page page = ui?.activePage;
            if (page == null || page is DialogPage)
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
                return RoomSettingsSnapshot.Capture(ui.room?.roomSettings);
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning("DryCycle DevUI external history capture failed: " + ex.Message);
                return null;
            }
        }
    }

    private sealed class PlacedObjectRecord
    {
        internal readonly PlacedObject Target;
        internal readonly PlacedObject.Type Type;
        internal readonly Vector2 Pos;
        internal readonly bool Active;
        internal readonly bool DeactivatedByWarpFilter;
        internal readonly bool Save;
        internal readonly string[] UnrecognizedAttributes;
        internal readonly PlacedObject.Data DataReference;
        internal readonly string DataSerialized;
        internal readonly string Fingerprint;

        private PlacedObjectRecord(PlacedObject target)
        {
            Target = target;
            Type = target.type;
            Pos = target.pos;
            Active = target.active;
            DeactivatedByWarpFilter = target.deactivatedByWarpFilter;
            Save = target.save;
            UnrecognizedAttributes = CloneStrings(target.unrecognizedAttributes);
            DataReference = target.data;
            DataSerialized = target.data?.ToString() ?? string.Empty;
            Fingerprint = BuildFingerprint();
        }

        internal static PlacedObjectRecord Capture(PlacedObject target)
        {
            return target == null ? null : new PlacedObjectRecord(target);
        }

        internal void Restore()
        {
            if (Target == null)
            {
                return;
            }

            Target.type = Type;
            Target.pos = Pos;
            Target.active = Active;
            Target.deactivatedByWarpFilter = DeactivatedByWarpFilter;
            Target.save = Save;
            Target.unrecognizedAttributes = CloneStrings(UnrecognizedAttributes);
            Target.data = DataReference;

            if (DataReference != null)
            {
                DataReference.owner = Target;
                DataReference.FromString(DataSerialized);
                try
                {
                    DataReference.RefreshLiveVisuals();
                }
                catch (Exception ex)
                {
                    Plugin.Logger?.LogWarning("DryCycle DevUI placed-object live refresh failed: " + ex.Message);
                }
            }
        }

        private string BuildFingerprint()
        {
            StringBuilder builder = new();
            builder.Append(RuntimeHelpers.GetHashCode(Target)).Append('|')
                .Append(Type?.value ?? string.Empty).Append('|')
                .Append(Pos.x.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(Pos.y.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(Active ? '1' : '0')
                .Append(DeactivatedByWarpFilter ? '1' : '0')
                .Append(Save ? '1' : '0').Append('|')
                .Append(DataReference == null ? 0 : RuntimeHelpers.GetHashCode(DataReference)).Append('|')
                .Append(DataSerialized).Append('|');

            if (UnrecognizedAttributes != null)
            {
                for (int i = 0; i < UnrecognizedAttributes.Length; i++)
                {
                    builder.Append(UnrecognizedAttributes[i] ?? string.Empty).Append('\u001f');
                }
            }
            return builder.ToString();
        }
    }

    private sealed class PlacedObjectSnapshot : HistoryState
    {
        private readonly RoomSettings _settings;
        private readonly PlacedObject _target;
        private readonly int _index;
        private readonly PlacedObjectRecord _record;

        private PlacedObjectSnapshot(
            RoomSettings settings,
            PlacedObject target,
            int index,
            PlacedObjectRecord record)
            : base(BuildFingerprint(index, record))
        {
            _settings = settings;
            _target = target;
            _index = index;
            _record = record;
        }

        protected override string Kind => "PlacedObject:" + RuntimeHelpers.GetHashCode(_target);

        internal static PlacedObjectSnapshot Capture(RoomSettings settings, PlacedObject target)
        {
            if (settings == null || target == null || settings.placedObjects == null)
            {
                return null;
            }

            int index = settings.placedObjects.IndexOf(target);
            return new PlacedObjectSnapshot(settings, target, index, PlacedObjectRecord.Capture(target));
        }

        internal override HistoryState CaptureCurrent(global::DevInterface.DevUI ui)
        {
            RoomSettings current = ui?.room?.roomSettings;
            return ReferenceEquals(current, _settings) ? Capture(current, _target) : null;
        }

        internal override bool Restore(global::DevInterface.DevUI ui)
        {
            RoomSettings current = ui?.room?.roomSettings;
            if (!ReferenceEquals(current, _settings) || current?.placedObjects == null)
            {
                return false;
            }

            try
            {
                // Remove all accidental duplicate references first. A redo after an add
                // must reinsert the exact original object rather than constructing a copy.
                for (int i = current.placedObjects.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(current.placedObjects[i], _target))
                    {
                        current.placedObjects.RemoveAt(i);
                    }
                }

                if (_index >= 0)
                {
                    _record?.Restore();
                    int insert = Mathf.Clamp(_index, 0, current.placedObjects.Count);
                    current.placedObjects.Insert(insert, _target);
                }

                ui.activePage?.Refresh();
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning("DryCycle DevUI placed-object restore failed: " + ex.Message);
                return false;
            }
        }

        private static string BuildFingerprint(int index, PlacedObjectRecord record)
        {
            return index.ToString(CultureInfo.InvariantCulture) + "|" + (record?.Fingerprint ?? string.Empty);
        }
    }

    private sealed class PlacedObjectsSnapshot : HistoryState
    {
        private readonly RoomSettings _settings;
        private readonly List<PlacedObjectRecord> _records;

        private PlacedObjectsSnapshot(RoomSettings settings, List<PlacedObjectRecord> records, string fingerprint)
            : base(fingerprint)
        {
            _settings = settings;
            _records = records;
        }

        protected override string Kind => "PlacedObjects";

        internal static PlacedObjectsSnapshot Capture(RoomSettings settings)
        {
            if (settings?.placedObjects == null)
            {
                return null;
            }

            List<PlacedObjectRecord> records = new(settings.placedObjects.Count);
            StringBuilder fingerprint = new();
            for (int i = 0; i < settings.placedObjects.Count; i++)
            {
                PlacedObjectRecord record = PlacedObjectRecord.Capture(settings.placedObjects[i]);
                if (record == null)
                {
                    continue;
                }
                records.Add(record);
                fingerprint.Append(i).Append(':').Append(record.Fingerprint).Append('\u001e');
            }

            return new PlacedObjectsSnapshot(settings, records, fingerprint.ToString());
        }

        internal override HistoryState CaptureCurrent(global::DevInterface.DevUI ui)
        {
            RoomSettings current = ui?.room?.roomSettings;
            return ReferenceEquals(current, _settings) ? Capture(current) : null;
        }

        internal override bool Restore(global::DevInterface.DevUI ui)
        {
            if (!RestoreCollection(ui?.room?.roomSettings))
            {
                return false;
            }

            ui.activePage?.Refresh();
            return true;
        }

        internal bool RestoreCollection(RoomSettings settings)
        {
            if (!ReferenceEquals(settings, _settings) || settings?.placedObjects == null)
            {
                return false;
            }

            try
            {
                for (int i = 0; i < _records.Count; i++)
                {
                    _records[i].Restore();
                }

                settings.placedObjects.Clear();
                for (int i = 0; i < _records.Count; i++)
                {
                    settings.placedObjects.Add(_records[i].Target);
                }
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning("DryCycle DevUI placed-object collection restore failed: " + ex.Message);
                return false;
            }
        }
    }

    private sealed class RoomSettingsSnapshot : HistoryState
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
            "filePath",
            // Restored separately so PlacedObject/Data instances keep stable identity.
            "placedObjects"
        };

        private readonly RoomSettings _target;
        private readonly string _serialized;
        private readonly PlacedObjectsSnapshot _placedObjects;

        private RoomSettingsSnapshot(
            RoomSettings target,
            string serialized,
            PlacedObjectsSnapshot placedObjects)
            : base(serialized + "\n@DryCyclePlacedObjects=" + (placedObjects?.Fingerprint ?? string.Empty))
        {
            _target = target;
            _serialized = serialized;
            _placedObjects = placedObjects;
        }

        protected override string Kind => "RoomSettings";

        internal static RoomSettingsSnapshot Capture(RoomSettings settings)
        {
            if (settings == null)
            {
                return null;
            }

            string serialized = Serialize(settings);
            if (serialized == null)
            {
                return null;
            }

            return new RoomSettingsSnapshot(settings, serialized, PlacedObjectsSnapshot.Capture(settings));
        }

        internal override HistoryState CaptureCurrent(global::DevInterface.DevUI ui)
        {
            RoomSettings current = ui?.room?.roomSettings;
            return ReferenceEquals(current, _target) ? Capture(current) : null;
        }

        internal override bool Restore(global::DevInterface.DevUI ui)
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
                if (_placedObjects != null && !_placedObjects.RestoreCollection(current))
                {
                    return false;
                }

                ui.activePage?.Refresh();
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning("DryCycle DevUI RoomSettings restore failed: " + ex.Message);
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

    private sealed class RelationshipSnapshot : HistoryState
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

        internal override HistoryState CaptureCurrent(global::DevInterface.DevUI ui)
        {
            return ui?.activePage is RelationshipPage ? Capture() : null;
        }

        internal override bool Restore(global::DevInterface.DevUI ui)
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

    private sealed class MapSnapshot : HistoryState
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

        internal override HistoryState CaptureCurrent(global::DevInterface.DevUI ui)
        {
            return ui?.activePage is MapPage page && ReferenceEquals(page.world, _world)
                ? Capture(page)
                : null;
        }

        internal override bool Restore(global::DevInterface.DevUI ui)
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

    private static string[] CloneStrings(string[] source)
    {
        return source == null ? null : (string[])source.Clone();
    }
}
