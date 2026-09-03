using System;
using DevInterface;
using DryCycle.DevUI.Controls;
using RWCustom;
using UnityEngine;
using DevUIOwner = DevInterface.DevUI;

namespace DryCycle.WorldLink;

internal sealed class MultiGateControllerRepresentation : PlacedObjectRepresentation
{
    private ControllerPanel _panel;
    private int _linkSprite = -1;
    private bool _panelBuildAttempted;

    private MultiGateControllerData Data => pObj?.data as MultiGateControllerData;

    internal MultiGateControllerRepresentation(DevUIOwner owner, DevUINode parent, PlacedObject pObj)
        : base(owner, "WorldLink_ControllerRep", parent, pObj, "MultiGate Controller")
    {
        defaultColor = new Color(0.82f, 0.64f, 0.2f);
        // Intentionally no child construction here. PlacedObjectRepresentation creates
        // its root label immediately in Futile; if a child constructor throws before the
        // root is attached to ObjectsPage, that label is exactly what leaks at (0,0).
    }

    public override void Update()
    {
        base.Update();
        EnsurePanel();
        if (_panel != null && _panel.dragged && Data != null) Data.PanelPos = _panel.pos;
        if (_panel != null) Refresh();
    }

    public override void Refresh()
    {
        base.Refresh();
        if (_panel != null && _linkSprite >= 0 && _linkSprite < fSprites.Count)
        {
            DrawLine(fSprites[_linkSprite], absPos, _panel.absPos, 1.2f);
        }
    }

    private void EnsurePanel()
    {
        if (_panel != null || _panelBuildAttempted) return;
        _panelBuildAttempted = true;
        if (Data == null)
        {
            Plugin.Logger?.LogError("WorldLink DevUI: MultiGateController has no MultiGateControllerData after placement; panel was not built.");
            return;
        }

        try
        {
            _panel = new ControllerPanel(owner, this, Data);
            subNodes.Add(_panel);
            _panel.BuildContents();

            _linkSprite = fSprites.Count;
            FSprite link = NewLine(defaultColor);
            fSprites.Add(link);
            owner?.placedObjectsContainer?.AddChild(link);
            Refresh();
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"WorldLink DevUI: controller panel build failed; root representation remains usable: {ex}");
            if (_panel != null)
            {
                _panel.ClearSprites();
                subNodes.Remove(_panel);
                _panel = null;
            }
        }
    }

    internal static FSprite NewLine(Color color) => new("pixel") { anchorY = 0f, color = color, alpha = 0.8f };

    internal static void DrawLine(FSprite sprite, Vector2 a, Vector2 b, float width)
    {
        if (sprite == null) return;
        Vector2 d = b - a;
        sprite.x = a.x;
        sprite.y = a.y;
        sprite.rotation = d.sqrMagnitude > 0.001f ? Custom.VecToDeg(d) : 0f;
        sprite.scaleY = d.magnitude;
        sprite.scaleX = width;
        sprite.isVisible = d.sqrMagnitude > 0.001f;
    }

    private sealed class ControllerPanel : Panel
    {
        private readonly MultiGateControllerData _data;
        internal DryCycleTextField GateId;

        internal ControllerPanel(DevUIOwner owner, DevUINode parent, MultiGateControllerData data)
            : base(owner, "WorldLink_ControllerPanel", parent, data.PanelPos, new Vector2(260f, 55f), "WorldLink Controller")
        {
            _data = data;
        }

        internal void BuildContents()
        {
            subNodes.Add(new DevUILabel(owner, "WorldLink_Controller_GateLabel", this, new Vector2(8f, 8f), 70f, "Gate ID"));
            GateId = new DryCycleTextField(owner, "WorldLink_Controller_Gate", this, new Vector2(82f, 8f), 165f,
                _data.GateId, ValidateId, IsIdChar, maxLength: 48, selectAllOnFocus: true);
            GateId.AcceptedTextChanged += (_, text, __) => _data.GateId = MultiGateControllerData.SafeId(text, "MainGate");
            subNodes.Add(GateId);
        }

        private static DryCycleTextValidationState ValidateId(string text) =>
            string.IsNullOrWhiteSpace(text) ? DryCycleTextValidationState.Intermediate : DryCycleTextValidationState.Valid;

        private static bool IsIdChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.';
    }
}

internal sealed class MultiGatePortRepresentation : PlacedObjectRepresentation, IDevUISignals
{
    private static readonly Color GeometryColor = new(0.2f, 0.78f, 1f);
    private static readonly Color TriggerColor = new(1f, 0.7f, 0.15f);
    private static readonly Color GlyphColor = new(0.9f, 0.3f, 0.9f);
    private static readonly Color MapColor = new(0.35f, 1f, 0.45f);

