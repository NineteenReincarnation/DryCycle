using DevInterface;
using RWCustom;
using UnityEngine;

namespace DryCycle.Weather.HeatWave;

/// <summary>
/// Intentionally sparse DevUI for the first HeatColumn implementation: the mapper
/// places the base and drags one flow handle to author the preferred plume direction
/// and reach. Radius/strength/turbulence are serialized already and can gain a panel
/// without changing room data or the runtime emitter contract.
/// </summary>
internal sealed class HeatColumnRepresentation : PlacedObjectRepresentation
{
    private readonly Handle _flowHandle;
    private readonly FSprite _flowLine;

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

        _flowLine = new FSprite("pixel")
        {
            anchorY = 0f,
            color = new Color(1f, 0.72f, 0.30f, 0.85f)
        };
        fSprites.Add(_flowLine);
        owner.placedObjectsContainer.AddChild(_flowLine);
    }

    public override void Update()
    {
        base.Update();
        if (pObj?.data is HeatColumnData data)
        {
            data.FlowVector = _flowHandle.pos;
        }
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

        Vector2 vector = _flowHandle.pos;
        float length = Mathf.Max(0.001f, vector.magnitude);
        _flowLine.x = absPos.x;
        _flowLine.y = absPos.y;
        _flowLine.scaleY = length;
        _flowLine.rotation = Custom.VecToDeg(vector);
    }
}
