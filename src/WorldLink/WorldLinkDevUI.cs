using System;
using DevInterface;
using DryCycle.DevUI.Controls;
using RWCustom;
using UnityEngine;
using DevUIOwner = DevInterface.DevUI;

namespace DryCycle.WorldLink;

internal sealed class MultiGateControllerRepresentation : PlacedObjectRepresentation
{
    private sealed class ControllerPanel : Panel
    {
        internal readonly DryCycleTextField GateId;

        internal ControllerPanel(DevUIOwner owner, DevUINode parent, MultiGateControllerData data)
            : base(owner, "WorldLink_ControllerPanel", parent, data.PanelPos, new Vector2(260f, 55f), "WorldLink Controller")
        {
            subNodes.Add(new DevUILabel(owner, "WorldLink_Controller_GateLabel", this, new Vector2(8f, 8f), 70f, "Gate ID"));
            GateId = new DryCycleTextField(owner, "WorldLink_Controller_Gate", this, new Vector2(82f, 8f), 165f,
                data.GateId, ValidateId, IsIdChar, maxLength: 48, selectAllOnFocus: true);
            GateId.AcceptedTextChanged += (_, text, __) => data.GateId = MultiGateControllerData.SafeId(text, "MainGate");
            subNodes.Add(GateId);
        }

        private static DryCycleTextValidationState ValidateId(string text) =>
            string.IsNullOrWhiteSpace(text) ? DryCycleTextValidationState.Intermediate : DryCycleTextValidationState.Valid;
        private static bool IsIdChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.';
    }

    private readonly ControllerPanel _panel;
    private readonly int _linkSprite;
    private MultiGateControllerData Data => pObj.data as MultiGateControllerData;

    internal MultiGateControllerRepresentation(DevUIOwner owner, DevUINode parent, PlacedObject pObj)
        : base(owner, "WorldLink_ControllerRep", parent, pObj, "MultiGate Controller")
    {
        defaultColor = new Color(0.82f, 0.64f, 0.2f);
        _panel = new ControllerPanel(owner, this, Data);
        subNodes.Add(_panel);
        _linkSprite = fSprites.Count;
        FSprite link = NewLine(defaultColor);
        fSprites.Add(link);
        owner.placedObjectsContainer.AddChild(link);
        Refresh();
    }

    public override void Update()
    {
        base.Update();
        if (Data != null && _panel.dragged)
        {
            Data.PanelPos = _panel.pos;
        }
        Refresh();
    }

    public override void Refresh()
    {
        base.Refresh();
        DrawLine(fSprites[_linkSprite], absPos, _panel.absPos, 1.2f);
    }

    internal static FSprite NewLine(Color color) => new("pixel") { anchorY = 0f, color = color, alpha = 0.8f };
    internal static void DrawLine(FSprite sprite, Vector2 a, Vector2 b, float width)
    {
        Vector2 d = b - a;
        sprite.x = a.x;
        sprite.y = a.y;
        sprite.rotation = Custom.VecToDeg(d);
        sprite.scaleY = d.magnitude;
        sprite.scaleX = width;
    }
}

internal sealed class MultiGatePortRepresentation : PlacedObjectRepresentation, IDevUISignals
{
    private sealed class PortHandle : Handle
    {
        internal PortHandle(DevUIOwner owner, string id, DevUINode parent, Vector2 pos, Color color)
            : base(owner, id, parent, pos)
        {
            defaultColor = color;
            if (fSprites.Count > 0) fSprites[0].scale = 0.38f;
        }
    }

    private sealed class PortPanel : Panel
    {
        internal readonly DryCycleTextField GateId;
        internal readonly DryCycleTextField PortId;
        internal readonly DryCycleIntegerField Node;
        internal readonly DryCycleFloatField Width;
        internal readonly DryCycleFloatField Thickness;
        internal readonly DryCycleFloatField Trigger;
        internal readonly DryCycleFloatField OpenFrames;
        internal readonly DryCycleFloatField CloseFrames;
        internal readonly DryCycleTextField DestRegion;
        internal readonly DryCycleTextField DestRoom;
        internal readonly DryCycleTextField DestGate;
        internal readonly DryCycleTextField DestPort;
        internal readonly Button Mode;
        internal readonly Button Enabled;
        internal readonly Button HideDestination;
        internal readonly Button MapDirectionMode;

