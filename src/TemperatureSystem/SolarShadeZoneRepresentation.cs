using System.Collections.Generic;
using System.Globalization;
using DevInterface;
using RWCustom;
using UnityEngine;

namespace DryCycle.TemperatureSystem;

/// <summary>
/// DevInterface representation for a freely editable local solar-shade polygon.
///
/// Controls:
/// - drag a blue vertex to reshape the polygon;
/// - Shift + left click an edge to insert a vertex;
/// - Ctrl + left click a vertex to delete it (minimum three vertices);
/// - edit Shade from the attached control panel. The arrows change Shade by 0.01;
///   Shift changes by 0.10 and Ctrl changes by 1.00 (clamped to 0..1).
/// </summary>
internal sealed class SolarShadeZoneRepresentation : PlacedObjectRepresentation
{
    private const float InsertDistance = 12f;

    private static readonly Color EdgeColor = new(0.18f, 0.55f, 1f);
    private static readonly Color VertexColor = new(0.35f, 0.72f, 1f);
    private static readonly Color DeleteColor = new(1f, 0.35f, 0.10f);

    private sealed class VertexHandle : Handle
    {
        internal int Index;

        internal VertexHandle(
            DevUI owner,
            DevUINode parentNode,
            int index,
            Vector2 position)
            : base(owner, "DryCycleShade_Vertex_" + index, parentNode, position)
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

    private sealed class ShadeValueControl : IntegerControl
    {
        internal ShadeValueControl(
            DevUI owner,
            string idString,
            DevUINode parentNode,
            Vector2 position)
            : base(owner, idString, parentNode, position, "Shade")
        {
        }

        private SolarShadeZoneData Data =>
            (parentNode?.parentNode as SolarShadeZoneRepresentation)?.pObj?.data as SolarShadeZoneData;

        public override void Increment(int change)
        {
            SolarShadeZoneData data = Data;
            if (data == null)
            {
                return;
            }

            // IntegerControl already expands its change amount with Shift/Ctrl.
            // Treat one unit as one percentage point so ordinary clicks are 0.01.
            data.SetShade(data.Shade + change * 0.01f);
            Refresh();
            parentNode?.parentNode?.Refresh();
        }

        public override void Refresh()
        {
            base.Refresh();
            SolarShadeZoneData data = Data;
            NumberLabelText = (data?.Shade ?? 0f).ToString("0.00", CultureInfo.InvariantCulture);
        }
    }

    private sealed class ShadeControlPanel : Panel
    {
        internal ShadeControlPanel(
            DevUI owner,
            string idString,
            DevUINode parentNode,
            Vector2 position)
            : base(
                owner,
                idString,
                parentNode,
                position,
                new Vector2(220f, 48f),
                "Shade Zone")
        {
            subNodes.Add(new ShadeValueControl(
                owner,
                "DryCycleShade_Value",
                this,
                new Vector2(8f, 8f)));
        }
    }

    private readonly List<VertexHandle> _vertexHandles = new();
    private readonly ShadeControlPanel _controlPanel;
    private readonly int _firstEdgeSprite;
    private readonly int _panelLinkSprite;

    private SolarShadeZoneData Data => pObj.data as SolarShadeZoneData;

    internal SolarShadeZoneRepresentation(
        DevUI owner,
        string idString,
        DevUINode parentNode,
        PlacedObject placedObject,
        string name)
        : base(owner, idString, parentNode, placedObject, name)
    {
        _controlPanel = new ShadeControlPanel(
            owner,
            "DryCycleShade_Panel",
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

        VertexHandle deleteTarget = control && leftClick
            ? FindVertexUnderMouse()
            : null;

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

        SolarShadeZoneData data = Data;
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

        SolarShadeZoneData data = Data;
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

        SolarShadeZoneData data = Data;
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

        SolarShadeZoneData data = Data;
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
