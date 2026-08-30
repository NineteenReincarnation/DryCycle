using System.Collections.Generic;
using System.Globalization;
using DevInterface;
using RWCustom;
using UnityEngine;

namespace DryCycle.TemperatureSystem;

/// <summary>
/// DevInterface representation for a freely editable local humidity polygon.
///
/// Controls:
/// - drag a blue vertex to reshape the polygon;
/// - Shift + left click an edge to insert a vertex;
/// - Ctrl + left click a vertex to delete it (minimum three vertices);
/// - click the Humidity value field and type directly from the keyboard;
/// - Enter commits a valid -1..1 value, Escape cancels the edit.
/// Invalid values are never written to HumidityZoneData and are shown in red.
/// </summary>
internal sealed class HumidityZoneRepresentation : PlacedObjectRepresentation
{
    private const float InsertDistance = 12f;

    private static readonly Color EdgeColor = new(0.18f, 0.55f, 1f);
    private static readonly Color VertexColor = new(0.35f, 0.72f, 1f);
    private static readonly Color DeleteColor = new(1f, 0.35f, 0.10f);
    private static readonly Color InvalidColor = new(1f, 0.16f, 0.12f);

    private sealed class VertexHandle : Handle
    {
        internal int Index;

        internal VertexHandle(
            DevUI owner,
            DevUINode parentNode,
            int index,
            Vector2 position)
            : base(owner, "DryCycleHumidity_Vertex_" + index, parentNode, position)
        {
            Index = index;
            defaultColor = VertexColor;
            if (fSprites.Count > 0)
            {
                fSprites[0].scale = 0.42f;
                fSprites[0].color = VertexColor;
            }
        }
    }

    private sealed class HumidityTextInput : DevUILabel
    {
        private const int MaxInputCharacters = 12;

        private string _draft;
        private bool _editing;
        private bool _replaceOnType;
        private bool _draftIsUncommitted;
        private float _lastKnownDataValue;

        internal HumidityTextInput(
            DevUI owner,
            string idString,
            DevUINode parentNode,
            Vector2 position,
            float width)
            : base(owner, idString, parentNode, position, width, string.Empty)
        {
            float initial = Data?.Humidity ?? 0f;
            _lastKnownDataValue = initial;
            _draft = FormatValue(initial);
            ApplyVisualState();
        }

        private HumidityZoneRepresentation Representation =>
            parentNode?.parentNode as HumidityZoneRepresentation;

        private HumidityControlPanel Panel => parentNode as HumidityControlPanel;

        private HumidityZoneData Data =>
            Representation?.pObj?.data as HumidityZoneData;

        public override void Update()
        {
            base.Update();

            if (owner == null)
            {
                return;
            }

            if (owner.mouseClick)
            {
                if (MouseOver)
                {
                    BeginEditing();
                    owner.mouseClick = false;
                }
                else if (_editing)
                {
                    if (TryParseDraft(out float clickedAwayValue))
                    {
                        Commit(clickedAwayValue);
                    }
                    else
                    {
                        _editing = false;
                        _replaceOnType = false;
                    }
                }
            }

            if (_editing)
            {
                HandleKeyboardInput();
            }
            else if (!_draftIsUncommitted)
            {
                SyncFromDataIfChanged();
            }

            ApplyVisualState();
        }

        public override void Refresh()
        {
            base.Refresh();
            if (!_editing && !_draftIsUncommitted)
            {
                SyncFromDataIfChanged();
            }
            ApplyVisualState();
        }

        private void BeginEditing()
        {
            if (_editing)
            {
                return;
            }

            if (!_draftIsUncommitted)
            {
                float current = Data?.Humidity ?? 0f;
                _lastKnownDataValue = current;
                _draft = FormatValue(current);
            }

            _editing = true;
            _replaceOnType = true;
        }

        private void HandleKeyboardInput()
        {
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                CancelEditing();
                return;
            }

            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                if (TryParseDraft(out float value))
                {
                    Commit(value);
                }
                return;
            }

