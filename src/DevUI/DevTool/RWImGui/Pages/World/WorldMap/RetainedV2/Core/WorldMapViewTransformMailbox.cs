using System;
using System.Threading;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Lock-free transform handoff. Pan/zoom may publish every render frame without copying scene data.
/// </summary>
internal sealed class WorldMapViewTransformMailbox
{
    private int sequence;
    private float originX;
    private float originY;
    private float sizeX;
    private float sizeY;
    private float panX;
    private float panY;
    private float zoom = 1f;
    private long revision;

    internal void Publish(WorldMapViewTransform value, long viewRevision)
    {
        Interlocked.Increment(ref sequence);
        Volatile.Write(ref originX, value.CanvasOrigin.X);
        Volatile.Write(ref originY, value.CanvasOrigin.Y);
        Volatile.Write(ref sizeX, value.CanvasSize.X);
        Volatile.Write(ref sizeY, value.CanvasSize.Y);
        Volatile.Write(ref panX, value.Pan.X);
        Volatile.Write(ref panY, value.Pan.Y);
        Volatile.Write(ref zoom, value.Zoom);
        Interlocked.Exchange(ref revision, viewRevision);
        Interlocked.Increment(ref sequence);
    }

    internal bool TryRead(out WorldMapViewTransform value, out long viewRevision)
    {
        value = default;
        viewRevision = 0L;

        for (int attempt = 0; attempt < 8; attempt++)
        {
            int before = Volatile.Read(ref sequence);
            if ((before & 1) != 0) continue;

            float nextOriginX = Volatile.Read(ref originX);
            float nextOriginY = Volatile.Read(ref originY);
            float nextSizeX = Volatile.Read(ref sizeX);
            float nextSizeY = Volatile.Read(ref sizeY);
            float nextPanX = Volatile.Read(ref panX);
            float nextPanY = Volatile.Read(ref panY);
            float nextZoom = Volatile.Read(ref zoom);
            long nextRevision = Interlocked.Read(ref revision);

            int after = Volatile.Read(ref sequence);
            if (before != after || (after & 1) != 0)
                continue;

            value = new WorldMapViewTransform(
                new Num.Vector2(nextOriginX, nextOriginY),
                new Num.Vector2(
                    Math.Max(0f, nextSizeX),
                    Math.Max(0f, nextSizeY)),
                new Num.Vector2(nextPanX, nextPanY),
                Math.Max(0.0001f, nextZoom));
            viewRevision = nextRevision;
            return true;
        }

        return false;
    }
}
