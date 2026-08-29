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
/// - drag a yellow vertex to reshape the polygon;
/// - Shift + left click an edge to insert a vertex;
/// - Ctrl + left click a vertex to delete it (minimum three vertices);
/// - drag the Shade slider handle to author a 0..1 local shade value.
/// </summary>
internal sealed class SolarShadeZoneRepresentation : PlacedObjectRepresentation
{
    private const float InsertDistance = 12f;
    private const float ShadeSliderLength = 120f;

    private static readonly Color EdgeColor = new(1f, 0.78f, 0.18f);
    private static readonly Color VertexColor = new(1f, 0.88f, 0.30f);
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

    private readonly List<VertexHandle> _vertexHandles = new();
    private readonly SliderHandle _shadeHandle;
    private readonly int _firstEdgeSprite;

    private SolarShadeZoneData Data => pObj.data as SolarShadeZoneData;

    internal SolarShadeZoneRepresentation(
        DevUI owner,
        string idString,
        DevUINode parentNode,
        PlacedObject placedObject,
        string name)
        : base(owner, idString, parentNode, placedObject, name)
    {
        SolarShadeZoneData data = Data;

        _shadeHandle = new SliderHandle(
            owner,
            "DryCycleShade_Value",
            this,
            new Vector2(-60f, -92f),
            (data?.Shade ?? 0f) * ShadeSliderLength,
            vertical: false,
            drawLine: true)
        {
            defaultColor = EdgeColor
        };
        subNodes.Add(_shadeHandle);

        fLabels.Add(new FLabel(Custom.GetFont(), string.Empty)
        {
            alignment = FLabelAlignment.Left,
            color = EdgeColor
        });
        owner.placedObjectsContainer.AddChild(fLabels[1]);

        _firstEdgeSprite = fSprites.Count;
        RebuildVertexHandles();
        EnsureEdgeSpriteCount(data?.Vertices.Count ?? 0);
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

        // Prevent the base Handle logic from starting a drag on the representation
        // while a modifier-click is being consumed for polygon editing.
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

        float previousShade = data.Shade;
        data.SetShade(_shadeHandle.Value / ShadeSliderLength);
        _shadeHandle.Value = data.Shade * ShadeSliderLength;
        if (Mathf.Abs(previousShade - data.Shade) > 0.00001f)
        {
            changed = true;
        }

        if (changed || dragged)
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

        _shadeHandle.Value = data.Shade * ShadeSliderLength;
        if (fLabels.Count > 1)
        {
            fLabels[1].text = "Shade " + data.Shade.ToString("0.00", CultureInfo.InvariantCulture);
            MoveLabel(1, absPos + new Vector2(-64f, -112f));
        }
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
