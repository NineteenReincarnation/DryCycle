using UnityEngine;

namespace DryCycle.Creatures.MantleCrab.Rendering;

internal sealed class MantleCrabVisualPhenotype
{
    internal readonly float ShellWidth, WingAngle, EdgeRoughness, FootBulk, JointBulk;
    internal readonly float MotifScale, MotifSharpness, MotifContrast, MotifWarp, Fragmentation;
    internal readonly float StripePhase, Curvature, Asymmetry, ChitinRoughness, LegDetail, PincerAccent;
    internal readonly float EyeSize, EyeAspect, EyeCore, EyeWarmth, Hue;
    internal readonly Vector4 NoiseSeed;

    internal MantleCrabVisualPhenotype(MantleCrabVisualGenome g)
    {
        ShellWidth = .96f + .065f * g.Channel("ShellWidth") + .015f * g.Dominance;
        WingAngle = Mathf.Lerp(-.035f, .035f, g.Channel("WingAngle"));
        EdgeRoughness = .5f + .8f * g.Channel("EdgeRoughness");
        FootBulk = .92f + .16f * g.Channel("FootBulk") + .08f * g.Dominance;
        JointBulk = .95f + .12f * g.Channel("JointBulk") + .05f * g.Dominance;
        MotifScale = 3.5f + 2.5f * g.Channel("StripeCount");
        MotifSharpness = 1.1f + .8f * g.Channel("StripeSharpness") + .45f * g.Aggression;
        MotifContrast = .55f + .28f * g.Channel("StripeContrast") + .17f * g.Bravery;
        MotifWarp = .08f + .15f * g.Channel("StripeWarp") + .08f * g.Nervous;
        Fragmentation = .07f + .15f * g.Channel("StripeBreaks") + .12f * g.Nervous - .04f * g.Bravery;
        StripePhase = g.Channel("StripePhase") * 6.283185f;
        Curvature = .35f + .3f * g.Channel("StripeCurvature") + .15f * g.Sympathy;
        Asymmetry = .02f + .03f * g.Channel("Asymmetry") + .01f * g.Nervous;
        ChitinRoughness = .68f + .18f * g.Channel("ChitinRoughness");
        LegDetail = 7f + 5f * g.Channel("LegDetail") + 3f * g.Energy;
        PincerAccent = .2f + .35f * g.Channel("PincerAccent") + .2f * g.Aggression;
        EyeSize = 5.7f + .7f * g.Channel("EyeSize");
        EyeAspect = 1.35f + .2f * g.Channel("EyeAspect");
        EyeCore = .16f + .08f * g.Channel("EyeCore");
        EyeWarmth = g.Channel("EyeWarmth");
        Hue = (g.Channel("ShellHue") - .5f) * .035f;
        NoiseSeed = new Vector4(g.Channel("PlateSeed") * 31f, g.Channel("PoreSeed") * 31f,
            g.Channel("LeftMutation") * 13f, g.Channel("RightMutation") * 13f);
    }
}
