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
        RGBClipboardBridge.SetText(RGBColorEffectEditor.ToHex(StoredColor));
    }

    internal static bool TryPaste(out Color color)
    {
        string text = RGBClipboardBridge.GetText();
        if (RGBColorEffectEditor.TryParseHex(text, out color))
        {
            StoredColor = Opaque(color);
            HasColor = true;
            return true;
        }

        color = StoredColor;
        return HasColor;
    }

    private static Color Opaque(Color color) => new(color.r, color.g, color.b, 1f);
}

internal sealed class RGBColorEffectEditor : Panel
{
    private const float ExpandedWidth = 396f;
    private const float ExpandedHeight = 522f;
    private const float CollapsedWidth = 272f;
    private const float CollapsedHeight = 28f;
    private const float ChannelTrackWidth = 216f;
    private const float ChannelTrackX = 118f;
    private const float RingCenterX = 197f;
    private const float RingCenterY = 202f;
    private const float RingOuterRadius = 88f;
    private const float RingInnerRadius = 67f;
    private const float SVSize = 108f;
    private const float SVHalf = SVSize * 0.5f;
    private const int RingSegments = 96;
    private const int SVGrid = 12;

    private static RGBColorEffectEditor _pointerCapture;

    private readonly RoomSettings.RoomEffect _effect;
    private readonly bool _replaceA;
    private readonly bool _readOnly;
    private readonly Color _previousColor;
    private readonly FSprite _previousSwatch;
    private readonly FSprite _currentSwatch;
    private readonly FLabel _collapsedColorLabel;
    private readonly DryCycleIntegerField _rField;
    private readonly DryCycleIntegerField _gField;
    private readonly DryCycleIntegerField _bField;
    private readonly DryCycleIntegerField _hField;
    private readonly DryCycleIntegerField _sField;
    private readonly DryCycleIntegerField _vField;
    private readonly DryCycleTextField _hexField;
    private readonly FSprite[] _rTrack = new FSprite[2];
    private readonly FSprite[] _gTrack = new FSprite[2];
    private readonly FSprite[] _bTrack = new FSprite[2];
    private readonly FSprite[] _channelNubs = new FSprite[3];
    private readonly TriangleMesh _hueRing;
    private readonly TriangleMesh _svMesh;
    private readonly FSprite _hueOuterCursor;
    private readonly FSprite _hueInnerCursor;
    private readonly FSprite _svCursorHorizontal;
    private readonly FSprite _svCursorVertical;
    private readonly SimpleButton _resetButton;
    private readonly SimpleButton _copyButton;
    private readonly SimpleButton _pasteButton;
    private readonly SimpleButton _collapseButton;
    private readonly PositionedDevUINode[] _expandedNodes;

    private Color _color;
    private float _hue;
    private float _saturation;
    private float _value;
    private bool _synchronizing;
    private bool _collapsed;
    private CaptureMode _captureMode;

