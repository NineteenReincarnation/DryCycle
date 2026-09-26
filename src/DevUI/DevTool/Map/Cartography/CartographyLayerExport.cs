using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;

namespace DryCycle.DevUI.DevTool.Map.Cartography;

internal static class CartographyLayerExport
{
    // PSD v1, uncompressed RGBA channels. Stream sections are backpatched, so all layers never coexist in RAM.
    internal static void WritePsd(Stream stream,CartographyDocument d,CartographyScene scene,int width,int height)
    {
        using BinaryWriter w=new(stream,Encoding.UTF8,true);
        void Short(int n){w.Write((byte)(n>>8));w.Write((byte)n);}
        void Int(long n){w.Write((byte)(n>>24));w.Write((byte)(n>>16));w.Write((byte)(n>>8));w.Write((byte)n);}
        void Tag(string s)=>w.Write(Encoding.ASCII.GetBytes(s));
        void Length(long position){long end=stream.Position;stream.Position=position;Int(end-position-4);stream.Position=end;}
        void Channels(CartographyRaster raster)
        {
            byte[] row=new byte[width];foreach(int shift in new[]{16,8,0,24})
            {Short(0);for(int y=0;y<height;y++){for(int x=0;x<width;x++)row[x]=(byte)(raster.Pixels[y*width+x]>>shift);w.Write(row);}}
        }
        Tag("8BPS");Short(1);w.Write(new byte[6]);Short(4);Int(height);Int(width);Short(8);Short(3);Int(0);Int(0);
        long section=stream.Position;Int(0);long info=stream.Position;Int(0);
        var layers=scene.RenderLayers.Where(l=>l.Pass!=CartographyRenderPass.Guides).Reverse().ToArray();
        Short(-layers.Length); // Negative count declares merged transparency.
        foreach(var layer in layers)
        {
            Int(0);Int(0);Int(height);Int(width);Short(4);
            foreach(int channel in new[]{0,1,2,-1}){Short(channel);Int((long)width*height+2);}
            Tag("8BIM");Tag("norm");w.Write((byte)255);w.Write((byte)0);w.Write((byte)0);w.Write((byte)0);
            long extra=stream.Position;Int(0);Int(0);Int(0);
            byte[] name=Encoding.ASCII.GetBytes(layer.Name);int count=Math.Min(255,name.Length);w.Write((byte)count);w.Write(name,0,count);
            for(int pad=(count+1)%4;pad!=0&&pad<4;pad++)w.Write((byte)0);
            Tag("8BIM");Tag("luni");Int(4+layer.Name.Length*2);Int(layer.Name.Length);foreach(char c in layer.Name)Short(c);
            Length(extra);
        }
        foreach(var layer in layers){using Bitmap b=CartographyExporter.RenderBitmap(d,scene,width,height,null,renderLayer:layer);Channels(CartographyRaster.FromBitmap(b));}
        if((stream.Position-info-4)%2!=0)w.Write((byte)0);Length(info);Int(0);Length(section);
        using Bitmap composite=CartographyExporter.RenderBitmap(d,scene,width,height,null);CartographyRaster merged=CartographyRaster.FromBitmap(composite);
        Short(0);byte[] scan=new byte[width];foreach(int shift in new[]{16,8,0,24})for(int y=0;y<height;y++){for(int x=0;x<width;x++)scan[x]=(byte)(merged.Pixels[y*width+x]>>shift);w.Write(scan);}
    }
    internal static void WriteImageMap(Stream stream,CartographyDocument d,CartographyScene scene,int width,int height)
    {
        using StreamWriter w=new(stream,new UTF8Encoding(false),4096,true);CartographyRect bounds=CartographyExporter.OutputBounds(d,scene);
        string N(float f)=>f.ToString("R",System.Globalization.CultureInfo.InvariantCulture);
        string Q(string s)=>"\""+(s??"").Replace("\\","\\\\").Replace("\"","\\\"").Replace("\r","\\r").Replace("\n","\\n").Replace("\t","\\t")+"\"";
        w.Write("{\"version\":1,\"region\":"+Q(d.Region)+",\"width\":"+width+",\"height\":"+height+",\"origin\":["+N(bounds.X)+","+N(bounds.Y)+"],\"scale\":"+N(d.ExportScale)+",\"objects\":[");
        bool first=true;foreach(var node in scene.Nodes)
        {
            if(!first)w.Write(',');first=false;
            w.Write("{\"id\":"+Q(node.Id)+",\"layer\":"+Q(node.LayerId)+",\"room\":"+(node.Room?"true":"false")+",\"bounds\":["+N((node.Bounds.X-bounds.X)*d.ExportScale)+","+N((node.Bounds.Y-bounds.Y)*d.ExportScale)+","+N(node.Bounds.Width*d.ExportScale)+","+N(node.Bounds.Height*d.ExportScale)+"],\"from\":"+Q(node.FromId)+",\"to\":"+Q(node.ToId)+"}");
        }
        w.Write("]}");
    }
}