    private PortHandle _direction;
    private PortHandle _widthA;
    private PortHandle _widthB;
    private PortHandle _trigger;
    private PortHandle _glyph;
    private PortHandle _mapAnchor;
    private PortHandle _mapDirection;
    private PortHandle _mapGlyph;
    private PortPanel _panel;
    private int _lineStart = -1;
    private bool _geometryBuildAttempted;
    private bool _panelBuildAttempted;

    private MultiGatePortData Data => pObj?.data as MultiGatePortData;

    internal MultiGatePortRepresentation(DevUIOwner owner, DevUINode parent, PlacedObject pObj)
        : base(owner, "WorldLink_PortRep", parent, pObj, "MultiGate Port")
    {
        defaultColor = GeometryColor;
        // Child construction is deferred until Update; see controller representation.
    }

    public override void Update()
    {
        base.Update();
        EnsureGeometry();
        if (!_geometryBuildAttempted || _direction == null) return;

        // Delay the large panel by one update after geometry. If any complex numeric/text
        // editor fails, mapper-critical geometry handles still survive and remain usable.
        EnsurePanel();

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
        if (_glyph.dragged) d.GlyphOffset = d.ToGateLocal(_glyph.pos);
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
        if (_panel != null && _panel.dragged) d.PanelPos = _panel.pos;

        _panel?.RefreshButtons(d);
        _panel?.RefreshSupport(owner?.room, pObj);
        Refresh();
    }

    public override void Refresh()
    {
        base.Refresh();
        MultiGatePortData d = Data;
        if (d == null || _direction == null) return;

        Vector2 n = d.Normal;
        Vector2 t = d.Tangent;
        if (!_direction.dragged) _direction.Move(n * 80f);
        if (!_widthA.dragged) _widthA.Move(t * d.PassageWidth * 0.5f);
        if (!_widthB.dragged) _widthB.Move(-t * d.PassageWidth * 0.5f);
        if (!_trigger.dragged) _trigger.Move(-n * d.TriggerDepth);
        if (!_glyph.dragged) _glyph.Move(d.GlyphWorldOffset);
        if (!_mapAnchor.dragged) _mapAnchor.Move(d.MapAnchorOffset);
        if (!_mapDirection.dragged) _mapDirection.Move(d.MapAnchorOffset + d.EffectiveMapDirection * 70f);
        if (!_mapGlyph.dragged) _mapGlyph.Move(d.MapAnchorOffset + d.MapGlyphOffset);

        if (_lineStart < 0 || _lineStart + 7 >= fSprites.Count) return;
        int i = _lineStart;
        Line(i++, absPos + _widthA.pos, absPos + _widthB.pos, 2f);
        Line(i++, absPos, absPos + _direction.pos, 1.5f);
        Line(i++, absPos + _widthA.pos - n * d.TriggerDepth, absPos + _widthB.pos - n * d.TriggerDepth, 1f);
        Line(i++, absPos + _trigger.pos, absPos, 1f);
        Line(i++, absPos, absPos + _glyph.pos, 1f);
        Line(i++, absPos, absPos + _mapAnchor.pos, 1f);
        Line(i++, absPos + _mapAnchor.pos, absPos + _mapDirection.pos, 1.5f);
        Line(i, absPos + _mapAnchor.pos, absPos + _mapGlyph.pos, 1f);
    }

