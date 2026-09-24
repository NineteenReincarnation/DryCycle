using System.Collections.Generic;
using System.Linq;
using DryCycle.DevUI.DevTool.Map.Cartography;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal static class CartographyCanvasImages
{
    private sealed class Entry
    {
        internal readonly WorldMapTextureBridge Bridge = new();
        internal long Used, RetryAfter;
        internal bool Uploaded;
    }
    private static readonly object Gate = new();
    private static readonly Dictionary<CartographyRaster, Entry> Entries = new();
    private static long frame;
    internal static string Error { get; private set; } = "";

    internal static void Draw(ImDrawListPtr draw, CartographyPrimitive shape, Num.Vector2 min, Num.Vector2 max, uint color)
    {
        Entry entry;
        lock (Gate)
        {
            if (!Entries.TryGetValue(shape.Raster, out entry)) Entries[shape.Raster] = entry = new Entry();
            entry.Used = frame;
        }
        if (!entry.Bridge.TryPresent(draw, min, max, color)) draw.AddRect(min, max, 0xAA8096AF);
    }

    internal static void UpdateMainThread()
    {
        KeyValuePair<CartographyRaster, Entry>[] pending;
        lock (Gate) { frame++; pending = Entries.ToArray(); }
        int budget = 8;
        string error = "";
        foreach (var pair in pending)
        {
            Entry entry = pair.Value;
            bool stale;
            lock (Gate)
            {
                stale = frame - entry.Used > 180;
                if (stale) Entries.Remove(pair.Key);
            }
            if (stale) { entry.Bridge.Reset(); continue; }
            if (!entry.Uploaded && frame >= entry.RetryAfter && budget-- > 0)
            {
                CartographyRaster raster = pair.Key;
                entry.Bridge.Initialize(global::DryCycle.Plugin.Logger);
                entry.Uploaded = entry.Bridge.Upload(raster.Width, raster.Height, raster.Pixels);
                // Device startup is retryable; a transient failure cannot permanently blank the map.
                entry.RetryAfter = frame + 60;
            }
            if (!entry.Uploaded && entry.Bridge.Error.Length > 0) error = entry.Bridge.Error;
        }
        Error = error;
    }
}