    internal RGBColorEffectEditor(
        DevUIOwner owner,
        string IDstring,
        DevUINode parentNode,
        Vector2 pos,
        RoomSettings.RoomEffect effect,
        bool replaceA,
        bool readOnly)
        : base(owner, IDstring, parentNode, pos, new Vector2(ExpandedWidth, ExpandedHeight), effect.type.ToString())
    {
        _effect = effect ?? throw new ArgumentNullException(nameof(effect));
        _replaceA = replaceA;
        _readOnly = readOnly;
        _color = ReadColor(effect);
        _previousColor = _color;
        Color.RGBToHSV(_color, out _hue, out _saturation, out _value);

        _previousSwatch = MakePixel(new Vector2(20f, 442f), new Vector2(72f, 42f));
        _currentSwatch = MakePixel(new Vector2(112f, 442f), new Vector2(72f, 42f));
        _collapsedColorLabel = new FLabel(Custom.GetFont(), string.Empty)
        {
            anchorX = 0f,
            anchorY = 0f
        };
        Futile.stage.AddChild(_collapsedColorLabel);

        AddLabel("PREVIOUS", new Vector2(20f, 489f), 72f);
        AddLabel("CURRENT", new Vector2(112f, 489f), 72f);

        _rField = MakeChannelField("R", 390f, 0);
        _gField = MakeChannelField("G", 350f, 1);
        _bField = MakeChannelField("B", 310f, 2);

        AddChannelTrack(_rTrack, 390f, 0);
        AddChannelTrack(_gTrack, 350f, 1);
        AddChannelTrack(_bTrack, 310f, 2);

        AddLabel("HEX", new Vector2(20f, 272f), 44f);
        _hexField = new DryCycleTextField(
            owner,
            IDstring + "_Hex",
            this,
            new Vector2(68f, 272f),
            116f,
            ToHex(_color),
            text => TryParseHex(text, out _) ? DryCycleTextValidationState.Valid : DryCycleTextValidationState.Invalid,
            c => char.IsDigit(c) || c == '#' || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'),
            maxLength: 7,
            selectAllOnFocus: true);
        _hexField.AcceptedTextChanged += (_, text, __) =>
        {
            if (!_synchronizing && TryParseHex(text, out Color parsed))
            {
                SetColor(parsed, write: true);
            }
        };
        subNodes.Add(_hexField);

        _hueRing = BuildHueRing();
        _svMesh = BuildSVField();
        _hueOuterCursor = MakeCursor("pixel", 5f, 5f);
        _hueInnerCursor = MakeCursor("pixel", 3f, 3f);
        _svCursorHorizontal = MakeCursor("pixel", SVSize + 8f, 1f);
        _svCursorVertical = MakeCursor("pixel", 1f, SVSize + 8f);

        _hField = MakeHSVField("H", 25f, 0, 359);
        _sField = MakeHSVField("S", 25f, 1, 100);
        _vField = MakeHSVField("V", 25f, 2, 100);
        _hField.Move(new Vector2(20f, 25f));
        _sField.Move(new Vector2(137f, 25f));
        _vField.Move(new Vector2(254f, 25f));

        _resetButton = AddButton("RESET", IDstring + "_Reset", new Vector2(20f, 0f), 62f);
        _copyButton = AddButton("COPY", IDstring + "_Copy", new Vector2(88f, 0f), 62f);
        _pasteButton = AddButton("PASTE", IDstring + "_Paste", new Vector2(156f, 0f), 62f);
        _collapseButton = AddButton("EXPAND", IDstring + "_Collapse", new Vector2(ExpandedWidth - 82f, ExpandedHeight - 24f), 62f);

        _expandedNodes = new PositionedDevUINode[]
        {
            _rField, _gField, _bField, _hexField, _hField, _sField, _vField,
            _resetButton, _copyButton, _pasteButton
        };

        ApplyCollapsedState(collapsed: true, persist: false);
        RefreshAllVisuals();
    }

    public override void Update()
    {
        base.Update();

        if (_effect == null)
        {
            return;
        }

        if (_collapsed)
        {
            UpdateCollapsedLabel();
            return;
        }

        if (!_readOnly)
        {
            HandlePointerInput();
        }

        Color authoritative = ReadColor(_effect);
        if (!Approximately(authoritative, _color) && _pointerCapture != this)
        {
            SetColor(authoritative, write: false);
        }

        RefreshAllVisuals();
    }

    public override void Signal(DevUISignalType type, DevUINode sender, string message)
    {
        base.Signal(type, sender, message);

        if (sender == _collapseButton)
        {
            ApplyCollapsedState(!_collapsed, persist: true);
            return;
        }

        if (_readOnly)
        {
            return;
        }

        if (sender == _resetButton)
        {
            SetColor(_previousColor, write: true);
        }
        else if (sender == _copyButton)
        {
            RGBColorClipboard.Copy(_color);
        }
        else if (sender == _pasteButton && RGBColorClipboard.TryPaste(out Color color))
        {
            SetColor(color, write: true);
        }
    }

    public override void Refresh()
    {
        base.Refresh();
        ApplyCollapsedGeometry();
        RefreshAllVisuals();
    }

