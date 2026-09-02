using System;
using DevInterface;
using UnityEngine;
using DevUIOwner = DevInterface.DevUI;

namespace DryCycle.DevUI.Controls;

/// <summary>
/// Slider with a real editable numeric field. Vanilla Slider's fixed 16/42 pixel
/// number label is replaced and the track geometry is recalculated around the field.
/// </summary>
internal class DryCycleNumericSlider : Slider
{
    private readonly float _minValue;
    private readonly float _maxValue;
    private readonly float _defaultValue;
    private readonly float _inputWidth;
    private readonly int _decimalPlaces;
    private readonly DryCycleFloatField _field;
    private float _value;

    internal event Action<DryCycleNumericSlider, float, float> ValueChanged;
    internal event Action<DryCycleNumericSlider, float> ValueCommitted;

    internal float Value => _value;
    internal float MinValue => _minValue;
    internal float MaxValue => _maxValue;
    internal DryCycleFloatField Field => _field;

    private SliderNub Nub => subNodes[inheritButton ? 3 : 2] as SliderNub;

    private float ActualSliderStartCoord
        => !inheritButton
            ? titleWidth + 10f + _inputWidth + 4f
            : titleWidth + 10f + _inputWidth + 4f + 34f;

    internal DryCycleNumericSlider(
        DevUIOwner owner,
        string IDstring,
        DevUINode parentNode,
        Vector2 pos,
        string title,
        bool inheritButton,
        float titleWidth,
        float initialValue,
        float minValue,
        float maxValue,
        int decimalPlaces = 2,
        float inputWidth = 42f,
        float? defaultValue = null)
        : base(owner, IDstring, parentNode, pos, title, inheritButton, titleWidth)
    {
        if (float.IsNaN(minValue) || float.IsNaN(maxValue)
            || float.IsInfinity(minValue) || float.IsInfinity(maxValue)
            || minValue > maxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(minValue), "Slider minimum cannot exceed maximum.");
        }

        _minValue = minValue;
        _maxValue = maxValue;
        _decimalPlaces = Math.Max(0, Math.Min(9, decimalPlaces));
        _inputWidth = Math.Max(24f, inputWidth);
        _value = Mathf.Clamp(initialValue, _minValue, _maxValue);
        _defaultValue = Mathf.Clamp(defaultValue ?? _minValue, _minValue, _maxValue);

        subNodes[1].ClearSprites();
        _field = new DryCycleFloatField(
            owner,
            IDstring + "_Value",
            this,
            new Vector2(titleWidth + 10f, 0f),
            _inputWidth,
            _value,
            _minValue,
            _maxValue,
            _decimalPlaces,
            allowScientificNotation: false,
            readValue: () => _value,
            writeValue: value => SetValueInternal(value, notify: true));
        _field.ValueCommitted += (_, value) => ValueCommitted?.Invoke(this, value);
        subNodes[1] = _field;

        if (inheritButton && subNodes[2] is PositionedDevUINode inheritNode)
        {
            inheritNode.Move(new Vector2(titleWidth + 10f + _inputWidth + 4f, 0f));
        }

        Refresh();
    }

    public override void Update()
    {
        // base.Update updates all child nodes first. Its subsequent drag calculation
        // uses vanilla's fixed SliderStartCoord; our NubDragged override intentionally
        // ignores that result, then this block applies the correct custom geometry.
        base.Update();

        SliderNub nub = Nub;
        if (owner != null && nub != null && nub.held)
        {
            float t = Mathf.InverseLerp(
                absPos.x + ActualSliderStartCoord,
                absPos.x + ActualSliderStartCoord + 92f,
                owner.mousePos.x + nub.mousePosOffset);
            SetValueInternal(Mathf.Lerp(_minValue, _maxValue, t), notify: true);
        }
    }

    public override void Refresh()
    {
        base.Refresh();

        float t = Mathf.Approximately(_minValue, _maxValue)
            ? 0f
            : Mathf.InverseLerp(_minValue, _maxValue, _value);

        SliderNub nub = Nub;
        nub?.Move(new Vector2(Mathf.Lerp(ActualSliderStartCoord, ActualSliderStartCoord + 92f, t), 0f));
        MoveSprite(0, absPos + new Vector2(ActualSliderStartCoord, 0f));
        MoveSprite(1, absPos + new Vector2(ActualSliderStartCoord, 7f));

        if (_field != null && !_field.IsFocused)
        {
            _field.SetValue(_value, notify: false);
        }
    }

    public override void NubDragged(float nubPos)
    {
        // Suppress vanilla Slider.Update's calculation because it is based on the
        // original fixed number-label width. Correct handling occurs in Update().
    }

    public override void ClickedResetToInherent()
    {
        SetValueInternal(_defaultValue, notify: true);
        ValueCommitted?.Invoke(this, _value);
    }

    internal void SetValue(float value, bool notify = false)
    {
        SetValueInternal(value, notify);
    }

    private void SetValueInternal(float value, bool notify)
    {
        float next = Mathf.Clamp(value, _minValue, _maxValue);
        if (_decimalPlaces >= 0)
        {
            next = (float)Math.Round(next, _decimalPlaces);
        }

        float old = _value;
        _value = next;
        if (_field != null && !_field.IsFocused)
        {
            _field.SetValue(next, notify: false);
        }

        RefreshTrackOnly();

        if (notify && !Mathf.Approximately(old, next))
        {
            ValueChanged?.Invoke(this, next, old);
        }
    }

    private void RefreshTrackOnly()
    {
        float t = Mathf.Approximately(_minValue, _maxValue)
            ? 0f
            : Mathf.InverseLerp(_minValue, _maxValue, _value);
        Nub?.Move(new Vector2(Mathf.Lerp(ActualSliderStartCoord, ActualSliderStartCoord + 92f, t), 0f));
        MoveSprite(0, absPos + new Vector2(ActualSliderStartCoord, 0f));
        MoveSprite(1, absPos + new Vector2(ActualSliderStartCoord, 7f));
    }
}
