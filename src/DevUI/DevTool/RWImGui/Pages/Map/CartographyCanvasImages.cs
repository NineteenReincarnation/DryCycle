using System;
using System.Collections.Generic;
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
        internal int RequestedWidth, RequestedHeight;
        internal int UploadedWidth, UploadedHeight;
    }
    private static readonly object Gate = new();
    private static readonly Dictionary<CartographyRaster, Entry> Entries = new();
    private static readonly List<KeyValuePair<CartographyRaster, Entry>> Pending = new();
    private static long frame;
    internal static string Error { get; private set; } = "";

    internal static void Draw(ImDrawListPtr draw, CartographyPrimitive shape, Num.Vector2 min, Num.Vector2 max, uint color)
    {
        Entry entry;
        lock (Gate)
        {
            if (!Entries.TryGetValue(shape.Raster, out entry))
                Entries[shape.Raster] = entry = new Entry();

            entry.Used = frame;

            if (shape.PixelPerfect)
            {
                int width = Math.Max(1, (int)Math.Round(Math.Abs(max.X - min.X)));
                int height = Math.Max(1, (int)Math.Round(Math.Abs(max.Y - min.Y)));

                if (entry.RequestedWidth != width || entry.RequestedHeight != height)
                {
                    entry.RequestedWidth = width;
                    entry.RequestedHeight = height;
                    entry.Uploaded = entry.UploadedWidth == width && entry.UploadedHeight == height;
                    entry.RetryAfter = Math.Min(entry.RetryAfter, frame);
                }
            }
            else if (entry.RequestedWidth != 0 || entry.RequestedHeight != 0)
            {
                entry.RequestedWidth = 0;
                entry.RequestedHeight = 0;
                entry.Uploaded = entry.UploadedWidth == shape.Raster.Width &&
                                 entry.UploadedHeight == shape.Raster.Height;
                entry.RetryAfter = Math.Min(entry.RetryAfter, frame);
            }
        }

        if (!entry.Bridge.TryPresent(draw, min, max, color))
            draw.AddRect(min, max, 0xAA8096AF);
    }

    internal static void UpdateMainThread()
    {
        lock (Gate)
        {
            frame++;
            if (Entries.Count == 0)
            {
                Pending.Clear();
                Error = "";
                return;
            }

            Pending.Clear();
            foreach (KeyValuePair<CartographyRaster, Entry> pair in Entries)
                Pending.Add(pair);
        }

        int budget = 8;
        string error = "";
        for (int pendingIndex = 0; pendingIndex < Pending.Count; pendingIndex++)
        {
            KeyValuePair<CartographyRaster, Entry> pair = Pending[pendingIndex];
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
                int uploadWidth = entry.RequestedWidth > 0 ? entry.RequestedWidth : raster.Width;
                int uploadHeight = entry.RequestedHeight > 0 ? entry.RequestedHeight : raster.Height;
                uint[] uploadPixels =
                    uploadWidth == raster.Width && uploadHeight == raster.Height
                        ? raster.Pixels
                        : ScaleNearest(raster, uploadWidth, uploadHeight);

                entry.Bridge.Initialize(global::DryCycle.Plugin.Logger);
                entry.Uploaded = entry.Bridge.Upload(uploadWidth, uploadHeight, uploadPixels);
                if (entry.Uploaded)
                {
                    entry.UploadedWidth = uploadWidth;
                    entry.UploadedHeight = uploadHeight;
                }

                // Device startup is retryable; a transient failure cannot permanently blank the map.
                entry.RetryAfter = frame + 60;
            }
            if (!entry.Uploaded && entry.Bridge.Error.Length > 0) error = entry.Bridge.Error;
        }
        Error = error;
    }
    private static uint[] ScaleNearest(CartographyRaster raster, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        uint[] result = new uint[checked(width * height)];

        for (int y = 0; y < height; y++)
        {
            int sourceY = Math.Min(
                raster.Height - 1,
                (int)(((long)y * raster.Height) / height));
            int sourceRow = sourceY * raster.Width;
            int targetRow = y * width;

            for (int x = 0; x < width; x++)
            {
                int sourceX = Math.Min(
                    raster.Width - 1,
                    (int)(((long)x * raster.Width) / width));
                result[targetRow + x] = raster.Pixels[sourceRow + sourceX];
            }
        }

        return result;
    }

}
