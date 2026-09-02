using System;
using System.Globalization;
using DevInterface;
using DryCycle.DevUI.Controls;
using UnityEngine;
using DevUIOwner = DevInterface.DevUI;

namespace DryCycle.RoomSettingsExt.DevUI;

internal static class RGBColorClipboard
{
    internal static bool HasColor { get; private set; }
    internal static Color StoredColor { get; private set; }

    internal static void Copy(Color color)
    {
        StoredColor = Opaque(color);
        HasColor = true;
        GUIUtility.systemCopyBuffer = RGBColorEffectEditor.ToHex(StoredColor);
    }

    internal static bool TryPaste(out Color color)
    {
        if (RGBColorEffectEditor.TryParseHex(GUIUtility.systemCopyBuffer, out color))
        {
            StoredColor = Opaque(color);
            HasColor = true;
            return true;
        }

        if (HasColor)
        {
            color = StoredColor;
            return true;
        }

        color = Color.white;
        return false;
    }

    internal static void ClearTransientState()
    {
        HasColor = false;
        StoredColor = UnityEngine.Color.white;
    }

    private static Color Opaque(Color color)
    {
        color.a = 1f;
        return color;
    }
}

internal sealed class RGBColorEffectEditor : PositionedDevUINode, IDevUISignals
{
    private readonly RoomSettings.RoomEffect _effect;
    private readonly Color _previousColor;
    private readonly bool _effectA;
    private readonly ColorSwatch _previousSwatch;
    private readonly ColorSwatch _currentSwatch;
    private readonly ByteChannelControl _red;
    private readonly ByteChannelControl _green;
    private readonly ByteChannelControl _blue;
    private readonly DryCycleTextField _hexField;
    private readonly HueRingControl _hueRing;
    private readonly SaturationValueControl _svField;
    private readonly DryCycleIntegerField _hueField;
    private readonly DryCycleIntegerField _saturationField;
    private readonly DryCycleIntegerField _valueField;
    private readonly DevUILabel _readOnlyLabel;
    private Color _lastObservedColor;

