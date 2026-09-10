using System.Collections.Generic;

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

    internal const int ThreadCount = 22;
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

    // Shared by Futile and the offscreen production-mesh preview; allocation order is not depth.
    internal static IEnumerable<int> DrawOrder()
    {
        for (int leg = 0; leg < LegCount; leg++)
        {
            for (int shaft = 0; shaft < LegShaftCount; shaft++) yield return LegShaft(leg, shaft);
            yield return LegFoot(leg);
            for (int joint = 0; joint < LegJointCount; joint++) yield return LegJoint(leg, joint);
        }
        for (int pincer = 0; pincer < PincerCount; pincer++) yield return PincerShaft(pincer, 0);
        for (int thread = 0; thread < ThreadCount; thread++) yield return Thread(thread);
        yield return Shell;
        for (int pincer = 0; pincer < PincerCount; pincer++)
        {
            for (int shaft = 1; shaft < 4; shaft++) yield return PincerShaft(pincer, shaft);
            yield return PincerPalm(pincer);
            yield return PincerMovableFinger(pincer);
            for (int joint = 0; joint < 3; joint++) yield return PincerJoint(pincer, joint);
        }
        for (int eye = 0; eye < 2; eye++) { yield return EyeStalk(eye); yield return Eye(eye); }
    }
}
