using System;
using System.Globalization;
using System.Reflection;
using DevInterface;
using UnityEngine;

namespace DryCycle.Weather.Spatial;

internal static partial class WeatherSpatialSelectionUiCleanup
{
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
                new Vector2(250f, 470f),
                42f,
                "100%")
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

            FieldInfo worldField = editorType.GetField(
                "_world",
                BindingFlags.Instance | BindingFlags.NonPublic);
            bool restoringActivePreview =
                worldField?.GetValue(editor) is World world &&
                WeatherSpatialPreview.IsActiveFor(world);
            if (!restoringActivePreview)
            {
                _previewIntensityField?.SetValue(_editor, 1f);
            }

            _lastValidIntensity = ReadIntensity();
            _buffer = PercentText(_lastValidIntensity);
            Text = _buffer + "%";

            DevUILabel label = new(
                owner,
                "DryCycle_Weather_Preview_Percent_Label",
                parent,
                new Vector2(152f, 470f),
                96f,
                "PreviewIntensity");
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
            return 1f;
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

}
