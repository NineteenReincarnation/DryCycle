using System.Globalization;
using UnityEngine;

namespace DryCycle.Items.RopeSpear;

internal sealed class AbstractRopeSpear : AbstractSpear
{
    public const float DefaultRopeLength = 260f;
    public const float MinRopeLength = 65f;

    // Rope is allowed to pay out freely during a throw. Keep the authored/runtime
    // ceiling well above any normal Rain World room span so projectile flight is
    // never range-limited by the old 360 px cap. Reeling can still shorten it all
    // the way back to MinRopeLength.
    public const float MaxRopeLength = 10000f;

    internal const string FixedHandlePrefix = "DRYCYCLE_ROPESPEAR_FIXED_HANDLE=";
    internal const string FixedHandleAnchorPrefix = "DRYCYCLE_ROPESPEAR_FIXED_ANCHOR=";

    public float RopeLength;
    public bool RopeBroken;
    public bool HasPersistentHandleAnchor;
    public Vector2 PersistentHandleAnchor;

    public AbstractRopeSpear(
        World world,
        WorldCoordinate pos,
        EntityID id,
        float ropeLength = DefaultRopeLength,
        bool ropeBroken = false)
        : base(world, null, pos, id, explosive: false)
    {
        type = RopeSpearHooks.ObjectType;
        RopeLength = Mathf.Clamp(ropeLength, MinRopeLength, MaxRopeLength);
        RopeBroken = ropeBroken;
        HasPersistentHandleAnchor = false;
        PersistentHandleAnchor = Vector2.zero;
    }

    public override string ToString()
    {
        string baseString = base.ToString();
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}<oA>DRYCYCLE_ROPESPEAR_LENGTH={1}" +
            "<oA>DRYCYCLE_ROPESPEAR_BROKEN={2}" +
            "<oA>{3}{4}" +
            "<oA>{5}{6},{7}",
            baseString,
            RopeLength,
            RopeBroken ? 1 : 0,
            FixedHandlePrefix,
            HasPersistentHandleAnchor ? 1 : 0,
            FixedHandleAnchorPrefix,
            PersistentHandleAnchor.x,
            PersistentHandleAnchor.y);
    }
}
