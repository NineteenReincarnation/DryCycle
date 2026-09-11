using UnityEngine;

namespace DryCycle.Creatures.MantleCrab;

/// <summary>
/// Species landmarks for load-bearing walking legs. Capture-appendage anatomy lives in
/// MantleCrabPincerAnatomy; the Claws alias is retained only for test/tool compatibility so there
/// is still one authoritative pincer silhouette definition.
/// </summary>
internal static class MantleCrabAnatomy
{
    // V3：保持约 300 px 的整体站高，但不再让腿在静止时接近 100% 伸直。
    // 四条腿增加明显的侧向折角，使正常站姿落在约 83%~87% 的总 Reach 内，给迈步、受力和地形变化留下真实关节余量。
    // V3 keeps the roughly 300 px body clearance but no longer uses an almost fully extended rest chain.
    // The stronger lateral bends place the normal stance around 83%~87% of total reach, leaving real joint reserve.
    internal static readonly Vector2[][] Walking =
    [
        [new(-34f, -24f), new(-90f, -90f), new(-135f, -180f), new(-90f, -250f), new(-69f, -302f)],
        [new(35f, -25f), new(86f, -88f), new(130f, -180f), new(82f, -250f), new(42f, -302f)],
        [new(-18f, -27f), new(-72f, -94f), new(-108f, -184f), new(-68f, -251f), new(-42f, -302f)],
        [new(20f, -27f), new(69f, -92f), new(102f, -184f), new(62f, -250f), new(22f, -302f)]
    ];

    internal static readonly Vector2[][] Claws = MantleCrabPincerAnatomy.Chains;

    internal static Vector2[] Landmarks(int index, bool pincer) =>
        pincer ? MantleCrabPincerAnatomy.Landmarks(index) : Walking[index];
}