    public override void ClearSprites()
    {
        if (_pointerCapture == this)
        {
            _pointerCapture = null;
        }

        _collapsedColorLabel.RemoveFromContainer();
        base.ClearSprites();
    }

    internal void RestorePersistedCollapsedState(bool? collapsed)
    {
        ApplyCollapsedState(collapsed ?? true, persist: false);
    }

    private void ApplyCollapsedState(bool collapsed, bool persist)
    {
        if (_collapsed == collapsed && !persist)
        {
            ApplyCollapsedGeometry();
            return;
        }

        _collapsed = collapsed;
        _collapseButton.Text = collapsed ? "EXPAND" : "COLLAPSE";

        if (_pointerCapture == this)
        {
            _pointerCapture = null;
        }

        ApplyCollapsedGeometry();
        RefreshAllVisuals();

        if (persist)
        {
            RGBEffectEditorPanelState.SetCollapsed(_effect, collapsed);
        }
    }

    private void ApplyCollapsedGeometry()
    {
        size = _collapsed
            ? new Vector2(CollapsedWidth, CollapsedHeight)
            : new Vector2(ExpandedWidth, ExpandedHeight);

        _collapseButton.Move(_collapsed
            ? new Vector2(CollapsedWidth - 82f, 2f)
            : new Vector2(ExpandedWidth - 82f, ExpandedHeight - 24f));

        bool visible = !_collapsed;
        SetNodeVisible(_rField, visible);
        SetNodeVisible(_gField, visible);
        SetNodeVisible(_bField, visible);
        SetNodeVisible(_hexField, visible);
        SetNodeVisible(_hField, visible);
        SetNodeVisible(_sField, visible);
        SetNodeVisible(_vField, visible);
        SetNodeVisible(_resetButton, visible);
        SetNodeVisible(_copyButton, visible);
        SetNodeVisible(_pasteButton, visible);

        SetSpriteVisible(_previousSwatch, visible);
        SetSpriteVisible(_currentSwatch, visible);
        SetSpriteVisible(_hueRing, visible);
        SetSpriteVisible(_svMesh, visible);
        SetSpriteVisible(_hueOuterCursor, visible);
        SetSpriteVisible(_hueInnerCursor, visible);
        SetSpriteVisible(_svCursorHorizontal, visible);
        SetSpriteVisible(_svCursorVertical, visible);
        for (int i = 0; i < 3; i++)
        {
            SetSpriteVisible(_channelNubs[i], visible);
        }
        SetTrackVisible(_rTrack, visible);
        SetTrackVisible(_gTrack, visible);
        SetTrackVisible(_bTrack, visible);

        for (int i = 0; i < fLabels.Count; i++)
        {
            if (fLabels[i] != null)
            {
                fLabels[i].isVisible = visible || i == 0;
            }
        }

        _collapsedColorLabel.isVisible = _collapsed;
        UpdateCollapsedLabel();
    }

    private void UpdateCollapsedLabel()
    {
        _collapsedColorLabel.text = ToHex(_color);
        _collapsedColorLabel.color = _color;
        _collapsedColorLabel.x = absPos.x + 8f;
        _collapsedColorLabel.y = absPos.y + 4f;
    }

    private static void SetNodeVisible(PositionedDevUINode node, bool visible)
    {
        if (node == null)
        {
            return;
        }

        if (node.fLabels != null)
        {
            for (int i = 0; i < node.fLabels.Count; i++)
            {
                node.fLabels[i].isVisible = visible;
            }
        }
        if (node.fSprites != null)
        {
            for (int i = 0; i < node.fSprites.Count; i++)
            {
                node.fSprites[i].isVisible = visible;
            }
        }
    }

    private static void SetSpriteVisible(FNode sprite, bool visible)
    {
        if (sprite is FSprite fs)
        {
            fs.isVisible = visible;
        }
        else if (sprite is TriangleMesh mesh)
        {
            mesh.isVisible = visible;
        }
    }