    internal RGBColorEffectEditor(
        DevUIOwner owner,
        string IDstring,
        DevUINode parentNode,
        Vector2 pos,
        RoomSettings.RoomEffect effect,
        bool effectA)
        : base(owner, IDstring, parentNode, pos)
    {
        _effect = effect;
        _effectA = effectA;
        _previousColor = RGBEffectRuntime.ReadColor(effect);
        _lastObservedColor = _previousColor;

        subNodes.Add(new DevUILabel(owner, "RGB_Mode", this, new Vector2(8f, 427f), 210f,
            effectA ? "EFFECT COLOR A / PRESERVE LUMINANCE" : "EFFECT COLOR B / PRESERVE LUMINANCE"));

        subNodes.Add(new DevUILabel(owner, "RGB_Previous_Label", this, new Vector2(8f, 401f), 72f, "PREVIOUS"));
        subNodes.Add(new DevUILabel(owner, "RGB_Current_Label", this, new Vector2(112f, 401f), 72f, "CURRENT"));

        _previousSwatch = new ColorSwatch(owner, "RGB_Previous_Swatch", this, new Vector2(8f, 366f), new Vector2(88f, 28f), _previousColor,
            () => SetColor(_previousColor));
        _currentSwatch = new ColorSwatch(owner, "RGB_Current_Swatch", this, new Vector2(112f, 366f), new Vector2(88f, 28f), _previousColor, null);
        subNodes.Add(_previousSwatch);
        subNodes.Add(_currentSwatch);

        _readOnlyLabel = new DevUILabel(owner, "RGB_ReadOnly", this, new Vector2(218f, 376f), 200f,
            effect.inherited ? "<T> INHERITED / READ ONLY" : "LIVE ROOM CAMERA PREVIEW");
        subNodes.Add(_readOnlyLabel);

        _red = new ByteChannelControl(owner, "RGB_R", this, new Vector2(8f, 333f), "R", ReadByteChannel(0),
            () => ReadByteChannel(0), value => SetByteChannel(0, value));
        _green = new ByteChannelControl(owner, "RGB_G", this, new Vector2(8f, 307f), "G", ReadByteChannel(1),
            () => ReadByteChannel(1), value => SetByteChannel(1, value));
        _blue = new ByteChannelControl(owner, "RGB_B", this, new Vector2(8f, 281f), "B", ReadByteChannel(2),
            () => ReadByteChannel(2), value => SetByteChannel(2, value));
        subNodes.Add(_red);
        subNodes.Add(_green);
        subNodes.Add(_blue);

        subNodes.Add(new DevUILabel(owner, "RGB_Hex_Label", this, new Vector2(8f, 251f), 48f, "HEX"));
        _hexField = new DryCycleTextField(
            owner,
            "RGB_Hex_Field",
            this,
            new Vector2(58f, 251f),
            100f,
            ToHex(_previousColor),
            ValidateHex,
            IsHexCharacter,
            maxLength: 7,
            selectAllOnFocus: true);
        _hexField.AcceptedTextChanged += HexField_AcceptedTextChanged;
        subNodes.Add(_hexField);

        subNodes.Add(new DevUILabel(owner, "RGB_Hue_Label", this, new Vector2(8f, 226f), 150f, "HUE"));
        subNodes.Add(new DevUILabel(owner, "RGB_SV_Label", this, new Vector2(206f, 226f), 190f, "SATURATION / VALUE"));

        Color.RGBToHSV(_previousColor, out float initialH, out float initialS, out float initialV);
        _hueRing = new HueRingControl(owner, "RGB_Hue_Ring", this, new Vector2(8f, 69f), 150f, initialH, SetHueFromPicker);
        _svField = new SaturationValueControl(owner, "RGB_SV_Field", this, new Vector2(206f, 69f), 150f, initialH, initialS, initialV, SetSVFromPicker);
        subNodes.Add(_hueRing);
        subNodes.Add(_svField);

        subNodes.Add(new DevUILabel(owner, "RGB_H_Label", this, new Vector2(8f, 43f), 20f, "H"));
        _hueField = new DryCycleIntegerField(owner, "RGB_H_Field", this, new Vector2(28f, 43f), 48f,
            Mathf.RoundToInt(initialH * 359f), 0, 359,
            readValue: ReadHueDegrees,
            writeValue: SetHueDegrees);
        subNodes.Add(_hueField);

        subNodes.Add(new DevUILabel(owner, "RGB_S_Label", this, new Vector2(90f, 43f), 20f, "S"));
        _saturationField = new DryCycleIntegerField(owner, "RGB_S_Field", this, new Vector2(110f, 43f), 48f,
            Mathf.RoundToInt(initialS * 100f), 0, 100,
            readValue: ReadSaturationPercent,
            writeValue: SetSaturationPercent);
        subNodes.Add(_saturationField);

        subNodes.Add(new DevUILabel(owner, "RGB_V_Label", this, new Vector2(172f, 43f), 20f, "V"));
        _valueField = new DryCycleIntegerField(owner, "RGB_V_Field", this, new Vector2(192f, 43f), 48f,
            Mathf.RoundToInt(initialV * 100f), 0, 100,
            readValue: ReadValuePercent,
            writeValue: SetValuePercent);
        subNodes.Add(_valueField);

        subNodes.Add(new Button(owner, "RGB_Reset", this, new Vector2(258f, 43f), 50f, "RESET"));
        subNodes.Add(new Button(owner, "RGB_Copy", this, new Vector2(312f, 43f), 48f, "COPY"));
        subNodes.Add(new Button(owner, "RGB_Paste", this, new Vector2(364f, 43f), 56f, "PASTE"));

        subNodes.Add(new DevUILabel(owner, "RGB_Help", this, new Vector2(8f, 15f), 412f,
            "RGB / HSV / HEX / WHEEL are one ColorModel. Dragging previews immediately."));

        SyncVisuals(_previousColor, force: true);
    }

