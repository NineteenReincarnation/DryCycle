using System;
using DevInterface;
using DryCycle.DevUI.Controls;
using RWCustom;
using UnityEngine;
using DevUIOwner = DevInterface.DevUI;

namespace DryCycle.WorldLink;

internal sealed class MultiGateControllerRepresentation : PlacedObjectRepresentation
{
    private const int RetryDelayFrames = 120;

    private ControllerPanel _panel;
    private int _linkSprite = -1;
    private int _panelRetryDelay;
    private int _panelFailures;

    private MultiGateControllerData Data => pObj?.data as MultiGateControllerData;

    internal MultiGateControllerRepresentation(DevUIOwner owner, DevUINode parent, PlacedObject pObj)
        : base(owner, "WorldLink_ControllerRep", parent, pObj, "MultiGate Controller")
    {
        defaultColor = new Color(0.82f, 0.64f, 0.2f);
        // Keep the root constructor minimal. All complex widgets are attached only after
        // a complete successful build so a failing child can never orphan half a panel.
    }

    public override void Update()
    {
        base.Update();
        if (_panelRetryDelay > 0) _panelRetryDelay--;
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
        if (_panel != null || _panelRetryDelay > 0) return;
        MultiGateControllerData data = Data;
        if (data == null)
        {
            if (_panelFailures++ == 0)
                Plugin.Logger?.LogError("WorldLink DevUI: MultiGateController has no MultiGateControllerData after placement.");
            _panelRetryDelay = RetryDelayFrames;
            return;
        }

        ControllerPanel candidate = null;
        FSprite link = null;
        try
        {
            candidate = new ControllerPanel(owner, this, data);
            candidate.BuildContents();
            link = NewLine(defaultColor);

            subNodes.Add(candidate);
            _panel = candidate;
            candidate = null;

            _linkSprite = fSprites.Count;
            fSprites.Add(link);
            owner.placedObjectsContainer.AddChild(link);
            link = null;
            _panelFailures = 0;
            Refresh();
        }
        catch (Exception ex)
        {
            link?.RemoveFromContainer();
            candidate?.ClearSprites();
            _panelFailures++;
            _panelRetryDelay = RetryDelayFrames;
            Plugin.Logger?.LogError($"WorldLink DevUI: controller panel build failed transactionally (attempt {_panelFailures}): {ex}");
        }
    }

    internal static FSprite NewLine(Color color) => new("pixel")
    {
        anchorX = 0.5f,
        anchorY = 0f,
        color = color,
        alpha = 0.8f
    };

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
    private const int RetryDelayFrames = 120;
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
    private int _geometryRetryDelay;
    private int _panelRetryDelay;
    private int _geometryFailures;
    private int _panelFailures;

    private MultiGatePortData Data => pObj?.data as MultiGatePortData;

    internal MultiGatePortRepresentation(DevUIOwner owner, DevUINode parent, PlacedObject pObj)
        : base(owner, "WorldLink_PortRep", parent, pObj, "MultiGate Port")
    {
        defaultColor = GeometryColor;
    }