    private static void SetTrackVisible(FSprite[] track, bool visible)
    {
        if (track == null)
        {
            return;
        }
        for (int i = 0; i < track.Length; i++)
        {
            if (track[i] != null)
            {
                track[i].isVisible = visible;
            }
        }
    }

    private DryCycleIntegerField MakeChannelField(string label, float y, int channel)
    {
        AddLabel(label, new Vector2(20f, y), 16f);
        DryCycleIntegerField field = new(
            owner,
            IDstring + "_" + label,
            this,
            new Vector2(42f, y),
            58f,
            GetChannel255(_color, channel),
            0,
            255,
            readValue: () => GetChannel255(_color, channel),
            writeValue: value =>
            {
                if (_readOnly || _synchronizing)
                {
                    return;
                }
                SetChannel(channel, value / 255f);
            });
        subNodes.Add(field);
        return field;
    }

    private DryCycleIntegerField MakeHSVField(string label, float y, int channel, int max)
    {
        DryCycleIntegerField field = new(
            owner,
            IDstring + "_" + label,
            this,
            Vector2.zero,
            58f,
            channel switch
            {
                0 => HueDegrees,
                1 => Mathf.RoundToInt(_saturation * 100f),
                _ => Mathf.RoundToInt(_value * 100f)
            },
            0,
            max,
            readValue: () => channel switch
            {
                0 => HueDegrees,
                1 => Mathf.RoundToInt(_saturation * 100f),
                _ => Mathf.RoundToInt(_value * 100f)
            },
            writeValue: value =>
            {
                if (_readOnly || _synchronizing)
                {
                    return;
                }
                if (channel == 0)
                {
                    _hue = Mathf.Repeat(value / 360f, 1f);
                }
                else if (channel == 1)
                {
                    _saturation = Mathf.Clamp01(value / 100f);
                }
                else
                {
                    _value = Mathf.Clamp01(value / 100f);
                }
                SetColor(Color.HSVToRGB(_hue, _saturation, _value), write: true);
            });
        subNodes.Add(field);
        AddLabel(label, new Vector2(field.pos.x - 18f, field.pos.y), 16f);
        return field;
    }

    private void AddChannelTrack(FSprite[] track, float y, int channel)
    {
        track[0] = MakePixel(new Vector2(ChannelTrackX, y + 7f), new Vector2(ChannelTrackWidth, 2f));
        track[0].color = new Color(0.16f, 0.16f, 0.16f);
        track[1] = MakePixel(new Vector2(ChannelTrackX, y + 7f), new Vector2(ChannelTrackWidth, 2f));
        _channelNubs[channel] = MakeCursor("pixel", 3f, 13f);
    }

    private void HandlePointerInput()
    {
        if (owner == null)
        {
            return;
        }

        Vector2 mouse = owner.mousePos;
        if (owner.mouseClick && _pointerCapture == null)
        {
            if (PointInChannelTrack(mouse, 0))
            {
                _pointerCapture = this;
                _captureMode = CaptureMode.Red;
            }
            else if (PointInChannelTrack(mouse, 1))
            {
                _pointerCapture = this;
                _captureMode = CaptureMode.Green;
            }
            else if (PointInChannelTrack(mouse, 2))
            {
                _pointerCapture = this;
                _captureMode = CaptureMode.Blue;
            }
            else
            {
                Vector2 local = mouse - absPos;
                Vector2 delta = local - new Vector2(RingCenterX, RingCenterY);
                float radius = delta.magnitude;
                if (radius >= RingInnerRadius && radius <= RingOuterRadius)
                {
                    _pointerCapture = this;
                    _captureMode = CaptureMode.Hue;
                }
                else if (Mathf.Abs(delta.x) <= SVHalf && Mathf.Abs(delta.y) <= SVHalf)
                {
                    _pointerCapture = this;
                    _captureMode = CaptureMode.SV;
                }
            }
        }

        if (_pointerCapture != this)
        {
            return;
        }

        if (!owner.mouseDown)
        {
            _pointerCapture = null;
            _captureMode = CaptureMode.None;
            return;
        }

        switch (_captureMode)
        {
            case CaptureMode.Red:
                SetChannel(0, ChannelValueAtMouse(mouse));
                break;
            case CaptureMode.Green:
                SetChannel(1, ChannelValueAtMouse(mouse));
                break;
            case CaptureMode.Blue:
                SetChannel(2, ChannelValueAtMouse(mouse));
                break;
            case CaptureMode.Hue:
                Vector2 delta = mouse - (absPos + new Vector2(RingCenterX, RingCenterY));
                _hue = Mathf.Repeat(Mathf.Atan2(delta.y, delta.x) / (Mathf.PI * 2f), 1f);
                SetColor(Color.HSVToRGB(_hue, _saturation, _value), write: true);
                break;
            case CaptureMode.SV:
                Vector2 local = mouse - (absPos + new Vector2(RingCenterX, RingCenterY));
                _saturation = Mathf.Clamp01((local.x + SVHalf) / SVSize);
                _value = Mathf.Clamp01((local.y + SVHalf) / SVSize);
                SetColor(Color.HSVToRGB(_hue, _saturation, _value), write: true);
                break;
        }
    }

