using System;
using System.Globalization;
using System.Reflection;
using DevInterface;
using UnityEngine;

namespace DryCycle.Weather.Spatial;

internal static partial class WeatherSpatialSelectionUiCleanup
{
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

}
