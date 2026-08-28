using System.Collections.Generic;
using DevInterface;
using RWCustom;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

internal sealed class QuicksandZoneRepresentation : PlacedObjectRepresentation
{
    private const float FlowHandleScale = 60f;
    private const int MaterialLineSegments = 64;
    private const float MaterialInsertDistance = 12f;

    private static readonly Color TerrainLineColor = new(0.90f, 0.90f, 0.90f);
    private static readonly Color QuicksandLineColor = new(0.95f, 0.70f, 0.23f);
    private static readonly Color BottomLineColor = new(0.50f, 0.31f, 0.18f);

    private sealed class MaterialBoundaryHandle : Handle
    {
        internal float U;

        internal MaterialBoundaryHandle(
            DevUI owner,
            DevUINode parentNode,
            float u)
            : base(owner, "Quicksand_MaterialBoundary", parentNode, Vector2.zero)
        {
            U = Mathf.Clamp01(u);
            defaultColor = new Color(1f, 0.58f, 0.12f);
            if (fSprites.Count > 0)
            {
                fSprites[0].scale = 0.42f;
            }
        }
    }

    private readonly BezierSplineControl _surfaceControl;
    private readonly Handle _bottomHandle;
    private readonly SliderHandle _flowHandle;
    private readonly List<MaterialBoundaryHandle> _materialHandles = new();
    private readonly int _firstEdgeSprite;
    private readonly int _firstMaterialSprite;
    private QuicksandZone _terrain;

    private QuicksandZoneData Data => pObj.data as QuicksandZoneData;

    internal QuicksandZoneRepresentation(
        DevUI owner,
        string idString,
        DevUINode parentNode,
        PlacedObject placedObject,
        string name)
        : base(owner, idString, parentNode, placedObject, name)
    {
        QuicksandZoneData data = Data;

        _surfaceControl = new BezierSplineControl(
            owner,
            "Quicksand_Surface",
            this,
            Vector2.zero,
            data.SurfaceSpline,
            originHandle: true);
        subNodes.Add(_surfaceControl);

        _bottomHandle = new Handle(
            owner,
            "Quicksand_Bottom",
            this,
            new Vector2(0f, -data.BottomDepth));
        _bottomHandle.defaultColor = BottomLineColor;
        subNodes.Add(_bottomHandle);

        _flowHandle = new SliderHandle(
            owner,
            "Quicksand_Flow",
            this,
            new Vector2(0f, 28f),
            data.FlowSpeed * FlowHandleScale,
            vertical: false,
            drawLine: true);
        subNodes.Add(_flowHandle);

        _firstEdgeSprite = fSprites.Count;
        for (int i = 0; i < 3; i++)
        {
            FSprite line = new("pixel")
            {
                anchorY = 0f,
                color = BottomLineColor,
                alpha = 0.68f
            };
            fSprites.Add(line);
            owner.placedObjectsContainer.AddChild(line);
        }

        _firstMaterialSprite = fSprites.Count;
        for (int i = 0; i < MaterialLineSegments; i++)
        {
            FSprite line = new("pixel")
            {
                anchorY = 0f,
                scaleX = 2.0f,
                alpha = 0.92f
            };
            fSprites.Add(line);
            owner.placedObjectsContainer.AddChild(line);
        }

        TintSpline(_surfaceControl, TerrainLineColor);
        if (_flowHandle.fSprites.Count > 0)
        {
            _flowHandle.fSprites[0].color = QuicksandLineColor;
        }

        RebuildBoundaryHandles();
        FindOrCreateTerrain();
    }

    public override void Update()
    {
        base.Update();

        if (Data == null)
        {
            return;
        }

        bool boundariesChanged = false;
        bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);

        MaterialBoundaryHandle deleteHandle = null;
        if (alt && owner.mouseClick)
        {
            for (int i = 0; i < _materialHandles.Count; i++)
            {
                if (_materialHandles[i].MouseOver)
                {
                    deleteHandle = _materialHandles[i];
                    break;
                }
            }
        }

        if (deleteHandle != null)
        {
            if (owner.draggedNode == deleteHandle)
            {
                owner.draggedNode = null;
            }
            deleteHandle.dragged = false;
            subNodes.Remove(deleteHandle);
            _materialHandles.Remove(deleteHandle);
            deleteHandle.ClearSprites();
            boundariesChanged = true;
        }
        else
        {
            for (int i = 0; i < _materialHandles.Count; i++)
            {
                MaterialBoundaryHandle handle = _materialHandles[i];
                if (!handle.dragged)
                {
                    continue;
                }

                Vector2 localMouse = owner.mousePos - absPos;
                handle.U = QuicksandSurface.FindNearestU(
                    Data.SurfaceSpline,
                    localMouse,
                    out _);
                boundariesChanged = true;
            }

            if (alt && owner.mouseClick && owner.draggedNode == null)
            {
                Vector2 localMouse = owner.mousePos - absPos;
                float u = QuicksandSurface.FindNearestU(
                    Data.SurfaceSpline,
                    localMouse,
                    out float distance);
                if (distance <= MaterialInsertDistance)
                {
                    AddBoundaryHandle(Mathf.Clamp(u, 0.002f, 0.998f));
                    boundariesChanged = true;
                }
            }
        }