    private void EnsureGeometry()
    {
        if (_geometryBuildAttempted) return;
        _geometryBuildAttempted = true;
        MultiGatePortData data = Data;
        if (data == null)
        {
            Plugin.Logger?.LogError("WorldLink DevUI: MultiGatePort has no MultiGatePortData after placement; geometry editor was not built.");
            return;
        }

        try
        {
            Vector2 n = data.Normal;
            Vector2 t = data.Tangent;
            _direction = H("Dir", n * 80f, GeometryColor);
            _widthA = H("WidthA", t * data.PassageWidth * 0.5f, GeometryColor);
            _widthB = H("WidthB", -t * data.PassageWidth * 0.5f, GeometryColor);
            _trigger = H("Trigger", -n * data.TriggerDepth, TriggerColor);
            _glyph = H("Glyph", data.GlyphWorldOffset, GlyphColor);
            _mapAnchor = H("MapAnchor", data.MapAnchorOffset, MapColor);
            _mapDirection = H("MapDir", data.MapAnchorOffset + data.EffectiveMapDirection * 70f, MapColor);
            _mapGlyph = H("MapGlyph", data.MapAnchorOffset + data.MapGlyphOffset, MapColor);

            _lineStart = fSprites.Count;
            for (int i = 0; i < 8; i++)
            {
                FSprite line = MultiGateControllerRepresentation.NewLine(i < 4 ? GeometryColor : (i == 4 ? TriggerColor : MapColor));
                fSprites.Add(line);
                owner?.placedObjectsContainer?.AddChild(line);
            }
            Refresh();
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"WorldLink DevUI: port geometry editor build failed: {ex}");
        }
    }

    private void EnsurePanel()
    {
        if (_panel != null || _panelBuildAttempted) return;
        _panelBuildAttempted = true;
        MultiGatePortData data = Data;
        if (data == null) return;

        try
        {
            _panel = new PortPanel(owner, this, data);
            subNodes.Add(_panel);
            _panel.BuildContents();
            _panel.RefreshSupport(owner?.room, pObj);
            Refresh();
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"WorldLink DevUI: port parameter panel build failed; geometry handles remain available: {ex}");
            if (_panel != null)
            {
                _panel.ClearSprites();
                subNodes.Remove(_panel);
                _panel = null;
            }
        }
    }

    private PortHandle H(string id, Vector2 pos, Color color)
    {
        var handle = new PortHandle(owner, "WorldLink_" + id, this, pos, color);
        subNodes.Add(handle);
        return handle;
    }

    private void Line(int index, Vector2 a, Vector2 b, float width) =>
        MultiGateControllerRepresentation.DrawLine(fSprites[index], a, b, width);

    public void Signal(DevUISignalType type, DevUINode sender, string message)
    {
        if (type != DevUISignalType.ButtonClick || Data == null || _panel == null) return;

        if (sender == _panel.Mode)
        {
            Data.TransitMode = Data.TransitMode switch
            {
                WorldLinkTransitMode.VanillaNode => WorldLinkTransitMode.CrossRegion,
                _ => WorldLinkTransitMode.VanillaNode
            };
        }
        else if (sender == _panel.Enabled)
        {
            Data.Enabled = !Data.Enabled;
        }
        else if (sender == _panel.HideDestination)
        {
            Data.HideExternalDestinationUntilTraversed = !Data.HideExternalDestinationUntilTraversed;
        }
        else if (sender == _panel.MapDirectionMode)
        {
            if (Data.MapDirectionOverride) Data.MapDirectionOverride = false;
            else
            {
                Data.MapDirection = Data.Normal;
                Data.MapDirectionOverride = true;
            }
        }

        _panel.RefreshButtons(Data);
    }

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
        private readonly MultiGatePortData _data;
        private DevUILabel _support;

        internal DryCycleTextField GateId;
        internal DryCycleTextField PortId;
        internal DryCycleIntegerField Node;
        internal DryCycleFloatField Width;
        internal DryCycleFloatField Thickness;
        internal DryCycleFloatField Trigger;
        internal DryCycleFloatField OpenFrames;
        internal DryCycleFloatField CloseFrames;
        internal DryCycleTextField DestRegion;
        internal DryCycleTextField DestRoom;
        internal DryCycleTextField DestGate;
        internal DryCycleTextField DestPort;
        internal Button Mode;
        internal Button Enabled;
        internal Button HideDestination;
        internal Button MapDirectionMode;

        internal PortPanel(DevUIOwner owner, DevUINode parent, MultiGatePortData data)
            : base(owner, "WorldLink_PortPanel", parent, data.PanelPos, new Vector2(330f, 430f), "MultiGate Port")
        {
            _data = data;
        }

        internal void BuildContents()
        {
            int row = 386;
            GateId = IdText("Gate", row, _data.GateId, false, t => _data.GateId = MultiGateControllerData.SafeId(t, "MainGate")); row -= 24;
            PortId = IdText("Port", row, _data.PortId, false, t => _data.PortId = MultiGateControllerData.SafeId(t, "PortA")); row -= 24;
            Label("Transit", row); Mode = new Button(owner, "WorldLink_Mode", this, new Vector2(100f, row), 210f, "Mode: " + _data.TransitMode); subNodes.Add(Mode); row -= 24;
            Node = Int("Node", row, _data.VanillaNodeIndex, -1, 255, v => _data.VanillaNodeIndex = v); row -= 24;
            Width = Float("Width", row, _data.PassageWidth, 40f, 900f, v => _data.PassageWidth = v); row -= 24;
            Thickness = Float("Thickness", row, _data.PanelThickness, 2f, 60f, v => _data.PanelThickness = v); row -= 24;
            Trigger = Float("Trigger", row, _data.TriggerDepth, 30f, 600f, v => _data.TriggerDepth = v); row -= 24;
            OpenFrames = Float("Open frames", row, _data.OpenFrames, 15f, 600f, v => _data.OpenFrames = v); row -= 24;
            CloseFrames = Float("Close frames", row, _data.CloseFrames, 15f, 600f, v => _data.CloseFrames = v); row -= 24;
            DestRegion = IdText("Dest region", row, _data.DestinationRegion, true, t => _data.DestinationRegion = MultiGateControllerData.SafeId(t, string.Empty)); row -= 24;
            DestRoom = IdText("Dest room", row, _data.DestinationRoom, true, t => _data.DestinationRoom = MultiGateControllerData.SafeId(t, string.Empty)); row -= 24;
            DestGate = IdText("Dest gate", row, _data.DestinationGateId, true, t => _data.DestinationGateId = MultiGateControllerData.SafeId(t, string.Empty)); row -= 24;
            DestPort = IdText("Dest port", row, _data.DestinationPortId, true, t => _data.DestinationPortId = MultiGateControllerData.SafeId(t, string.Empty)); row -= 24;

            Enabled = new Button(owner, "WorldLink_Enabled", this, new Vector2(8f, row), 145f, string.Empty); subNodes.Add(Enabled);
            HideDestination = new Button(owner, "WorldLink_HideDest", this, new Vector2(165f, row), 145f, string.Empty); subNodes.Add(HideDestination); row -= 28;
            MapDirectionMode = new Button(owner, "WorldLink_MapDirMode", this, new Vector2(8f, row), 302f, string.Empty); subNodes.Add(MapDirectionMode); row -= 24;
            _support = new DevUILabel(owner, "WorldLink_FrameSupport", this, new Vector2(8f, row), 302f, "Frame support: checking...");
            subNodes.Add(_support);
            RefreshButtons(_data);
        }

        internal void RefreshButtons(MultiGatePortData data)
        {
            if (Mode != null) Mode.Text = "Mode: " + data.TransitMode;
            if (Enabled != null) Enabled.Text = data.Enabled ? "Route: ENABLED" : "Route: DISABLED";
            if (HideDestination != null) HideDestination.Text = data.HideExternalDestinationUntilTraversed ? "Map dest: HIDDEN" : "Map dest: SHOWN";
            if (MapDirectionMode != null) MapDirectionMode.Text = data.MapDirectionOverride ? "Map direction: MANUAL" : "Map direction: AUTO (physical)";
        }

        internal void RefreshSupport(Room room, PlacedObject placed)
        {
            if (_support == null || room == null || placed == null)
            {
                return;
            }

            bool a = HasJambSupport(room, placed.pos, _data, -1);
            bool b = HasJambSupport(room, placed.pos, _data, 1);
            if (a && b)
            {
                _support.Text = "Frame support: OK";
                _support.textColor = new Color(0.1f, 0.45f, 0.1f);
            }
            else
            {
                _support.Text = "Frame support: " + (!a && !b ? "MISSING A+B" : (!a ? "MISSING A" : "MISSING B"));
                _support.textColor = Color.red;
            }
        }

        private DryCycleTextField IdText(string label, int y, string value, bool allowEmpty, Action<string> write)
        {
            Label(label, y);
            var field = new DryCycleTextField(owner, "WorldLink_" + label, this, new Vector2(100f, y), 210f,
                value ?? string.Empty,
                text => string.IsNullOrWhiteSpace(text)
                    ? (allowEmpty ? DryCycleTextValidationState.Valid : DryCycleTextValidationState.Intermediate)
                    : DryCycleTextValidationState.Valid,
                c => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.',
                maxLength: 64, selectAllOnFocus: true);
            field.AcceptedTextChanged += (_, text, __) => write(text);
            subNodes.Add(field);
            return field;
        }

        private DryCycleIntegerField Int(string label, int y, int value, int min, int max, Action<int> write)
        {
            Label(label, y);
            var field = new DryCycleIntegerField(owner, "WorldLink_" + label, this, new Vector2(100f, y), 210f, value, min, max, writeValue: write);
            subNodes.Add(field);
            return field;
        }

        private DryCycleFloatField Float(string label, int y, float value, float min, float max, Action<float> write)
        {
            Label(label, y);
            var field = new DryCycleFloatField(owner, "WorldLink_" + label, this, new Vector2(100f, y), 210f, value, min, max, 2, writeValue: write);
            subNodes.Add(field);
            return field;
        }

        private void Label(string text, int y) =>
            subNodes.Add(new DevUILabel(owner, "WorldLink_Label_" + text, this, new Vector2(8f, y), 88f, text));

        private static bool HasJambSupport(Room room, Vector2 center, MultiGatePortData data, int side)
        {
            float half = data.PassageWidth * 0.5f;
            float jambThickness = Mathf.Max(18f, data.PanelThickness * 2.4f);
            float u = side * (half + jambThickness * 0.35f);
            Vector2 basePoint = center + data.Tangent * u;
            float step = Mathf.Max(4f, data.PanelThickness * 0.45f);
            for (int n = -1; n <= 1; n++)
            {
                if (room.GetTile(basePoint + data.Normal * (n * step)).Solid) return true;
            }
            return false;
        }
    }
}
