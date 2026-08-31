namespace DryCycle.Creatures.MossySpider;

/// <summary>
/// Shared rectangular side-profile used by MossySpiderGraphics and the dorsal platform.
/// The torso BodyChunks may still flex underneath, but the painted body no longer tapers
/// into hanging front/rear points. This restores the broad rectangular silhouette.
/// </summary>
internal static class MossySpiderSilhouette
{
    internal const float WalkableStartU = 0f;
    internal const float WalkableEndU = 1f;

    internal static float CarapaceLow(float u) => -31f;
    internal static float CarapaceHigh(float u) => 9f;

    internal static float MossLow(float u) => 12.5f;
    internal static float MossHigh(float u) => MossySpiderDorsalPlane.SurfaceHeight;

    internal static float MossShadowHigh(float u) => 19f;
    internal static float MossCapLow(float u) => 15f;
}