        if (boundariesChanged)
        {
            SyncMaterialBoundaries();
        }

        if (dragged || _bottomHandle.dragged || _surfaceControl.Alterred)
        {
            _terrain?.RefreshCurve();
        }
    }

    public override void Refresh()
    {
        base.Refresh();

        if (Data == null)
        {
            return;
        }

        Data.BottomDepth = Mathf.Max(20f, -_bottomHandle.pos.y);
        _bottomHandle.Move(new Vector2(0f, -Data.BottomDepth));
        Data.FlowSpeed = Mathf.Clamp(_flowHandle.Value / FlowHandleScale, -2f, 2f);

        Vector2 surfaceA = absPos + Data.SurfaceSpline.posA;
        Vector2 surfaceB = absPos + Data.SurfaceSpline.posB;
        Vector2 bottomA = new(surfaceA.x, absPos.y - Data.BottomDepth);
        Vector2 bottomB = new(surfaceB.x, absPos.y - Data.BottomDepth);

        DrawLine(_firstEdgeSprite, surfaceA, bottomA, BottomLineColor);
        DrawLine(_firstEdgeSprite + 1, surfaceB, bottomB, BottomLineColor);
        DrawLine(_firstEdgeSprite + 2, bottomA, bottomB, BottomLineColor);

        for (int i = 0; i < MaterialLineSegments; i++)
        {
            float u0 = (float)i / MaterialLineSegments;
            float u1 = (float)(i + 1) / MaterialLineSegments;
            Vector2 a = absPos + QuicksandSurface.EvaluateByApproximateLength(Data.SurfaceSpline, u0);
            Vector2 b = absPos + QuicksandSurface.EvaluateByApproximateLength(Data.SurfaceSpline, u1);
            Color color = Data.IsQuicksand((u0 + u1) * 0.5f)
                ? QuicksandLineColor
                : TerrainLineColor;
            DrawLine(_firstMaterialSprite + i, a, b, color);
        }

        for (int i = 0; i < _materialHandles.Count; i++)
        {
            MaterialBoundaryHandle handle = _materialHandles[i];
            Vector2 local = QuicksandSurface.EvaluateByApproximateLength(Data.SurfaceSpline, handle.U);
            handle.Move(local);
        }
    }

    private void FindOrCreateTerrain()
    {
        if (owner?.room?.terrain?.terrainList != null)
        {
            for (int i = 0; i < owner.room.terrain.terrainList.Count; i++)
            {
                if (owner.room.terrain.terrainList[i] is QuicksandZone existing &&
                    existing.PlacedObject == pObj)
                {
                    _terrain = existing;
                    return;
                }
            }
        }

        if (owner?.room != null)
        {
            _terrain = new QuicksandZone(owner.room, pObj);
            owner.room.AddObject(_terrain);
        }
    }

    private void RebuildBoundaryHandles()
    {
        for (int i = 0; i < _materialHandles.Count; i++)
        {
            subNodes.Remove(_materialHandles[i]);
            _materialHandles[i].ClearSprites();
        }
        _materialHandles.Clear();

        if (Data == null)
        {
            return;
        }

        for (int i = 0; i < Data.MaterialBoundaries.Count; i++)
        {
            AddBoundaryHandle(Data.MaterialBoundaries[i]);
        }
    }

    private void AddBoundaryHandle(float u)
    {
        MaterialBoundaryHandle handle = new(owner, this, u);
        _materialHandles.Add(handle);
        subNodes.Add(handle);
    }

    private void SyncMaterialBoundaries()
    {
        List<float> values = new(_materialHandles.Count);
        for (int i = 0; i < _materialHandles.Count; i++)
        {
            values.Add(_materialHandles[i].U);
        }

        Data.SetMaterialBoundaries(values);
        Refresh();
    }

    private void DrawLine(int sprite, Vector2 from, Vector2 to, Color color)
    {
        MoveSprite(sprite, from);
        fSprites[sprite].scaleY = Vector2.Distance(from, to);
        fSprites[sprite].rotation = Custom.AimFromOneVectorToAnother(from, to);
        fSprites[sprite].color = color;
    }

    private static void TintSpline(BezierSplineControl control, Color color)
    {
        if (control == null)
        {
            return;
        }

        for (int i = 0; i < control.fSprites.Count; i++)
        {
            control.fSprites[i].color = color;
        }

        control.ghostSprite.color = color;
    }
}
