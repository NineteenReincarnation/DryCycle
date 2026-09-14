using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using DryCycle.Iterators;
using UColor = UnityEngine.Color;
using Vector2 = UnityEngine.Vector2;

namespace IteratorFramework.Tests;

// Export the compiled runtime's vertex positions and colors. This is a CPU
// rasterizer, not a replacement for the game's palette, lighting or shaders.
internal static class GraphicsPreview
{
    internal static void Export(IteratorRuntime runtime, string directory)
    {
        Directory.CreateDirectory(directory);
        using (Bitmap portrait = Raster(runtime, 720, 1100, new Vector2(360, 550), 7f, Color.Transparent))
            portrait.Save(Path.Combine(directory, "pwn-iterator-transparent.png"), ImageFormat.Png);
        using (Bitmap preview = Raster(runtime, 1080, 1100, new Vector2(380, 550), 7f, Color.FromArgb(28, 33, 51)))
        using (Graphics canvas = Graphics.FromImage(preview))
        using (var title = new Font("Segoe UI", 18f))
        using (var small = new Font("Segoe UI", 11f))
        using (var ink = new SolidBrush(Color.FromArgb(203, 211, 229)))
        {
            canvas.DrawString("PWN_AI  /  ITERATOR FRAMEWORK", title, ink, 30, 20);
            canvas.DrawString("Compiled mesh preview", small, ink, 770, 265);
            canvas.DrawString("1 game pixel = 1 image pixel", small, ink, 770, 290);
            using (Bitmap actual = Raster(runtime, 250, 210, new Vector2(125, 105), 1f, Color.Transparent)) canvas.DrawImageUnscaled(actual, 780, 330);
            canvas.DrawString("White mask + ring mark\nTwisted gold headdress\nWhite / lavender gown\n\nNo floating blue spheres", small, ink, 785, 590);
            canvas.DrawString("Geometry and vertex colors only. In-game lighting / shaders still require validation.", small, ink, 30, 1055);
            preview.Save(Path.Combine(directory, "pwn-iterator-preview.png"), ImageFormat.Png);
        }

        Room room = runtime.Context.Room;
        int w = room.TileWidth * 20, h = room.TileHeight * 20;
        using (var map = new Bitmap(w + 80, h + 130, PixelFormat.Format32bppArgb))
        using (Graphics canvas = Graphics.FromImage(map))
        using (var solid = new SolidBrush(Color.FromArgb(70, 77, 98)))
        using (var font = new Font("Segoe UI", 13f))
        {
            canvas.Clear(Color.FromArgb(24, 29, 43));
            canvas.DrawString("PWN_AI  /  authored terrain and sample placement (1:1)", font, Brushes.White, 40, 25);
            for (int x = 0; x < room.TileWidth; x++)
            for (int y = 0; y < room.TileHeight; y++)
                if (room.GetTile(x, y).Solid) canvas.FillRectangle(solid, 40 + x * 20, 70 + h - (y + 1) * 20, 20, 20);
            Vector2 p = runtime.Body.Position;
            using (Bitmap character = Raster(runtime, map.Width, map.Height, new Vector2(40 + p.x, 70 + h - p.y), 1f, Color.Transparent)) canvas.DrawImageUnscaled(character, 0, 0);
            canvas.DrawString($"Spawn ({p.x:0}, {p.y:0})   |   CPU geometry view; room artwork is not reproduced", font, Brushes.LightGray, 40, h + 90);
            map.Save(Path.Combine(directory, "pwn-ai-placement.png"), ImageFormat.Png);
        }
    }

    private static Bitmap Raster(IteratorRuntime runtime, int width, int height, Vector2 origin, float scale, Color background)
    {
        const int samples = 2;
        int w = width * samples, h = height * samples;
        var pixels = new byte[w * h * 4];
        for (int p = 0; p < pixels.Length; p += 4) { pixels[p] = background.B; pixels[p + 1] = background.G; pixels[p + 2] = background.R; pixels[p + 3] = background.A; }
        Vector2 position = runtime.Body.Position;
        foreach (SpriteHandle sprite in runtime.Graphics.Sprites.Entries.OrderBy(s => s.Layer))
        {
            if (!sprite.Visible) continue;
            IteratorMesh mesh = sprite.Mesh;
            for (int i = 0; i < mesh.Triangles.Count; i += 3)
            {
                int ai = mesh.Triangles[i], bi = mesh.Triangles[i + 1], ci = mesh.Triangles[i + 2];
                Vector2 a = Screen(mesh.Vertices[ai]), b = Screen(mesh.Vertices[bi]), c = Screen(mesh.Vertices[ci]);
                float area = Cross(b - a, c - a);
                if (Math.Abs(area) < 0.0001f) continue;
                int minX = Math.Max(0, (int)Math.Floor(Math.Min(a.x, Math.Min(b.x, c.x))));
                int maxX = Math.Min(w - 1, (int)Math.Ceiling(Math.Max(a.x, Math.Max(b.x, c.x))));
                int minY = Math.Max(0, (int)Math.Floor(Math.Min(a.y, Math.Min(b.y, c.y))));
                int maxY = Math.Min(h - 1, (int)Math.Ceiling(Math.Max(a.y, Math.Max(b.y, c.y))));
                UColor ca = mesh.Colors[ai], cb = mesh.Colors[bi], cc = mesh.Colors[ci];
                for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    float u = Cross(b - p, c - p) / area, v = Cross(c - p, a - p) / area, t = 1f - u - v;
                    if (u < -0.00001f || v < -0.00001f || t < -0.00001f) continue;
                    UColor col = ca * u + cb * v + cc * t;
                    int offset = (y * w + x) * 4;
                    float alpha = Math.Max(0f, Math.Min(1f, col.a)), destAlpha = pixels[offset + 3] / 255f;
                    float combined = alpha + destAlpha * (1f - alpha);
                    if (combined <= 0f) continue;
                    pixels[offset] = Byte((col.b * alpha + pixels[offset] / 255f * destAlpha * (1f - alpha)) / combined);
                    pixels[offset + 1] = Byte((col.g * alpha + pixels[offset + 1] / 255f * destAlpha * (1f - alpha)) / combined);
                    pixels[offset + 2] = Byte((col.r * alpha + pixels[offset + 2] / 255f * destAlpha * (1f - alpha)) / combined);
                    pixels[offset + 3] = Byte(combined);
                }
            }
        }
        using (var high = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            BitmapData data = high.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try { Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); }
            finally { high.UnlockBits(data); }
            var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(result))
            {
                g.CompositingMode = CompositingMode.SourceCopy; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(high, new Rectangle(0, 0, width, height));
            }
            return result;
        }
        Vector2 Screen(Vector2 vertex) => new((origin.x + (vertex.x - position.x) * scale) * samples, (origin.y - (vertex.y - position.y) * scale) * samples);
    }
    private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
    private static byte Byte(float value) => (byte)Math.Max(0, Math.Min(255, (int)(value * 255f + 0.5f)));
}
