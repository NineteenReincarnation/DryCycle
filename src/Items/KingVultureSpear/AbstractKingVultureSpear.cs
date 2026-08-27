using System.Globalization;
using UnityEngine;

namespace DryCycle.Items.KingVultureSpear;

internal sealed class AbstractKingVultureSpear : AbstractSpear
{
    public int SourceSide;
    public Color ArmorColor;
    public HSLColor ColorA;
    public HSLColor ColorB;
    public float PatternDisplace;
    public Vector2 Profile;

    public AbstractKingVultureSpear(
        World world,
        WorldCoordinate pos,
        EntityID id,
        int sourceSide,
        Color armorColor,
        HSLColor colorA,
        HSLColor colorB,
        float patternDisplace,
        Vector2 profile)
        : base(world, null, pos, id, explosive: false)
    {
        type = KingVultureSpearHooks.ObjectType;
        SourceSide = sourceSide;
        ArmorColor = armorColor;
        ColorA = colorA;
        ColorB = colorB;
        PatternDisplace = Mathf.Clamp01(patternDisplace);
        Profile = profile;
    }

    public override string ToString()
    {
        string baseString = base.ToString();

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}<oA>DRYCYCLE_KVS_SIDE={1}" +
            "<oA>DRYCYCLE_KVS_ARMOR={2},{3},{4}" +
            "<oA>DRYCYCLE_KVS_A={5},{6},{7}" +
            "<oA>DRYCYCLE_KVS_B={8},{9},{10}" +
            "<oA>DRYCYCLE_KVS_PATTERN={11}" +
            "<oA>DRYCYCLE_KVS_PROFILE={12},{13}",
            baseString,
            SourceSide,
            ArmorColor.r,
            ArmorColor.g,
            ArmorColor.b,
            ColorA.hue,
            ColorA.saturation,
            ColorA.lightness,
            ColorB.hue,
            ColorB.saturation,
            ColorB.lightness,
            PatternDisplace,
            Profile.x,
            Profile.y);
    }
}
