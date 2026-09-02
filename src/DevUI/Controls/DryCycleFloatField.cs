using System;
using System.Globalization;
using DevInterface;
using UnityEngine;
using DevUIOwner = DevInterface.DevUI;

namespace DryCycle.DevUI.Controls;

internal sealed class DryCycleFloatField : DryCycleTextField
{
    private readonly float _minValue;
    private readonly float _maxValue;
    private readonly int _decimalPlaces;
    private readonly Func<float> _readValue;
    private readonly Action<float> _writeValue;
    private float _value;

    internal event Action<DryCycleFloatField, float, float> ValueChanged;
    internal event Action<DryCycleFloatField, float> ValueCommitted;
    internal event Action<DryCycleFloatField, float> ValueCancelled;

    internal float Value => _value;
    internal float MinValue => _minValue;
    internal float MaxValue => _maxValue;
    internal int DecimalPlaces => _decimalPlaces;

    internal DryCycleFloatField(
        DevUIOwner owner,
        string IDstring,
        DevUINode parentNode,
        Vector2 pos,
        float width,
        float initialValue,
        float minValue = float.MinValue,
        float maxValue = float.MaxValue,
        int decimalPlaces = 3,
        bool allowScientificNotation = false,
        Func<float> readValue = null,
        Action<float> writeValue = null)
        : base(
            owner,
            IDstring,
            parentNode,
            pos,
            width,
            Format(ClampFinite(initialValue, minValue, maxValue), decimalPlaces),
            text => Validate(text, minValue, maxValue, allowScientificNotation),
            c => IsAllowedCharacter(c, minValue, allowScientificNotation),
            maxLength: 32,
            selectAllOnFocus: true)
    {
        if (float.IsNaN(minValue) || float.IsNaN(maxValue)
            || float.IsInfinity(minValue) || float.IsInfinity(maxValue)
            || minValue > maxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(minValue), "Float field requires a finite ordered range.");
        }

        _minValue = minValue;
        _maxValue = maxValue;
        _decimalPlaces = Math.Max(0, Math.Min(9, decimalPlaces));
        _value = ClampFinite(initialValue, minValue, maxValue);
        _readValue = readValue;
        _writeValue = writeValue;

        AcceptedTextChanged += HandleAcceptedTextChanged;
        EditCommitted += HandleCommitted;
        EditCancelled += HandleCancelled;
    }

    public override void Update()
    {
        if (!IsFocused && _readValue != null)
        {
            float source = ClampFinite(_readValue(), _minValue, _maxValue);
            if (!Mathf.Approximately(source, _value))
            {
                SetValue(source, notify: false);
            }
        }

        base.Update();
    }

    internal void SetValue(float value, bool notify = false, bool writeThrough = false)
    {
        float clamped = ClampFinite(value, _minValue, _maxValue);
        float old = _value;
        _value = clamped;
        base.SetValue(Format(clamped, _decimalPlaces), notify: false, updateWhileFocused: true);

        if (writeThrough && !Mathf.Approximately(old, clamped))
        {
            _writeValue?.Invoke(clamped);
        }

        if (notify && !Mathf.Approximately(old, clamped))
        {
            ValueChanged?.Invoke(this, clamped, old);
        }
    }

    private void HandleAcceptedTextChanged(DryCycleTextField _, string text, string __)
    {
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
            || float.IsNaN(parsed) || float.IsInfinity(parsed))
        {
            return;
        }

        float next = ClampFinite(parsed, _minValue, _maxValue);
        float old = _value;
        _value = next;
        if (!Mathf.Approximately(next, old))
        {
            _writeValue?.Invoke(next);
            ValueChanged?.Invoke(this, next, old);
        }
    }

    private void HandleCommitted(DryCycleTextField _, string __)
    {
        float source = _readValue != null ? ClampFinite(_readValue(), _minValue, _maxValue) : _value;
        _value = source;
        base.SetValue(Format(source, _decimalPlaces), notify: false, updateWhileFocused: true);
        ValueCommitted?.Invoke(this, source);
    }

    private void HandleCancelled(DryCycleTextField _, string __)
    {
        float source = _readValue != null ? ClampFinite(_readValue(), _minValue, _maxValue) : _value;
        _value = source;
        base.SetValue(Format(source, _decimalPlaces), notify: false, updateWhileFocused: true);
        ValueCancelled?.Invoke(this, source);
    }

    private static DryCycleTextValidationState Validate(
        string text,
        float minValue,
        float maxValue,
        bool allowScientificNotation)
    {
        if (string.IsNullOrEmpty(text)
            || text == "."
            || (minValue < 0f && (text == "-" || text == "-.")))
        {
            return DryCycleTextValidationState.Intermediate;
        }

        if (!allowScientificNotation && (text.IndexOf('e') >= 0 || text.IndexOf('E') >= 0))
        {
            return DryCycleTextValidationState.Invalid;
        }

        if (allowScientificNotation)
        {
            string lower = text.ToLowerInvariant();
            if (lower.EndsWith("e", StringComparison.Ordinal)
                || lower.EndsWith("e+", StringComparison.Ordinal)
                || lower.EndsWith("e-", StringComparison.Ordinal))
            {
                return DryCycleTextValidationState.Intermediate;
            }
        }

        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            || float.IsNaN(value) || float.IsInfinity(value))
        {
            return DryCycleTextValidationState.Invalid;
        }

        return value >= minValue && value <= maxValue
            ? DryCycleTextValidationState.Valid
            : DryCycleTextValidationState.Invalid;
    }

    private static bool IsAllowedCharacter(char c, float minValue, bool allowScientificNotation)
    {
        if (char.IsDigit(c) || c == '.')
        {
            return true;
        }
        if (c == '-' && (minValue < 0f || allowScientificNotation))
        {
            return true;
        }
        if (allowScientificNotation && (c == 'e' || c == 'E' || c == '+'))
        {
            return true;
        }
        return false;
    }

    private static float ClampFinite(float value, float minValue, float maxValue)
    {
        if (float.IsNaN(value))
        {
            return Mathf.Clamp(0f, minValue, maxValue);
        }
        if (float.IsNegativeInfinity(value))
        {
            return minValue;
        }
        if (float.IsPositiveInfinity(value))
        {
            return maxValue;
        }
        return Mathf.Clamp(value, minValue, maxValue);
    }

    private static string Format(float value, int decimalPlaces)
    {
        int digits = Math.Max(0, Math.Min(9, decimalPlaces));
        if (digits == 0)
        {
            return Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
        }

        return value.ToString("0." + new string('#', digits), CultureInfo.InvariantCulture);
    }
}
