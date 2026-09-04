using System.Globalization;
using UnityEngine;

namespace DryCycle.Items.RopeSpear;

internal sealed class AbstractRopeHandle : AbstractPhysicalObject
{
    internal const string ParentPrefix = "DRYCYCLE_ROPEHANDLE_PARENT=";
    internal const string AnchoredPrefix = "DRYCYCLE_ROPEHANDLE_ANCHORED=";
    internal const string AnchorPrefix = "DRYCYCLE_ROPEHANDLE_ANCHOR=";

    internal EntityID ParentSpearID;
    internal bool Anchored;
    internal Vector2 AnchorPosition;

    internal AbstractRopeHandle(
        World world,
        WorldCoordinate pos,
        EntityID id,
        EntityID parentSpearID,
        bool anchored = false,
        Vector2 anchorPosition = default)
        : base(world, RopeSpearHooks.HandleObjectType, null, pos, id)
    {
        ParentSpearID = parentSpearID;
        Anchored = anchored;
        AnchorPosition = anchorPosition;
    }

    public override string ToString()
    {
        string baseString = base.ToString();
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}<oA>{1}{2}<oA>{3}{4}<oA>{5}{6},{7}",
            baseString,
            ParentPrefix,
            ParentSpearID,
            AnchoredPrefix,
            Anchored ? 1 : 0,
            AnchorPrefix,
            AnchorPosition.x,
            AnchorPosition.y);
    }
}