    private bool PointInChannelTrack(Vector2 mouse, int channel)
    {
        float y = channel switch
        {
            0 => 390f,
            1 => 350f,
            _ => 310f
        };
        Vector2 local = mouse - absPos;
        return local.x >= ChannelTrackX - 5f
            && local.x <= ChannelTrackX + ChannelTrackWidth + 5f
            && local.y >= y - 5f
            && local.y <= y + 17f;
    }

    private float ChannelValueAtMouse(Vector2 mouse)
    {
        float localX = mouse.x - absPos.x;
        return Mathf.InverseLerp(ChannelTrackX, ChannelTrackX + ChannelTrackWidth, localX);
    }

    private void SetChannel(int channel, float value)
    {
        value = Mathf.Clamp01(value);
        Color next = _color;
        if (channel == 0)
        {
            next.r = value;
        }
        else if (channel == 1)
        {
            next.g = value;
        }
        else
        {
            next.b = value;
        }
        SetColor(next, write: true);
    }

    private void SetColor(Color color, bool write)
    {
        color.a = 1f;
        _color = color;
        Color.RGBToHSV(color, out _hue, out _saturation, out _value);

        if (write && !_readOnly)
        {
            RGBEffectRuntime.WriteColor(_effect, color);
            RGBEffectRuntime.ApplyCurrentRoom(owner?.room);
        }

        SynchronizeFields();
        RefreshAllVisuals();
    }

    private void SynchronizeFields()
    {
        _synchronizing = true;
        try
        {
            _rField?.SetValue(GetChannel255(_color, 0), notify: false);
            _gField?.SetValue(GetChannel255(_color, 1), notify: false);
            _bField?.SetValue(GetChannel255(_color, 2), notify: false);
            _hField?.SetValue(HueDegrees, notify: false);
            _sField?.SetValue(Mathf.RoundToInt(_saturation * 100f), notify: false);
            _vField?.SetValue(Mathf.RoundToInt(_value * 100f), notify: false);
            _hexField?.SetValue(ToHex(_color), notify: false);
        }
        finally
        {
            _synchronizing = false;
        }
    }

    private void RefreshAllVisuals()
    {
        if (_previousSwatch != null)
        {
            _previousSwatch.color = _previousColor;
        }
        if (_currentSwatch != null)
        {
            _currentSwatch.color = _color;
        }

        if (_collapsed)
        {
            UpdateCollapsedLabel();
            return;
        }

        RefreshChannelTrack(0, _rTrack, _color.r, Color.red);
        RefreshChannelTrack(1, _gTrack, _color.g, Color.green);
        RefreshChannelTrack(2, _bTrack, _color.b, Color.blue);
        RefreshHueRingCursor();
        RefreshSVField();
    }

