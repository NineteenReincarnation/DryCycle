using System;
using System.Reflection;
using DevInterface;
using DryCycle.Weather.Scheduling;
using UnityEngine;

namespace DryCycle.Weather.Spatial;

internal static partial class WeatherSpatialSelectionUiCleanup
{
    private sealed class FixedTargetPicker : PositionedDevUINode, IDevUISignals
    {
        private const string MainButtonId = "DryCycle_Weather_Target_Picker_Fixed";
        private const string ItemPrefix = "DryCycle_Weather_Target_Item_Fixed_";
        private const float RowHeight = 20f;
        private const float GroupGap = 7f;
        private const float PopupWidth = 300f;
        private const float PopupGap = 10f;

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

            RestoreRememberedTarget();

            _button = new Button(
                owner,
                MainButtonId,
                this,
                new Vector2(8f, 536f),
                284f,
                string.Empty);
            subNodes.Add(_button);
            RefreshButtonText();
            RememberCurrentTarget();
        }

        public override void Update()
        {
            base.Update();
            RefreshButtonText();
            RememberCurrentTarget();

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

            float contentHeight = VisibleContentHeight();
            float height = Mathf.Max(70f, 36f + contentHeight);
            float bottom = 532f - height;

            // Keep the picker outside the main Weather Zones panel. Its right edge
            // stops 10 px before the panel, so selecting weather never covers or
            // accidentally activates Overview/Brush/Preview/Save controls underneath.
            _popup = new PickerPopup(
                owner,
                this,
                new Vector2(-(PopupWidth + PopupGap), bottom),
                new Vector2(PopupWidth, height),
                this);
            subNodes.Add(_popup);
            _popup.BuildItems(ItemPrefix);
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

        private void ToggleFamily(string familyId)
        {
            WeatherSpatialPickerState.ToggleCollapsed(familyId);

            // Recreate the popup so its panel height and child layout immediately
            // reflect the new fold state. Fold state itself lives outside DevUI and
            // therefore survives closing/reopening H mode.
            ClosePopup();
            OpenPopup();
            ConsumeLeftClick();
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
            WeatherSpatialPickerState.RememberTarget(WeatherSpatialCatalog.AllTargets[index]);
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

        private void RestoreRememberedTarget()
        {
            if (_targetIndexField == null || _editor == null)
            {
                return;
            }

            int index = WeatherSpatialPickerState.FindRememberedTargetIndex();
            if (index >= 0 && index < WeatherSpatialCatalog.AllTargets.Count)
            {
                _targetIndexField.SetValue(_editor, index);
            }
        }

        private void RememberCurrentTarget()
        {
            int count = WeatherSpatialCatalog.AllTargets.Count;
            if (count <= 0)
            {
                return;
            }

            int index = Mathf.Clamp(GetTargetIndex(), 0, count - 1);
            WeatherSpatialPickerState.RememberTarget(WeatherSpatialCatalog.AllTargets[index]);
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
            WeatherSpatialTarget target = WeatherSpatialCatalog.AllTargets[index];
            _button.Text = (_popup == null ? "▼  " : "▲  ") + TargetText(target);
        }

        private static string TargetText(in WeatherSpatialTarget target)
        {
            if (target.IsFamily)
            {
                return "[Family] " + target.FamilyId;
            }

            return target.Kind == WeatherScheduleEventKind.DangerType
                ? "[Danger] " + target.WeatherId
                : target.WeatherId;
        }

        private static int FindTargetIndex(string targetKey)
        {
            if (string.IsNullOrEmpty(targetKey))
            {
                return -1;
            }

            for (int i = 0; i < WeatherSpatialCatalog.AllTargets.Count; i++)
            {
                if (string.Equals(
                        WeatherSpatialCatalog.AllTargets[i].Key,
                        targetKey,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            return -1;
        }

        private static float VisibleContentHeight()
        {
            int rows = 0;
            int families = WeatherSpatialCatalog.AllFamilies.Count;
            for (int i = 0; i < families; i++)
            {
                WeatherSpatialFamily family = WeatherSpatialCatalog.AllFamilies[i];
                rows++; // Family header always remains visible.
                if (!WeatherSpatialPickerState.IsCollapsed(family.Id))
                {
                    rows += family.Members.Count;
                }
            }

            return rows * RowHeight + Mathf.Max(0, families - 1) * GroupGap;
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

            internal void BuildItems(string itemPrefix)
            {
                float y = size.y - 28f - RowHeight;
                for (int familyIndex = 0;
                     familyIndex < WeatherSpatialCatalog.AllFamilies.Count;
                     familyIndex++)
                {
                    if (familyIndex > 0)
                    {
                        y -= GroupGap;
                    }

                    WeatherSpatialFamily family = WeatherSpatialCatalog.AllFamilies[familyIndex];
                    int familyTargetIndex = FindTargetIndex("Family/" + family.Id);
                    bool collapsed = WeatherSpatialPickerState.IsCollapsed(family.Id);

                    if (familyTargetIndex >= 0)
                    {
                        FamilyRowButton familyButton = new(
                            owner,
                            "DryCycle_Weather_Target_Family_" + familyIndex,
                            this,
                            new Vector2(8f, y),
                            size.x - 16f,
                            (collapsed ? "▶  " : "▼  ") + "[Family] " + family.Id,
                            _picker,
                            familyTargetIndex,
                            family.Id);
                        subNodes.Add(familyButton);
                    }
                    y -= RowHeight;

                    if (collapsed)
                    {
                        continue;
                    }

                    for (int memberIndex = 0; memberIndex < family.Members.Count; memberIndex++)
                    {
                        WeatherSpatialMember member = family.Members[memberIndex];
                        int targetIndex = FindTargetIndex(member.Key);
                        if (targetIndex < 0)
                        {
                            continue;
                        }

                        bool danger = member.Kind == WeatherScheduleEventKind.DangerType;
                        string text = danger
                            ? "[Danger] " + member.Id
                            : member.Id;

                        PickerItemButton item = new(
                            owner,
                            itemPrefix + targetIndex,
                            this,
                            new Vector2(30f, y),
                            size.x - 38f,
                            text,
                            _picker,
                            targetIndex,
                            isFamily: false,
                            isDanger: danger);
                        subNodes.Add(item);
                        y -= RowHeight;
                    }
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

        private class PickerItemButton : Button
        {
            private readonly FixedTargetPicker _picker;
            private readonly int _targetIndex;
            private readonly bool _isFamily;
            private readonly bool _isDanger;

            internal PickerItemButton(
                DevInterface.DevUI owner,
                string id,
                DevUINode parent,
                Vector2 pos,
                float width,
                string text,
                FixedTargetPicker picker,
                int targetIndex,
                bool isFamily,
                bool isDanger)
                : base(owner, id, parent, pos, width, text)
            {
                _picker = picker;
                _targetIndex = targetIndex;
                _isFamily = isFamily;
                _isDanger = isDanger;
            }

            public override void Update()
            {
                base.Update();

                bool selected = _picker != null &&
                                _picker.GetTargetIndex() == _targetIndex;
                if (selected)
                {
                    // Keep the complete selected row visible even when the pointer
                    // leaves it; vanilla Button only highlights while hovered/down.
                    spriteColor = new Color(0.62f, 0.08f, 0.08f);
                    textColor = new Color(1f, 1f, 1f);
                    return;
                }

                if (MouseOver)
                {
                    return;
                }

                if (_isFamily)
                {
                    // Family rows are headers, so give them a slightly darker band
                    // than their indented children while retaining vanilla DevUI red.
                    spriteColor = new Color(0.72f, 0.72f, 0.72f);
                    textColor = new Color(0.78f, 0.08f, 0.08f);
                }
                else if (_isDanger)
                {
                    // Danger rows use a warm accent so the marker is distinguishable
                    // from ordinary weather even before reading the prefix.
                    spriteColor = new Color(1f, 1f, 1f);
                    textColor = new Color(1f, 0.48f, 0.08f);
                }
            }
        }

        private sealed class FamilyRowButton : PickerItemButton
        {
            private const float FoldHotZoneWidth = 28f;

            private readonly FixedTargetPicker _picker;
            private readonly string _familyId;

            internal FamilyRowButton(
                DevInterface.DevUI owner,
                string id,
                DevUINode parent,
                Vector2 pos,
                float width,
                string text,
                FixedTargetPicker picker,
                int targetIndex,
                string familyId)
                : base(
                    owner,
                    id,
                    parent,
                    pos,
                    width,
                    text,
                    picker,
                    targetIndex,
                    isFamily: true,
                    isDanger: false)
            {
                _picker = picker;
                _familyId = familyId;
            }

            public override void Clicked()
            {
                if (_picker == null)
                {
                    return;
                }

                // The arrow is a dedicated fold control; clicking the text portion
                // selects the Family target without changing its fold state.
                if (owner != null && owner.mousePos.x <= absPos.x + FoldHotZoneWidth)
                {
                    _picker.ToggleFamily(_familyId);
                }
                else
                {
                    int targetIndex = FindTargetIndex("Family/" + _familyId);
                    _picker.SelectTarget(targetIndex);
                    _picker.ConsumeLeftClick();
                }
            }
        }
    }
}