    public override void Update()
    {
        base.Update();
        if (_geometryRetryDelay > 0) _geometryRetryDelay--;
        if (_panelRetryDelay > 0) _panelRetryDelay--;

        EnsureGeometry();
        if (_direction == null) return;
        EnsurePanel();

        MultiGatePortData d = Data;
        if (d == null) return;

        if (_direction.dragged)
        {
            d.Direction = _direction.pos.sqrMagnitude < 4f ? Vector2.right : _direction.pos.normalized;
        }

        Vector2 n = d.Normal;
        Vector2 t = d.Tangent;
        if (_widthA.dragged)
        {
            d.PassageWidth = Mathf.Clamp(2f * Mathf.Abs(Vector2.Dot(_widthA.pos, t)), 40f, 900f);
        }
        else if (_widthB.dragged)
        {
            d.PassageWidth = Mathf.Clamp(2f * Mathf.Abs(Vector2.Dot(_widthB.pos, t)), 40f, 900f);
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
        _panel?.RefreshDiagnostics(owner?.room, pObj);
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
        if (_direction != null || _geometryRetryDelay > 0) return;
        MultiGatePortData data = Data;
        if (data == null)
        {
            if (_geometryFailures++ == 0)
                Plugin.Logger?.LogError("WorldLink DevUI: MultiGatePort has no MultiGatePortData after placement.");
            _geometryRetryDelay = RetryDelayFrames;
            return;
        }

        PortHandle[] handles = new PortHandle[8];
        FSprite[] lines = new FSprite[8];
        try
        {
            Vector2 n = data.Normal;
            Vector2 t = data.Tangent;
            handles[0] = NewHandle("Dir", n * 80f, GeometryColor);
            handles[1] = NewHandle("WidthA", t * data.PassageWidth * 0.5f, GeometryColor);
            handles[2] = NewHandle("WidthB", -t * data.PassageWidth * 0.5f, GeometryColor);
            handles[3] = NewHandle("Trigger", -n * data.TriggerDepth, TriggerColor);
            handles[4] = NewHandle("Glyph", data.GlyphWorldOffset, GlyphColor);
            handles[5] = NewHandle("MapAnchor", data.MapAnchorOffset, MapColor);
            handles[6] = NewHandle("MapDir", data.MapAnchorOffset + data.EffectiveMapDirection * 70f, MapColor);
            handles[7] = NewHandle("MapGlyph", data.MapAnchorOffset + data.MapGlyphOffset, MapColor);

            lines[0] = MultiGateControllerRepresentation.NewLine(GeometryColor);
            lines[1] = MultiGateControllerRepresentation.NewLine(GeometryColor);
            lines[2] = MultiGateControllerRepresentation.NewLine(TriggerColor);
            lines[3] = MultiGateControllerRepresentation.NewLine(TriggerColor);
            lines[4] = MultiGateControllerRepresentation.NewLine(GlyphColor);
            lines[5] = MultiGateControllerRepresentation.NewLine(MapColor);
            lines[6] = MultiGateControllerRepresentation.NewLine(MapColor);
            lines[7] = MultiGateControllerRepresentation.NewLine(MapColor);

            // Commit only after every constructor succeeded. Until this point none of
            // the handles are reachable from subNodes and none of the lines are in the
            // representation's sprite list.
            for (int i = 0; i < handles.Length; i++) subNodes.Add(handles[i]);
            _direction = handles[0];
            _widthA = handles[1];
            _widthB = handles[2];
            _trigger = handles[3];
            _glyph = handles[4];
            _mapAnchor = handles[5];
            _mapDirection = handles[6];
            _mapGlyph = handles[7];

            _lineStart = fSprites.Count;
            for (int i = 0; i < lines.Length; i++)
            {
                fSprites.Add(lines[i]);
                owner.placedObjectsContainer.AddChild(lines[i]);
                lines[i] = null;
            }

            _geometryFailures = 0;
            Refresh();
        }
        catch (Exception ex)
        {
            for (int i = 0; i < handles.Length; i++)
            {
                if (handles[i] != null && !subNodes.Contains(handles[i])) handles[i].ClearSprites();
            }
            for (int i = 0; i < lines.Length; i++) lines[i]?.RemoveFromContainer();

            // If the exception occurred during the final commit, remove every partially
            // attached child/sprite and reset all field references before retrying.
            for (int i = 0; i < handles.Length; i++)
            {
                if (handles[i] != null && subNodes.Contains(handles[i]))
                {
                    handles[i].ClearSprites();
                    subNodes.Remove(handles[i]);
                }
            }
            while (_lineStart >= 0 && fSprites.Count > _lineStart)
            {
                FSprite sprite = fSprites[fSprites.Count - 1];
                sprite?.RemoveFromContainer();
                fSprites.RemoveAt(fSprites.Count - 1);
            }

            _direction = null;
            _widthA = null;
            _widthB = null;
            _trigger = null;
            _glyph = null;
            _mapAnchor = null;
            _mapDirection = null;
            _mapGlyph = null;
            _lineStart = -1;
            _geometryFailures++;
            _geometryRetryDelay = RetryDelayFrames;
            Plugin.Logger?.LogError($"WorldLink DevUI: port geometry editor build failed transactionally (attempt {_geometryFailures}): {ex}");
        }
    }

    private void EnsurePanel()
    {
        if (_panel != null || _panelRetryDelay > 0) return;
        MultiGatePortData data = Data;
        if (data == null) return;

        PortPanel candidate = null;
        try
        {
            candidate = new PortPanel(owner, this, data);
            candidate.BuildContents();
            candidate.RefreshDiagnostics(owner?.room, pObj);
            subNodes.Add(candidate);
            _panel = candidate;
            candidate = null;
            _panelFailures = 0;
            Refresh();
        }
        catch (Exception ex)
        {
            candidate?.ClearSprites();
            _panelFailures++;
            _panelRetryDelay = RetryDelayFrames;
            Plugin.Logger?.LogError($"WorldLink DevUI: port parameter panel build failed transactionally (attempt {_panelFailures}); geometry handles remain available: {ex}");
        }
    }

    private PortHandle NewHandle(string id, Vector2 pos, Color color) =>
        new(owner, "WorldLink_" + id, this, pos, color);

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
        _panel.RefreshDiagnostics(owner?.room, pObj);
    }

