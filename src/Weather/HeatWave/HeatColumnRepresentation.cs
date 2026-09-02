using System.Globalization;
using DevInterface;
using RWCustom;
using UnityEngine;

namespace DryCycle.Weather.HeatWave;

/// <summary>
/// Full mapper authoring surface for HeatColumn. The center handle controls plume reach
/// and preferred direction. The panel controls shape/energy separately, while the cyan
/// envelope previews widening in room space without pretending to be the final fluid.
/// </summary>
internal sealed class HeatColumnRepresentation : PlacedObjectRepresentation
{
    private const float PanelWidth = 286f;
    private const float PanelHeight = 154f;
    private const float SliderTitleWidth = 84f;

    private static readonly Color CenterColor = new(1f, 0.72f, 0.30f, 0.90f);
    private static readonly Color EnvelopeColor = new(0.30f, 0.92f, 1f, 0.74f);
    private static readonly Color WidthColor = new(1f, 0.88f, 0.50f, 0.62f);

    private enum ColumnField
    {
        Radius,
        Strength,
        FlowSpeed,
        Turbulence,
        Expansion,
        Pulse
    }

    private sealed class ColumnSlider : Slider
    {
        private readonly ColumnField _field;
        private readonly float _min;
        private readonly float _max;

        internal ColumnSlider(
            DevUI owner,
            string idString,
            DevUINode parentNode,
            Vector2 position,
            string title,
            ColumnField field,
            float min,
            float max)
            : base(owner, idString, parentNode, position, title, inheritButton: false, SliderTitleWidth)
        {
            _field = field;
            _min = min;
            _max = max;
        }

        private HeatColumnRepresentation Representation =>
            parentNode?.parentNode as HeatColumnRepresentation;

        private HeatColumnData Data => Representation?.pObj?.data as HeatColumnData;

        public override void Refresh()
        {
            base.Refresh();
            HeatColumnData data = Data;
            if (data == null)
            {
                return;
            }

            float value = GetValue(data);
            RefreshNubPos(Mathf.InverseLerp(_min, _max, value));
            NumberText = Format(value);
        }

        public override void NubDragged(float nubPos)
        {
            HeatColumnData data = Data;
            if (data == null)
            {
                return;
            }

            float value = Mathf.Lerp(_min, _max, Mathf.Clamp01(nubPos));
            SetValue(data, value);
            Refresh();
        }

        private float GetValue(HeatColumnData data)
        {
            return _field switch
            {
                ColumnField.Radius => data.Radius,
                ColumnField.Strength => data.Strength,
                ColumnField.FlowSpeed => data.FlowSpeed,
                ColumnField.Turbulence => data.Turbulence,
                ColumnField.Expansion => data.Expansion,
                ColumnField.Pulse => data.Pulse,
                _ => 0f
            };
        }

        private void SetValue(HeatColumnData data, float value)
        {
            switch (_field)
            {
                case ColumnField.Radius:
                    data.Radius = Mathf.Clamp(value, 16f, 360f);
                    break;
                case ColumnField.Strength:
                    data.Strength = Mathf.Clamp(value, 0f, 2.5f);
                    break;
                case ColumnField.FlowSpeed:
                    data.FlowSpeed = Mathf.Clamp(value, 0.15f, 3f);
                    break;
                case ColumnField.Turbulence:
                    data.Turbulence = Mathf.Clamp(value, 0f, 2.5f);
                    break;
                case ColumnField.Expansion:
                    data.Expansion = Mathf.Clamp(value, 0.35f, 2.6f);
                    break;
                case ColumnField.Pulse:
                    data.Pulse = Mathf.Clamp01(value);
                    break;
            }
        }

        private string Format(float value)
        {
            if (_field == ColumnField.Radius)
            {
                return Mathf.RoundToInt(value).ToString(CultureInfo.InvariantCulture);
            }

            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }
    }

    private readonly Handle _flowHandle;
    private readonly Panel _panel;
    private readonly FSprite _flowLine;
    private readonly FSprite _leftEnvelope;
    private readonly FSprite _rightEnvelope;
    private readonly FSprite _baseWidth;
    private readonly FSprite _endWidth;
    private readonly FSprite _panelLine;