        internal PortPanel(DevUIOwner owner, DevUINode parent, MultiGatePortData data)
            : base(owner, "WorldLink_PortPanel", parent, data.PanelPos, new Vector2(330f, 410f), "MultiGate Port")
        {
            int row = 366;
            GateId = IdText(owner, this, "Gate", row, data.GateId, allowEmpty: false, t => data.GateId = MultiGateControllerData.SafeId(t, "MainGate")); row -= 24;
            PortId = IdText(owner, this, "Port", row, data.PortId, allowEmpty: false, t => data.PortId = MultiGateControllerData.SafeId(t, "PortA")); row -= 24;
            Mode = new Button(owner, "WorldLink_Mode", this, new Vector2(100f, row), 210f, "Mode: " + data.TransitMode); Label(owner, this, "Transit", row); subNodes.Add(Mode); row -= 24;
            Node = Int(owner, this, "Node", row, data.VanillaNodeIndex, -1, 255, v => data.VanillaNodeIndex = v); row -= 24;
            Width = Float(owner, this, "Width", row, data.PassageWidth, 40f, 900f, v => data.PassageWidth = v); row -= 24;
            Thickness = Float(owner, this, "Thickness", row, data.PanelThickness, 2f, 60f, v => data.PanelThickness = v); row -= 24;
            Trigger = Float(owner, this, "Trigger", row, data.TriggerDepth, 30f, 600f, v => data.TriggerDepth = v); row -= 24;
            OpenFrames = Float(owner, this, "Open frames", row, data.OpenFrames, 15f, 600f, v => data.OpenFrames = v); row -= 24;
            CloseFrames = Float(owner, this, "Close frames", row, data.CloseFrames, 15f, 600f, v => data.CloseFrames = v); row -= 24;
            DestRegion = IdText(owner, this, "Dest region", row, data.DestinationRegion, allowEmpty: true, t => data.DestinationRegion = MultiGateControllerData.SafeId(t, string.Empty)); row -= 24;
            DestRoom = IdText(owner, this, "Dest room", row, data.DestinationRoom, allowEmpty: true, t => data.DestinationRoom = MultiGateControllerData.SafeId(t, string.Empty)); row -= 24;
            DestGate = IdText(owner, this, "Dest gate", row, data.DestinationGateId, allowEmpty: true, t => data.DestinationGateId = MultiGateControllerData.SafeId(t, string.Empty)); row -= 24;
            DestPort = IdText(owner, this, "Dest port", row, data.DestinationPortId, allowEmpty: true, t => data.DestinationPortId = MultiGateControllerData.SafeId(t, string.Empty)); row -= 24;
            Enabled = new Button(owner, "WorldLink_Enabled", this, new Vector2(8f, row), 145f, data.Enabled ? "Enabled: YES" : "Enabled: NO"); subNodes.Add(Enabled);
            HideDestination = new Button(owner, "WorldLink_HideDest", this, new Vector2(165f, row), 145f, data.HideExternalDestinationUntilTraversed ? "Map dest: HIDDEN" : "Map dest: SHOWN"); subNodes.Add(HideDestination); row -= 28;
            MapDirectionMode = new Button(owner, "WorldLink_MapDirMode", this, new Vector2(8f, row), 302f,
                data.MapDirectionOverride ? "Map direction: MANUAL" : "Map direction: AUTO (physical)");
            subNodes.Add(MapDirectionMode);
        }

        internal void RefreshButtons(MultiGatePortData data)
        {
            Mode.Text = "Mode: " + data.TransitMode;
            Enabled.Text = data.Enabled ? "Enabled: YES" : "Enabled: NO";
            HideDestination.Text = data.HideExternalDestinationUntilTraversed ? "Map dest: HIDDEN" : "Map dest: SHOWN";
            MapDirectionMode.Text = data.MapDirectionOverride ? "Map direction: MANUAL" : "Map direction: AUTO (physical)";
        }

        private static void Label(DevUIOwner owner, DevUINode parent, string text, int y) =>
            parent.subNodes.Add(new DevUILabel(owner, "WorldLink_Label_" + text, parent, new Vector2(8f, y), 88f, text));

        private static DryCycleTextField Text(DevUIOwner owner, DevUINode parent, string label, int y, string value, Action<string> write)
        {
            Label(owner, parent, label, y);
            var field = new DryCycleTextField(owner, "WorldLink_" + label, parent, new Vector2(100f, y), 210f,
                value ?? string.Empty, _ => DryCycleTextValidationState.Valid, c => c >= 32 && c != '~', maxLength: 64, selectAllOnFocus: true);
            field.AcceptedTextChanged += (_, text, __) => write(text);
            parent.subNodes.Add(field);
            return field;
        }

