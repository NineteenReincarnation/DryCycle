using System;
using System.Globalization;
using System.Reflection;
using DevInterface;
using UnityEngine;

namespace DryCycle.Weather.Spatial;

internal static partial class WeatherSpatialSelectionUiCleanup
{
    private sealed class FamilyScheduleTable : PositionedDevUINode
    {
        private const float HeaderY = 646f;
        private const float FirstRowY = 624f;
        private const float RowStep = 20f;

        private readonly DevUINode _editor;
        private readonly FieldInfo _regionIdField;
        private readonly FieldInfo _statusField;
        private readonly MethodInfo _runValidationMethod;
        private readonly MethodInfo _updateStateLabelsMethod;

        internal FamilyScheduleTable(
            DevInterface.DevUI owner,
            DevUINode parent,
            DevUINode editor)
            : base(owner, "DryCycle_Weather_Family_Schedule_Table", parent, Vector2.zero)
        {
            _editor = editor;
            Type editorType = editor.GetType();
            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            _regionIdField = editorType.GetField("_regionId", flags);
            _statusField = editorType.GetField("_status", flags);
            _runValidationMethod = editorType.GetMethod("RunValidation", flags);
            _updateStateLabelsMethod = editorType.GetMethod("UpdateStateLabels", flags);

            AddLabel("DryCycle_Weather_Family_Header_Name", 8f, HeaderY, 88f, "FamWeather");
            AddLabel("DryCycle_Weather_Family_Header_Enabled", 100f, HeaderY, 96f, "Enabled");
            AddLabel("DryCycle_Weather_Family_Header_Chance", 202f, HeaderY, 90f, "FamWeatherChance");

            for (int i = 0; i < WeatherSpatialCatalog.AllFamilies.Count; i++)
            {
                WeatherSpatialFamily family = WeatherSpatialCatalog.AllFamilies[i];
                float y = FirstRowY - i * RowStep;
                AddLabel(
                    "DryCycle_Weather_Family_Name_" + family.Id,
                    8f,
                    y,
                    88f,
                    family.Id);

                subNodes.Add(new FamilyEnableButton(
                    owner,
                    "DryCycle_Weather_Family_Yes_" + family.Id,
                    this,
                    new Vector2(100f, y),
                    46f,
                    "YES",
                    this,
                    family.Id,
                    enabledValue: true));
                subNodes.Add(new FamilyEnableButton(
                    owner,
                    "DryCycle_Weather_Family_No_" + family.Id,
                    this,
                    new Vector2(150f, y),
                    46f,
                    "NO",
                    this,
                    family.Id,
                    enabledValue: false));
                subNodes.Add(new FamilyChanceField(
                    owner,
                    "DryCycle_Weather_Family_Chance_" + family.Id,
                    this,
                    new Vector2(202f, y),
                    90f,
                    this,
                    family.Id));
            }
        }

        internal bool TryGetState(string familyId, out bool enabled, out float chancePercent)
        {
            return WeatherSpatialRegistry.TryGetFamilySchedule(
                RegionId,
                familyId,
                out enabled,
                out chancePercent);
        }

        internal void SetEnabled(string familyId, bool enabled)
        {
            if (!WeatherSpatialRegistry.SetFamilyScheduleEnabled(RegionId, familyId, enabled))
            {
                SetStatus("Could not update " + familyId + " FamWeather state.");
                return;
            }

            SetStatus(familyId + " FamWeather: " + (enabled ? "YES" : "NO"));
            _runValidationMethod?.Invoke(_editor, null);
            _updateStateLabelsMethod?.Invoke(_editor, null);
        }

        internal void SetChance(string familyId, int percent)
        {
            if (!WeatherSpatialRegistry.SetFamilyScheduleChance(RegionId, familyId, percent))
            {
                SetStatus("Could not update " + familyId + " FamWeatherChance.");
                return;
            }

            SetStatus(familyId + " FamWeatherChance: " + percent + "%");
            _runValidationMethod?.Invoke(_editor, null);
            _updateStateLabelsMethod?.Invoke(_editor, null);
        }

        private void SetStatus(string text)
        {
            _statusField?.SetValue(_editor, text);
        }

        private string RegionId =>
            (_regionIdField?.GetValue(_editor) as string ?? string.Empty).Trim().ToUpperInvariant();

        private DevUILabel AddLabel(string id, float x, float y, float width, string text)
        {
            DevUILabel label = new(owner, id, this, new Vector2(x, y), width, text);
            label.spriteColor = Color.black;
            label.textColor = Color.white;
            subNodes.Add(label);
            return label;
        }

        private sealed class FamilyEnableButton : Button
        {
            private readonly FamilyScheduleTable _table;
            private readonly string _familyId;
            private readonly bool _enabledValue;

            internal FamilyEnableButton(
                DevInterface.DevUI owner,
                string id,
                DevUINode parent,
                Vector2 pos,
                float width,
                string text,
                FamilyScheduleTable table,
                string familyId,
                bool enabledValue)
                : base(owner, id, parent, pos, width, text)
            {
                _table = table;
                _familyId = familyId;
                _enabledValue = enabledValue;
            }

            public override void Clicked()
            {
                _table?.SetEnabled(_familyId, _enabledValue);
            }

            public override void Update()
            {
                base.Update();
                bool enabled = false;
                bool configured = _table != null &&
                                  _table.TryGetState(_familyId, out enabled, out _);
                bool selected = _enabledValue
                    ? configured && enabled
                    : !configured || !enabled;
                if (selected)
                {
                    spriteColor = new Color(0.62f, 0.08f, 0.08f);
                    textColor = Color.white;
                }
                else if (!MouseOver)
                {
                    spriteColor = new Color(0.82f, 0.82f, 0.82f);
                    textColor = new Color(0.78f, 0.08f, 0.08f);
                }
            }
        }

        private sealed class FamilyChanceField : Button
        {
            private readonly FamilyScheduleTable _table;
            private readonly string _familyId;
            private bool _editing;
            private string _buffer = string.Empty;

            internal FamilyChanceField(
                DevInterface.DevUI owner,
                string id,
                DevUINode parent,
                Vector2 pos,
                float width,
                FamilyScheduleTable table,
                string familyId)
                : base(owner, id, parent, pos, width, "--")
            {
                _table = table;
                _familyId = familyId;
            }

            public override void Clicked()
            {
                int value = 100;
                if (_table != null && _table.TryGetState(_familyId, out _, out float chance))
                {
                    value = Mathf.RoundToInt(chance);
                }

                _editing = true;
                _buffer = value.ToString(CultureInfo.InvariantCulture);
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
                    Text = _table != null && _table.TryGetState(_familyId, out _, out float chance)
                        ? Mathf.RoundToInt(chance).ToString(CultureInfo.InvariantCulture) + "%"
                        : "--";
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
                    return;
                }
                if (commit)
                {
                    Commit();
                    return;
                }

                Text = _buffer.Length == 0 ? "_" : _buffer + "_";
            }

            private void Commit()
            {
                if (!int.TryParse(_buffer, NumberStyles.None, CultureInfo.InvariantCulture, out int percent) ||
                    percent < 0 || percent > 100)
                {
                    Text = _buffer.Length == 0 ? "_" : _buffer + "_";
                    return;
                }

                _table?.SetChance(_familyId, percent);
                _editing = false;
                _buffer = percent.ToString(CultureInfo.InvariantCulture);
                Text = _buffer + "%";
            }
        }
    }
}