    private void RefreshChannelTrack(int channel, FSprite[] track, float value, Color tint)
    {
        track[0].x = absPos.x + ChannelTrackX;
        track[0].y = absPos.y + (channel == 0 ? 397f : channel == 1 ? 357f : 317f);
        track[0].scaleX = ChannelTrackWidth;
        track[1].x = track[0].x;
        track[1].y = track[0].y;
        track[1].scaleX = ChannelTrackWidth * value;
        track[1].color = tint;

        FSprite nub = _channelNubs[channel];
        nub.x = absPos.x + ChannelTrackX + ChannelTrackWidth * value;
        nub.y = track[0].y;
    }

    private void RefreshHueRingCursor()
    {
        float angle = _hue * Mathf.PI * 2f;
        Vector2 dir = new(Mathf.Cos(angle), Mathf.Sin(angle));
        Vector2 center = absPos + new Vector2(RingCenterX, RingCenterY);
        _hueOuterCursor.SetPosition(center + dir * RingOuterRadius);
        _hueInnerCursor.SetPosition(center + dir * RingInnerRadius);
    }

    private void RefreshSVField()
    {
        Color hueColor = Color.HSVToRGB(_hue, 1f, 1f);
        int stride = SVGrid + 1;
        for (int y = 0; y <= SVGrid; y++)
        {
            float v = y / (float)SVGrid;
            for (int x = 0; x <= SVGrid; x++)
            {
                float s = x / (float)SVGrid;
                _svMesh.verticeColors[y * stride + x] = Color.HSVToRGB(_hue, s, v);
            }
        }

        _svMesh.Refresh();
        Vector2 cursor = absPos + new Vector2(
            RingCenterX - SVHalf + _saturation * SVSize,
            RingCenterY - SVHalf + _value * SVSize);
        _svCursorHorizontal.SetPosition(cursor);
        _svCursorVertical.SetPosition(cursor);
    }

    private TriangleMesh BuildHueRing()
    {
        int verts = RingSegments * 2;
        TriangleMesh.Triangle[] tris = new TriangleMesh.Triangle[RingSegments * 2];
        for (int i = 0; i < RingSegments; i++)
        {
            int next = (i + 1) % RingSegments;
            int o0 = i * 2;
            int i0 = o0 + 1;
            int o1 = next * 2;
            int i1 = o1 + 1;
            tris[i * 2] = new TriangleMesh.Triangle(o0, o1, i0);
            tris[i * 2 + 1] = new TriangleMesh.Triangle(i0, o1, i1);
        }

        TriangleMesh mesh = new("Futile_White", tris, customColor: true, customUV: false);
        fSprites.Add(mesh);
        Futile.stage.AddChild(mesh);
        Vector2 center = absPos + new Vector2(RingCenterX, RingCenterY);
        for (int i = 0; i < RingSegments; i++)
        {
            float h = i / (float)RingSegments;
            float a = h * Mathf.PI * 2f;
            Vector2 dir = new(Mathf.Cos(a), Mathf.Sin(a));
            mesh.vertices[i * 2] = center + dir * RingOuterRadius;
            mesh.vertices[i * 2 + 1] = center + dir * RingInnerRadius;
            Color c = Color.HSVToRGB(h, 1f, 1f);
            mesh.verticeColors[i * 2] = c;
            mesh.verticeColors[i * 2 + 1] = c;
        }
        mesh.Refresh();
        return mesh;
    }

    private TriangleMesh BuildSVField()
    {
        int stride = SVGrid + 1;
        int vertCount = stride * stride;
        TriangleMesh.Triangle[] tris = new TriangleMesh.Triangle[SVGrid * SVGrid * 2];
        int tri = 0;
        for (int y = 0; y < SVGrid; y++)
        {
            for (int x = 0; x < SVGrid; x++)
            {
                int a = y * stride + x;
                int b = a + 1;
                int c = a + stride;
                int d = c + 1;
                tris[tri++] = new TriangleMesh.Triangle(a, b, c);
                tris[tri++] = new TriangleMesh.Triangle(c, b, d);
            }
        }

        TriangleMesh mesh = new("Futile_White", tris, customColor: true, customUV: false);
        fSprites.Add(mesh);
        Futile.stage.AddChild(mesh);
        Vector2 origin = absPos + new Vector2(RingCenterX - SVHalf, RingCenterY - SVHalf);
        for (int y = 0; y <= SVGrid; y++)
        {
            for (int x = 0; x <= SVGrid; x++)
            {
                mesh.vertices[y * stride + x] = origin + new Vector2(x * SVSize / SVGrid, y * SVSize / SVGrid);
            }
        }
        mesh.Refresh();
        return mesh;
    }