        private static DryCycleTextField IdText(DevUIOwner owner, DevUINode parent, string label, int y, string value, bool allowEmpty, Action<string> write)
        {
            Label(owner, parent, label, y);
            var field = new DryCycleTextField(owner, "WorldLink_" + label, parent, new Vector2(100f, y), 210f,
                value ?? string.Empty,
                text => string.IsNullOrWhiteSpace(text)
                    ? (allowEmpty ? DryCycleTextValidationState.Valid : DryCycleTextValidationState.Intermediate)
                    : DryCycleTextValidationState.Valid,
                c => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.',
                maxLength: 64, selectAllOnFocus: true);
            field.AcceptedTextChanged += (_, text, __) => write(text);
            parent.subNodes.Add(field);
            return field;
        }

        private static DryCycleIntegerField Int(DevUIOwner owner, DevUINode parent, string label, int y, int value, int min, int max, Action<int> write)
        {
            Label(owner, parent, label, y);
            var field = new DryCycleIntegerField(owner, "WorldLink_" + label, parent, new Vector2(100f, y), 210f, value, min, max, writeValue: write);
            parent.subNodes.Add(field);
            return field;
        }

        private static DryCycleFloatField Float(DevUIOwner owner, DevUINode parent, string label, int y, float value, float min, float max, Action<float> write)
        {
            Label(owner, parent, label, y);
            var field = new DryCycleFloatField(owner, "WorldLink_" + label, parent, new Vector2(100f, y), 210f, value, min, max, 2, writeValue: write);
            parent.subNodes.Add(field);
            return field;
        }
    }

    private static readonly Color GeometryColor = new(0.2f, 0.78f, 1f);
    private static readonly Color TriggerColor = new(1f, 0.7f, 0.15f);
    private static readonly Color GlyphColor = new(0.9f, 0.3f, 0.9f);
    private static readonly Color MapColor = new(0.35f, 1f, 0.45f);

    private readonly PortHandle _direction;
    private readonly PortHandle _widthA;
    private readonly PortHandle _widthB;
    private readonly PortHandle _trigger;
    private readonly PortHandle _glyph;
    private readonly PortHandle _mapAnchor;
    private readonly PortHandle _mapDirection;
    private readonly PortHandle _mapGlyph;
    private readonly PortPanel _panel;
    private readonly int _lineStart;

    private MultiGatePortData Data => pObj.data as MultiGatePortData;

    internal MultiGatePortRepresentation(DevUIOwner owner, DevUINode parent, PlacedObject pObj)
        : base(owner, "WorldLink_PortRep", parent, pObj, "MultiGate Port")
    {
        defaultColor = GeometryColor;
        MultiGatePortData data = Data;
        Vector2 n = data.Normal;
        Vector2 t = data.Tangent;
        _direction = H("Dir", n * 80f, GeometryColor);
        _widthA = H("WidthA", t * data.PassageWidth * 0.5f, GeometryColor);
        _widthB = H("WidthB", -t * data.PassageWidth * 0.5f, GeometryColor);
        _trigger = H("Trigger", -n * data.TriggerDepth, TriggerColor);
        _glyph = H("Glyph", data.GlyphOffset, GlyphColor);
        _mapAnchor = H("MapAnchor", data.MapAnchorOffset, MapColor);
        _mapDirection = H("MapDir", data.MapAnchorOffset + data.EffectiveMapDirection * 70f, MapColor);
        _mapGlyph = H("MapGlyph", data.MapAnchorOffset + data.MapGlyphOffset, MapColor);
        _panel = new PortPanel(owner, this, data);
        subNodes.Add(_panel);

        _lineStart = fSprites.Count;
        for (int i = 0; i < 8; i++)
        {
            FSprite line = MultiGateControllerRepresentation.NewLine(i < 4 ? GeometryColor : (i == 4 ? TriggerColor : MapColor));
            fSprites.Add(line);
            owner.placedObjectsContainer.AddChild(line);
        }
        Refresh();
    }

    private PortHandle H(string id, Vector2 pos, Color c)
    {
        var h = new PortHandle(owner, "WorldLink_" + id, this, pos, c);
        subNodes.Add(h);
        return h;
    }