    public override void Update()
    {
        base.Update();

        Color current = RGBEffectRuntime.ReadColor(_effect);
        if (!Approximately(current, _lastObservedColor))
        {
            SyncVisuals(current, force: false);
        }

        _readOnlyLabel.Text = _effect.inherited
            ? "<T> INHERITED / READ ONLY"
            : (_effectA ? "LIVE PREVIEW -> EFFECT COLOR A" : "LIVE PREVIEW -> EFFECT COLOR B");
    }

    public void Signal(DevUISignalType type, DevUINode sender, string message)
    {
        if (type != DevUISignalType.ButtonClick || sender == null)
        {
            return;
        }

        switch (sender.IDstring)
        {
            case "RGB_Reset":
                SetColor(_previousColor);
                break;
            case "RGB_Copy":
                RGBColorClipboard.Copy(RGBEffectRuntime.ReadColor(_effect));
                break;
            case "RGB_Paste":
                if (RGBColorClipboard.TryPaste(out Color pasted))
                {
                    SetColor(pasted);
                }
                break;
        }
    }

    internal static string ToHex(Color color)
    {
        Color32 c = color;
        return $"#{c.r:X2}{c.g:X2}{c.b:X2}";
    }

    internal static bool TryParseHex(string text, out Color color)
    {
        string value = (text ?? string.Empty).Trim();
        if (value.StartsWith("#", StringComparison.Ordinal))
        {
            value = value.Substring(1);
        }

        if (value.Length == 6
            && byte.TryParse(value.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r)
            && byte.TryParse(value.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g)
            && byte.TryParse(value.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
        {
            color = new Color32(r, g, b, 255);
            return true;
        }

        color = Color.white;
        return false;
    }

    private static DryCycleTextValidationState ValidateHex(string text)
    {
        string value = text ?? string.Empty;
        if (value.StartsWith("#", StringComparison.Ordinal))
        {
            value = value.Substring(1);
        }

        if (value.Length < 6)
        {
            return value.Length == 0 || IsAllHex(value)
                ? DryCycleTextValidationState.Intermediate
                : DryCycleTextValidationState.Invalid;
        }

        return value.Length == 6 && IsAllHex(value)
            ? DryCycleTextValidationState.Valid
            : DryCycleTextValidationState.Invalid;
    }

    private static bool IsHexCharacter(char c)
        => c == '#' || Uri.IsHexDigit(c);

    private static bool IsAllHex(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            if (!Uri.IsHexDigit(value[i]))
            {
                return false;
            }
        }
        return true;
    }

    private void HexField_AcceptedTextChanged(DryCycleTextField field, string text, string oldText)
    {
        if (TryParseHex(text, out Color color))
        {
            SetColor(color);
        }
    }

    private int ReadByteChannel(int channel)
    {
        Color32 c = RGBEffectRuntime.ReadColor(_effect);
        return channel switch
        {
            0 => c.r,
            1 => c.g,
            _ => c.b
        };
    }

    private void SetByteChannel(int channel, int value)
    {
        Color32 c = RGBEffectRuntime.ReadColor(_effect);
        byte next = (byte)Mathf.Clamp(value, 0, 255);
        if (channel == 0)
        {
            c.r = next;
        }
        else if (channel == 1)
        {
            c.g = next;
        }
        else
        {
            c.b = next;
        }
        SetColor(c);
    }

    private int ReadHueDegrees()
    {
        Color.RGBToHSV(RGBEffectRuntime.ReadColor(_effect), out float h, out _, out _);
        return Mathf.Clamp(Mathf.RoundToInt(h * 359f), 0, 359);
    }

    private int ReadSaturationPercent()
    {
        Color.RGBToHSV(RGBEffectRuntime.ReadColor(_effect), out _, out float s, out _);
        return Mathf.Clamp(Mathf.RoundToInt(s * 100f), 0, 100);
    }

    private int ReadValuePercent()
    {
        Color.RGBToHSV(RGBEffectRuntime.ReadColor(_effect), out _, out _, out float v);
        return Mathf.Clamp(Mathf.RoundToInt(v * 100f), 0, 100);
    }

    private void SetHueDegrees(int degrees)
    {
        Color.RGBToHSV(RGBEffectRuntime.ReadColor(_effect), out _, out float s, out float v);
        SetColor(Color.HSVToRGB(Mathf.Repeat(degrees / 360f, 1f), s, v));
    }

    private void SetSaturationPercent(int percent)
    {
        Color.RGBToHSV(RGBEffectRuntime.ReadColor(_effect), out float h, out _, out float v);
        SetColor(Color.HSVToRGB(h, Mathf.Clamp01(percent / 100f), v));
    }

    private void SetValuePercent(int percent)
    {
        Color.RGBToHSV(RGBEffectRuntime.ReadColor(_effect), out float h, out float s, out _);
        SetColor(Color.HSVToRGB(h, s, Mathf.Clamp01(percent / 100f)));
    }

    private void SetHueFromPicker(float hue)
    {
        Color.RGBToHSV(RGBEffectRuntime.ReadColor(_effect), out _, out float s, out float v);
        SetColor(Color.HSVToRGB(Mathf.Repeat(hue, 1f), s, v));
    }

    private void SetSVFromPicker(float saturation, float value)
    {
        Color.RGBToHSV(RGBEffectRuntime.ReadColor(_effect), out float h, out _, out _);
        SetColor(Color.HSVToRGB(h, Mathf.Clamp01(saturation), Mathf.Clamp01(value)));
    }

    private void SetColor(Color color)
    {
        color.a = 1f;
        RGBEffectRuntime.WriteColor(_effect, color, owner);
        SyncVisuals(RGBEffectRuntime.ReadColor(_effect), force: false);
    }

    private void SyncVisuals(Color color, bool force)
    {
        _lastObservedColor = color;
        _currentSwatch.SetColor(color);

        Color.RGBToHSV(color, out float h, out float s, out float v);
        _hueRing.SetHue(h);
        _svField.SetHSV(h, s, v);
        _red.SyncFromSource();
        _green.SyncFromSource();
        _blue.SyncFromSource();

        if (!_hexField.IsFocused || force)
        {
            _hexField.SetValue(ToHex(color), notify: false, updateWhileFocused: force);
        }

        if (!_hueField.IsFocused || force)
        {
            _hueField.SetValue(ReadHueDegrees(), notify: false);
        }
        if (!_saturationField.IsFocused || force)
        {
            _saturationField.SetValue(ReadSaturationPercent(), notify: false);
        }
        if (!_valueField.IsFocused || force)
        {
            _valueField.SetValue(ReadValuePercent(), notify: false);
        }
    }

    private static bool Approximately(Color a, Color b)
        => Mathf.Abs(a.r - b.r) < 0.0005f
           && Mathf.Abs(a.g - b.g) < 0.0005f
           && Mathf.Abs(a.b - b.b) < 0.0005f;
}

internal sealed class ByteChannelControl : PositionedDevUINode
{
    private readonly Func<int> _readValue;
    private readonly Action<int> _writeValue;
    private readonly DryCycleIntegerField _field;
    private readonly ByteSlider _slider;