    private sealed class PortHandle : Handle
    {
        internal PortHandle(DevUIOwner owner, string id, DevUINode parent, Vector2 pos, Color color)
            : base(owner, id, parent, pos)
        {
            defaultColor = color;
            if (fSprites.Count > 0)
            {
                fSprites[0].scale = 0.38f;
                fSprites[0].color = color;
            }
        }
    }

    private sealed class PortPanel : Panel
    {
        private readonly MultiGatePortData _data;
        private DevUILabel _support;
        private DevUILabel _routeStatus;

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
            : base(owner, "WorldLink_PortPanel", parent, data.PanelPos, new Vector2(330f, 454f), "MultiGate Port")
        {
            _data = data;
        }

        internal void BuildContents()
        {
            int row = 410;
            GateId = IdText("Gate", row, _data.GateId, false, t => _data.GateId = MultiGateControllerData.SafeId(t, "MainGate")); row -= 24;
            PortId = IdText("Port", row, _data.PortId, false, t => _data.PortId = MultiGateControllerData.SafeId(t, "PortA")); row -= 24;
            Label("Transit", row); Mode = new Button(owner, "WorldLink_Mode", this, new Vector2(100f, row), 210f, "Mode: " + _data.TransitMode); subNodes.Add(Mode); row -= 24;
            Node = Int("Node", row, _data.VanillaNodeIndex, -1, 255, () => _data.VanillaNodeIndex, v => _data.VanillaNodeIndex = v); row -= 24;
            Width = Float("Width", row, _data.PassageWidth, 40f, 900f, () => _data.PassageWidth, v => _data.PassageWidth = v); row -= 24;
            Thickness = Float("Thickness", row, _data.PanelThickness, 2f, 60f, () => _data.PanelThickness, v => _data.PanelThickness = v); row -= 24;
            Trigger = Float("Trigger", row, _data.TriggerDepth, 30f, 600f, () => _data.TriggerDepth, v => _data.TriggerDepth = v); row -= 24;
            OpenFrames = Float("Open frames", row, _data.OpenFrames, 15f, 600f, () => _data.OpenFrames, v => _data.OpenFrames = v); row -= 24;
            CloseFrames = Float("Close frames", row, _data.CloseFrames, 15f, 600f, () => _data.CloseFrames, v => _data.CloseFrames = v); row -= 24;
            DestRegion = IdText("Dest region", row, _data.DestinationRegion, true, t => _data.DestinationRegion = MultiGateControllerData.SafeId(t, string.Empty)); row -= 24;
            DestRoom = IdText("Dest room", row, _data.DestinationRoom, true, t => _data.DestinationRoom = MultiGateControllerData.SafeId(t, string.Empty)); row -= 24;
            DestGate = IdText("Dest gate", row, _data.DestinationGateId, true, t => _data.DestinationGateId = MultiGateControllerData.SafeId(t, string.Empty)); row -= 24;
            DestPort = IdText("Dest port", row, _data.DestinationPortId, true, t => _data.DestinationPortId = MultiGateControllerData.SafeId(t, string.Empty)); row -= 24;

            Enabled = new Button(owner, "WorldLink_Enabled", this, new Vector2(8f, row), 145f, string.Empty); subNodes.Add(Enabled);
            HideDestination = new Button(owner, "WorldLink_HideDest", this, new Vector2(165f, row), 145f, string.Empty); subNodes.Add(HideDestination); row -= 28;
            MapDirectionMode = new Button(owner, "WorldLink_MapDirMode", this, new Vector2(8f, row), 302f, string.Empty); subNodes.Add(MapDirectionMode); row -= 24;
            _routeStatus = new DevUILabel(owner, "WorldLink_RouteStatus", this, new Vector2(8f, row), 302f, "Route: checking..."); subNodes.Add(_routeStatus); row -= 24;
            _support = new DevUILabel(owner, "WorldLink_FrameSupport", this, new Vector2(8f, row), 302f, "Frame support: checking..."); subNodes.Add(_support);
            RefreshButtons(_data);
        }