    private FSprite MakePixel(Vector2 pos, Vector2 size)
    {
        FSprite sprite = new("pixel")
        {
            anchorX = 0f,
            anchorY = 0f,
            x = absPos.x + pos.x,
            y = absPos.y + pos.y,
            scaleX = size.x,
            scaleY = size.y
        };
        fSprites.Add(sprite);
        Futile.stage.AddChild(sprite);
        return sprite;
    }

    private FSprite MakeCursor(string element, float width, float height)
    {
        FSprite cursor = new(element)
        {
            anchorX = 0.5f,
            anchorY = 0.5f,
            scaleX = width,
            scaleY = height,
            color = Color.white
        };
        fSprites.Add(cursor);
        Futile.stage.AddChild(cursor);
        return cursor;
    }

    private void AddLabel(string text, Vector2 pos, float width)
    {
        subNodes.Add(new DevUILabel(owner, IDstring + "_Label_" + text + "_" + pos.y.ToString(CultureInfo.InvariantCulture), this, pos, width, text));
    }

    private SimpleButton AddButton(string text, string id, Vector2 pos, float width)
    {
        SimpleButton button = new(owner, id, this, pos, width, text);
        subNodes.Add(button);
        return button;
    }

    private void ApplyCollapsedState(bool collapsed, bool persist, bool _dummy = false)
    {
        ApplyCollapsedState(collapsed, persist);
    }

    private static Color ReadColor(RoomSettings.RoomEffect effect)
    {
        return RGBEffectRuntime.ReadColor(effect);
    }

    private int HueDegrees => Mathf.RoundToInt(_hue * 359f);

    private static int GetChannel255(Color color, int channel)
    {
        float value = channel switch
        {
            0 => color.r,
            1 => color.g,
            _ => color.b
        };
        return Mathf.Clamp(Mathf.RoundToInt(value * 255f), 0, 255);
    }

    private static bool Approximately(Color a, Color b)
    {
        return Mathf.Abs(a.r - b.r) < 0.0005f
            && Mathf.Abs(a.g - b.g) < 0.0005f
            && Mathf.Abs(a.b - b.b) < 0.0005f;
    }

    internal static string ToHex(Color color)
    {
        int r = GetChannel255(color, 0);
        int g = GetChannel255(color, 1);
        int b = GetChannel255(color, 2);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    internal static bool TryParseHex(string text, out Color color)
    {
        color = Color.white;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string s = text.Trim();
        if (s.StartsWith("#", StringComparison.Ordinal))
        {
            s = s.Substring(1);
        }
        if (s.Length != 6)
        {
            return false;
        }

        if (!byte.TryParse(s.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r)
            || !byte.TryParse(s.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g)
            || !byte.TryParse(s.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
        {
            return false;
        }

        color = new Color(r / 255f, g / 255f, b / 255f, 1f);
        return true;
    }

    private enum CaptureMode
    {
        None,
        Red,
        Green,
        Blue,
        Hue,
        SV
    }
}

internal static class RGBEffectEditorPanelState
{
    private sealed class Holder
    {
        internal bool? Collapsed;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RoomSettings.RoomEffect, Holder> State = new();

    internal static bool? GetCollapsed(RoomSettings.RoomEffect effect)
    {
        return effect != null && State.TryGetValue(effect, out Holder holder)
            ? holder.Collapsed
            : null;
    }

    internal static void SetCollapsed(RoomSettings.RoomEffect effect, bool collapsed)
    {
        if (effect == null)
        {
            return;
        }
        State.GetOrCreateValue(effect).Collapsed = collapsed;
    }
}