    internal ByteChannelControl(
        DevUIOwner owner,
        string IDstring,
        DevUINode parentNode,
        Vector2 pos,
        string label,
        int initialValue,
        Func<int> readValue,
        Action<int> writeValue)
        : base(owner, IDstring, parentNode, pos)
    {
        _readValue = readValue;
        _writeValue = writeValue;
        subNodes.Add(new DevUILabel(owner, IDstring + "_Label", this, Vector2.zero, 18f, label));

        _field = new DryCycleIntegerField(
            owner,
            IDstring + "_Field",
            this,
            new Vector2(24f, 0f),
            48f,
            initialValue,
            0,
            255,
            readValue,
            value => _writeValue(value));
        subNodes.Add(_field);

        _slider = new ByteSlider(owner, IDstring + "_Slider", this, new Vector2(82f, 0f), 330f, initialValue,
            value => _writeValue(value));
        subNodes.Add(_slider);
    }

    public override void Update()
    {
        base.Update();

        if (_field.MouseOver && owner != null && !_field.IsFocused)
        {
            float wheel = Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) > 0.01f)
            {
                bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                int step = shift ? 10 : 1;
                int direction = wheel > 0f ? 1 : -1;
                _writeValue(Mathf.Clamp(_readValue() + step * direction, 0, 255));
            }
        }

