using System;
using System.Collections;
using System.Reflection;
using DevInterface;
using UnityEngine;

namespace DryCycle.Weather.Spatial;

internal static partial class WeatherSpatialSelectionUiCleanup
{
    private sealed class ClearRegionZonesControl : PositionedDevUINode
    {
        private const string ButtonId = "DryCycle_Weather_Clear_Region_Zones";
        private const string ConfirmationPanelId = "DryCycle_Weather_Clear_Region_Confirm";
        private const float ButtonX = 152f;
        private const float ButtonWidth = 140f;
        private const float PopupWidth = 300f;
        private const float PopupHeight = 112f;
        private const float PopupGap = 10f;

        private readonly DevUINode _editor;
        private readonly MapPage _mapPage;
        private readonly FieldInfo _regionIdField;
        private readonly FieldInfo _statusField;
        private readonly FieldInfo _undoField;
        private readonly FieldInfo _redoField;
        private readonly MethodInfo _endPaintMethod;
        private readonly MethodInfo _runValidationMethod;
        private readonly MethodInfo _updateStateLabelsMethod;
        private readonly Button _button;

        private Panel _confirmationPanel;
        private Button _yesButton;
        private Button _noButton;

        internal ClearRegionZonesControl(
            DevInterface.DevUI owner,
            DevUINode parent,
            DevUINode editor)
            : base(owner, "DryCycle_Weather_Clear_Region_Control", parent, Vector2.zero)
        {
            _editor = editor;
            _mapPage = editor?.parentNode as MapPage;

            Type editorType = editor?.GetType();
            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            _regionIdField = editorType?.GetField("_regionId", flags);
            _statusField = editorType?.GetField("_status", flags);
            _undoField = editorType?.GetField("_undo", flags);
            _redoField = editorType?.GetField("_redo", flags);
            _endPaintMethod = editorType?.GetMethod("EndPaintIfNeeded", flags);
            _runValidationMethod = editorType?.GetMethod("RunValidation", flags);
            _updateStateLabelsMethod = editorType?.GetMethod("UpdateStateLabels", flags);

            float rowY = Find(editor, "Selection") is PositionedDevUINode selection
                ? selection.pos.y
                : 445f;

            _button = new ClearActionButton(
                owner,
                ButtonId,
                this,
                new Vector2(ButtonX, rowY),
                ButtonWidth,
                "Clear Region Zones",
                ToggleConfirmation);
            subNodes.Add(_button);
        }

        public override void Update()
        {
            base.Update();

            if (_confirmationPanel == null || owner == null || !owner.mouseClick)
            {
                return;
            }

            if (_button.MouseOver ||
                _confirmationPanel.MouseOver ||
                (_yesButton != null && _yesButton.MouseOver) ||
                (_noButton != null && _noButton.MouseOver))
            {
                return;
            }

            CloseConfirmation();
        }

        public override void ClearSprites()
        {
            CloseConfirmation();
            base.ClearSprites();
        }

        private void ToggleConfirmation()
        {
            if (_confirmationPanel == null)
            {
                OpenConfirmation();
            }
            else
            {
                CloseConfirmation();
            }
        }

