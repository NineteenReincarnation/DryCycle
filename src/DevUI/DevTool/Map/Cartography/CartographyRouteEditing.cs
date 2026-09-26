using System;
using System.Linq;

namespace DryCycle.DevUI.DevTool.Map.Cartography;

/// <summary>Route geometry shared by hit testing and author gestures. Endpoints remain source ports.</summary>
internal static class CartographyRouteEditing
{
    internal static int NearestSegment(CartographyPoint[] points, float x, float y, float radius, out CartographyPoint closest)
    {
        int found = -1;
        float best = radius * radius;
        closest = null;
        for (int i = 1; i < points.Length; i++)
        {
            CartographyPoint a = points[i - 1], b = points[i];
            float dx = b.X - a.X, dy = b.Y - a.Y, length = dx * dx + dy * dy;
            float t = length < .000001f ? 0 : Math.Max(0, Math.Min(1, ((x - a.X) * dx + (y - a.Y) * dy) / length));
            float px = a.X + t * dx, py = a.Y + t * dy;
            float distance = (x - px) * (x - px) + (y - py) * (y - py);
            if (distance > best) continue;
            best = distance; found = i - 1; closest = new CartographyPoint { X = px, Y = py };
        }
        return found;
    }

    internal static int NearestHandle(CartographyPoint[] points, float x, float y, float radius)
    {
        int found = -1;
        float best = radius * radius;
        // First/last points are live room ports, never detached author handles.
        for (int i = 1; i < points.Length - 1; i++)
        {
            float dx = x - points[i].X, dy = y - points[i].Y, distance = dx * dx + dy * dy;
            if (distance > best) continue;
            best = distance; found = i - 1;
        }
        return found;
    }

    internal static CartographyItem Manual(CartographyItem item, CartographySceneNode node)
    {
        if (item.Kind != CartographyItemKind.Connection || node.Points.Length < 2)
            throw new InvalidOperationException("A route edit needs a connection with both source endpoints.");
        CartographyItem edit = item.Clone();
        if (edit.Appearance.Route != CartographyRouteMode.Manual)
            edit.Points = node.Points.Skip(1).Take(node.Points.Length - 2).Select(p => p.Clone()).ToList();
        edit.Appearance.Route = CartographyRouteMode.Manual;
        return edit;
    }

    internal static CartographyPoint Drag(float x, float y, CartographyPoint anchor, bool constrainAxis)
    {
        if (constrainAxis)
        {
            if (Math.Abs(x - anchor.X) > Math.Abs(y - anchor.Y)) y = anchor.Y;
            else x = anchor.X;
        }
        return new CartographyPoint { X = x, Y = y };
    }
}