    internal HeatColumnRepresentation(
        DevUI owner,
        string idString,
        DevUINode parentNode,
        PlacedObject placedObject)
        : base(owner, idString, parentNode, placedObject, "Heat Column")
    {
        HeatColumnData data = placedObject.data as HeatColumnData ??
                              new HeatColumnData(placedObject);
        placedObject.data = data;

        _flowHandle = new Handle(owner, idString + "_Flow", this, data.FlowVector);
        subNodes.Add(_flowHandle);

        _panel = new Panel(
            owner,
            idString + "_Panel",
            this,
            data.PanelPos,
            new Vector2(PanelWidth, PanelHeight),
            "Heat Column / Thermal Emitter");
        subNodes.Add(_panel);

        AddSlider(owner, idString, "Radius", ColumnField.Radius, 16f, 360f, 122f);
        AddSlider(owner, idString, "Heat", ColumnField.Strength, 0f, 2.5f, 102f);
        AddSlider(owner, idString, "Flow Speed", ColumnField.FlowSpeed, 0.15f, 3f, 82f);
        AddSlider(owner, idString, "Turbulence", ColumnField.Turbulence, 0f, 2.5f, 62f);
        AddSlider(owner, idString, "Expansion", ColumnField.Expansion, 0.35f, 2.6f, 42f);
        AddSlider(owner, idString, "Pulse", ColumnField.Pulse, 0f, 1f, 22f);

        _flowLine = MakeLine(CenterColor);
        _leftEnvelope = MakeLine(EnvelopeColor);
        _rightEnvelope = MakeLine(EnvelopeColor);
        _baseWidth = MakeLine(WidthColor);
        _endWidth = MakeLine(WidthColor);
        _panelLine = MakeLine(new Color(0.78f, 0.78f, 0.78f, 0.42f));
    }

    public override void Update()
    {
        base.Update();
        if (pObj?.data is not HeatColumnData data)
        {
            return;
        }

        data.FlowVector = _flowHandle.pos;
        data.PanelPos = _panel.pos;
    }

    public override void Refresh()
    {
        base.Refresh();
        if (pObj?.data is not HeatColumnData data)
        {
            return;
        }

        if (!_flowHandle.dragged)
        {
            _flowHandle.Move(data.FlowVector);
        }

        if (!_panel.dragged)
        {
            _panel.Move(data.PanelPos);
        }

        Vector2 flow = _flowHandle.pos;
        float length = Mathf.Max(0.001f, flow.magnitude);
        Vector2 direction = flow / length;
        Vector2 normal = new(-direction.y, direction.x);

        // Preview is deliberately conservative: it displays the authored influence
        // envelope, while the compute solver is still free to bend/split the actual
        // thermal mass around terrain and other columns.
        float baseRadius = Mathf.Max(8f, data.Radius * 0.52f);
        float endRadius = baseRadius * Mathf.Clamp(data.Expansion, 0.35f, 2.6f);
        Vector2 start = absPos;
        Vector2 end = absPos + flow;
        Vector2 startLeft = start - normal * baseRadius;
        Vector2 startRight = start + normal * baseRadius;
        Vector2 endLeft = end - normal * endRadius;
        Vector2 endRight = end + normal * endRadius;

        SetSegment(_flowLine, start, end, 2f);
        SetSegment(_leftEnvelope, startLeft, endLeft, 1f);
        SetSegment(_rightEnvelope, startRight, endRight, 1f);
        SetSegment(_baseWidth, startLeft, startRight, 1f);
        SetSegment(_endWidth, endLeft, endRight, 1f);
        SetSegment(
            _panelLine,
            start,
            _panel.nonCollapsedAbsPos + new Vector2(10f, PanelHeight + 7f),
            1f);
    }

    private void AddSlider(
        DevUI owner,
        string idString,
        string title,
        ColumnField field,
        float min,
        float max,
        float y)
    {
        ColumnSlider slider = new(
            owner,
            idString + "_" + field,
            _panel,
            new Vector2(8f, y),
            title,
            field,
            min,
            max);
        _panel.subNodes.Add(slider);
    }

    private FSprite MakeLine(Color color)
    {
        // DevInterface's native control lines stretch the pixel sprite on Y and use
        // Custom.VecToDeg (0 degrees = up). Match that convention exactly so editor
        // previews do not rotate ninety degrees relative to their handles.
        FSprite line = new("pixel")
        {
            anchorX = 0.5f,
            anchorY = 0f,
            color = color,
            alpha = color.a
        };
        fSprites.Add(line);
        owner.placedObjectsContainer.AddChild(line);
        return line;
    }

    private static void SetSegment(
        FSprite line,
        Vector2 from,
        Vector2 to,
        float thickness)
    {
        Vector2 delta = to - from;
        float length = Mathf.Max(0.001f, delta.magnitude);
        line.x = from.x;
        line.y = from.y;
        line.scaleX = thickness;
        line.scaleY = length;
        line.rotation = Custom.VecToDeg(delta);
    }
}