        SyncFromSource();
    }

    internal void SyncFromSource()
    {
        int value = Mathf.Clamp(_readValue(), 0, 255);
        if (!_field.IsFocused)
        {
            _field.SetValue(value, notify: false);
        }
        _slider.SetValue(value);
    }
}

internal sealed class ByteSlider : RectangularDevUINode
{
    private readonly Action<int> _writeValue;
    private readonly FSprite _track;
    private readonly FSprite _fill;
    private readonly FSprite _nub;
    private int _value;
    private bool _held;

    internal ByteSlider(
        DevUIOwner owner,
        string IDstring,
        DevUINode parentNode,
        Vector2 pos,
        float width,
        int initialValue,
        Action<int> writeValue)
        : base(owner, IDstring, parentNode, pos, new Vector2(width, 16f))
    {
        _value = Mathf.Clamp(initialValue, 0, 255);
        _writeValue = writeValue;

        _track = NewPixel(new Color(1f, 1f, 1f, 0.38f));
        _fill = NewPixel(new Color(0f, 0f, 0f, 0.65f));
        _nub = NewPixel(Color.black);
        fSprites.Add(_track);
        fSprites.Add(_fill);
        fSprites.Add(_nub);
        AddToStage(_track);
        AddToStage(_fill);
        AddToStage(_nub);
        Refresh();
    }

    public override void Update()
    {
        base.Update();
        if (owner == null)
        {
            _held = false;
            return;
        }

        if (owner.mouseClick && MouseOver && (owner.draggedNode == null || owner.draggedNode == this))
        {
            _held = true;
            UpdateFromMouse();
        }

        if (_held)
        {
            owner.draggedNode = this;
            if (owner.mouseDown)
            {
                UpdateFromMouse();
            }
            else
            {
                _held = false;
            }
        }

        _nub.color = _held ? Color.blue : MouseOver ? Color.red : Color.black;
    }

    public override void Refresh()
    {
        base.Refresh();
        float t = _value / 255f;

        _track.x = absPos.x;
        _track.y = absPos.y + 7f;
        _track.scaleX = size.x;
        _track.scaleY = 2f;

        _fill.x = absPos.x;
        _fill.y = absPos.y + 6f;
        _fill.scaleX = Mathf.Max(1f, size.x * t);
        _fill.scaleY = 4f;

        _nub.x = absPos.x + Mathf.Lerp(0f, size.x - 6f, t);
        _nub.y = absPos.y;
        _nub.scaleX = 6f;
        _nub.scaleY = 16f;
    }

    internal void SetValue(int value)
    {
        int clamped = Mathf.Clamp(value, 0, 255);
        if (_value == clamped)
        {
            return;
        }
        _value = clamped;
        Refresh();
    }

    private void UpdateFromMouse()
    {
        float t = Mathf.InverseLerp(absPos.x, absPos.x + size.x, owner.mousePos.x);
        int next = Mathf.Clamp(Mathf.RoundToInt(t * 255f), 0, 255);
        if (next != _value)
        {
            _value = next;
            _writeValue(next);
            Refresh();
        }
    }

