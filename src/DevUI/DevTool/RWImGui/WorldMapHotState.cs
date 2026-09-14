using System;
using System.Reflection;
using UnityEngine;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Shared once-per-frame snapshot of the private WorldMapView interaction state.
///
/// Several compatibility plugins need pan/zoom/layer visibility, and reading those private static
/// fields through reflection at every room/hover/background query creates avoidable boxing and
/// reflection traffic. Centralizing the read keeps the compatibility boundary while reducing it to
/// at most one reflected state capture per Unity frame.
/// </summary>
internal static class WorldMapHotState
{
    private static readonly FieldInfo PanField;
    private static readonly FieldInfo ZoomField;
    private static readonly FieldInfo LayerVisibleField;

    private static int capturedFrame = int.MinValue;
    private static Num.Vector2 pan = Num.Vector2.Zero;
    private static float zoom = 1f;
    private static int layerMask = 7;

    static WorldMapHotState()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        Type mapType = typeof(WorldMapView);
        PanField = mapType.GetField("pan", flags);
        ZoomField = mapType.GetField("zoom", flags);
        LayerVisibleField = mapType.GetField("layerVisible", flags);
    }

    internal static Num.Vector2 Pan
    {
        get
        {
            Capture();
            return pan;
        }
    }

    internal static float Zoom
    {
        get
        {
            Capture();
            return zoom;
        }
    }

    internal static int LayerMask
    {
        get
        {
            Capture();
            return layerMask;
        }
    }

    internal static void Invalidate() => capturedFrame = int.MinValue;

    private static void Capture()
    {
        int frame = Time.frameCount;
        if (capturedFrame == frame) return;
        capturedFrame = frame;

        try
        {
            pan = PanField?.GetValue(null) is Num.Vector2 p ? p : Num.Vector2.Zero;
            zoom = ZoomField?.GetValue(null) is float z ? Math.Max(0.0001f, z) : 1f;

            bool[] layers = LayerVisibleField?.GetValue(null) as bool[];
            int mask = 0;
            for (int i = 0; i < 3; i++)
                if (layers == null || i >= layers.Length || layers[i]) mask |= 1 << i;
            layerMask = mask;
        }
        catch
        {
            pan = Num.Vector2.Zero;
            zoom = 1f;
            layerMask = 7;
        }
    }
}
