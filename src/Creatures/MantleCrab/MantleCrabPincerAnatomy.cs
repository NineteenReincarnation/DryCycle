using UnityEngine;

namespace DryCycle.Creatures.MantleCrab;

/// <summary>
/// Authored anatomy for the two long capture appendages. Walking legs and pincers deliberately
/// do not share silhouette data: they only share low-level articulated-chain math. Each pincer
/// consists of three arm plates and a fourth, terminal chela. The last control point describes
/// the chela direction/tip; it is not the wrist of an additional fifth segment.
/// </summary>
internal static class MantleCrabPincerAnatomy
{
    internal const int SegmentCount = 4;

    // Reference-oriented rest silhouettes. They are intentionally not exact mirror copies: both
    // appendages share the same anatomical plan while keeping the asymmetric, multi-bend hanging
    // posture visible in the source design. The right capture arm carries the stronger distal bend.
    internal static readonly Vector2[][] Chains =
    [
        [new(-23f, -24f), new(-13f, -94f), new(-15f, -132f), new(-13f, -233f), new(-16f, -280f)],
        [new(27f, -24f), new(24f, -89f), new(46f, -197f), new(53f, -223f), new(65f, -275f)]
    ];

    // Half-widths for the four arm shafts. The first segment leaves the mantle with visible mass,
    // then the limb narrows toward the wrist instead of reading as a uniform wire.
    internal static readonly float[][] SegmentWidths =
    [
        [3.7f, 3.25f, 3.15f, 2.65f],
        [4.0f, 3.8f, 3.5f, 3.05f]
    ];

    // Capture-arm joints are narrow overlapping collars, not the broad bearing capsules used by
    // the walking legs. They only slightly exceed the adjoining shaft width, matching the source
    // design's long red articulated appendages instead of creating conspicuous horizontal knobs.
    internal static readonly float[][] JointWidths =
    [
        [3.5f, 3.35f, 3.2f],
        [4.0f, 3.9f, 3.65f]
    ];

    // The slender left chela has a long narrow plate and short distal tips. The larger right
    // chela has a short angular palm and long curved blades. Neither has a swollen round palm.
    internal static readonly float[] PalmLengths = [28.0f, 13.0f];
    internal static readonly float[] PalmWidths = [3.7f, 6.0f];
    internal static readonly float[] FingerLengths = [18.0f, 39.0f];
    internal static readonly float[] FingerWidths = [1.45f, 2.8f];

    // The palm is not merely a continuation of the last shaft. A small authored carpal angle gives
    // the left claw a restrained inward set and the right claw the pronounced reference-like bend.
    internal static readonly float[] PalmRestAngleDegrees = [-5.5f, 6.0f];
    internal static readonly float[] IdleOpen = [.12f, .48f];

    internal static Vector2[] Landmarks(int index) => Chains[index];
    internal static float SegmentWidth(int index, int segment) => SegmentWidths[index][segment];
    internal static float JointWidth(int index, int joint) => JointWidths[index][joint];
}
