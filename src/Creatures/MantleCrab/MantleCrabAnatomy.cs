using UnityEngine;

namespace DryCycle.Creatures.MantleCrab;

/// <summary>
/// Species landmarks measured in shell-local units. These points describe the visual/IK
/// skeleton only; the shell BodyChunks remain the collision and load-bearing body.
/// Walking limbs are ordered rear-left, rear-right, front-left, front-right.
/// Every chain is root -> proximal segment -> knee/elbow -> ankle/wrist -> terminal tip.
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

    // The red appendages remain very long, but V2 gives them an actual elbow and wrist.
    // Their total reach stays close to the previous implementation so this is an appearance
    // correction rather than a gameplay reach change.
    internal static readonly Vector2[][] Claws =
    [
        [new(-22f, -23f), new(-34f, -78f), new(-20f, -132f), new(-35f, -214f), new(-40f, -278f)],
        [new(28f, -23f), new(16f, -82f), new(31f, -133f), new(47f, -207f), new(61f, -275f)]
    ];

    internal static Vector2[] Landmarks(int index, bool pincer) => pincer ? Claws[index] : Walking[index];
}