            bool control = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (control && Input.GetKeyDown(KeyCode.A))
            {
                _draft = string.Empty;
                _replaceOnType = false;
                _draftIsUncommitted = true;
                return;
            }

            if (Input.GetKeyDown(KeyCode.Delete))
            {
                _draft = string.Empty;
                _replaceOnType = false;
                _draftIsUncommitted = true;
            }

            string typed = Input.inputString;
            if (string.IsNullOrEmpty(typed))
            {
                return;
            }

            for (int i = 0; i < typed.Length; i++)
            {
                char c = typed[i];
                if (c == '\b')
                {
                    if (_replaceOnType)
                    {
                        _draft = string.Empty;
                        _replaceOnType = false;
                    }
                    else if (_draft.Length > 0)
                    {
                        _draft = _draft.Substring(0, _draft.Length - 1);
                    }

                    _draftIsUncommitted = true;
                    continue;
                }

                if (c == '\n' || c == '\r')
                {
                    continue;
                }

                if (!IsAllowedNumericCharacter(c))
                {
                    continue;
                }

                if (_replaceOnType)
                {
                    _draft = string.Empty;
                    _replaceOnType = false;
                }

                if (_draft.Length < MaxInputCharacters)
                {
                    _draft += c;
                    _draftIsUncommitted = true;
                }
            }
        }

        private void Commit(float value)
        {
            HumidityZoneData data = Data;
            if (data == null)
            {
                return;
            }

            data.SetHumidity(value);
            _lastKnownDataValue = data.Humidity;
            _draft = FormatValue(data.Humidity);
            _draftIsUncommitted = false;
            _editing = false;
            _replaceOnType = false;
            Representation?.Refresh();
        }

        private void CancelEditing()
        {
            float current = Data?.Humidity ?? _lastKnownDataValue;
            _lastKnownDataValue = current;
            _draft = FormatValue(current);
            _draftIsUncommitted = false;
            _editing = false;
            _replaceOnType = false;
        }

        private void SyncFromDataIfChanged()
        {
            HumidityZoneData data = Data;
            if (data == null || Mathf.Approximately(data.Humidity, _lastKnownDataValue))
            {
                return;
            }

            _lastKnownDataValue = data.Humidity;
            _draft = FormatValue(data.Humidity);
        }

        private bool TryParseDraft(out float value)
        {
            value = 0f;
            if (string.IsNullOrWhiteSpace(_draft))
            {
                return false;
            }

            string normalized = _draft.Trim().Replace(',', '.');
            if (!float.TryParse(
                    normalized,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out value) ||
                float.IsNaN(value) ||
                float.IsInfinity(value))
            {
                return false;
            }

            return value >= -1f && value <= 1f;
        }

        private void ApplyVisualState()
        {
            bool valid = TryParseDraft(out _);
            HumidityControlPanel panel = Panel;

            if (valid)
            {
                if (panel != null)
                {
                    panel.Title = "Humidity Zone";
                    if (panel.fLabels.Count > 0)
                    {
                        panel.fLabels[0].color = Color.white;
                    }
                }

                spriteColor = _editing ? VertexColor : Color.white;
                textColor = Color.black;
            }
            else
            {
                if (panel != null)
                {
                    panel.Title = "Humidity Zone - INVALID (-1..1)";
                    if (panel.fLabels.Count > 0)
                    {
                        panel.fLabels[0].color = InvalidColor;
                    }
                }

                spriteColor = InvalidColor;
                textColor = Color.white;
            }

            Text = _draft + (_editing ? "|" : string.Empty);
        }

        private static bool IsAllowedNumericCharacter(char c)
        {
            return (c >= '0' && c <= '9') ||
                   c == '.' ||
                   c == ',' ||
                   c == '-' ||
                   c == '+';
        }

        private static string FormatValue(float value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }
    }

    private sealed class HumidityControlPanel : Panel
    {
        internal HumidityControlPanel(
            DevUI owner,
            string idString,
            DevUINode parentNode,
            Vector2 position)
            : base(
                owner,
                idString,
                parentNode,
                position,
                new Vector2(250f, 48f),
                "Humidity Zone")
        {
            subNodes.Add(new DevUILabel(
                owner,
                "DryCycleHumidity_Label",
                this,
                new Vector2(8f, 8f),
                120f,
                "Humidity"));

            subNodes.Add(new HumidityTextInput(
                owner,
                "DryCycleHumidity_ValueInput",
                this,
                new Vector2(132f, 8f),
                106f));
        }
    }

    private readonly List<VertexHandle> _vertexHandles = new();
    private readonly HumidityControlPanel _controlPanel;
    private readonly int _firstEdgeSprite;
    private readonly int _panelLinkSprite;

    private HumidityZoneData Data => pObj.data as HumidityZoneData;

    internal HumidityZoneRepresentation(
        DevUI owner,
        string idString,
        DevUINode parentNode,
        PlacedObject placedObject,
        string name)
        : base(owner, idString, parentNode, placedObject, name)
    {
        defaultColor = VertexColor;

        _controlPanel = new HumidityControlPanel(
            owner,
            "DryCycleHumidity_Panel",
            this,
            new Vector2(105f, 80f));
        subNodes.Add(_controlPanel);

        _panelLinkSprite = fSprites.Count;
        FSprite panelLink = new("pixel")
        {
            anchorY = 0f,
            scaleX = 1.5f,
            color = EdgeColor,
            alpha = 0.75f
        };
        fSprites.Add(panelLink);
        owner.placedObjectsContainer.AddChild(panelLink);

        _firstEdgeSprite = fSprites.Count;
        RebuildVertexHandles();
        EnsureEdgeSpriteCount(Data?.Vertices.Count ?? 0);
        Refresh();
    }

    public override void Update()
    {
        bool control = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        bool leftClick = Input.GetMouseButtonDown(0);

        VertexHandle deleteTarget = control && leftClick ? FindVertexUnderMouse() : null;

        bool insertCandidate = false;
        int insertAfterEdge = -1;
        Vector2 insertPoint = Vector2.zero;

        if (!control && shift && leftClick && FindVertexUnderMouse() == null)
        {
            Vector2 localMouse = owner.mousePos - absPos;
            insertCandidate = TryFindNearestEdge(localMouse, out insertAfterEdge, out insertPoint);
        }

        bool savedMouseClick = owner != null && owner.mouseClick;
        bool suppressBaseClick = savedMouseClick && (deleteTarget != null || insertCandidate);
        if (suppressBaseClick)
        {
            owner.mouseClick = false;
        }

        try
        {
            base.Update();
        }
        finally
        {
            if (suppressBaseClick && owner != null)
            {
                owner.mouseClick = savedMouseClick;
            }
        }

        HumidityZoneData data = Data;
        if (data == null)
        {
            return;
        }

        bool changed = false;

        if (control)
        {
            VertexHandle hover = FindVertexUnderMouse();
            if (hover != null)
            {
                hover.SetColor(DeleteColor);
            }
        }

        if (deleteTarget != null && data.Vertices.Count > 3)
        {
            if (owner?.draggedNode == deleteTarget)
            {
                owner.draggedNode = null;
            }

            if (data.RemoveVertexAt(deleteTarget.Index))
            {
                RebuildVertexHandles();
                changed = true;
            }
        }
        else if (insertCandidate && owner?.draggedNode == null)
        {
            data.InsertVertex(insertAfterEdge + 1, insertPoint);
            RebuildVertexHandles();
            changed = true;
        }
        else
        {
            for (int i = 0; i < _vertexHandles.Count; i++)
            {
                VertexHandle handle = _vertexHandles[i];
                if (!handle.dragged)
                {
                    continue;
                }

                data.SetVertex(handle.Index, handle.pos);
                changed = true;
            }
        }

        if (changed || dragged || _controlPanel.dragged)
        {
            Refresh();
        }
    }

    public override void Refresh()
    {
        base.Refresh();

        HumidityZoneData data = Data;
        if (data == null)
        {
            return;
        }

        EnsureEdgeSpriteCount(data.Vertices.Count);

        for (int i = 0; i < _vertexHandles.Count && i < data.Vertices.Count; i++)
        {
            if (!_vertexHandles[i].dragged)
            {
                _vertexHandles[i].Move(data.Vertices[i]);
            }
        }

        int edgeCapacity = fSprites.Count - _firstEdgeSprite;
        for (int i = 0; i < edgeCapacity; i++)
        {
            FSprite line = fSprites[_firstEdgeSprite + i];
            bool visible = i < data.Vertices.Count;
            line.isVisible = visible;
            if (!visible)
            {
                continue;
            }

            Vector2 from = absPos + data.Vertices[i];
            Vector2 to = absPos + data.Vertices[(i + 1) % data.Vertices.Count];
            DrawLine(_firstEdgeSprite + i, from, to);
        }

        DrawLine(_panelLinkSprite, absPos, _controlPanel.absPos);
        _controlPanel.Refresh();
    }

    private void RebuildVertexHandles()
    {
        for (int i = 0; i < _vertexHandles.Count; i++)
        {
            VertexHandle handle = _vertexHandles[i];
            if (owner?.draggedNode == handle)
            {
                owner.draggedNode = null;
            }

            subNodes.Remove(handle);
            handle.ClearSprites();
        }
        _vertexHandles.Clear();

        HumidityZoneData data = Data;
        if (data == null)
        {
            return;
        }

        for (int i = 0; i < data.Vertices.Count; i++)
        {
            VertexHandle handle = new(owner, this, i, data.Vertices[i]);
            _vertexHandles.Add(handle);
            subNodes.Add(handle);
        }
    }

    private VertexHandle FindVertexUnderMouse()
    {
        VertexHandle nearest = null;
        float nearestDistance = float.PositiveInfinity;

        for (int i = 0; i < _vertexHandles.Count; i++)
        {
            VertexHandle handle = _vertexHandles[i];
            if (!handle.MouseOver)
            {
                continue;
            }

            float distance = Vector2.SqrMagnitude(handle.absPos - owner.mousePos);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = handle;
            }
        }

        return nearest;
    }

    private bool TryFindNearestEdge(
        Vector2 localMouse,
        out int edgeIndex,
        out Vector2 point)
    {
        edgeIndex = -1;
        point = Vector2.zero;

        HumidityZoneData data = Data;
        if (data == null || data.Vertices.Count < 2)
        {
            return false;
        }

        float nearestSquared = InsertDistance * InsertDistance;
        for (int i = 0; i < data.Vertices.Count; i++)
        {
            Vector2 a = data.Vertices[i];
            Vector2 b = data.Vertices[(i + 1) % data.Vertices.Count];
            Vector2 ab = b - a;
            float lengthSquared = ab.sqrMagnitude;
            if (lengthSquared <= 0.00001f)
            {
                continue;
            }

            float t = Mathf.Clamp01(Vector2.Dot(localMouse - a, ab) / lengthSquared);
            Vector2 candidate = a + ab * t;
            float distanceSquared = Vector2.SqrMagnitude(localMouse - candidate);
            if (distanceSquared > nearestSquared)
            {
                continue;
            }

            nearestSquared = distanceSquared;
            edgeIndex = i;
            point = candidate;
        }

        return edgeIndex >= 0;
    }

    private void EnsureEdgeSpriteCount(int count)
    {
        int current = fSprites.Count - _firstEdgeSprite;
        while (current < count)
        {
            FSprite line = new("pixel")
            {
                anchorY = 0f,
                scaleX = 1.5f,
                color = EdgeColor,
                alpha = 0.92f
            };
            fSprites.Add(line);
            owner.placedObjectsContainer.AddChild(line);
            current++;
        }
    }

    private void DrawLine(int spriteIndex, Vector2 from, Vector2 to)
    {
        MoveSprite(spriteIndex, from);
        FSprite line = fSprites[spriteIndex];
        line.scaleY = Vector2.Distance(from, to);
        line.rotation = Custom.AimFromOneVectorToAnother(from, to);
        line.color = EdgeColor;
    }
}
