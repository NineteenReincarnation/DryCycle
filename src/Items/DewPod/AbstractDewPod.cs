using System.Globalization;
using UnityEngine;

namespace DryCycle.Items.DewPod;

internal sealed class AbstractDewPod : AbstractConsumable
{
    public const float MaxWaterWV = 800f;

    public float WaterWV;
    public bool Broken;

    public AbstractDewPod(
        World world,
        WorldCoordinate pos,
        EntityID id,
        int originRoom,
        int placedObjectIndex,
        PlacedObject.ConsumableObjectData consumableData,
        float waterWV = MaxWaterWV,
        bool broken = false)
        : base(
            world,
            DewPodHooks.ObjectType,
            null,
            pos,
            id,
            originRoom,
            placedObjectIndex,
            consumableData)
    {
        WaterWV = Mathf.Clamp(waterWV, 0f, MaxWaterWV);
        Broken = broken;
    }

    public override string ToString()
    {
        string baseString = string.Format(
            CultureInfo.InvariantCulture,
            "{0}<oA>{1}<oA>{2}<oA>{3}<oA>{4}" +
            "<oA>DRYCYCLE_DEWPOD_WATER={5}" +
            "<oA>DRYCYCLE_DEWPOD_BROKEN={6}",
            base.IDAndRippleLayerString,
            type.ToString(),
            pos.SaveToString(),
            originRoom,
            placedObjectIndex,
            WaterWV,
            Broken ? 1 : 0);

        baseString = SaveState.SetCustomData(this, baseString);
        return SaveUtils.AppendUnrecognizedStringAttrs(baseString, "<oA>", unrecognizedAttributes);
    }
}
