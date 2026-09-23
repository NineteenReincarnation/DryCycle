using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace DryCycle.DevUI.DevTool.Map.Cartography;

// Immutable top-down ARGB pixels shared by the CPU exporter and the GPU presentation adapter.
internal sealed class CartographyRaster
{
    internal readonly int Width, Height;
    internal readonly uint[] Pixels;
    internal CartographyRaster(int width, int height, uint[] pixels) { Width = width; Height = height; Pixels = pixels; }
    internal static CartographyRaster FromBitmap(Bitmap source)
    {
        using Bitmap bitmap = new(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bitmap)) { g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy; g.DrawImageUnscaled(source, 0, 0); }
        BitmapData data = bitmap.LockBits(new Rectangle(0,0,bitmap.Width,bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int[] row = new int[bitmap.Width]; uint[] pixels = new uint[bitmap.Width * bitmap.Height];
        try { for (int y=0;y<bitmap.Height;y++) { Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length); for(int x=0;x<row.Length;x++) pixels[y*row.Length+x]=unchecked((uint)row[x]); } }
        finally { bitmap.UnlockBits(data); }
        return new CartographyRaster(bitmap.Width, bitmap.Height, pixels);
    }
    internal Bitmap Bitmap()
    {
        Bitmap b = new(Width,Height,PixelFormat.Format32bppArgb);
        BitmapData data = b.LockBits(new Rectangle(0,0,Width,Height),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
        int[] row = new int[Width];
        try { for(int y=0;y<Height;y++) { for(int x=0;x<Width;x++) row[x]=unchecked((int)Pixels[y*Width+x]); Marshal.Copy(row,0,IntPtr.Add(data.Scan0,y*data.Stride),Width); } }
        finally { b.UnlockBits(data); }
        return b;
    }
    internal byte[] Png() { using Bitmap b=Bitmap(); using MemoryStream stream=new(); b.Save(stream,ImageFormat.Png); return stream.ToArray(); }
    internal static CartographyRaster Decode(byte[] bytes)
    {
        if(bytes.Length>12*1024*1024) throw new InvalidDataException("Image exceeds 12 MiB.");
        using MemoryStream stream=new(bytes); using Bitmap b=new(stream);
        if(b.Width>8192 || b.Height>8192 || (long)b.Width*b.Height>16*1024*1024) throw new InvalidDataException("Image exceeds 16 megapixels or 8192 pixels per side.");
        return FromBitmap(b);
    }
    internal CartographyRaster Tint(uint color, float opacity = 1)
    {
        uint[] p=new uint[Pixels.Length];
        for(int i=0;i<p.Length;i++) { uint v=Pixels[i]; p[i]= ((uint)((v>>24)*(color>>24)/255f*opacity)<<24) | (((v>>16&255)*(color>>16&255)/255)<<16) | (((v>>8&255)*(color>>8&255)/255)<<8) | ((v&255)*(color&255)/255); }
        return new CartographyRaster(Width,Height,p);
    }
}

internal static class CartographyAssets
{
    private static readonly ConcurrentDictionary<string, CartographyRaster> Sprites = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, CartographyRaster> Images = new(StringComparer.Ordinal);
    internal static string[] IconNames => Sprites.Keys.OrderBy(n=>n).ToArray();
    internal static string[] FontNames => new[] { "Microsoft YaHei UI", "Arial", "Consolas" }.Concat(BitmapFonts.Keys).Concat(new InstalledFontCollection().Families.Select(f=>f.Name)).Distinct().OrderBy(n=>n).ToArray();
    internal static readonly ConcurrentDictionary<string, CartographyBitmapFont> BitmapFonts = new(StringComparer.OrdinalIgnoreCase);
    internal static void Register(string name, CartographyRaster raster) => Sprites[name]=raster;
    internal static CartographyRaster Sprite(string name) => Sprites.TryGetValue(name??"",out CartographyRaster raster)?raster:null;
    internal static CartographyRaster Image(string base64) => Images.GetOrAdd(CartographyStorage.HashText(base64),_=>CartographyRaster.Decode(Convert.FromBase64String(base64)));
    internal static string ImportImage(string path) => Convert.ToBase64String(CartographyRaster.Decode(File.ReadAllBytes(path)).Png());
    internal static void LoadDirectory(string directory)
    {
        string atlases=Path.Combine(directory,"Assets","Atlases");
        if(!Directory.Exists(atlases)) atlases=directory;
        if(Directory.Exists(atlases)) foreach(string file in Directory.EnumerateFiles(atlases,"*.txt"))
        {
            string png=Path.ChangeExtension(file,".png"); if(!File.Exists(png)) continue;
            var data=CartographyJson.Parse(File.ReadAllText(file)); var frames=data.Object("frames");
            using Bitmap sheet=new(png);
            foreach(var pair in frames)
            {
                if(pair.Value is not Dictionary<string,object> frame) continue;
                var r=frame.Object("frame"); int x=(int)r.Number("x"),y=(int)r.Number("y"),w=(int)r.Number("w"),h=(int)r.Number("h");
                if(w<=0||h<=0||x<0||y<0||x+w>sheet.Width||y+h>sheet.Height) throw new InvalidDataException("Invalid atlas frame: "+pair.Key);
                using Bitmap crop=sheet.Clone(new Rectangle(x,y,w,h),PixelFormat.Format32bppArgb);
                if(frame.Flag("rotated")) crop.RotateFlip(RotateFlipType.Rotate270FlipNone);
                Register(Path.GetFileNameWithoutExtension(pair.Key),CartographyRaster.FromBitmap(crop));
            }
        }
        string content=Path.Combine(directory,"Content");
        if(Directory.Exists(content)) foreach(string file in Directory.EnumerateFiles(content,"*.txt"))
            if(File.ReadLines(file).FirstOrDefault()?.StartsWith("texture ")==true) BitmapFonts[Path.GetFileNameWithoutExtension(file)]=CartographyBitmapFont.Load(file);
    }
}

internal sealed class CartographyBitmapFont
{
    internal int LineHeight=20, Spacing=1, Space=5;
    internal bool IgnoreCase;
    internal readonly Dictionary<char,CartographyRaster> Glyphs=new();
    internal static CartographyBitmapFont Load(string path)
    {
        string[] lines=File.ReadAllLines(path); string texture=lines.First(l=>l.StartsWith("texture ")).Substring(8).Trim();
        using Bitmap sheet=new(Path.Combine(Path.GetDirectoryName(path),texture)); CartographyBitmapFont f=new(); int x=0,y=0,gap=0;
        foreach(string line in lines)
        {
            string[] p=line.Split(new[]{' '},StringSplitOptions.RemoveEmptyEntries); if(p.Length<2)continue;
            switch(p[0])
            {
                case "lineHeight":f.LineHeight=int.Parse(p[1]);break;
                case "glyphSpacing":gap=int.Parse(p[1]);break;
                case "spacing":f.Spacing=int.Parse(p[1]);break;
                case "spaceWidth":f.Space=int.Parse(p[1]);break;
                case "ignoreCase":f.IgnoreCase=bool.Parse(p[1]);break;
                case "pos":x=int.Parse(p[1]);y=int.Parse(p[2]);break;
                case "chars":for(int i=1;i+1<p.Length;i+=2){int w=int.Parse(p[i+1]);using Bitmap glyph=sheet.Clone(new Rectangle(x,y,w,f.LineHeight),PixelFormat.Format32bppArgb);f.Glyphs[p[i][0]]=CartographyRaster.FromBitmap(glyph);x+=w+gap;}break;
            }
        }
        return f;
    }
}
