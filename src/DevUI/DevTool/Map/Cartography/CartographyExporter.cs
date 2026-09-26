using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;

namespace DryCycle.DevUI.DevTool.Map.Cartography;

internal enum CartographyExportFormat { Png, Svg, LayerPngZip, Psd, ImageMap }

internal sealed class CartographyExportResult
{
    internal string Path;
    internal int Width, Height;
    internal string Note = string.Empty;
}

/// <summary>Consumes frozen managed data on a worker; does not touch Unity, ImGui or the author document.</summary>
internal static class CartographyExporter
{
    internal const long MaxPixels = 1024L * 1024 * 1024;
    internal const int MaxDimension = 65535;

    internal static CartographyRect OutputBounds(CartographyDocument document, CartographyScene scene) => document.Options.ExportArea ? new CartographyRect(document.Options.AreaX, document.Options.AreaY, document.Options.AreaWidth, document.Options.AreaHeight) : scene.Bounds.Inflate(document.Padding);

    internal static void Dimensions(CartographyDocument document, CartographyScene scene, out int width, out int height, CartographyExportFormat format = CartographyExportFormat.Png)
    {
        CartographyRect bounds = OutputBounds(document, scene);
        double w = Math.Ceiling(bounds.Width * document.ExportScale), h = Math.Ceiling(bounds.Height * document.ExportScale);
        bool vector = format == CartographyExportFormat.Svg || format == CartographyExportFormat.ImageMap;
        int limit = vector ? 1000000 : format == CartographyExportFormat.Psd ? 30000 : MaxDimension;
        long pixels = format == CartographyExportFormat.Psd ? 32L*1024*1024 : MaxPixels;
        if (w < 1 || h < 1 || double.IsNaN(w) || double.IsNaN(h) || w > limit || h > limit || !vector && w * h > pixels)
            throw new InvalidOperationException("Export " + w + " x " + h + " exceeds " + format + " limits (" + limit + " px/side" + (vector ? "" : ", " + pixels/1024/1024 + " MP") + ").");
        width = (int)w; height = (int)h;
    }

