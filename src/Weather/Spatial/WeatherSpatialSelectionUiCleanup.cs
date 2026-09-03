using System;
using System.Globalization;
using System.Reflection;
using DevInterface;
using UnityEngine;

namespace DryCycle.Weather.Spatial;

/// <summary>
/// Final pass over the Weather Zones panel. Removes retired controls, fixes the
/// weather picker so it lives in the editor's coordinate space, and replaces the
/// preview +/- controls with a directly editable percentage field.
/// </summary>
internal static class WeatherSpatialSelectionUiCleanup
{
    private const string EditorNodeId = "DryCycle_WeatherSpatial";
    private const string MarkerId = "DryCycle_Weather_SelectionUi_Clean";
    private const string BrokenPickerId = "DryCycle_Weather_Target_Picker_Node";

    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        On.DevInterface.MapPage.Update += MapPage_Update;
        _enabled = true;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.DevInterface.MapPage.Update -= MapPage_Update;
        _enabled = false;
    }

    private static void MapPage_Update(
        On.DevInterface.MapPage.orig_Update orig,
        MapPage self)
    {
        orig(self);

        DevUINode editor = FindEditor(self);
        if (editor == null || HasMarker(editor))
        {
            return;
        }

        // Retired bulk-selection controls.
        Remove(editor, "SelectConnected");
        Remove(editor, "SelectOffscreen");
        Remove(editor, "StopGate");
        Remove(editor, "StopSubregion");
        Remove(editor, "ForceAllow");
        Remove(editor, "ForceDeny");
        Remove(editor, "ForceInherit");

        // Remove both the original arrow selector and the first picker implementation.
        // The latter inherited directly from DevUINode, so its Positioned children were
        // accidentally screen-relative instead of Weather-Zones-panel-relative.
        Remove(editor, "TargetPrev");
        Remove(editor, "Target");
        Remove(editor, "TargetNext");
        Remove(editor, BrokenPickerId);

        // Preview intensity is typed directly now; +/- and the passive percentage label
        // are no longer part of the UI.
        Remove(editor, "PreviewMinus");
        Remove(editor, "PreviewPlus");
        Remove(editor, "PreviewValue");

        Stretch(editor, "SelectSubregion", 8f, 284f);
        Stretch(editor, "SelectShelters", 8f, 140f);
        Stretch(editor, "SelectGates", 152f, 140f);
        Stretch(editor, "Preview", 8f, 140f);

        // Two deleted selection rows sat above history/validation controls.
        ShiftY(editor, "Undo", 50f);
        ShiftY(editor, "Redo", 50f);
        ShiftY(editor, "Validate", 50f);
        ShiftY(editor, "Save", 50f);
        ShiftY(editor, "Repair", 50f);
        ShiftY(editor, "Status", 50f);
        ShiftY(editor, "Path", 50f);
        for (int i = 0; i < 7; i++)
        {
            ShiftY(editor, "Issue" + i, 50f);
        }

        // These are deliberately appended last so their sprites and click handling sit
        // above the older editor controls that the popup temporarily covers.
        editor.subNodes.Add(new PreviewPercentInput(editor.owner, editor, editor));
        editor.subNodes.Add(new FixedTargetPicker(editor.owner, editor, editor));
        editor.subNodes.Add(new CleanupMarker(editor.owner, editor));
        editor.Refresh();
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

    private static bool HasMarker(DevUINode editor)
    {
        for (int i = 0; i < editor.subNodes.Count; i++)
        {
            if (editor.subNodes[i]?.IDstring == MarkerId)
            {
                return true;
            }
        }
        return false;
    }

    private static DevUINode Find(DevUINode editor, string id)
    {
        for (int i = 0; i < editor.subNodes.Count; i++)
        {
            DevUINode node = editor.subNodes[i];
            if (node != null && node.IDstring == id)
            {
                return node;
            }
        }
        return null;
    }

    private static void Remove(DevUINode editor, string id)
    {
        for (int i = editor.subNodes.Count - 1; i >= 0; i--)
        {
            DevUINode node = editor.subNodes[i];
            if (node == null || node.IDstring != id)
            {
                continue;
            }

            editor.subNodes.RemoveAt(i);
            node.ClearSprites();
        }
    }

    private static void ShiftY(DevUINode editor, string id, float deltaY)
    {
        if (Find(editor, id) is not PositionedDevUINode node)
        {
            return;
        }

        node.pos = new Vector2(node.pos.x, node.pos.y + deltaY);
        node.Refresh();
    }

    private static void Stretch(DevUINode editor, string id, float x, float width)
    {
        if (Find(editor, id) is not RectangularDevUINode node)
        {
            return;
        }

        node.pos = new Vector2(x, node.pos.y);
        node.size = new Vector2(width, node.size.y);
        if (node.fSprites.Count > 0)
        {
            node.fSprites[0].scaleX = width;
        }
        node.Refresh();
    }

    private sealed class FixedTargetPicker : PositionedDevUINode, IDevUISignals
    {
        private const string MainButtonId = "DryCycle_Weather_Target_Picker_Fixed";
        private const string ItemPrefix = "DryCycle_Weather_Target_Item_Fixed_";

        private readonly DevUINode _editor;
        private readonly FieldInfo _targetIndexField;
        private readonly MethodInfo _refreshPreviewTargetMethod;
        private readonly MethodInfo _updateStateLabelsMethod;
        private readonly Button _button;
        private PickerPopup _popup;

        internal FixedTargetPicker(
            DevInterface.DevUI owner,
            DevUINode parent,
            DevUINode editor)
            : base(owner, "DryCycle_Weather_Target_Picker_Fixed_Node", parent, Vector2.zero)
        {
            _editor = editor;
            Type editorType = editor.GetType();
            _targetIndexField = editorType.GetField(
                "_targetIndex",
                BindingFlags.Instance | BindingFlags.NonPublic);
            _refreshPreviewTargetMethod = editorType.GetMethod(
                "RefreshPreviewTarget",
                BindingFlags.Instance | BindingFlags.NonPublic);
            _updateStateLabelsMethod = editorType.GetMethod(
                "UpdateStateLabels",
                BindingFlags.Instance | BindingFlags.NonPublic);

            _button = new Button(
                owner,
                MainButtonId,
                this,
                new Vector2(8f, 536f),
                284f,
                string.Empty);
            subNodes.Add(_button);
            RefreshButtonText();
        }

        public override void Update()
        {
            base.Update();
            RefreshButtonText();

            if (_popup != null &&
                owner != null &&
                owner.mouseClick &&
                !_button.MouseOver &&
                !_popup.MouseOver)
            {
                ClosePopup();
            }
        }

        public override void ClearSprites()
        {
            ClosePopup();
            base.ClearSprites();
        }

        public void Signal(DevUISignalType type, DevUINode sender, string message)
        {
            if (type != DevUISignalType.ButtonClick || sender == null)
            {
                return;
            }

            if (sender.IDstring == MainButtonId)
            {
                if (_popup == null)
                {
                    OpenPopup();
                }
                else
                {
                    ClosePopup();
                }
                ConsumeLeftClick();
                return;
            }

            if (sender.IDstring.StartsWith(ItemPrefix, StringComparison.Ordinal) &&
                int.TryParse(sender.IDstring.Substring(ItemPrefix.Length), out int index))
            {
                SelectTarget(index);
                ConsumeLeftClick();
            }
        }

        private void OpenPopup()
        {
            if (_popup != null)
            {
                return;
            }

            int count = WeatherSpatialCatalog.AllTargets.Count;
            const float rowHeight = 20f;
            const float popupWidth = 300f;
            const float popupGap = 10f;
            float height = Mathf.Max(70f, 34f + count * rowHeight);
            float bottom = 532f - height;

            // Keep the picker outside the main Weather Zones panel. Its right edge
            // stops 10 px before the panel, so selecting weather never covers or
            // accidentally activates Overview/Brush/Preview/Save controls underneath.
            _popup = new PickerPopup(
                owner,
                this,
                new Vector2(-(popupWidth + popupGap), bottom),
                new Vector2(popupWidth, height),
                this);
            subNodes.Add(_popup);
            _popup.BuildItems(ItemPrefix, rowHeight);
            _popup.Refresh();
        }

        private void ClosePopup()
        {
            if (_popup == null)
            {
                return;
            }

            subNodes.Remove(_popup);
            _popup.ClearSprites();
            _popup = null;
        }

        private void SelectTarget(int index)
        {
            int count = WeatherSpatialCatalog.AllTargets.Count;
            if (count <= 0 || index < 0 || index >= count || _targetIndexField == null)
            {
                ClosePopup();
                return;
            }

            _targetIndexField.SetValue(_editor, index);
            _refreshPreviewTargetMethod?.Invoke(_editor, null);
            _updateStateLabelsMethod?.Invoke(_editor, null);
            ClosePopup();
            RefreshButtonText();
        }

        private int GetTargetIndex()
        {
            if (_targetIndexField == null || _editor == null)
            {
                return 0;
            }

            object value = _targetIndexField.GetValue(_editor);
            return value is int index ? index : 0;
        }

        private void RefreshButtonText()
        {
            int count = WeatherSpatialCatalog.AllTargets.Count;
            if (count <= 0)
            {
                _button.Text = "Select Weather";
                return;
            }

            int index = Mathf.Clamp(GetTargetIndex(), 0, count - 1);
            _button.Text = (_popup == null ? "▼  " : "▲  ") +
                           WeatherSpatialCatalog.AllTargets[index].DisplayName;
        }

        private void ConsumeLeftClick()
        {
            if (owner == null)
            {
                return;
            }

            owner.mouseClick = false;
        }

        private sealed class PickerPopup : Panel, IDevUISignals
        {
            private readonly FixedTargetPicker _picker;

            internal PickerPopup(
                DevInterface.DevUI owner,
                DevUINode parent,
                Vector2 pos,
                Vector2 size,
                FixedTargetPicker picker)
                : base(owner, "DryCycle_Weather_Target_Popup_Fixed", parent, pos, size, "Select Weather")
            {
                _picker = picker;
            }

            internal void BuildItems(string itemPrefix, float rowHeight)
            {
                int count = WeatherSpatialCatalog.AllTargets.Count;
                float y = size.y - 28f - rowHeight;
                for (int i = 0; i < count; i++)
                {
                    WeatherSpatialTarget target = WeatherSpatialCatalog.AllTargets[i];
                    Button item = new(
                        owner,
                        itemPrefix + i,
                        this,
                        new Vector2(8f, y),
                        size.x - 16f,
                        target.DisplayName);
                    subNodes.Add(item);
                    y -= rowHeight;
                }
            }

            public override void Update()
            {
                base.Update();
                if (owner != null && owner.mouseClick && MouseOver)
                {
                    // Prevent a popup click from falling through to the map below it.
                    owner.mouseClick = false;
                }
            }

            public void Signal(DevUISignalType type, DevUINode sender, string message)
            {
                _picker?.Signal(type, sender, message);
            }
        }
    }

    private sealed class PreviewPercentInput : Button
    {
        private readonly DevUINode _editor;
        private readonly FieldInfo _previewIntensityField;
        private readonly FieldInfo _statusField;
        private readonly MethodInfo _refreshPreviewTargetMethod;
        private readonly MethodInfo _updateStateLabelsMethod;

        private bool _editing;
        private string _buffer;
        private float _lastValidIntensity;

        internal PreviewPercentInput(
            DevInterface.DevUI owner,
            DevUINode parent,
            DevUINode editor)
            : base(
                owner,
                "DryCycle_Weather_Preview_Percent_Input",
                parent,
                new Vector2(226f, 470f),
                66f,
                "75%")
        {
            _editor = editor;
            Type editorType = editor.GetType();
            _previewIntensityField = editorType.GetField(
                "_previewIntensity",
                BindingFlags.Instance | BindingFlags.NonPublic);
            _statusField = editorType.GetField(
                "_status",
                BindingFlags.Instance | BindingFlags.NonPublic);
            _refreshPreviewTargetMethod = editorType.GetMethod(
                "RefreshPreviewTarget",
                BindingFlags.Instance | BindingFlags.NonPublic);
            _updateStateLabelsMethod = editorType.GetMethod(
                "UpdateStateLabels",
                BindingFlags.Instance | BindingFlags.NonPublic);

            _lastValidIntensity = ReadIntensity();
            _buffer = PercentText(_lastValidIntensity);
            Text = _buffer + "%";

            DevUILabel label = new(
                owner,
                "DryCycle_Weather_Preview_Percent_Label",
                parent,
                new Vector2(152f, 470f),
                70f,
                "Intensity %");
            label.spriteColor = new Color(0f, 0f, 0f);
            label.textColor = new Color(1f, 1f, 1f);
            parent.subNodes.Add(label);
        }

        public override void Clicked()
        {
            _editing = true;
            _lastValidIntensity = ReadIntensity();
            _buffer = PercentText(_lastValidIntensity);
            Text = _buffer + "_";
            if (owner != null)
            {
                owner.mouseClick = false;
            }
        }

        public override void Update()
        {
            base.Update();

            if (!_editing)
            {
                float current = ReadIntensity();
                if (Mathf.Abs(current - _lastValidIntensity) > 0.0001f)
                {
                    _lastValidIntensity = current;
                }
                Text = PercentText(_lastValidIntensity) + "%";
                return;
            }

            bool commit = false;
            bool cancel = false;
            string typed = Input.inputString ?? string.Empty;
            for (int i = 0; i < typed.Length; i++)
            {
                char c = typed[i];
                if (c >= '0' && c <= '9')
                {
                    if (_buffer.Length < 3)
                    {
                        _buffer += c;
                    }
                }
                else if (c == '\b')
                {
                    if (_buffer.Length > 0)
                    {
                        _buffer = _buffer.Substring(0, _buffer.Length - 1);
                    }
                }
                else if (c == '\n' || c == '\r')
                {
                    commit = true;
                }
                else if (!char.IsControl(c))
                {
                    SetStatus("Preview intensity accepts digits only (1-100).");
                }
            }

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                cancel = true;
            }
            if (owner != null && owner.mouseClick && !MouseOver)
            {
                commit = true;
            }

            if (cancel)
            {
                _editing = false;
                _buffer = PercentText(_lastValidIntensity);
                Text = _buffer + "%";
                return;
            }

            if (commit)
            {
                CommitBuffer();
                return;
            }

            Text = (_buffer.Length == 0 ? "_" : _buffer + "_");
        }

        private void CommitBuffer()
        {
            if (!int.TryParse(_buffer, NumberStyles.None, CultureInfo.InvariantCulture, out int percent))
            {
                SetStatus("Preview intensity is empty or invalid; enter 1-100.");
                Text = (_buffer.Length == 0 ? "_" : _buffer + "_");
                return;
            }

            if (percent < 1 || percent > 100)
            {
                SetStatus("Preview intensity must be between 1 and 100.");
                Text = _buffer + "_";
                return;
            }

            float value = percent / 100f;
            _previewIntensityField?.SetValue(_editor, value);
            _lastValidIntensity = value;
            _editing = false;
            _buffer = percent.ToString(CultureInfo.InvariantCulture);
            SetStatus("Preview intensity: " + percent + "%");
            _refreshPreviewTargetMethod?.Invoke(_editor, null);
            _updateStateLabelsMethod?.Invoke(_editor, null);
            Text = _buffer + "%";
        }

        private float ReadIntensity()
        {
            if (_previewIntensityField?.GetValue(_editor) is float value)
            {
                return Mathf.Clamp01(value);
            }
            return 0.75f;
        }

        private void SetStatus(string text)
        {
            _statusField?.SetValue(_editor, text);
            _updateStateLabelsMethod?.Invoke(_editor, null);
        }

        private static string PercentText(float intensity)
        {
            return Mathf.RoundToInt(Mathf.Clamp01(intensity) * 100f)
                .ToString(CultureInfo.InvariantCulture);
        }
    }

    private sealed class CleanupMarker : DevUINode
    {
        internal CleanupMarker(DevInterface.DevUI owner, DevUINode parent)
            : base(owner, MarkerId, parent)
        {
        }
    }
}