    private static FSprite NewPixel(Color color)
        => new("pixel") { anchorX = 0f, anchorY = 0f, color = color };

    private void AddToStage(FSprite sprite)
    {
        if (owner != null)
        {
            Futile.stage.AddChild(sprite);
        }
    }
}

internal sealed class ColorSwatch : RectangularDevUINode
{
    private readonly FSprite _body;
    private readonly FSprite _border;
    private readonly Action _clicked;

    internal ColorSwatch(
        DevUIOwner owner,
        string IDstring,
        DevUINode parentNode,
        Vector2 pos,
        Vector2 size,
        Color color,
        Action clicked)
        : base(owner, IDstring, parentNode, pos, size)
    {
        _clicked = clicked;
        _border = new FSprite("pixel") { anchorX = 0f, anchorY = 0f, color = Color.white };
        _body = new FSprite("pixel") { anchorX = 0f, anchorY = 0f, color = color };
        fSprites.Add(_border);
        fSprites.Add(_body);
        if (owner != null)
        {
            Futile.stage.AddChild(_border);
            Futile.stage.AddChild(_body);
        }
        Refresh();
    }

    public override void Update()
    {
        base.Update();
        if (_clicked != null && owner != null && owner.mouseClick && MouseOver)
        {
            _clicked();
        }
        _border.color = MouseOver && _clicked != null ? Color.red : Color.white;
    }

    public override void Refresh()
    {
        base.Refresh();
        _border.x = absPos.x - 1f;
        _border.y = absPos.y - 1f;
        _border.scaleX = size.x + 2f;
        _border.scaleY = size.y + 2f;
        _body.x = absPos.x;
        _body.y = absPos.y;
        _body.scaleX = size.x;
        _body.scaleY = size.y;
    }

    internal void SetColor(Color color)
    {
        color.a = 1f;
        _body.color = color;
    }
}

internal sealed class HueRingControl : RectangularDevUINode
{
    private const int Segments = 72;
    private readonly float _radius;
    private readonly float _innerRadius;
    private readonly Action<float> _writeHue;
    private readonly TriangleMesh _mesh;
    private readonly FSprite _markerOuter;
    private readonly FSprite _markerInner;
    private float _hue;
    private bool _held;

    internal HueRingControl(
        DevUIOwner owner,
        string IDstring,
        DevUINode parentNode,
        Vector2 pos,
        float diameter,
        float initialHue,
        Action<float> writeHue)
        : base(owner, IDstring, parentNode, pos, new Vector2(diameter, diameter))
    {
        _radius = diameter * 0.5f;
        _innerRadius = _radius - 18f;
        _hue = Mathf.Repeat(initialHue, 1f);
        _writeHue = writeHue;
        _mesh = CreateRingMesh();
        _markerOuter = new FSprite("Circle20") { scale = 0.48f, color = Color.black };
        _markerInner = new FSprite("Circle20") { scale = 0.28f, color = Color.white };
        fSprites.Add(_mesh);
        fSprites.Add(_markerOuter);
        fSprites.Add(_markerInner);
        if (owner != null)
        {
            Futile.stage.AddChild(_mesh);
            Futile.stage.AddChild(_markerOuter);
            Futile.stage.AddChild(_markerInner);
        }
        Refresh();
    }

    public override void Update()
    {
        base.Update();
        if (owner == null)
        {
            _held = false;
            return;
        }

        Vector2 center = absPos + new Vector2(_radius, _radius);
        float distance = Vector2.Distance(owner.mousePos, center);
        bool overRing = distance >= _innerRadius - 5f && distance <= _radius + 5f;

        if (owner.mouseClick && overRing && (owner.draggedNode == null || owner.draggedNode == this))
        {
            _held = true;
            UpdateFromMouse(center);
        }

        if (_held)
        {
            owner.draggedNode = this;
            if (owner.mouseDown)
            {
                UpdateFromMouse(center);
            }
            else
            {
                _held = false;
            }
        }
    }

