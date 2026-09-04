using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using DevInterface;
using UnityEngine;

namespace DryCycle.Weather.Spatial;

/// <summary>
/// Binary Weather Zones authoring.
/// Shift+LMB marquee builds a persistent room selection. RMB on any selected room
/// toggles the complete selection; RMB elsewhere clears that selection and starts a
/// normal per-room toggle stroke. Ordinary LMB outside the selected set clears it.
/// </summary>
internal static class WeatherSpatialTogglePaintRuntime
{
    private const string EditorNodeId = "DryCycle_WeatherSpatial";

    private static bool _enabled;
    private static bool _rightWasDown;
    private static bool _leftWasDown;
    private static bool _batchToggledThisStroke;
    private static int _rightUndoStart = -1;
    private static readonly HashSet<string> _rightTouched = new(StringComparer.OrdinalIgnoreCase);
    private static DevUINode _lastEditor;

    private static Type _editorType;
    private static FieldInfo _overviewField;
    private static FieldInfo _regionIdField;
    private static FieldInfo _targetIndexField;
    private static FieldInfo _selectionField;
    private static FieldInfo _undoField;
    private static MethodInfo _applyRuleToSelectionMethod;
    private static MethodInfo _runValidationMethod;
    private static MethodInfo _updateStateLabelsMethod;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        On.DevInterface.MapPage.Update += MapPage_Update;
        On.DevInterface.Button.Clicked += Button_Clicked;
        _enabled = true;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.DevInterface.Button.Clicked -= Button_Clicked;
        On.DevInterface.MapPage.Update -= MapPage_Update;
        _rightWasDown = false;
        _leftWasDown = false;
        _batchToggledThisStroke = false;
        _rightUndoStart = -1;
        _rightTouched.Clear();
        _lastEditor = null;
        ClearReflectionCache();
        _enabled = false;
    }

    private static void MapPage_Update(
        On.DevInterface.MapPage.orig_Update orig,
        MapPage self)
    {
        DevUINode editor = FindEditor(self);
        bool rightDown = Input.GetMouseButton(1);
        bool leftDown = Input.GetMouseButton(0);

        if (editor != null)
        {
            EnsureReflection(editor);
            _lastEditor = editor;

            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool overview = _overviewField?.GetValue(editor) is bool overviewValue && overviewValue;
            bool collapsed = editor is Panel panel && panel.collapsed;

            if (!overview && !collapsed)
            {
                if (leftDown && !_leftWasDown && !shift)
                {
                    ClearSelectionWhenClickingElsewhere(self, editor);
                }

                if (rightDown && !_rightWasDown)
                {
                    BeginRightStroke(self, editor);
                }
                else if (rightDown && !_batchToggledThisStroke)
                {
                    ToggleHoveredOnce(self, editor);
                }
            }
        }

        orig(self);

        if (_rightWasDown && !rightDown && _lastEditor != null)
        {
            EndRightStroke(_lastEditor);
        }

        if (editor != null)
        {
            RewriteShortcutLegend(editor);
        }

        _rightWasDown = rightDown;
        _leftWasDown = leftDown;
        if (!rightDown && editor == null)
        {
            _lastEditor = null;
        }
    }

    private static void Button_Clicked(
        On.DevInterface.Button.orig_Clicked orig,
        Button self)
    {
        if (self != null &&
            self.IDstring == "ApplySelected" &&
            TryFindEditor(self, out DevUINode editor))
        {
            ToggleSelection(editor);
            return;
        }

        orig(self);
    }

    private static void BeginRightStroke(MapPage mapPage, DevUINode editor)
    {
        EnsureReflection(editor);
        _rightTouched.Clear();
        _batchToggledThisStroke = false;
        _rightUndoStart = _undoField?.GetValue(editor) is IList undo ? undo.Count : -1;

        RoomPanel hovered = HoveredRoom(mapPage);
        string roomName = hovered?.roomRep?.room?.name;

        if (_selectionField?.GetValue(editor) is HashSet<string> selection && selection.Count > 0)
        {
            // Standard selection semantics: RMB on any selected room acts on the whole
            // selected set. No modifier is required after the Shift+LMB marquee.
            if (!string.IsNullOrWhiteSpace(roomName) && selection.Contains(roomName))
            {
                ToggleSelection(editor);
                _batchToggledThisStroke = true;
                return;
            }

            // RMB somewhere outside the selection starts a new single-room operation,
            // so the old marquee selection is no longer relevant.
            selection.Clear();
            _updateStateLabelsMethod?.Invoke(editor, null);
        }

        ToggleHoveredOnce(mapPage, editor);
    }

    private static void EndRightStroke(DevUINode editor)
    {
        EnsureReflection(editor);
        if (_rightUndoStart >= 0 && _undoField?.GetValue(editor) is IList undo)
        {
            MergeNewUndoCommands(undo, _rightUndoStart, "Toggle Weather Zone");
        }

        _runValidationMethod?.Invoke(editor, null);
        _updateStateLabelsMethod?.Invoke(editor, null);
        _rightTouched.Clear();
        _batchToggledThisStroke = false;
        _rightUndoStart = -1;
    }

    private static void ToggleHoveredOnce(MapPage mapPage, DevUINode editor)
    {
        RoomPanel hovered = HoveredRoom(mapPage);
        string roomName = hovered?.roomRep?.room?.name;
        if (string.IsNullOrWhiteSpace(roomName) || !_rightTouched.Add(roomName))
        {
            return;
        }

        ToggleSingleRoom(editor, roomName);
    }

    private static void ToggleSingleRoom(DevUINode editor, string roomName)
    {
        EnsureReflection(editor);
        if (string.IsNullOrWhiteSpace(roomName) ||
            _selectionField?.GetValue(editor) is not HashSet<string> selection ||
            _applyRuleToSelectionMethod == null)
        {
            return;
        }

        if (!TryGetCurrentTarget(editor, out string regionId, out WeatherSpatialTarget target))
        {
            return;
        }

        bool currentlyAllowed = target.IsFamily
            ? WeatherSpatialRegistry.IsFamilyAllowed(regionId, roomName, target.FamilyId)
            : WeatherSpatialRegistry.IsAllowed(regionId, roomName, target.Kind, target.WeatherId);

        List<string> original = new(selection);
        selection.Clear();
        selection.Add(roomName);
        _applyRuleToSelectionMethod.Invoke(
            editor,
            new object[]
            {
                currentlyAllowed ? WeatherSpatialRule.Deny : WeatherSpatialRule.Allow,
                "Toggle " + roomName
            });

        selection.Clear();
        for (int i = 0; i < original.Count; i++)
        {
            selection.Add(original[i]);
        }
        _updateStateLabelsMethod?.Invoke(editor, null);
    }

    private static void ClearSelectionWhenClickingElsewhere(MapPage mapPage, DevUINode editor)
    {
        if (_selectionField?.GetValue(editor) is not HashSet<string> selection || selection.Count == 0)
        {
            return;
        }

        // Interacting with the right-side editor itself must not destroy the selection;
        // buttons such as Toggle Sel are expected to keep using it.
        if (editor is RectangularDevUINode editorRect && editorRect.MouseOver)
        {
            return;
        }

        RoomPanel hovered = HoveredRoom(mapPage);
        string roomName = hovered?.roomRep?.room?.name;
        if (!string.IsNullOrWhiteSpace(roomName) && selection.Contains(roomName))
        {
            return;
        }

        selection.Clear();
        _updateStateLabelsMethod?.Invoke(editor, null);
    }

    private static void ToggleSelection(DevUINode editor)
    {
        EnsureReflection(editor);
        if (_selectionField?.GetValue(editor) is not HashSet<string> selection ||
            _applyRuleToSelectionMethod == null ||
            _undoField?.GetValue(editor) is not IList undo ||
            selection.Count == 0)
        {
            return;
        }

        if (!TryGetCurrentTarget(editor, out string regionId, out WeatherSpatialTarget target))
        {
            return;
        }

        List<string> original = new(selection);
        List<string> toAllow = new();
        List<string> toDeny = new();
        for (int i = 0; i < original.Count; i++)
        {
            string roomName = original[i];
            bool allowed = target.IsFamily
                ? WeatherSpatialRegistry.IsFamilyAllowed(regionId, roomName, target.FamilyId)
                : WeatherSpatialRegistry.IsAllowed(regionId, roomName, target.Kind, target.WeatherId);
            (allowed ? toDeny : toAllow).Add(roomName);
        }

        int undoBefore = undo.Count;
        ApplySubset(editor, selection, toAllow, WeatherSpatialRule.Allow);
        ApplySubset(editor, selection, toDeny, WeatherSpatialRule.Deny);

        selection.Clear();
        for (int i = 0; i < original.Count; i++)
        {
            selection.Add(original[i]);
        }

        MergeNewUndoCommands(undo, undoBefore, "Toggle selected rooms");
        _runValidationMethod?.Invoke(editor, null);
        _updateStateLabelsMethod?.Invoke(editor, null);
    }

    private static void ApplySubset(
        DevUINode editor,
        HashSet<string> selection,
        List<string> rooms,
        WeatherSpatialRule rule)
    {
        if (rooms == null || rooms.Count == 0)
        {
            return;
        }

        selection.Clear();
        for (int i = 0; i < rooms.Count; i++)
        {
            selection.Add(rooms[i]);
        }

        _applyRuleToSelectionMethod?.Invoke(
            editor,
            new object[] { rule, "Toggle selected rooms" });
    }

    private static void MergeNewUndoCommands(IList undo, int firstNewIndex, string label)
    {
        if (undo == null || firstNewIndex < 0 || undo.Count <= firstNewIndex)
        {
            return;
        }

        object first = undo[firstNewIndex];
        if (first == null)
        {
            return;
        }

        Type commandType = first.GetType();
        FieldInfo changesField = commandType.GetField(
            "Changes",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        FieldInfo labelField = commandType.GetField(
            "Label",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        if (changesField?.GetValue(first) is not IList firstChanges)
        {
            return;
        }

        for (int index = undo.Count - 1; index > firstNewIndex; index--)
        {
            object extra = undo[index];
            if (extra != null &&
                extra.GetType() == commandType &&
                changesField.GetValue(extra) is IList extraChanges)
            {
                for (int i = 0; i < extraChanges.Count; i++)
                {
                    firstChanges.Add(extraChanges[i]);
                }
                undo.RemoveAt(index);
            }
        }

        labelField?.SetValue(first, label);
    }

    private static bool TryGetCurrentTarget(
        DevUINode editor,
        out string regionId,
        out WeatherSpatialTarget target)
    {
        regionId = _regionIdField?.GetValue(editor) as string ?? string.Empty;
        target = default;
        int count = WeatherSpatialCatalog.AllTargets.Count;
        if (count <= 0 || _targetIndexField == null)
        {
            return false;
        }

        int index = _targetIndexField.GetValue(editor) is int targetIndex
            ? Mathf.Clamp(targetIndex, 0, count - 1)
            : 0;
        target = WeatherSpatialCatalog.AllTargets[index];
        return true;
    }

    private static RoomPanel HoveredRoom(MapPage mapPage)
    {
        if (mapPage?.subNodes == null)
        {
            return null;
        }

        for (int i = mapPage.subNodes.Count - 1; i >= 0; i--)
        {
            if (mapPage.subNodes[i] is RoomPanel panel &&
                panel.Visible &&
                panel.miniMap != null &&
                panel.miniMap.MouseOver)
            {
                return panel;
            }
        }
        return null;
    }

    private static void RewriteShortcutLegend(DevUINode editor)
    {
        if (FindDirect(editor, "WeatherShortcutPaint") is DevUILabel paint)
        {
            paint.Text = "RMB Drag  - Toggle Weather Zone";
        }
        if (FindDirect(editor, "WeatherShortcutBox") is DevUILabel box)
        {
            box.Text = "Shift + LMB Drag  - Box Select Rooms";
        }
        if (FindDirect(editor, "WeatherShortcutSelect") is DevUILabel select)
        {
            select.Text = "RMB Selected  - Toggle Selected Rooms";
        }
    }

    private static bool TryFindEditor(Button button, out DevUINode editor)
    {
        editor = null;
        DevUINode node = button;
        while (node != null)
        {
            if (node.IDstring == EditorNodeId)
            {
                editor = node;
                return true;
            }
            node = node.parentNode;
        }
        return false;
    }

    private static DevUINode FindEditor(MapPage mapPage)
    {
        if (mapPage?.subNodes == null)
        {
            return null;
        }

        for (int i = 0; i < mapPage.subNodes.Count; i++)
        {
            DevUINode node = mapPage.subNodes[i];
            if (node != null && node.IDstring == EditorNodeId)
            {
                return node;
            }
        }
        return null;
    }

    private static DevUINode FindDirect(DevUINode parent, string id)
    {
        if (parent?.subNodes == null)
        {
            return null;
        }

        for (int i = 0; i < parent.subNodes.Count; i++)
        {
            DevUINode node = parent.subNodes[i];
            if (node != null && node.IDstring == id)
            {
                return node;
            }
        }
        return null;
    }

    private static void EnsureReflection(DevUINode editor)
    {
        Type type = editor?.GetType();
        if (type == null || type == _editorType)
        {
            return;
        }

        _editorType = type;
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        _overviewField = type.GetField("_overview", flags);
        _regionIdField = type.GetField("_regionId", flags);
        _targetIndexField = type.GetField("_targetIndex", flags);
        _selectionField = type.GetField("_selection", flags);
        _undoField = type.GetField("_undo", flags);
        _applyRuleToSelectionMethod = type.GetMethod("ApplyRuleToSelection", flags);
        _runValidationMethod = type.GetMethod("RunValidation", flags);
        _updateStateLabelsMethod = type.GetMethod("UpdateStateLabels", flags);
    }

    private static void ClearReflectionCache()
    {
        _editorType = null;
        _overviewField = null;
        _regionIdField = null;
        _targetIndexField = null;
        _selectionField = null;
        _undoField = null;
        _applyRuleToSelectionMethod = null;
        _runValidationMethod = null;
        _updateStateLabelsMethod = null;
    }
}
