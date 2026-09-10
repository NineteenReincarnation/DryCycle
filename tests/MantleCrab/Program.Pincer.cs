using System;
using DryCycle.Creatures.MantleCrab;
using UnityEngine;

internal static partial class Program
{
    private static void PincerTests()
    {
        Check(MantleCrabPincerAnatomy.Chains.Length == 2,
            "MantleCrab must expose two dedicated pincer anatomy chains");

        for (int i = 0; i < MantleCrabPincerAnatomy.Chains.Length; i++)
        {
            Vector2[] chain = MantleCrabPincerAnatomy.Chains[i];
            Check(chain.Length == MantleCrabPincerAnatomy.SegmentCount + 1,
                "Pincer " + i + " must have four arm segments plus its root landmark");
            Check(HasArticulation(chain),
                "Pincer " + i + " lost the authored multi-bend silhouette");

            for (int segment = 1; segment < MantleCrabPincerAnatomy.SegmentCount; segment++)
            {
                Check(MantleCrabPincerAnatomy.SegmentWidth(i, segment) <
                      MantleCrabPincerAnatomy.SegmentWidth(i, segment - 1),
                    "Pincer " + i + " shaft widths must taper toward the wrist");
            }

            for (int joint = 0; joint < 3; joint++)
            {
                Check(MantleCrabPincerAnatomy.JointWidth(i, joint) >
                      MantleCrabPincerAnatomy.SegmentWidth(i, joint + 1),
                    "Pincer " + i + " joint plate must remain wider than the adjacent distal shaft");
            }

            Check(MantleCrabPincerAnatomy.PalmWidths[i] >
                  MantleCrabPincerAnatomy.SegmentWidth(i, 3) * 1.7f,
                "Pincer " + i + " palm must flare visibly beyond the wrist shaft");
            Check(MantleCrabPincerAnatomy.FingerLengths[i] < MantleCrabPincerAnatomy.PalmLengths[i],
                "Pincer " + i + " digit must remain shorter than the manus instead of becoming a terminal needle");
            Check(MantleCrabPincerAnatomy.FingerLengths[i] < 22f,
                "Pincer " + i + " fingers regressed beyond the V3 compact-chela envelope");
            Check(Math.Abs(MantleCrabPincerAnatomy.PalmRestAngleDegrees[i]) >= 4f,
                "Pincer " + i + " lost the carpal angle separating palm from terminal shaft");
        }

        Check(!SimpleMirror(MantleCrabPincerAnatomy.Chains[0], MantleCrabPincerAnatomy.Chains[1]),
            "Dedicated pincer rest poses must preserve left/right asymmetry");
        Check(MantleCrabPincerAnatomy.PalmWidths[1] > MantleCrabPincerAnatomy.PalmWidths[0],
            "Right chela must retain the stronger heterochelous palm");

        MantleCrab crab = Empty<MantleCrab>();
        crab.ShellScale = 1f;
        crab.bodyChunks = new BodyChunk[5];
        Vector2 shellOffset = new(200f, 340f);
        for (int i = 0; i < crab.bodyChunks.Length; i++)
        {
            BodyChunk chunk = Empty<BodyChunk>();
            chunk.pos = chunk.lastPos = MantleCrab.ShellRest[i] + shellOffset;
            crab.bodyChunks[i] = chunk;
        }

        for (int index = 0; index < 2; index++)
        {
            MantleCrabPincerRig rig = new(index);
            Vector2 root = MantleCrabPincerAnatomy.Chains[index][0];
            Vector2 anchor = crab.bodyChunks[2].pos + crab.Axis * root.x +
                             new Vector2(-crab.Axis.y, crab.Axis.x) * root.y;
            rig.Reset(crab, anchor);
            rig.TargetOpen = .82f;

            for (int tick = 0; tick < 80; tick++)
            {
                Vector2 movingAnchor = anchor + new Vector2(Mathf.Sin(tick * .08f) * 3f, Mathf.Cos(tick * .05f) * 2f);
                rig.Update(crab, movingAnchor);
            }

            Vector2 previous = rig.Anchor;
            for (int segment = 0; segment < MantleCrabPincerAnatomy.SegmentCount; segment++)
            {
                Check(Finite(rig.Pos[segment]),
                    "Pincer rig produced a non-finite joint at segment=" + segment);
                float drift = Math.Abs(Vector2.Distance(previous, rig.Pos[segment]) - rig.Lengths[segment]);
                Check(drift < .01f,
                    "Pincer rig lost rigid segment length at segment=" + segment + "; drift=" + drift);
                previous = rig.Pos[segment];
            }

            Vector2 shaftAxis = (rig.Pos[3] - rig.Pos[2]).normalized;
            Vector2 palmAxis = rig.PalmAxis(1f);
            Check(Math.Abs(shaftAxis.x * palmAxis.y - shaftAxis.y * palmAxis.x) > .05f,
                "Pincer carpal joint collapsed back into a straight continuation of the arm");
            Check(rig.Open > MantleCrabPincerAnatomy.IdleOpen[index] + .4f,
                "Pincer open channel failed to follow its animation target");
        }
    }
}
