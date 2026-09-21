using System;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Shared once-per-frame snapshot of the private WorldMapView interaction state.
///
/// The private-field boundary is bound once to visibility-skipping DynamicMethod getters. Hot
/// consumers therefore share one direct static-field capture per Unity frame without FieldInfo
/// invocation or value-type boxing.
/// </summary>
internal static class WorldMapHotState
{
    private delegate Num.Vector2 PanGetter();
    private delegate float ZoomGetter();
    private delegate bool[] LayerGetter();

    private static readonly PanGetter ReadPan;
    private static readonly ZoomGetter ReadZoom;
    private static readonly LayerGetter ReadLayers;

    private static int capturedFrame = int.MinValue;
    private static Num.Vector2 pan = Num.Vector2.Zero;
    private static float zoom = 1f;
    private static int layerMask = 7;

    static WorldMapHotState()
    {
        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            Type mapType = typeof(WorldMapView);
            ReadPan = BuildGetter<PanGetter>(mapType.GetField("pan", flags), typeof(Num.Vector2), "ReadWorldMapPan");
            ReadZoom = BuildGetter<ZoomGetter>(mapType.GetField("zoom", flags), typeof(float), "ReadWorldMapZoom");
            ReadLayers = BuildGetter<LayerGetter>(mapType.GetField("layerVisible", flags), typeof(bool[]), "ReadWorldMapLayers");
        }
        catch
        {
            ReadPan = null;
            ReadZoom = null;
            ReadLayers = null;
        }
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
            pan = ReadPan != null ? ReadPan() : Num.Vector2.Zero;
            zoom = ReadZoom != null ? Math.Max(0.0001f, ReadZoom()) : 1f;

            bool[] layers = ReadLayers?.Invoke();
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

    private static TDelegate BuildGetter<TDelegate>(FieldInfo field, Type resultType, string name)
        where TDelegate : class
    {
        if (field == null || field.FieldType != resultType)
            return null;

        DynamicMethod method = new(
            name,
            resultType,
            Type.EmptyTypes,
            typeof(WorldMapHotState).Module,
            true);
        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Ldsfld, field);
        il.Emit(OpCodes.Ret);
        return method.CreateDelegate(typeof(TDelegate)) as TDelegate;
    }
}