    internal static CartographyExportResult Export(CartographyDocument document, CartographyScene scene, string path, CartographyExportFormat format, string expectedHash, Action<string> log = null)
    {
        document.Validate();
        if (scene.Errors.Length != 0) throw new InvalidOperationException("Visible room terrain is incomplete: " + string.Join("; ", scene.Errors.Take(8)));
        if (scene.Nodes.Length == 0) throw new InvalidOperationException("There are no visible objects to export.");
        if (!Enum.IsDefined(typeof(CartographyExportFormat), format)) throw new InvalidOperationException("Unknown export format.");
        string extension = format == CartographyExportFormat.Png ? ".png" : format == CartographyExportFormat.Svg ? ".svg" : format == CartographyExportFormat.Psd ? ".psd" : format == CartographyExportFormat.ImageMap ? ".json" : ".zip";
        if (!string.Equals(System.IO.Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a " + extension + " output path.");
        Dimensions(document, scene, out int width, out int height, format);
        CartographyStorage.WriteAtomic(path, expectedHash, stream =>
        {
            if (format == CartographyExportFormat.Svg) WriteSvg(stream, document, scene, width, height);
            else if (format == CartographyExportFormat.Png) WritePng(stream, document, scene, width, height, null);
            else if (format == CartographyExportFormat.Psd) CartographyLayerExport.WritePsd(stream, document, scene, width, height);
            else if (format == CartographyExportFormat.ImageMap) CartographyLayerExport.WriteImageMap(stream, document, scene, width, height);
            else
            {
                using ZipArchive archive = new(stream, ZipArchiveMode.Create, true);
                int ordinal = 0;
                foreach (CartographyLayer layer in document.Layers.Where(layer => layer.Visible && layer.Opacity > 0))
                {
                    if (!scene.Nodes.Any(node => node.LayerId == layer.Id)) continue;
                    // Numeric filenames are safe even for imported layer IDs and have a shared canvas.
                    ZipArchiveEntry entry = archive.CreateEntry((ordinal++).ToString("D3", CultureInfo.InvariantCulture) + ".png", CompressionLevel.Fastest);
                    using Stream target = entry.Open();
                    WritePng(target, document, scene, width, height, layer.Id);
                }
                using Stream manifest = archive.CreateEntry("layers.txt").Open();
                using StreamWriter writer = new(manifest, new UTF8Encoding(false));
                writer.WriteLine("Bottom to top. All PNGs use the same origin and dimensions: " + width + " x " + height);
                ordinal = 0;
                foreach (CartographyLayer layer in document.Layers.Where(layer => layer.Visible && layer.Opacity > 0 && scene.Nodes.Any(node => node.LayerId == layer.Id)))
                    writer.WriteLine((ordinal++).ToString("D3", CultureInfo.InvariantCulture) + ".png\t" + layer.Name);
            }
        }, log);
        using Font probe = new(document.FontFamily, 12, FontStyle.Regular, GraphicsUnit.Pixel);
        string note = probe.Name.Equals(document.FontFamily, StringComparison.OrdinalIgnoreCase) ? string.Empty : "Font fallback: " + probe.Name;
        return new CartographyExportResult { Path = System.IO.Path.GetFullPath(path), Width = width, Height = height, Note = note };
    }

    private static void WritePng(Stream output, CartographyDocument document, CartographyScene scene, int width, int height, string layerId)
    {
        CartographyPngEncoder.Write(output,width,height,(top,rows)=>RenderBitmap(document,scene,width,rows,layerId,top));
    }

    internal static Bitmap RenderBitmap(CartographyDocument document, CartographyScene scene, int width, int height, string layerId, int pixelTop = 0)
    {
        Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Clear(layerId != null || document.Transparent ? Color.Transparent : ToColor(document.Background));
        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        CartographyRect bounds = OutputBounds(document, scene);
        graphics.ScaleTransform(document.ExportScale, document.ExportScale);
        graphics.TranslateTransform(-bounds.X, -bounds.Y - pixelTop/document.ExportScale);
        var visible = new CartographyRect(bounds.X,bounds.Y+pixelTop/document.ExportScale,width/document.ExportScale,height/document.ExportScale).Inflate(2/document.ExportScale);
        foreach (CartographySceneNode node in scene.Nodes)
        {
            if (layerId != null && node.LayerId != layerId || !visible.Intersects(node.Bounds)) continue;
            foreach (CartographyPrimitive shape in node.Primitives)
            {
                if(shape.GuideOnly)continue;
                CartographyRect r = shape.Rect;
                using SolidBrush brush = new(ToColor(shape.Color));
                using Pen pen = new(brush, shape.Stroke);
                if(shape.Dashed){pen.DashPattern=new[]{shape.DashLength/shape.Stroke,shape.DashGap/shape.Stroke};pen.DashOffset=shape.DashOffset/shape.Stroke;}
                switch (shape.Kind)
                {
                    case CartographyPrimitiveKind.Image:
                        DrawRasterBand(graphics, shape, bounds, document.ExportScale, width, height, pixelTop);
                        break;
                    case CartographyPrimitiveKind.Fill:
                        graphics.SmoothingMode = SmoothingMode.None;
                        graphics.FillRectangle(brush, r.X, r.Y, r.Width, r.Height);
                        graphics.SmoothingMode = SmoothingMode.AntiAlias;
                        break;
                    case CartographyPrimitiveKind.Outline: graphics.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height); break;
                    case CartographyPrimitiveKind.Ellipse: graphics.DrawEllipse(pen, r.X, r.Y, r.Width, r.Height); break;
                    case CartographyPrimitiveKind.Line:
                    {
                        SmoothingMode smoothing =
                            graphics.SmoothingMode;
                        PixelOffsetMode pixelOffset =
                            graphics.PixelOffsetMode;
                        LineCap startCap =
                            pen.StartCap;
                        LineCap endCap =
                            pen.EndCap;
                        DashCap dashCap =
                            pen.DashCap;

                        if (shape.PixelPerfect)
                        {
                            graphics.SmoothingMode =
                                SmoothingMode.None;
                            graphics.PixelOffsetMode =
                                PixelOffsetMode.Half;
                            pen.StartCap =
                                LineCap.Flat;
                            pen.EndCap =
                                LineCap.Flat;
                            pen.DashCap =
                                DashCap.Flat;
                        }

                        graphics.DrawLine(
                            pen,
                            r.X,
                            r.Y,
                            r.Right,
                            r.Bottom);

                        if (shape.PixelPerfect)
                        {
                            graphics.SmoothingMode =
                                smoothing;
                            graphics.PixelOffsetMode =
                                pixelOffset;
                            pen.StartCap =
                                startCap;
                            pen.EndCap =
                                endCap;
                            pen.DashCap =
                                dashCap;
                        }
                        break;
                    }
                    case CartographyPrimitiveKind.Text:
                        using (Font font = new(document.FontFamily, shape.Size, FontStyle.Regular, GraphicsUnit.Pixel))
                        using (StringFormat textFormat = new(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap })
                        {
                            string[] lines = shape.Text.Replace("\r", "").Replace("\t", "    ").Split('\n');
                            for (int i = 0; i < lines.Length; i++)
                                graphics.DrawString(lines[i], font, brush, new PointF(r.X, r.Y + i * shape.Size * 1.4f), textFormat);
                        }
                        break;
                }
            }
        }
        return bitmap;
    }

    private static void DrawRasterBand(Graphics graphics, CartographyPrimitive shape, CartographyRect bounds, float scale, int width, int height, int pixelTop)
    {
        CartographyRect r=shape.Rect;
        int left=(int)Math.Round((r.X-bounds.X)*scale),top=(int)Math.Round((r.Y-bounds.Y)*scale);
        int fullWidth=Math.Max(1,(int)Math.Round((r.Right-bounds.X)*scale)-left);
        int fullHeight=Math.Max(1,(int)Math.Round((r.Bottom-bounds.Y)*scale)-top);
        int x0=Math.Max(0,left),y0=Math.Max(pixelTop,top);
        int x1=Math.Min(width,left+fullWidth),y1=Math.Min(pixelTop+height,top+fullHeight);
        if(x1<=x0||y1<=y0)return;
        // GDI+ scales a clipped DrawImage differently depending on the destination bitmap's
        // height. Sample against full-image coordinates first, then composite at 1:1 so stripes
        // cannot introduce seams, even at fractional export scales or with transparent sprites.
        CartographyRaster raster=shape.Raster;
        using Bitmap sampled=new(x1-x0,y1-y0,PixelFormat.Format32bppArgb);
        BitmapData data=sampled.LockBits(new Rectangle(0,0,sampled.Width,sampled.Height),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
        int[] row=new int[sampled.Width];
        uint tint=shape.Color;
        try
        {
            for(int y=y0;y<y1;y++)
            {
                int sy=Math.Min(raster.Height-1,(int)((y-top+.5)*raster.Height/fullHeight));
                for(int x=x0;x<x1;x++)
                {
                    int sx=Math.Min(raster.Width-1,(int)((x-left+.5)*raster.Width/fullWidth));
                    uint c=raster.Pixels[sy*raster.Width+sx];
                    row[x-x0]=unchecked((int)(((c>>24)*(tint>>24)/255)<<24|((c>>16&255)*(tint>>16&255)/255)<<16|((c>>8&255)*(tint>>8&255)/255)<<8|(c&255)*(tint&255)/255));
                }
                Marshal.Copy(row,0,IntPtr.Add(data.Scan0,(y-y0)*data.Stride),row.Length);
            }
        }
        finally{sampled.UnlockBits(data);}
        GraphicsState state=graphics.Save();
        graphics.ResetTransform();
        graphics.DrawImageUnscaled(sampled,x0,y0-pixelTop);
        graphics.Restore(state);
    }

    private static void WriteSvg(Stream output, CartographyDocument document, CartographyScene scene, int width, int height)
    {
        const string ns = "http://www.w3.org/2000/svg";
        using XmlWriter xml = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, CloseOutput = false });
        CartographyRect bounds = OutputBounds(document, scene);
        xml.WriteStartElement("svg", ns);
        Attr(xml, "width", width); Attr(xml, "height", height);
        xml.WriteAttributeString("viewBox", F(bounds.X) + " " + F(bounds.Y) + " " + F(bounds.Width) + " " + F(bounds.Height));
        xml.WriteElementString("title", ns, document.Title);
        if (!document.Transparent)
            SvgPrimitive(xml, new CartographyPrimitive { Kind = CartographyPrimitiveKind.Fill, Rect = bounds, Color = document.Background }, document.FontFamily);
        foreach (CartographyLayer layer in document.Layers.Where(layer => layer.Visible && layer.Opacity > 0))
        {
            xml.WriteStartElement("g", ns); xml.WriteAttributeString("id", "layer-" + document.Layers.IndexOf(layer));
            xml.WriteElementString("title", ns, layer.Name);
            foreach (CartographySceneNode node in scene.Nodes.Where(node => node.LayerId == layer.Id))
                foreach (CartographyPrimitive shape in node.Primitives) SvgPrimitive(xml, shape, document.FontFamily);
            xml.WriteEndElement();
        }
        xml.WriteEndElement();
    }