        internal void RefreshButtons(MultiGatePortData data)
        {
            if (Mode != null) Mode.Text = "Mode: " + data.TransitMode;
            if (Enabled != null) Enabled.Text = data.Enabled ? "Route: ENABLED" : "Route: DISABLED";
            if (HideDestination != null) HideDestination.Text = data.HideExternalDestinationUntilTraversed ? "Map dest: HIDDEN" : "Map dest: SHOWN";
            if (MapDirectionMode != null) MapDirectionMode.Text = data.MapDirectionOverride ? "Map direction: MANUAL" : "Map direction: AUTO (physical)";
        }

        internal void RefreshDiagnostics(Room room, PlacedObject placed)
        {
            RefreshSupport(room, placed);
            RefreshRouteStatus(room, placed);
        }

        private void RefreshSupport(Room room, PlacedObject placed)
        {
            if (_support == null || room == null || placed == null) return;

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

        private void RefreshRouteStatus(Room room, PlacedObject placed)
        {
            if (_routeStatus == null || room == null || placed == null) return;

            string status;
            bool valid;
            if (!_data.Enabled)
            {
                status = "Route status: DISABLED";
                valid = false;
            }
            else if (_data.TransitMode == WorldLinkTransitMode.DirectTransit)
            {
                status = "Route status: DIRECT TRANSIT RESERVED";
                valid = false;
            }
            else if (_data.TransitMode == WorldLinkTransitMode.VanillaNode)
            {
                bool nodeValid = room.abstractRoom?.connections != null && _data.VanillaNodeIndex >= 0 &&
                                 _data.VanillaNodeIndex < room.abstractRoom.connections.Length &&
                                 room.abstractRoom.connections[_data.VanillaNodeIndex] >= 0;
                status = nodeValid ? "Route status: NODE OK" : "Route status: INVALID NODE";
                valid = nodeValid;
            }
            else
            {
                bool fields = !string.IsNullOrWhiteSpace(_data.DestinationRoom) &&
                              !string.IsNullOrWhiteSpace(_data.DestinationGateId) &&
                              !string.IsNullOrWhiteSpace(_data.DestinationPortId);
                status = fields ? "Route status: CROSS-REGION CONFIGURED" : "Route status: DESTINATION INCOMPLETE";
                valid = fields;
            }

            _routeStatus.Text = status;
            _routeStatus.textColor = valid ? new Color(0.1f, 0.45f, 0.1f) : Color.red;
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

        private DryCycleIntegerField Int(string label, int y, int value, int min, int max, Func<int> read, Action<int> write)
        {
            Label(label, y);
            var field = new DryCycleIntegerField(owner, "WorldLink_" + label, this, new Vector2(100f, y), 210f,
                value, min, max, readValue: read, writeValue: write);
            subNodes.Add(field);
            return field;
        }

        private DryCycleFloatField Float(string label, int y, float value, float min, float max, Func<float> read, Action<float> write)
        {
            Label(label, y);
            var field = new DryCycleFloatField(owner, "WorldLink_" + label, this, new Vector2(100f, y), 210f,
                value, min, max, 2, readValue: read, writeValue: write);
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
