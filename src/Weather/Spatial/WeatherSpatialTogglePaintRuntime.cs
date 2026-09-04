using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using DevInterface;
using UnityEngine;

namespace DryCycle.Weather.Spatial;

/// <summary>
/// Binary Weather Zones authoring: RMB toggles the effective state of every room
/// touched by the current stroke. Shift+RMB remains room selection.
/// </summary>
internal static class WeatherSpatialTogglePaintRuntime
{
    private const string EditorNodeId = "DryCycle_WeatherSpatial";

    private static bool _enabled;
    private static bool _rightWasDown;
    private static DevUINode _lastEditor;

    private static Type _editorType;
    private static FieldInfo _brushField;
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

        if (editor != null)
        {
            EnsureReflection(editor);
            _lastEditor = editor;

            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool overview = _overviewField?.GetValue(editor) is bool overviewValue && overviewValue;
            bool collapsed = editor is Panel panel && panel.collapsed;

            if (rightDown && !shift && !overview && !collapsed)
            {
                RoomPanel hovered = HoveredRoom(self);
                if (hovered?.roomRep?.room != null)
                {
                    SetBrushForToggle(editor, hovered.roomRep.room.name);
                }
            }
        }

        orig(self);

        if (_rightWasDown && !rightDown && _lastEditor != null)
        {
            EnsureReflection(_lastEditor);
            _runValidationMethod?.Invoke(_lastEditor, null);
            _updateStateLabelsMethod?.Invoke(_lastEditor, null);
        }

        if (editor != null)
        {
            RewriteShortcutLegend(editor);
        }

        _rightWasDown = rightDown;
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

    private static void SetBrushForToggle(DevUINode editor, string roomName)
    {
        if (_brushField == null ||
            _regionIdField == null ||
            _targetIndexField == null ||
            string.IsNullOrWhiteSpace(roomName))
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

        _brushField.SetValue(
            editor,
            currentlyAllowed ? WeatherSpatialRule.Deny : WeatherSpatialRule.Allow);
    }

    private static void ToggleSelection(DevUINode editor)
    {
        EnsureReflection(editor);
        if (_selectionField?.GetValue(editor) is not HashSet<string> selection ||
            _applyRuleToSelectionMethod == null ||
            _undoField?.GetValue(editor) is not IList undo)
        {
            return;
        }

        if (selection.Count == 0)
        {
            _applyRuleToSelectionMethod.Invoke(
                editor,
                new object[] { WeatherSpatialRule.Allow, "Toggle selected rooms" });
            _updateStateLabelsMethod?.Invoke(editor, null);
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
        if (FindDirect(editor, "WeatherShortcutSelect") is DevUILabel select)
        {
            select.Text = "Shift + RMB  - Toggle Room Select";
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
        _brushField = type.GetField("_brush", flags);
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
        _brushField = null;
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
