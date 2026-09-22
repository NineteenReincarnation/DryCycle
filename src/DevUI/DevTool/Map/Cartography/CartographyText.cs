using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Linq;
using System.Text;

namespace DryCycle.DevUI.DevTool.Map.Cartography;

internal static class CartographyText
{
    private sealed class Style
    {
        internal uint Color, ShadeColor;
        internal float Size, Align;
        internal bool Shade, Shadow, Bold, Italic, Underline;
        internal Style Clone()=>(Style)MemberwiseClone();
    }
    private sealed class Run { internal string Text,Icon; internal Style Style; internal float X,Y,Width,Height; }
    internal static CartographyRaster Render(CartographyItem item, string defaultFont)
    {
        CartographyAppearance a=item.Appearance;
        Style style=new(){Color=item.Color,ShadeColor=a.ShadeColor,Size=item.Size,Shade=a.Shade,Shadow=a.DropShadow,Bold=a.Bold,Italic=a.Italic,Underline=a.Underline,Align=a.Alignment};
        List<Run> runs=new(); Stack<(string Tag,Style Style)> stack=new(); StringBuilder word=new();
        void Flush(){if(word.Length>0){runs.Add(new Run{Text=word.ToString(),Style=style.Clone()});word.Clear();}}
        string text=item.Text??"";
        for(int i=0;i<text.Length;i++)
        {
            if(text[i]=='\\'&&i+1<text.Length&&(text[i+1]=='['||text[i+1]==']')){word.Append(text[++i]);continue;}
            if(text[i]!='['){word.Append(text[i]);continue;}
            int end=text.IndexOf(']',i+1);if(end<0){word.Append(text[i]);continue;}
            string tag=text.Substring(i+1,end-i-1);string[] parts=tag.Split(':');string name=parts[0];
            if(name.StartsWith("/"))
            { if(stack.Any(s=>s.Tag==name.Substring(1))){Flush();while(stack.Count>0){var previous=stack.Pop();style=previous.Style;if(previous.Tag==name.Substring(1))break;}i=end;continue;} }
            else if(new[]{"c","s","ns","i","b","u","sc","a","ic","ds"}.Contains(name))
            {
                Flush();
                if(name=="ic"){if(parts.Length>1){Style iconStyle=style.Clone();if(parts.Length>2)iconStyle.Color=Color(parts[2],style.Color);runs.Add(new Run{Icon=parts[1],Style=iconStyle});}}
                else
                {
                    stack.Push((name,style.Clone()));
                    switch(name){case "c":if(parts.Length>1)style.Color=Color(parts[1],style.Color);break;
                    case "s":style.Shade=true;if(parts.Length>1)style.ShadeColor=Color(parts[1],style.ShadeColor);break;
                    case "ns":style.Shade=false;break;case "b":style.Bold=true;break;case "i":style.Italic=true;break;case "u":style.Underline=true;break;
                    case "ds":style.Shadow=true;if(parts.Length>1)style.ShadeColor=Color(parts[1],style.ShadeColor);break;
                    case "sc":if(parts.Length>1&&float.TryParse(parts[1],NumberStyles.Float,CultureInfo.InvariantCulture,out float scale))style.Size=Math.Max(2,Math.Min(512,style.Size*scale));break;
                    case "a":if(parts.Length>1&&float.TryParse(parts[1],NumberStyles.Float,CultureInfo.InvariantCulture,out float align))style.Align=Math.Max(0,Math.Min(1,align));break;}
                }
                i=end;continue;
            }
            word.Append(text[i]);
        }
        Flush();
        string family=string.IsNullOrWhiteSpace(a.Font)?defaultFont:a.Font;
        CartographyAssets.BitmapFonts.TryGetValue(family,out CartographyBitmapFont bitmapFont);
        using Bitmap probe=new(1,1);using Graphics measure=Graphics.FromImage(probe);
        using StringFormat format=new(StringFormat.GenericTypographic){FormatFlags=StringFormatFlags.MeasureTrailingSpaces|StringFormatFlags.NoWrap};
        List<Run> positioned=new();float x=0,y=0,lineHeight=item.Size*1.25f,width=1;int lineStart=0;
        void EndLine(){foreach(Run r in positioned.Skip(lineStart))r.Y=y+(lineHeight-r.Height)*r.Style.Align;y+=lineHeight;x=0;lineHeight=item.Size*1.25f;lineStart=positioned.Count;}
        foreach(Run run in runs)
        {
            string[] lines=(run.Text??"").Replace("\r","").Replace("\t","    ").Split('\n');
            for(int n=0;n<lines.Length;n++)
            {
                Run r=new(){Text=lines[n],Icon=run.Icon,Style=run.Style,X=x,Y=y,Height=run.Style.Size*1.25f};
                if(r.Icon!=null){CartographyRaster icon=CartographyAssets.Sprite(r.Icon);r.Width=icon==null?r.Height:r.Height*icon.Width/icon.Height;}
                else if(bitmapFont!=null) r.Width=r.Text.Sum(c=>GlyphWidth(bitmapFont,c))*r.Style.Size/bitmapFont.LineHeight;
                else {using Font font=new(family,r.Style.Size,FontStyleFor(r.Style),GraphicsUnit.Pixel);r.Width=measure.MeasureString(r.Text,font,int.MaxValue,format).Width;}
                positioned.Add(r);x+=r.Width;lineHeight=Math.Max(lineHeight,r.Height);width=Math.Max(width,x);if(n<lines.Length-1)EndLine();
            }
        }
        EndLine();float padding=a.Outline+4;const float resolution=2;
        int w=(int)Math.Ceiling((width+padding*2)*resolution),h=(int)Math.Ceiling((y+padding*2)*resolution);
        if(w>8192||h>8192||(long)w*h>16*1024*1024)throw new InvalidOperationException("Text exceeds the label raster limit; split it into several objects.");
        using Bitmap output=new(Math.Max(1,w),Math.Max(1,h),PixelFormat.Format32bppArgb);using Graphics g=Graphics.FromImage(output);
        g.SmoothingMode=SmoothingMode.AntiAlias;g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;g.ScaleTransform(resolution,resolution);g.TranslateTransform(padding,padding);
        foreach(Run r in positioned)
        {
            using SolidBrush fill=new(System.Drawing.Color.FromArgb(unchecked((int)r.Style.Color)));
            if(r.Icon!=null||bitmapFont!=null)
            {
                if(r.Icon!=null)
                {CartographyRaster icon=CartographyAssets.Sprite(r.Icon);if(icon!=null)DrawGlyph(g,icon,r.X,r.Y,r.Width,r.Height,r.Style,a.Outline);else g.DrawString("?"+r.Icon,SystemFonts.DefaultFont,fill,r.X,r.Y);}
                else
                {
                    float cursor=r.X,k=r.Style.Size/bitmapFont.LineHeight;
                    foreach(char c in r.Text){char key=bitmapFont.IgnoreCase?char.ToUpperInvariant(c):c; if(bitmapFont.Glyphs.TryGetValue(key,out CartographyRaster glyph))DrawGlyph(g,glyph,cursor,r.Y,glyph.Width*k,r.Style.Size,r.Style,a.Outline);cursor+=GlyphWidth(bitmapFont,c)*k;}
                }
                if(r.Style.Underline){using Pen line=new(fill,1);g.DrawLine(line,r.X,r.Y+r.Height-1,r.X+r.Width,r.Y+r.Height-1);}
            }
            else if(r.Text.Length>0)
            {
                using Font font=new(family,r.Style.Size,FontStyleFor(r.Style),GraphicsUnit.Pixel);
                using GraphicsPath path=new();path.AddString(r.Text,font.FontFamily,(int)font.Style,r.Style.Size,new PointF(r.X,r.Y),format);
                using SolidBrush shade=new(System.Drawing.Color.FromArgb(unchecked((int)r.Style.ShadeColor)));
                if(r.Style.Shadow){using GraphicsPath shadow=(GraphicsPath)path.Clone();using Matrix move=new();move.Translate(3,3);shadow.Transform(move);g.FillPath(shade,shadow);}
                if(r.Style.Shade&&a.Outline>0){using Pen pen=new(shade,a.Outline*2){LineJoin=LineJoin.Round};g.DrawPath(pen,path);}
                g.FillPath(fill,path);
            }
        }
        return CartographyRaster.FromBitmap(output);
    }
    private static float GlyphWidth(CartographyBitmapFont font,char c)=>c==' '?font.Space+font.Spacing:font.Glyphs.TryGetValue(font.IgnoreCase?char.ToUpperInvariant(c):c,out CartographyRaster r)?r.Width+font.Spacing:font.Space;
    private static FontStyle FontStyleFor(Style s)=>(s.Bold?FontStyle.Bold:0)|(s.Italic?FontStyle.Italic:0)|(s.Underline?FontStyle.Underline:0);
    private static void DrawGlyph(Graphics g,CartographyRaster raster,float x,float y,float w,float h,Style style,float outline)
    {
        if(style.Shade||style.Shadow)
        {
            using Bitmap dark=raster.Tint(style.ShadeColor).Bitmap();
            if(style.Shadow)g.DrawImage(dark,x+3,y+3,w,h);
            if(style.Shade)for(int i=0;i<8;i++){double angle=i*Math.PI/4;g.DrawImage(dark,x+(float)Math.Cos(angle)*outline,y+(float)Math.Sin(angle)*outline,w,h);}
        }
        using Bitmap b=raster.Tint(style.Color).Bitmap();
        if(style.Italic){var state=g.Save();using Matrix skew=new(1,0,-.2f,1,x+h*.2f,y);g.MultiplyTransform(skew);g.DrawImage(b,0,0,w,h);g.Restore(state);}else g.DrawImage(b,x,y,w,h);
        if(style.Bold)g.DrawImage(b,x+.7f,y,w,h);
    }
    internal static uint Color(string text,uint fallback=0xFFFFFFFF)
    {
        text=text.TrimStart('#');if(text.Length==1)text=new string(text[0],6);else if(text.Length==3)text=string.Concat(text.Select(c=>new string(c,2)));
        if(!uint.TryParse(text,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out uint c))return fallback;
        return text.Length==8?(c<<24)|(c>>8):text.Length==6?0xFF000000|c:fallback;
    }
}
