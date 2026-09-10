using UnityEngine;

namespace DryCycle.Creatures.MantleCrab;

/// <summary>
/// Species landmarks for load-bearing walking legs. Capture-appendage anatomy lives in
/// MantleCrabPincerAnatomy; the Claws alias is retained only for test/tool compatibility so there
/// is still one authoritative pincer silhouette definition.
/// </summary>
internal static class MantleCrabAnatomy
{
    // V2 silhouette contract: preserve the roughly 300 px hanging stance while making the
    // four legs read as articulated limbs rather than mirrored vertical rods. The outer pair
    // carries a wider stance; the inner pair stays closer to the body and the knees are
    // deliberately staggered.
    internal static readonly Vector2[][] Walking =
    [
        [new(-34f, -24f), new(-59f, -97f), new(-79f, -204f), new(-72f, -266f), new(-69f, -302f)],
        [new(35f, -25f), new(43f, -91f), new(47f, -194f), new(38f, -260f), new(42f, -302f)],
        [new(-18f, -27f), new(-47f, -99f), new(-61f, -202f), new(-49f, -268f), new(-42f, -302f)],
        [new(20f, -27f), new(32f, -96f), new(27f, -194f), new(17f, -260f), new(22f, -302f)]
    ];

    internal static readonly Vector2[][] Claws = MantleCrabPincerAnatomy.Chains;

    internal static Vector2[] Landmarks(int index, bool pincer) =>
        pincer ? MantleCrabPincerAnatomy.Landmarks(index) : Walking[index];
}
