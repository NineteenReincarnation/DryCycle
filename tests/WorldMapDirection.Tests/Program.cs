using System;
using System.Collections.Generic;
using Num = System.Numerics;
using DryCycle.DevUI.DevTool.RWImGui;

internal static class Program
{
    private static int assertions;

    private static int Main()
    {
        try
        {
            TinyGap();
            RoomOcclusion();
            CanvasClipping();
            LongestVisibleRun();
            FullyOccluded();

            Console.WriteLine(
                "PASS: " + assertions +
                " assertions; World Map direction markers survive tiny links, respect room silhouettes, " +
                "stay inside the canvas and choose the clearest visible route segment.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void TinyGap()
    {
        var path = new[]
        {
            new Num.Vector2(100f, 80f),
            new Num.Vector2(114f, 80f)
        };
        var rooms = new[]
        {
            new Num.Vector4(70f, 60f, 98f, 100f),
            new Num.Vector4(116f, 60f, 150f, 100f)
        };

        Check(
            Resolve(path, Num.Vector2.Zero, new Num.Vector2(920f, 520f), rooms,
                out Num.Vector2 point, out Num.Vector2 tangent, out float scale),
            "A 14 px inter-room corridor must retain a direction marker.");
        Check(
            point.X > 100f && point.X < 114f,
            "Tiny-gap marker must stay between the two room sockets.");
        Check(
            Math.Abs(tangent.X - 1f) < 0.001f &&
            Math.Abs(tangent.Y) < 0.001f,
            "Tiny-gap marker must keep the route tangent.");
        Check(
            scale >= 0.32f && scale < 1f,
            "Tiny-gap marker must shrink instead of disappearing.");
    }

    private static void RoomOcclusion()
    {
        var path = new[]
        {
            new Num.Vector2(0f, 50f),
            new Num.Vector2(180f, 50f)
        };
        var rooms = new[]
        {
            new Num.Vector4(35f, 20f, 125f, 80f)
        };

        Check(
            Resolve(path, Num.Vector2.Zero, new Num.Vector2(180f, 100f), rooms,
                out Num.Vector2 point, out Num.Vector2 _, out float _),
            "A partially occluded route must still find a visible marker run.");
        Check(
            point.X > 127f || point.X < 33f,
            "Marker must not be placed inside the room silhouette.");
    }

    private static void CanvasClipping()
    {
        var path = new[]
        {
            new Num.Vector2(-200f, 50f),
            new Num.Vector2(800f, 50f)
        };
        var rooms = new[]
        {
            new Num.Vector4(0f, 0f, 25f, 100f)
        };

        Check(
            Resolve(path, Num.Vector2.Zero, new Num.Vector2(100f, 100f), rooms,
                out Num.Vector2 point, out Num.Vector2 _, out float _),
            "A route crossing the canvas must resolve a marker inside the visible clip.");
        Check(
            point.X > 30f && point.X < 96f,
            "Clipped marker must stay inside the canvas and outside the room.");
    }

    private static void LongestVisibleRun()
    {
        var path = new[]
        {
            new Num.Vector2(20f, 30f),
            new Num.Vector2(60f, 30f),
            new Num.Vector2(60f, 44f),
            new Num.Vector2(220f, 44f)
        };

        Check(
            Resolve(path, Num.Vector2.Zero, new Num.Vector2(300f, 120f), null,
                out Num.Vector2 point, out Num.Vector2 tangent, out float scale),
            "A stepped route must have a direction marker.");
        Check(
            Math.Abs(point.Y - 44f) < 0.01f,
            "Marker must prefer the longest clear segment instead of crowding the short terminal.");
        Check(
            Math.Abs(tangent.X - 1f) < 0.001f &&
            Math.Abs(tangent.Y) < 0.001f &&
            Math.Abs(scale - 1f) < 0.001f,
            "A long clear segment must use the full-size horizontal marker.");
    }

    private static void FullyOccluded()
    {
        var path = new[]
        {
            new Num.Vector2(20f, 50f),
            new Num.Vector2(80f, 50f)
        };
        var rooms = new[]
        {
            new Num.Vector4(0f, 0f, 100f, 100f)
        };

        Check(
            !Resolve(path, Num.Vector2.Zero, new Num.Vector2(120f, 120f), rooms,
                out Num.Vector2 _, out Num.Vector2 _, out float _),
            "A route with no visible run must not draw a marker over room geometry.");
    }

    private static bool Resolve(
        IReadOnlyList<Num.Vector2> path,
        Num.Vector2 clipMin,
        Num.Vector2 clipMax,
        IReadOnlyList<Num.Vector4> rooms,
        out Num.Vector2 point,
        out Num.Vector2 tangent,
        out float scale) =>
        WorldMapDirectionMarkerGeometry.TryResolve(
            path,
            clipMin,
            clipMax,
            rooms,
            out point,
            out tangent,
            out scale);

    private static void Check(bool value, string message)
    {
        assertions++;
        if (!value)
            throw new InvalidOperationException(message);
    }
}
