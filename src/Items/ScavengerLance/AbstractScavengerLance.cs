using System;
using System.Globalization;
using DryCycle.Registration;

namespace DryCycle.Items.ScavengerLance;

internal sealed class AbstractScavengerLance : AbstractPhysicalObject
{
    internal AbstractScavengerLance(World world, WorldCoordinate pos, EntityID id, float length = LanceCombatMath.DefaultLength)
        : base(world, ScavengerLanceHooks.ObjectType, null, pos, id)
    { Length = LanceCombatMath.ValidLength(length); }

    internal float Length { get; }

    public override void Realize()
    {
        if (realizedObject != null) return;
        realizedObject = new ScavengerLance(this, world);
        // Keep vanilla stick realization without allowing vanilla to replace the object.
        base.Realize();
    }

    public override string ToString() => SaveUtils.AppendUnrecognizedStringAttrs(
        string.Format(CultureInfo.InvariantCulture, "{0}<oA>{1}<oA>{2}<oA>v1;length={3:R}",
            IDAndRippleLayerString, type.value, pos.SaveToString(), Length), "<oA>", unrecognizedAttributes);

    internal static AbstractScavengerLance Parse(World world, ItemSaveData data)
    {
        float length = LanceCombatMath.DefaultLength;
        foreach (string part in data.CustomData.Split(';'))
            if (part.StartsWith("length=", StringComparison.Ordinal) &&
                float.TryParse(part.Substring(7), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                length = LanceCombatMath.ValidLength(parsed);
        var result = new AbstractScavengerLance(world, data.Position, data.ID, length) { rippleLayer = data.RippleLayer };
        if (data.RawFields.Length > 4)
        {
            result.unrecognizedAttributes = new string[data.RawFields.Length - 4];
            Array.Copy(data.RawFields, 4, result.unrecognizedAttributes, 0, result.unrecognizedAttributes.Length);
        }
        return result;
    }
}
