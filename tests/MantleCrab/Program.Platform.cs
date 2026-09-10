using System;
using DryCycle.Creatures.MantleCrab;
using DryCycle.Creatures.Platforming;
using UnityEngine;

internal static partial class Program
{
    private static void PlatformSurfaceTests()
    {
        MantleCrab crab = Empty<MantleCrab>();
        crab.ShellScale = 1f;
        crab.bodyChunks = new BodyChunk[5];

        for (int i = 0; i < crab.bodyChunks.Length; i++)
        {
            BodyChunk chunk = Empty<BodyChunk>();
            chunk.mass = i == 2 ? 3f : 1.8f;
            chunk.pos = MantleCrab.ShellRest[i] + new Vector2(300f, 420f);
            chunk.lastPos = chunk.pos;
            chunk.vel = Vector2.zero;
            crab.bodyChunks[i] = chunk;
        }

        Check(crab.TrySampleWalkableSurface(.5f, out WalkableSurfaceSample center),
            "MantleCrab finite curve must include its centre");
        Check(crab.TrySampleWalkableSurface(.15f, out WalkableSurfaceSample left),
            "MantleCrab finite curve lost the left shoulder");
        Check(crab.TrySampleWalkableSurface(.85f, out WalkableSurfaceSample right),
            "MantleCrab finite curve lost the right shoulder");
        Check(crab.TrySampleWalkableSurface(0f, out WalkableSurfaceSample leftEdge) &&
              crab.TrySampleWalkableSurface(1f, out WalkableSurfaceSample rightEdge),
            "MantleCrab finite curve must expose both physical endpoints");

        Check(center.Point.y > left.Point.y && center.Point.y > right.Point.y,
            "MantleCrab physical crown must stay higher than its shoulders");
        Check(center.Point.y - leftEdge.Point.y > 20f && center.Point.y - rightEdge.Point.y > 20f,
            "MantleCrab physical curve no longer follows the visible crown-to-edge drop");
        Check(Math.Abs(left.Point.y - right.Point.y) < .01f &&
              Math.Abs(leftEdge.Point.y - rightEdge.Point.y) < .01f,
            "Neutral MantleCrab physical curve should remain symmetric");

        foreach (WalkableSurfaceSample sample in new[] { left, center, right, leftEdge, rightEdge })
        {
            Check(Finite(sample.Point) && Finite(sample.PreviousPoint) &&
                  Finite(sample.Normal) && Finite(sample.Tangent),
                "Dynamic curve sample produced non-finite geometry");
            Check(Math.Abs(sample.Normal.magnitude - 1f) < .001f,
                "Dynamic curve normal must be normalized");
            Check(Math.Abs(sample.Tangent.magnitude - 1f) < .001f,
                "Dynamic curve tangent must be normalized");
            Check(Math.Abs(Vector2.Dot(sample.Normal, sample.Tangent)) < .001f,
                "Dynamic curve normal/tangent lost orthogonality");
            platformSurfaceCases++;
        }

        // World-space queries beyond a finite endpoint must resolve to that endpoint rather than
        // extending the last segment into an invisible platform. The radial endpoint normal is
        // what lets a circular BodyChunk roll off the shell edge naturally.
        Vector2 outsideProbe = rightEdge.Point + new Vector2(12f, 8f);
        Check(crab.TrySampleWalkableSurface(outsideProbe, out WalkableSurfaceSample endpointContact),
            "Finite MantleCrab curve could not resolve an endpoint contact");
        Check(endpointContact.Coordinate > .999f,
            "Finite curve projection extended beyond the physical right endpoint");
        Check(endpointContact.Normal.x > .7f && endpointContact.Normal.y > .35f && endpointContact.Normal.y < .75f,
            "Finite curve endpoint did not produce the expected radial roll-off normal");
        Check(Math.Abs(Vector2.Dot(endpointContact.Normal, endpointContact.Tangent)) < .001f,
            "Endpoint radial normal/tangent lost orthogonality");
        platformSurfaceCases++;

        Vector2 translation = new(6f, 3f);
        for (int i = 0; i < crab.bodyChunks.Length; i++)
        {
            crab.bodyChunks[i].lastPos = crab.bodyChunks[i].pos;
            crab.bodyChunks[i].pos += translation;
        }

        Check(crab.TrySampleWalkableSurface(.5f, out WalkableSurfaceSample moved),
            "Moving MantleCrab curve could not be re-sampled by normalized provider coordinate");
        Check(Vector2.Distance(moved.Velocity, translation) < .01f,
            "Dynamic curve did not preserve rigid platform translation for rider carry");
        platformSurfaceCases++;

        // A rotated rigid frame must rotate the curve normal/tangent without changing shape.
        float angle = 12f * Mathf.Deg2Rad;
        Vector2 axis = new(Mathf.Cos(angle), Mathf.Sin(angle));
        Vector2 up = new(-axis.y, axis.x);
        Vector2 pivot = new(520f, 360f);
        for (int i = 0; i < crab.bodyChunks.Length; i++)
        {
            Vector2 local = MantleCrab.ShellRest[i];
            crab.bodyChunks[i].lastPos = crab.bodyChunks[i].pos;
            crab.bodyChunks[i].pos = pivot + axis * local.x + up * local.y;
        }

        Check(crab.TrySampleWalkableSurface(.5f, out WalkableSurfaceSample rotated),
            "Rotated MantleCrab curve could not be sampled");
        Check(rotated.Normal.y > .8f && rotated.Normal.x < 0f,
            "Dynamic curve normal failed to follow rigid shell rotation");
        platformSurfaceCases++;
    }
}