    public override void Refresh()
    {
        base.Refresh();
        Vector2 center = absPos + new Vector2(_radius, _radius);
        _mesh.x = center.x;
        _mesh.y = center.y;
        UpdateMarker(center);
    }

    internal void SetHue(float hue)
    {
        float normalized = Mathf.Repeat(hue, 1f);
        if (Mathf.Abs(normalized - _hue) < 0.0001f)
        {
            return;
        }
        _hue = normalized;
        UpdateMarker(absPos + new Vector2(_radius, _radius));
    }

    private TriangleMesh CreateRingMesh()
    {
        TriangleMesh.Triangle[] triangles = new TriangleMesh.Triangle[Segments * 2];
        for (int i = 0; i < Segments; i++)
        {
            int a = i * 2;
            int b = (i + 1) * 2;
            triangles[i * 2] = new TriangleMesh.Triangle(a, a + 1, b + 1);
            triangles[i * 2 + 1] = new TriangleMesh.Triangle(a, b + 1, b);
        }

        TriangleMesh mesh = new("Futile_White", triangles, customColor: true);
        for (int i = 0; i <= Segments; i++)
        {
            float hue = i / (float)Segments;
            float angle = hue * Mathf.PI * 2f - Mathf.PI * 0.5f;
            Vector2 direction = new(Mathf.Cos(angle), Mathf.Sin(angle));
            int inner = i * 2;
            int outer = inner + 1;
            mesh.vertices[inner] = direction * _innerRadius;
            mesh.vertices[outer] = direction * _radius;
            Color color = Color.HSVToRGB(Mathf.Repeat(hue, 1f), 1f, 1f);
            mesh.verticeColors[inner] = color;
            mesh.verticeColors[outer] = color;
        }
        mesh.Refresh();
        return mesh;
    }

    private void UpdateFromMouse(Vector2 center)
    {
        Vector2 delta = owner.mousePos - center;
        float angle = Mathf.Atan2(delta.y, delta.x);
        float hue = Mathf.Repeat((angle + Mathf.PI * 0.5f) / (Mathf.PI * 2f), 1f);
        _hue = hue;
        _writeHue(hue);
        UpdateMarker(center);
    }

    private void UpdateMarker(Vector2 center)
    {
        float angle = _hue * Mathf.PI * 2f - Mathf.PI * 0.5f;
        float radius = (_innerRadius + _radius) * 0.5f;
        Vector2 p = center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
        _markerOuter.x = p.x;
        _markerOuter.y = p.y;
        _markerInner.x = p.x;
        _markerInner.y = p.y;
    }
}

internal sealed class SaturationValueControl : RectangularDevUINode
{
    private const int Grid = 12;
    private readonly Action<float, float> _writeSV;
    private readonly TriangleMesh _mesh;
    private readonly FSprite _border;
    private readonly FSprite _markerOuter;
    private readonly FSprite _markerInner;
    private float _hue;
    private float _saturation;
    private float _value;
    private bool _held;

    internal SaturationValueControl(
        DevUIOwner owner,
        string IDstring,
        DevUINode parentNode,
        Vector2 pos,
        float side,
        float hue,
        float saturation,
        float value,
        Action<float, float> writeSV)
        : base(owner, IDstring, parentNode, pos, new Vector2(side, side))
    {
        _hue = Mathf.Repeat(hue, 1f);
        _saturation = Mathf.Clamp01(saturation);
        _value = Mathf.Clamp01(value);
        _writeSV = writeSV;
        _mesh = CreateGridMesh();
        _border = new FSprite("pixel") { anchorX = 0f, anchorY = 0f, color = Color.white };
        _markerOuter = new FSprite("Circle20") { scale = 0.48f, color = Color.black };
        _markerInner = new FSprite("Circle20") { scale = 0.28f, color = Color.white };
        fSprites.Add(_border);
        fSprites.Add(_mesh);
        fSprites.Add(_markerOuter);
        fSprites.Add(_markerInner);
        if (owner != null)
        {
            Futile.stage.AddChild(_border);
            Futile.stage.AddChild(_mesh);
            Futile.stage.AddChild(_markerOuter);
            Futile.stage.AddChild(_markerInner);
        }
        RecolorMesh();
        Refresh();
    }

