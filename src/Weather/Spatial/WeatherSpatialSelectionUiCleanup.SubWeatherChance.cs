using System;
using System.Globalization;
using System.Reflection;
using DevInterface;
using UnityEngine;

namespace DryCycle.Weather.Spatial;

internal static partial class WeatherSpatialSelectionUiCleanup
{
    private sealed class WeatherChanceInput : Button
    {
        private readonly bool _familyChance;
        private readonly DevUINode _editor;
        private readonly FieldInfo _regionIdField;
        private readonly FieldInfo _targetIndexField;
        private readonly FieldInfo _statusField;
        private readonly MethodInfo _runValidationMethod;
        private readonly MethodInfo _updateStateLabelsMethod;
        private readonly DevUILabel _label;

        private bool _editing;
        private string _buffer = string.Empty;
        private string _lastBindingKey = string.Empty;
        private int _lastPercent;
        private bool _hasConfiguredChance;

        internal WeatherChanceInput(
            DevInterface.DevUI owner,
            DevUINode parent,
            DevUINode editor,
            bool familyChance)
            : base(
                owner,
                familyChance
                    ? "DryCycle_Weather_Family_Chance_Input"
                    : "DryCycle_Weather_SubWeather_Chance_Input",
                parent,
                new Vector2(160f, familyChance ? 470f : 492f),
                44f,
                "0%")
        {
            _familyChance = familyChance;
            _editor = editor;
            Type editorType = editor.GetType();
            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            _regionIdField = editorType.GetField("_regionId", flags);
            _targetIndexField = editorType.GetField("_targetIndex", flags);
            _statusField = editorType.GetField("_status", flags);
            _runValidationMethod = editorType.GetMethod("RunValidation", flags);
            _updateStateLabelsMethod = editorType.GetMethod("UpdateStateLabels", flags);

            _label = new DevUILabel(
                owner,
                familyChance
                    ? "DryCycle_Weather_Family_Chance_Label"
                    : "DryCycle_Weather_SubWeather_Chance_Label",
                parent,
                new Vector2(8f, familyChance ? 470f : 492f),
                148f,
                familyChance ? "FamWeatherChance" : "SubWeather Chance");
            parent.subNodes.Add(_label);
            RefreshFromTarget(force: true);
        }

        public override void Clicked()
        {
            if (!TryCurrentBinding(out _, out _, out _))
            {
                SetStatus(_familyChance
                    ? "Select a weather family before editing its chance."
                    : "Select a SubWeather before editing its chance.");
                return;
            }

            RefreshFromTarget(force: true);
            _editing = true;
            _buffer = _hasConfiguredChance
                ? _lastPercent.ToString(CultureInfo.InvariantCulture)
                : "0";
            Text = _buffer + "_";
            if (owner != null)
            {
                owner.mouseClick = false;
            }
        }

        public override void Update()
        {
            base.Update();

            if (!TryCurrentBinding(out _, out WeatherSpatialTarget target, out string bindingKey))
            {
                _editing = false;
                Text = "--";
                return;
            }

            if (!string.Equals(_lastBindingKey, bindingKey, StringComparison.OrdinalIgnoreCase))
            {
                _editing = false;
                RefreshFromTarget(force: true);
            }

            if (!_familyChance && target.IsFamily)
            {
                _editing = false;
                Text = "--";
                return;
            }

            if (!_editing)
            {
                RefreshFromTarget(force: false);
                Text = _lastPercent.ToString(CultureInfo.InvariantCulture) + "%";
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
                    SetStatus(ChanceName + " accepts digits only (0-100).");
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
                RefreshFromTarget(force: true);
                return;
            }

            if (commit)
            {
                CommitBuffer();
                return;
            }

            Text = _buffer.Length == 0 ? "_" : _buffer + "_";
        }