    public override void Update()
    {
        base.Update();
        MultiGatePortData d = Data;
        if (d == null) return;

        if (_direction.dragged)
        {
            d.Direction = _direction.pos.sqrMagnitude < 4f ? Vector2.right : _direction.pos.normalized;
        }
        Vector2 n = d.Normal;
        Vector2 t = d.Tangent;
        if (_widthA.dragged || _widthB.dragged)
        {
            float a = Mathf.Abs(Vector2.Dot(_widthA.pos, t));
            float b = Mathf.Abs(Vector2.Dot(_widthB.pos, t));
            d.PassageWidth = Mathf.Clamp(a + b, 40f, 900f);
        }
        if (_trigger.dragged)
        {
            d.TriggerDepth = Mathf.Clamp(-Vector2.Dot(_trigger.pos, n), 30f, 600f);
        }
        if (_glyph.dragged) d.GlyphOffset = _glyph.pos;
        if (_mapAnchor.dragged) d.MapAnchorOffset = _mapAnchor.pos;
        if (_mapDirection.dragged)
        {
            Vector2 md = _mapDirection.pos - d.MapAnchorOffset;
            if (md.sqrMagnitude > 4f)
            {
                d.MapDirection = md.normalized;
                d.MapDirectionOverride = true;
            }
        }
        if (_mapGlyph.dragged) d.MapGlyphOffset = _mapGlyph.pos - d.MapAnchorOffset;
        if (_panel.dragged) d.PanelPos = _panel.pos;
        Refresh();
    }

    public override void Refresh()
    {
        base.Refresh();
        MultiGatePortData d = Data;
        if (d == null) return;
        Vector2 n = d.Normal;
        Vector2 t = d.Tangent;
        if (!_direction.dragged) _direction.Move(n * 80f);
        if (!_widthA.dragged) _widthA.Move(t * d.PassageWidth * 0.5f);
        if (!_widthB.dragged) _widthB.Move(-t * d.PassageWidth * 0.5f);
        if (!_trigger.dragged) _trigger.Move(-n * d.TriggerDepth);
        if (!_glyph.dragged) _glyph.Move(d.GlyphOffset);
        if (!_mapAnchor.dragged) _mapAnchor.Move(d.MapAnchorOffset);
        if (!_mapDirection.dragged) _mapDirection.Move(d.MapAnchorOffset + d.EffectiveMapDirection * 70f);
        if (!_mapGlyph.dragged) _mapGlyph.Move(d.MapAnchorOffset + d.MapGlyphOffset);
        _panel.RefreshButtons(d);

        int i = _lineStart;
        Line(i++, absPos + _widthA.pos, absPos + _widthB.pos, 2f);
        Line(i++, absPos, absPos + _direction.pos, 1.5f);
        Line(i++, absPos + _widthA.pos - n * d.TriggerDepth, absPos + _widthB.pos - n * d.TriggerDepth, 1f);
        Line(i++, absPos + _trigger.pos, absPos, 1f);
        Line(i++, absPos, absPos + _glyph.pos, 1f);
        Line(i++, absPos, absPos + _mapAnchor.pos, 1f);
        Line(i++, absPos + _mapAnchor.pos, absPos + _mapDirection.pos, 1.5f);
        Line(i++, absPos + _mapAnchor.pos, absPos + _mapGlyph.pos, 1f);
    }

    private void Line(int index, Vector2 a, Vector2 b, float width) =>
        MultiGateControllerRepresentation.DrawLine(fSprites[index], a, b, width);

    public void Signal(DevUISignalType type, DevUINode sender, string message)
    {
        if (type != DevUISignalType.ButtonClick || Data == null) return;
        if (sender == _panel.Mode)
        {
            Data.TransitMode = Data.TransitMode switch
            {
                WorldLinkTransitMode.VanillaNode => WorldLinkTransitMode.CrossRegion,
                _ => WorldLinkTransitMode.VanillaNode
            };
        }
        else if (sender == _panel.Enabled) Data.Enabled = !Data.Enabled;
        else if (sender == _panel.HideDestination) Data.HideExternalDestinationUntilTraversed = !Data.HideExternalDestinationUntilTraversed;
        else if (sender == _panel.MapDirectionMode)
        {
            if (Data.MapDirectionOverride)
            {
                Data.MapDirectionOverride = false;
            }
            else
            {
                Data.MapDirection = Data.Normal;
                Data.MapDirectionOverride = true;
            }
        }
        _panel.RefreshButtons(Data);
    }
}
