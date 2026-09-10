namespace DryCycle.Creatures.MantleCrab.Rendering;

/// <summary>
/// Authoritative sprite allocation for MantleCrab. Rendering code should address anatomy through
/// these helpers instead of duplicating arithmetic offsets; this keeps appendage layering and
/// future mesh expansion independent from the creature's physics indices.
/// </summary>
internal static class MantleCrabSpriteLayout
{
    internal const int LegCount = 4;
    internal const int LegStride = 8;
    internal const int LegShaftCount = 4;
    internal const int LegJointCount = 3;
    internal const int LegStart = 0;

    internal const int ThreadCount = 28;
    internal const int ThreadStart = LegStart + LegCount * LegStride;
    internal const int Shell = ThreadStart + ThreadCount;

    internal const int PincerCount = 2;
    internal const int PincerStride = 9;
    internal const int PincersStart = Shell + 1;
    internal const int EyeStalkStart = PincersStart + PincerCount * PincerStride;
    internal const int EyesStart = EyeStalkStart + 2;
    internal const int SpriteCount = EyesStart + 2;

    internal static int LegShaft(int leg, int segment) => LegStart + leg * LegStride + segment;
    internal static int LegJoint(int leg, int joint) => LegStart + leg * LegStride + 4 + joint;
    internal static int LegFoot(int leg) => LegStart + leg * LegStride + 7;
    internal static int Thread(int thread) => ThreadStart + thread;

    internal static int PincerStart(int pincer) => PincersStart + pincer * PincerStride;
    internal static int PincerShaft(int pincer, int segment) =>
        PincerStart(pincer) + (segment < 3 ? segment : 6);
    internal static int PincerJoint(int pincer, int joint) => PincerStart(pincer) + 3 + joint;
    internal static int PincerPalm(int pincer) => PincerStart(pincer) + 7;
    internal static int PincerMovableFinger(int pincer) => PincerStart(pincer) + 8;
    internal static int EyeStalk(int eye) => EyeStalkStart + eye;
    internal static int Eye(int eye) => EyesStart + eye;

    internal static bool IsLegSprite(int index) => index >= LegStart && index < ThreadStart;
    internal static bool IsRearLeg(int leg) => leg < 2;
}