    public override void Update()
    {
        base.Update();
        if (owner == null)
        {
            _held = false;
            return;
        }

        if (owner.mouseClick && MouseOver && (owner.draggedNode == null || owner.draggedNode == this))
        {
            _held = true;
            UpdateFromMouse();
        }

        if (_held)
        {
            owner.draggedNode = this;
            if (owner.mouseDown)
            {
                UpdateFromMouse();
            }
            else
            {
                _held = false;
            }
        }
    }

    public override void Refresh()
    {
        base.Refresh();
        _border.x = absPos.x - 1f;
        _border.y = absPos.y - 1f;
        _border.scaleX = size.x + 2f;
        _border.scaleY = size.y + 2f;
        _mesh.x = absPos.x;
        _mesh.y = absPos.y;
        UpdateMarker();
    }

    internal void SetHSV(float hue, float saturation, float value)
    {
        float normalizedHue = Mathf.Repeat(hue, 1f);
        if (Mathf.Abs(normalizedHue - _hue) > 0.0001f)
        {
            _hue = normalizedHue;
            RecolorMesh();
        }
        _saturation = Mathf.Clamp01(saturation);
        _value = Mathf.Clamp01(value);
        UpdateMarker();
    }

    private TriangleMesh CreateGridMesh()
    {
        int verticesPerSide = Grid + 1;
        TriangleMesh.Triangle[] triangles = new TriangleMesh.Triangle[Grid * Grid * 2];
        int triangle = 0;
        for (int y = 0; y < Grid; y++)
        {
            for (int x = 0; x < Grid; x++)
            {
                int a = y * verticesPerSide + x;
                int b = a + 1;
                int c = a + verticesPerSide;
                int d = c + 1;
                triangles[triangle++] = new TriangleMesh.Triangle(a, b, d);
                triangles[triangle++] = new TriangleMesh.Triangle(a, d, c);
            }
        }

        TriangleMesh mesh = new("Futile_White", triangles, customColor: true);
        for (int y = 0; y <= Grid; y++)
        {
            for (int x = 0; x <= Grid; x++)
            {
                int index = y * verticesPerSide + x;
                mesh.vertices[index] = new Vector2(size.x * x / Grid, size.y * y / Grid);
            }
        }
        mesh.Refresh();
        return mesh;
    }

    private void RecolorMesh()
    {
        int verticesPerSide = Grid + 1;
        for (int y = 0; y <= Grid; y++)
        {
            float value = y / (float)Grid;
            for (int x = 0; x <= Grid; x++)
            {
                float saturation = x / (float)Grid;
                int index = y * verticesPerSide + x;
                _mesh.verticeColors[index] = Color.HSVToRGB(_hue, saturation, value);
            }
        }
        _mesh.Refresh();
    }

    private void UpdateFromMouse()
    {
        _saturation = Mathf.InverseLerp(absPos.x, absPos.x + size.x, owner.mousePos.x);
        _value = Mathf.InverseLerp(absPos.y, absPos.y + size.y, owner.mousePos.y);
        _writeSV(_saturation, _value);
        UpdateMarker();
    }

    private void UpdateMarker()
    {
        Vector2 p = absPos + new Vector2(_saturation * size.x, _value * size.y);
        _markerOuter.x = p.x;
        _markerOuter.y = p.y;
        _markerInner.x = p.x;
        _markerInner.y = p.y;
    }
}
