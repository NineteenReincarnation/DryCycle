using System.Globalization;

namespace DryCycle.Items.RopeSpear;

internal sealed class AbstractRopeSpear : AbstractSpear
{
    public const float DefaultRopeLength = 260f;
    public const float MinRopeLength = 65f;
    public const float MaxRopeLength = 360f;

    public float RopeLength;
    public bool RopeBroken;

    public AbstractRopeSpear(
        World world,
        WorldCoordinate pos,
        EntityID id,
        float ropeLength = DefaultRopeLength,
        bool ropeBroken = false)
        : base(world, null, pos, id, explosive: false)
    {
        type = RopeSpearHooks.ObjectType;
        RopeLength = UnityEngine.Mathf.Clamp(ropeLength, MinRopeLength, MaxRopeLength);
        RopeBroken = ropeBroken;
    }

    public override string ToString()
    {
        string baseString = base.ToString();
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}<oA>DRYCYCLE_ROPESPEAR_LENGTH={1}<oA>DRYCYCLE_ROPESPEAR_BROKEN={2}",
            baseString,
            RopeLength,
            RopeBroken ? 1 : 0);
    }
}
