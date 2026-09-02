using System;
using System.Globalization;
using DevInterface;
using UnityEngine;
using DevUIOwner = DevInterface.DevUI;

namespace DryCycle.DevUI.Controls;

internal sealed class DryCycleIntegerField : DryCycleTextField
{
    private readonly int _minValue;
    private readonly int _maxValue;
    private readonly Func<int> _readValue;
    private readonly Action<int> _writeValue;
    private int _value;

    internal event Action<DryCycleIntegerField, int, int> ValueChanged;
    internal event Action<DryCycleIntegerField, int> ValueCommitted;
    internal event Action<DryCycleIntegerField, int> ValueCancelled;

    internal int Value => _value;
    internal int MinValue => _minValue;
    internal int MaxValue => _maxValue;

    internal DryCycleIntegerField(
        DevUIOwner owner,
        string IDstring,
        DevUINode parentNode,
        Vector2 pos,
        float width,
        int initialValue,
        int minValue = int.MinValue,
        int maxValue = int.MaxValue,
        Func<int> readValue = null,
        Action<int> writeValue = null)
        : base(
            owner,
            IDstring,
            parentNode,
            pos,
            width,
            Clamp(initialValue, minValue, maxValue).ToString(CultureInfo.InvariantCulture),
            text => Validate(text, minValue, maxValue),
            c => char.IsDigit(c) || (c == '-' && minValue < 0),
            maxLength: 12,
            selectAllOnFocus: true)
    {
        if (minValue > maxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(minValue), "Minimum integer value cannot exceed maximum value.");
        }

        _minValue = minValue;
        _maxValue = maxValue;
        _value = Clamp(initialValue, minValue, maxValue);
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
            int source = Clamp(_readValue(), _minValue, _maxValue);
            if (source != _value)
            {
                SetValue(source, notify: false);
            }
        }

        base.Update();
    }

    internal void SetValue(int value, bool notify = false, bool writeThrough = false)
    {
        int clamped = Clamp(value, _minValue, _maxValue);
        int old = _value;
        _value = clamped;
        base.SetValue(clamped.ToString(CultureInfo.InvariantCulture), notify: false, updateWhileFocused: true);

        if (writeThrough && old != clamped)
        {
            _writeValue?.Invoke(clamped);
        }

        if (notify && old != clamped)
        {
            ValueChanged?.Invoke(this, clamped, old);
        }
    }

    private void HandleAcceptedTextChanged(DryCycleTextField _, string text, string __)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            return;
        }

        int next = Clamp(parsed, _minValue, _maxValue);
        int old = _value;
        _value = next;
        if (next != old)
        {
            _writeValue?.Invoke(next);
            ValueChanged?.Invoke(this, next, old);
        }
    }

    private void HandleCommitted(DryCycleTextField _, string __)
    {
        // Canonicalize accepted input (e.g. 0007 -> 7), then resync from the
        // authoritative source in case another DevUI control changed it this frame.
        int source = _readValue != null ? Clamp(_readValue(), _minValue, _maxValue) : _value;
        _value = source;
        base.SetValue(source.ToString(CultureInfo.InvariantCulture), notify: false, updateWhileFocused: true);
        ValueCommitted?.Invoke(this, source);
    }

    private void HandleCancelled(DryCycleTextField _, string __)
    {
        int source = _readValue != null ? Clamp(_readValue(), _minValue, _maxValue) : _value;
        _value = source;
        base.SetValue(source.ToString(CultureInfo.InvariantCulture), notify: false, updateWhileFocused: true);
        ValueCancelled?.Invoke(this, source);
    }

    private static DryCycleTextValidationState Validate(string text, int minValue, int maxValue)
    {
        if (string.IsNullOrEmpty(text) || (text == "-" && minValue < 0))
        {
            return DryCycleTextValidationState.Intermediate;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            return DryCycleTextValidationState.Invalid;
        }

        return value >= minValue && value <= maxValue
            ? DryCycleTextValidationState.Valid
            : DryCycleTextValidationState.Invalid;
    }

    private static int Clamp(int value, int minValue, int maxValue)
        => value < minValue ? minValue : value > maxValue ? maxValue : value;
}
