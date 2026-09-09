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

        Check(crab.TrySampleWalkableSurface(0f, out WalkableSurfaceSample center),
            "MantleCrab walkable surface must include its centre");
        Check(crab.TrySampleWalkableSurface(-70f, out WalkableSurfaceSample left),
            "MantleCrab walkable surface lost the left standing region");
        Check(crab.TrySampleWalkableSurface(70f, out WalkableSurfaceSample right),
            "MantleCrab walkable surface lost the right standing region");
        Check(!crab.TrySampleWalkableSurface(90f, out _),
            "Decorative MantleCrab wing tip became walkable ground");

        Check(center.Point.y > left.Point.y && center.Point.y > right.Point.y,
            "Walkable shell crown must stay gently higher than its edges");
        Check(Math.Abs(left.Point.y - right.Point.y) < .01f,
            "Neutral MantleCrab walkable surface should remain symmetric");

        foreach (WalkableSurfaceSample sample in new[] { left, center, right })
        {
            Check(Finite(sample.Point) && Finite(sample.PreviousPoint) &&
                  Finite(sample.Normal) && Finite(sample.Tangent),
                "Walkable surface sample produced non-finite geometry");
            Check(Math.Abs(sample.Normal.magnitude - 1f) < .001f,
                "Walkable surface normal must be normalized");
            Check(Math.Abs(sample.Tangent.magnitude - 1f) < .001f,
                "Walkable surface tangent must be normalized");
            Check(Math.Abs(Vector2.Dot(sample.Normal, sample.Tangent)) < .001f,
                "Walkable surface normal/tangent lost orthogonality");
            Check(sample.Normal.y > .8f,
                "MantleCrab standing region became too steep for player ground semantics");
            platformSurfaceCases++;
        }

        Vector2 translation = new(6f, 3f);
        for (int i = 0; i < crab.bodyChunks.Length; i++)
        {
            crab.bodyChunks[i].lastPos = crab.bodyChunks[i].pos;
            crab.bodyChunks[i].pos += translation;
        }

        Check(crab.TrySampleWalkableSurface(0f, out WalkableSurfaceSample moved),
            "Moving MantleCrab surface could not be re-sampled by provider coordinate");
        Check(Vector2.Distance(moved.Velocity, translation) < .01f,
            "Walkable surface did not preserve rigid platform translation for rider carry");
        platformSurfaceCases++;

        // A rotated rigid frame must rotate the standing normal/tangent without changing shape.
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

        Check(crab.TrySampleWalkableSurface(0f, out WalkableSurfaceSample rotated),
            "Rotated MantleCrab surface could not be sampled");
        Check(rotated.Normal.y > .8f && rotated.Normal.x < 0f,
            "Walkable surface normal failed to follow rigid shell rotation");
        platformSurfaceCases++;
    }
}
