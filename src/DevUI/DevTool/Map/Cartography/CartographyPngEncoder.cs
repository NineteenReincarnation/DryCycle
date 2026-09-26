using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace DryCycle.DevUI.DevTool.Map.Cartography;

// PNG's zlib stream spans IDAT chunks. Only one small band and two scanlines reside in RAM.
internal static class CartographyPngEncoder
{
    internal const int BandPixels = 2 * 1024 * 1024;

    internal static void Write(Stream output, int width, int height, Func<int, int, Bitmap> renderBand)
    {
        output.Write(new byte[] {137,80,78,71,13,10,26,10},0,8);
        byte[] header = new byte[13];
        BigEndian(header,0,(uint)width); BigEndian(header,4,(uint)height); header[8]=8; header[9]=6;
        Chunk(output,"IHDR",header,header.Length);
        using var idat = new IdatStream(output);
        idat.Write(new byte[] {0x78,0x9C},0,2);
        uint adlerA=1,adlerB=0;
        byte[] scanline=new byte[checked(width*4+1)];
        int[] row=new int[width];
        using (var deflate=new DeflateStream(idat,CompressionLevel.Optimal,true))
        {
            int bandHeight=Math.Max(1,Math.Min(256,BandPixels/width));
            for(int top=0;top<height;top+=bandHeight)
            {
                int rows=Math.Min(bandHeight,height-top);
                using Bitmap bitmap=renderBand(top,rows);
                BitmapData data=bitmap.LockBits(new Rectangle(0,0,width,rows),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
                try
                {
                    for(int y=0;y<rows;y++)
                    {
                        Marshal.Copy(IntPtr.Add(data.Scan0,y*data.Stride),row,0,width);
                        for(int x=0;x<width;x++)
                        {
                            uint pixel=unchecked((uint)row[x]);int n=1+x*4;
                            scanline[n]=(byte)(pixel>>16);scanline[n+1]=(byte)(pixel>>8);
                            scanline[n+2]=(byte)pixel;scanline[n+3]=(byte)(pixel>>24);
                        }
                        // Filter None preserves transparent RGB and avoids cross-band state.
                        for(int start=0;start<scanline.Length;start+=5552)
                        {
                            int end=Math.Min(scanline.Length,start+5552);
                            for(int n=start;n<end;n++){adlerA+=scanline[n];adlerB+=adlerA;}
                            adlerA%=65521;adlerB%=65521;
                        }
                        deflate.Write(scanline,0,scanline.Length);
                    }
                }
                finally { bitmap.UnlockBits(data); }
            }
        }
        byte[] checksum=new byte[4];BigEndian(checksum,0,adlerB<<16|adlerA);
        idat.Write(checksum,0,4);idat.Flush();
        Chunk(output,"IEND",Array.Empty<byte>(),0);
    }

    private static void BigEndian(byte[] bytes,int offset,uint value)
    { bytes[offset]=(byte)(value>>24);bytes[offset+1]=(byte)(value>>16);bytes[offset+2]=(byte)(value>>8);bytes[offset+3]=(byte)value; }

    private static readonly uint[] CrcTable=MakeCrcTable();
    private static uint[] MakeCrcTable()
    {
        uint[] table=new uint[256];
        for(uint n=0;n<table.Length;n++){uint c=n;for(int bit=0;bit<8;bit++)c=(c&1)!=0?0xEDB88320^(c>>1):c>>1;table[n]=c;}
        return table;
    }
    private static void Chunk(Stream output,string type,byte[] bytes,int count)
    {
        byte[] size=new byte[4],name=Encoding.ASCII.GetBytes(type);
        BigEndian(size,0,(uint)count);output.Write(size,0,4);output.Write(name,0,4);output.Write(bytes,0,count);
        uint crc=uint.MaxValue;
        foreach(byte b in name)crc=CrcTable[(crc^b)&255]^(crc>>8);
        for(int n=0;n<count;n++)crc=CrcTable[(crc^bytes[n])&255]^(crc>>8);
        BigEndian(size,0,crc^uint.MaxValue);output.Write(size,0,4);
    }

    private sealed class IdatStream : Stream
    {
        private readonly Stream output;
        private readonly byte[] buffer=new byte[64*1024];
        private int used;
        internal IdatStream(Stream output)=>this.output=output;
        public override void Write(byte[] bytes,int offset,int count)
        {
            while(count>0){int n=Math.Min(count,buffer.Length-used);Buffer.BlockCopy(bytes,offset,buffer,used,n);used+=n;offset+=n;count-=n;if(used==buffer.Length)Flush();}
        }
        public override void Flush(){if(used==0)return;Chunk(output,"IDAT",buffer,used);used=0;}
        public override bool CanRead=>false;
        public override bool CanSeek=>false;
        public override bool CanWrite=>true;
        public override long Length=>throw new NotSupportedException();
        public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override int Read(byte[] bytes,int offset,int count)=>throw new NotSupportedException();
        public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException();
    }
}
