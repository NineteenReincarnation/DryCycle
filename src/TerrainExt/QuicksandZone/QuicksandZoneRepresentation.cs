using DevInterface;
using RWCustom;
using UnityEngine;

namespace DryCycle.TerrainExt.QuicksandZone;

internal sealed class QuicksandZoneRepresentation : PlacedObjectRepresentation
{
    private const float FlowHandleScale = 60f;

    private readonly BezierSplineControl _surfaceControl;
    private readonly BezierSplineControl _bottomControl;
    private readonly SliderHandle _flowHandle;
    private readonly int _firstEdgeSprite;

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

        _bottomControl = new BezierSplineControl(
            owner,
            "Quicksand_Bottom",
            this,
            Vector2.zero,
            data.BottomSpline,
            originHandle: true);
        subNodes.Add(_bottomControl);

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
        for (int i = 0; i < 2; i++)
        {
            FSprite line = new("pixel")
            {
                anchorY = 0f,
                color = new Color(0.85f, 0.62f, 0.22f),
                alpha = 0.72f
            };
            fSprites.Add(line);
            owner.placedObjectsContainer.AddChild(line);
        }

        TintSpline(_surfaceControl, new Color(0.95f, 0.72f, 0.25f));
        TintSpline(_bottomControl, new Color(0.52f, 0.31f, 0.16f));
        if (_flowHandle.fSprites.Count > 0)
        {
            _flowHandle.fSprites[0].color = new Color(1f, 0.82f, 0.30f);
        }
    }

    public override void Refresh()
    {
        base.Refresh();

        if (Data == null)
        {
            return;
        }

        Data.FlowSpeed = Mathf.Clamp(_flowHandle.Value / FlowHandleScale, -2f, 2f);

        Vector2 surfaceA = absPos + Data.SurfaceSpline.posA;
        Vector2 surfaceB = absPos + Data.SurfaceSpline.posB;
        Vector2 bottomA = absPos + Data.BottomSpline.posA;
        Vector2 bottomB = absPos + Data.BottomSpline.posB;

        DrawLine(_firstEdgeSprite, surfaceA, bottomA);
        DrawLine(_firstEdgeSprite + 1, surfaceB, bottomB);
    }

    private void DrawLine(int sprite, Vector2 from, Vector2 to)
    {
        MoveSprite(sprite, from);
        fSprites[sprite].scaleY = Vector2.Distance(from, to);
        fSprites[sprite].rotation = Custom.AimFromOneVectorToAnother(from, to);
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
