using UnityEngine;

namespace DryCycle.Creatures.MantleCrab;

/// <summary>
/// Authored anatomy for the two long capture appendages. Walking legs and pincers deliberately
/// do not share silhouette data: they only share low-level articulated-chain math. Each pincer
/// consists of four rigid arm segments ending at a wrist, followed by a separately articulated
/// palm and two fingers owned by the renderer/animation layer.
/// </summary>
internal static class MantleCrabPincerAnatomy
{
    internal const int SegmentCount = 4;

    // Reference-oriented rest silhouettes. They are intentionally not exact mirror copies: both
    // appendages share the same anatomical plan while keeping the asymmetric, multi-bend hanging
    // posture visible in the source design. The right capture arm carries the stronger distal bend.
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

    // Capture-arm joints are narrow overlapping collars, not the broad bearing capsules used by
    // the walking legs. They only slightly exceed the adjoining shaft width, matching the source
    // design's long red articulated appendages instead of creating conspicuous horizontal knobs.
    internal static readonly float[][] JointWidths =
    [
        [5.25f, 4.70f, 4.15f],
        [5.65f, 5.05f, 4.50f]
    ];

    // V3 chela proportions. The left appendage keeps the source's slender fork-like chela, while
    // the right appendage is distinctly more massive. Both still obey true manus-first anatomy:
    // the palm is longer than either digit and the digits curve around a real cavity.
    internal static readonly float[] PalmLengths = [20.0f, 27.0f];
    internal static readonly float[] PalmWidths = [5.55f, 8.40f];
    internal static readonly float[] FingerLengths = [15.0f, 20.0f];
    internal static readonly float[] FingerWidths = [2.25f, 3.35f];

    // The palm is not merely a continuation of the last shaft. A small authored carpal angle gives
    // the left claw a restrained inward set and the right claw the pronounced reference-like bend.
    internal static readonly float[] PalmRestAngleDegrees = [-5.5f, 12.0f];
    internal static readonly float[] IdleOpen = [.11f, .18f];

    internal static Vector2[] Landmarks(int index) => Chains[index];
    internal static float SegmentWidth(int index, int segment) => SegmentWidths[index][segment];
    internal static float JointWidth(int index, int joint) => JointWidths[index][joint];
}
