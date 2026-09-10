using UnityEngine;

namespace DryCycle.Creatures.MantleCrab;

/// <summary>
/// Authored anatomy for the two long capture appendages. Walking legs and pincers deliberately
/// do not share silhouette data: they only share low-level articulated-chain math. Each pincer
/// consists of four rigid arm segments ending at a wrist, followed by a separate palm and two
/// fingers owned by the renderer/animation layer.
/// </summary>
internal static class MantleCrabPincerAnatomy
{
    internal const int SegmentCount = 4;

    // Reference-oriented rest silhouettes. They are intentionally not exact mirror copies: both
    // appendages share the same anatomical plan while keeping the asymmetric, multi-bend hanging
    // posture visible in the source design.
    internal static readonly Vector2[][] Chains =
    [
        [new(-23f, -24f), new(-31f, -82f), new(-18f, -151f), new(-31f, -224f), new(-27f, -263f)],
        [new(27f, -24f), new(34f, -86f), new(19f, -151f), new(39f, -221f), new(52f, -258f)]
    ];

    // Half-widths for the four arm shafts. The first segment leaves the mantle with visible mass,
    // then the limb narrows toward the wrist instead of reading as a uniform wire.
    internal static readonly float[][] SegmentWidths =
    [
        [4.8f, 4.15f, 3.55f, 2.85f],
        [5.15f, 4.4f, 3.8f, 3.05f]
    ];

    // Joint plates are wider than their adjacent shafts so every articulation remains readable at
    // Rain World's camera scale. The root connection is hidden under the mantle and is not drawn as
    // a separate plate; these values correspond to the elbow/intermediate/wrist-side joints.
    internal static readonly float[][] JointWidths =
    [
        [6.7f, 6.05f, 5.35f],
        [7.0f, 6.35f, 5.65f]
    ];

    internal static readonly float[] PalmLengths = [17.5f, 19f];
    internal static readonly float[] PalmWidths = [6.0f, 6.7f];
    internal static readonly float[] FingerLengths = [23f, 25f];
    internal static readonly float[] FingerWidths = [2.75f, 3.05f];
    internal static readonly float[] IdleOpen = [.20f, .24f];

    internal static Vector2[] Landmarks(int index) => Chains[index];
    internal static float SegmentWidth(int index, int segment) => SegmentWidths[index][segment];
    internal static float JointWidth(int index, int joint) => JointWidths[index][joint];
}