        private void OpenConfirmation()
        {
            if (_confirmationPanel != null || _mapPage == null || _editor == null)
            {
                return;
            }

            string regionId = RegionId;
            float rowY = _button.pos.y;
            Vector2 popupPos = _editor.pos + new Vector2(
                -(PopupWidth + PopupGap),
                rowY - 36f);

            _confirmationPanel = new Panel(
                owner,
                ConfirmationPanelId,
                _mapPage,
                popupPos,
                new Vector2(PopupWidth, PopupHeight),
                "Delete Region Weather Zones?");

            DevUILabel question = new(
                owner,
                "DryCycle_Weather_Clear_Region_Question",
                _confirmationPanel,
                new Vector2(8f, 60f),
                PopupWidth - 16f,
                "Delete all Weather Zones in region " + regionId + "?");
            question.spriteColor = new Color(0f, 0f, 0f);
            question.textColor = new Color(1f, 1f, 1f);
            _confirmationPanel.subNodes.Add(question);

            DevUILabel note = new(
                owner,
                "DryCycle_Weather_Clear_Region_Note",
                _confirmationPanel,
                new Vector2(8f, 40f),
                PopupWidth - 16f,
                "Schedule settings will be kept.");
            note.spriteColor = new Color(0f, 0f, 0f);
            note.textColor = new Color(1f, 1f, 1f);
            _confirmationPanel.subNodes.Add(note);

            _yesButton = new ClearActionButton(
                owner,
                "DryCycle_Weather_Clear_Region_Yes",
                _mapPage,
                popupPos + new Vector2(8f, 12f),
                136f,
                "YES",
                ConfirmDelete);

            _noButton = new ClearActionButton(
                owner,
                "DryCycle_Weather_Clear_Region_No",
                _mapPage,
                popupPos + new Vector2(156f, 12f),
                136f,
                "NO",
                CloseConfirmation);

            // YES/NO are direct MapPage buttons on purpose. WeatherSpatialMapMenuRuntime
            // already treats top-level buttons as interactive UI, so clicking either one
            // cannot leak through into map panning/painting even though the dialog is
            // rendered to the left of the Weather Zones panel.
            _mapPage.subNodes.Add(_confirmationPanel);
            _mapPage.subNodes.Add(_yesButton);
            _mapPage.subNodes.Add(_noButton);

            _confirmationPanel.Refresh();
            _yesButton.Refresh();
            _noButton.Refresh();
        }

        private void ConfirmDelete()
        {
            _endPaintMethod?.Invoke(_editor, null);

            string regionId = RegionId;
            bool cleared = WeatherSpatialRegistry.ClearRegionSpatialRules(regionId);
            if (cleared)
            {
                ClearHistory();
            }

            // Refresh the issue list immediately so stale warnings from the deleted
            // spatial rules disappear before the developer presses Validate again.
            _runValidationMethod?.Invoke(_editor, null);

            if (_statusField != null)
            {
                _statusField.SetValue(
                    _editor,
                    cleared
                        ? "Cleared Weather Zones for " + regionId + ". Save WeatherSpatial to persist."
                        : "No Weather Zones configured for " + regionId + ".");
            }

            _updateStateLabelsMethod?.Invoke(_editor, null);
            CloseConfirmation();
            _editor?.Refresh();
        }

        private void ClearHistory()
        {
            if (_undoField?.GetValue(_editor) is IList undo)
            {
                undo.Clear();
            }
            if (_redoField?.GetValue(_editor) is IList redo)
            {
                redo.Clear();
            }
        }

        private string RegionId =>
            (_regionIdField?.GetValue(_editor) as string ?? string.Empty).Trim().ToUpperInvariant();

        private void CloseConfirmation()
        {
            RemoveTopLevel(_yesButton);
            RemoveTopLevel(_noButton);
            RemoveTopLevel(_confirmationPanel);
            _yesButton = null;
            _noButton = null;
            _confirmationPanel = null;
        }

        private void RemoveTopLevel(DevUINode node)
        {
            if (node == null)
            {
                return;
            }

            if (_mapPage?.subNodes != null)
            {
                _mapPage.subNodes.Remove(node);
            }
            node.ClearSprites();
        }

        private sealed class ClearActionButton : Button
        {
            private readonly Action _action;

            internal ClearActionButton(
                DevInterface.DevUI owner,
                string id,
                DevUINode parent,
                Vector2 pos,
                float width,
                string text,
                Action action)
                : base(owner, id, parent, pos, width, text)
            {
                _action = action;
            }

            public override void Clicked()
            {
                _action?.Invoke();
            }
        }
    }
}