    private static void SvgPrimitive(XmlWriter xml, CartographyPrimitive shape, string font)
    {
        if(shape.GuideOnly)return;
        if (shape.Kind == CartographyPrimitiveKind.Image)
        {
            xml.WriteStartElement("image", "http://www.w3.org/2000/svg");
            Attr(xml,"x",shape.Rect.X); Attr(xml,"y",shape.Rect.Y); Attr(xml,"width",shape.Rect.Width); Attr(xml,"height",shape.Rect.Height);
            Attr(xml,"opacity",(shape.Color>>24)/255f);
            xml.WriteAttributeString("href","data:image/png;base64,"+Convert.ToBase64String(shape.Raster.Png()));
            if(shape.Text.Length>0)xml.WriteElementString("title",shape.Text);
            xml.WriteEndElement(); return;
        }

        CartographyRect r = shape.Rect;
        string tag = shape.Kind == CartographyPrimitiveKind.Line ? "line" : shape.Kind == CartographyPrimitiveKind.Ellipse ? "ellipse" : shape.Kind == CartographyPrimitiveKind.Text ? "text" : "rect";
        xml.WriteStartElement(tag, "http://www.w3.org/2000/svg");
        bool fill = shape.Kind == CartographyPrimitiveKind.Fill || shape.Kind == CartographyPrimitiveKind.Text;
        xml.WriteAttributeString(fill ? "fill" : "stroke", "#" + (shape.Color & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture));
        Attr(xml, "opacity", (shape.Color >> 24) / 255f);
        if (!fill)
        {
            xml.WriteAttributeString("fill", "none");
            Attr(xml, "stroke-width", shape.Stroke);

            if (shape.PixelPerfect)
            {
                xml.WriteAttributeString("shape-rendering", "crispEdges");
                xml.WriteAttributeString("stroke-linecap", "butt");
            }
        }
        if (shape.Dashed) { xml.WriteAttributeString("stroke-dasharray", F(shape.DashLength)+" "+F(shape.DashGap)); Attr(xml,"stroke-dashoffset",-shape.DashOffset); }
        if (shape.Kind == CartographyPrimitiveKind.Line)
        { Attr(xml, "x1", r.X); Attr(xml, "y1", r.Y); Attr(xml, "x2", r.Right); Attr(xml, "y2", r.Bottom); }
        else if (shape.Kind == CartographyPrimitiveKind.Ellipse)
        { Attr(xml, "cx", r.X + r.Width / 2); Attr(xml, "cy", r.Y + r.Height / 2); Attr(xml, "rx", r.Width / 2); Attr(xml, "ry", r.Height / 2); }
        else
        {
            Attr(xml, "x", r.X); Attr(xml, "y", r.Y);
            if (shape.Kind != CartographyPrimitiveKind.Text) { Attr(xml, "width", r.Width); Attr(xml, "height", r.Height); }
        }
        if (shape.Kind == CartographyPrimitiveKind.Text)
        {
            xml.WriteAttributeString("font-family", font); Attr(xml, "font-size", shape.Size);
            xml.WriteAttributeString("dominant-baseline", "text-before-edge");
            string[] lines = shape.Text.Replace("\r", "").Replace("\t", "    ").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                xml.WriteStartElement("tspan", "http://www.w3.org/2000/svg"); Attr(xml, "x", r.X); Attr(xml, "y", r.Y + i * shape.Size * 1.4f);
                xml.WriteString(lines[i]); xml.WriteEndElement();
            }
        }
        xml.WriteEndElement();
    }

    private static Color ToColor(uint color) => Color.FromArgb(unchecked((int)color));
    private static string F(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static void Attr(XmlWriter xml, string name, float value) => xml.WriteAttributeString(name, F(value));
}