        private void CommitBuffer()
        {
            if (!int.TryParse(_buffer, NumberStyles.None, CultureInfo.InvariantCulture, out int percent) ||
                percent < 0 ||
                percent > 100)
            {
                SetStatus(ChanceName + " must be between 0 and 100.");
                Text = _buffer.Length == 0 ? "_" : _buffer + "_";
                return;
            }

            if (!TryCurrentBinding(
                    out string regionId,
                    out WeatherSpatialTarget target,
                    out _))
            {
                _editing = false;
                Text = "--";
                return;
            }

            bool updated = _familyChance
                ? WeatherSpatialRegistry.SetFamilyWeatherChance(regionId, target, percent)
                : WeatherSpatialRegistry.SetSubWeatherChance(regionId, target, percent);
            if (!updated)
            {
                SetStatus("Could not update " + ChanceName + ".");
                return;
            }

            _lastPercent = percent;
            _hasConfiguredChance = true;
            _editing = false;
            _buffer = percent.ToString(CultureInfo.InvariantCulture);
            Text = _buffer + "%";
            SetStatus(BindingDisplayName(target) + " " + ChanceName + ": " + percent + "%");
            _runValidationMethod?.Invoke(_editor, null);
            _updateStateLabelsMethod?.Invoke(_editor, null);
        }

        private void RefreshFromTarget(bool force)
        {
            if (!TryCurrentBinding(
                    out string regionId,
                    out WeatherSpatialTarget target,
                    out string bindingKey))
            {
                return;
            }

            if (!force &&
                string.Equals(_lastBindingKey, bindingKey, StringComparison.OrdinalIgnoreCase))
            {
                if (TryReadChance(regionId, target, out float liveChance))
                {
                    int live = Mathf.RoundToInt(liveChance);
                    if (live == _lastPercent && _hasConfiguredChance)
                    {
                        return;
                    }
                }
                else if (!_hasConfiguredChance)
                {
                    return;
                }
            }

            _lastBindingKey = bindingKey;
            float chance = 0f;
            _hasConfiguredChance = TryReadChance(regionId, target, out chance);
            _lastPercent = _hasConfiguredChance ? Mathf.RoundToInt(chance) : 0;
            _buffer = _lastPercent.ToString(CultureInfo.InvariantCulture);
            Text = !_familyChance && target.IsFamily ? "--" : _buffer + "%";
        }

        private bool TryCurrentBinding(
            out string regionId,
            out WeatherSpatialTarget target,
            out string bindingKey)
        {
            bindingKey = string.Empty;
            if (!TryCurrentTarget(out regionId, out target))
            {
                return false;
            }

            string targetBinding;
            if (_familyChance)
            {
                if (!TryTargetFamily(target, out WeatherSpatialFamily family))
                {
                    return false;
                }
                targetBinding = "Family/" + family.Id;
            }
            else
            {
                if (target.IsFamily)
                {
                    return false;
                }
                targetBinding = target.Key;
            }

            bindingKey = (regionId ?? string.Empty).Trim().ToUpperInvariant() + "/" + targetBinding;
            return true;
        }

        private bool TryCurrentTarget(
            out string regionId,
            out WeatherSpatialTarget target)
        {
            regionId = _regionIdField?.GetValue(_editor) as string ?? string.Empty;
            target = default;
            int count = WeatherSpatialCatalog.AllTargets.Count;
            if (count <= 0 || _targetIndexField == null)
            {
                return false;
            }

            int index = _targetIndexField.GetValue(_editor) is int targetIndex
                ? Mathf.Clamp(targetIndex, 0, count - 1)
                : 0;
            target = WeatherSpatialCatalog.AllTargets[index];
            return true;
        }

        private void SetStatus(string text)
        {
            _statusField?.SetValue(_editor, text);
            _updateStateLabelsMethod?.Invoke(_editor, null);
        }

        private bool TryReadChance(
            string regionId,
            in WeatherSpatialTarget target,
            out float chance)
        {
            return _familyChance
                ? WeatherSpatialRegistry.TryGetFamilyWeatherChance(regionId, target, out chance)
                : WeatherSpatialRegistry.TryGetSubWeatherChance(regionId, target, out chance);
        }

        private string BindingDisplayName(in WeatherSpatialTarget target)
        {
            if (_familyChance && TryTargetFamily(target, out WeatherSpatialFamily family))
            {
                return family.Id;
            }
            return target.DisplayName.Trim();
        }

        private static bool TryTargetFamily(
            in WeatherSpatialTarget target,
            out WeatherSpatialFamily family)
        {
            return target.IsFamily
                ? WeatherSpatialCatalog.TryGetFamily(target.FamilyId, out family)
                : WeatherSpatialCatalog.TryGetFamily(target.Kind, target.WeatherId, out family);
        }

        private string ChanceName => _familyChance
            ? "FamWeatherChance"
            : "SubWeather chance";
    }
}
