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
        [new(-34f, -24f), new(-56f, -82f), new(-78f, -188f), new(-68f, -262f), new(-78f, -302f)],
        [new(35f, -25f), new(57f, -84f), new(66f, -190f), new(54f, -263f), new(66f, -302f)],
        [new(-18f, -27f), new(-36f, -94f), new(-52f, -192f), new(-39f, -266f), new(-44f, -302f)],
        [new(20f, -27f), new(34f, -92f), new(48f, -191f), new(38f, -264f), new(45f, -302f)]
    ];

    internal static readonly Vector2[][] Claws = MantleCrabPincerAnatomy.Chains;

    internal static Vector2[] Landmarks(int index, bool pincer) =>
        pincer ? MantleCrabPincerAnatomy.Landmarks(index) : Walking[index];
}
