using UnityEngine;

namespace DryCycle.Creatures.MantleCrab;

/// <summary>Species landmarks measured from the supplied drawing, in shell-local units.
/// These are joint / outline coordinates, not textures or a separate animation rig.</summary>
internal static class MantleCrabAnatomy
{
    // Rear left, rear right, front left, front right. Shell span is approximately 202.
    internal static readonly Vector2[][] Walking =
    [
        [new(-28,-27), new(-54,-93.5f), new(-76,-203), new(-71,-268.5f), new(-67,-301)],
        [new(29.5f,-27.5f), new(46,-88), new(46,-194), new(22,-264.5f), new(25,-301)],
        [new(-23,-27.5f), new(-43,-93.5f), new(-60,-201.5f), new(-47.5f,-265), new(-42.5f,-301)],
        [new(34,-28), new(25.5f,-92.5f), new(36,-192.5f), new(39.5f,-260.5f), new(46,-301)]
    ];
    internal static readonly Vector2[][] Claws =
    [
        [new(-19,-21), new(-10,-94), new(-10.5f,-128), new(-11,-226), new(-10,-273)],
        [new(34,-22), new(24,-88), new(31,-130), new(49,-193), new(66,-267)]
    ];
    internal static Vector2[] Landmarks(int index, bool pincer) => pincer ? Claws[index] : Walking[index];
}
