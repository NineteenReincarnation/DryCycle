using System;
using System.Collections.Generic;
using System.Linq;
using DryCycle.DevUI.DevTool.Map;

namespace DryCycle.DevUI.DevTool.Map.Cartography;

internal static class CartographyDrawing
{
    internal static CartographyRaster Room(CartographyDocument d,CartographyItem item,CartographyRoomSource room)
    {
        int w=room.Width,h=room.Height;byte[] kinds=new byte[w*h];bool[] water=new bool[w*h];
        foreach(CartographyTileRun run in room.Runs)for(int x=run.X;x<run.X+run.Length;x++){int i=(h-1-run.Y)*w+x;kinds[i]=(byte)run.Kind;water[i]=run.Water;}
        var a=item.Appearance;CartographyPalette palette=d.Palettes.Find(p=>p.Name==a.Subregion);
        uint bg=a.OverridePalette?a.Background:palette?.Background??d.Terrain;
        uint wall=a.OverridePalette?a.Wall:palette?.Wall??d.Options.Wall;
        uint waterColor=a.Acid?a.AcidColor:a.OverridePalette?a.Water:palette?.Water??d.Water;
        bool[] exterior=new bool[w*h];Queue<int> queue=new();
        void Seed(int i){if(kinds[i]==2&&!exterior[i]){exterior[i]=true;queue.Enqueue(i);}}
        for(int x=0;x<w;x++){Seed(x);Seed((h-1)*w+x);}for(int y=0;y<h;y++){Seed(y*w);Seed(y*w+w-1);}
        while(queue.Count>0){int i=queue.Dequeue(),x=i%w,y=i/w;if(x>0)Seed(i-1);if(x+1<w)Seed(i+1);if(y>0)Seed(i-w);if(y+1<h)Seed(i+w);}
        int width=w*3,height=h*3;uint[] pixels=new uint[width*height];
        bool Air(int x,int y)=>x>=0&&y>=0&&x<w&&y<h&&kinds[y*w+x]!=2;
        for(int y=0;y<h;y++)for(int x=0;x<w;x++)
        {
            int i=y*w+x,kind=kinds[i];bool cut=false;
            if(d.CropSolid&&kind==2)
            {
                if(a.CutAllSolid)cut=true;
                else if(exterior[i])
                {
                    bool border=Air(x-1,y)||Air(x+1,y)||Air(x,y-1)||Air(x,y+1);
                    bool brace=false;
                    if(a.BetterCutout&&!border)
                    {
                        bool left=false,right=false,top=false,bottom=false;
                        for(int n=1;n<=20;n++){left|=Air(x-n,y);right|=Air(x+n,y);top|=Air(x,y-n);bottom|=Air(x,y+n);}
                        brace=left&&right||top&&bottom;
                    }
                    cut=!border&&!brace;
                }
            }
            if(cut)continue;
            uint color=kind==2?wall:kind==1&&d.Options.TileWalls?Blend(wall,bg,.75f):kind==3?Blend(wall,bg,.35f):bg;
            bool wet=a.WaterLevel==-2?water[i]:a.WaterLevel>=0&&h-1-y<=a.WaterLevel;
            if(wet&&(kind!=2||a.WaterFront))color=Blend(color,waterColor,d.Options.WaterOpacity);
            if(a.Deathpit&&y>=h-5&&Air(x,h-1))color=Blend(wall,color,(h-y-.5f)/5);
            if(kind>=4&&d.Options.MarkShortcuts&&(!d.Options.ExitsOnly||kind==4))color=0xFFFF2020;
            else if(kind>=4&&!d.Options.ShortcutBackground)color=0xFFFFFFFF;
            for(int yy=0;yy<3;yy++)for(int xx=0;xx<3;xx++)pixels[(y*3+yy)*width+x*3+xx]=color;
        }

        PaintCurvedTerrain(
            d,
            a,
            room,
            kinds,
            water,
            pixels,
            width,
            height,
            bg,
            wall,
            waterColor);

        CartographyRaster raster=new(width,height,pixels);
        return d.Options.Borders?Outline(raster,d.Options.BorderSize,wall):raster;
    }

    private static void PaintCurvedTerrain(
        CartographyDocument document,
        CartographyAppearance appearance,
        CartographyRoomSource room,
        byte[] kinds,
        bool[] water,
        uint[] pixels,
        int width,
        int height,
        uint background,
        uint wall,
        uint waterColor)
    {
        EditorMapRectSnapshot[] fills =
            room.CurvedTerrainFills ??
            Array.Empty<EditorMapRectSnapshot>();

        if (fills.Length == 0)
            return;

        int roomWidth =
            room.Width;
        int roomHeight =
            room.Height;

        for (int i = 0;
             i < fills.Length;
             i++)
        {
            EditorMapRectSnapshot fill =
                fills[i];

            if (fill.Width <= 0f ||
                fill.Height <= 0f)
                continue;

            // Continuous terrain changes geometry only. It deliberately uses the exact ordinary
            // terrain material color instead of introducing a separate curve/structure tint.
            uint baseColor =
                wall;

            int minX =
                Math.Max(
                    0,
                    (int)Math.Floor(
                        fill.X * 3f));
            int maxX =
                Math.Min(
                    width - 1,
                    (int)Math.Ceiling(
                        (fill.X +
                         fill.Width) *
                        3f) -
                    1);
            int minY =
                Math.Max(
                    0,
                    (int)Math.Floor(
                        (roomHeight -
                         (fill.Y +
                          fill.Height)) *
                        3f));
            int maxY =
                Math.Min(
                    height - 1,
                    (int)Math.Ceiling(
                        (roomHeight -
                         fill.Y) *
                        3f) -
                    1);

            if (maxX < minX ||
                maxY < minY)
                continue;

            bool solid =
                fill.Kind ==
                    EditorMapGeometryKind.Solid ||
                fill.Kind ==
                    EditorMapGeometryKind.CurvedSlope;

            for (int py = minY;
                 py <= maxY;
                 py++)
            {
                float tileY =
                    roomHeight -
                    (py + 0.5f) /
                    3f;

                if (tileY < fill.Y ||
                    tileY >
                        fill.Y +
                        fill.Height)
                    continue;

                int tileRow =
                    Math.Max(
                        0,
                        Math.Min(
                            roomHeight - 1,
                            py / 3));

                for (int px = minX;
                     px <= maxX;
                     px++)
                {
                    float tileX =
                        (px + 0.5f) /
                        3f;

                    if (tileX < fill.X ||
                        tileX >
                            fill.X +
                            fill.Width)
                        continue;

                    int tileColumn =
                        Math.Max(
                            0,
                            Math.Min(
                                roomWidth - 1,
                                px / 3));
                    int tileIndex =
                        tileRow *
                        roomWidth +
                        tileColumn;

                    // Shortcut mouths and transport markers remain presentation overlays just like
                    // they do on ordinary terrain; continuous terrain must not paint over them.
                    if (kinds[tileIndex] >= 4)
                        continue;

                    uint color =
                        baseColor;

                    bool wet =
                        appearance.WaterLevel == -2
                            ? water[tileIndex]
                            : appearance.WaterLevel >= 0 &&
                              roomHeight -
                                  1 -
                                  tileRow <=
                              appearance.WaterLevel;

                    if (wet &&
                        (!solid ||
                         appearance.WaterFront))
                    {
                        color =
                            Blend(
                                color,
                                waterColor,
                                document.Options.WaterOpacity);
                    }

                    // Match the ordinary terrain death-pit fade. This is not a curve-specific
                    // effect; it is the same room presentation rule sampled at the curve pixel.
                    if (appearance.Deathpit &&
                        tileRow >=
                            roomHeight - 5 &&
                        IsAir(
                            kinds,
                            roomWidth,
                            roomHeight,
                            tileColumn,
                            roomHeight - 1))
                    {
                        color =
                            Blend(
                                wall,
                                color,
                                (roomHeight -
                                 tileRow -
                                 0.5f) /
                                5f);
                    }

                    pixels[
                        py *
                        width +
                        px] =
                        color;
                }
            }
        }
    }

    private static bool IsAir(
        byte[] kinds,
        int width,
        int height,
        int x,
        int y) =>
        x >= 0 &&
        y >= 0 &&
        x < width &&
        y < height &&
        kinds[
            y *
            width +
            x] != 2;
    internal static CartographyRaster Outline(CartographyRaster raster,float size,uint color)
    {
        int radius=(int)Math.Ceiling(size);if(radius<=0)return raster;
        int w=raster.Width+radius*2,h=raster.Height+radius*2;uint[] pixels=new uint[w*h];
        for(int y=0;y<raster.Height;y++)for(int x=0;x<raster.Width;x++)
        {
            if((raster.Pixels[y*raster.Width+x]>>24)==0)continue;
            // Only boundary pixels need dilation; interiors are copied below.
            if(x>0&&y>0&&x+1<raster.Width&&y+1<raster.Height&&(raster.Pixels[y*raster.Width+x-1]>>24)>0&&(raster.Pixels[y*raster.Width+x+1]>>24)>0&&(raster.Pixels[(y-1)*raster.Width+x]>>24)>0&&(raster.Pixels[(y+1)*raster.Width+x]>>24)>0)continue;
            for(int dy=-radius;dy<=radius;dy++)for(int dx=-radius;dx<=radius;dx++)if(dx*dx+dy*dy<=radius*radius)pixels[(y+radius+dy)*w+x+radius+dx]=color;
        }
        for(int y=0;y<raster.Height;y++)for(int x=0;x<raster.Width;x++){uint v=raster.Pixels[y*raster.Width+x];if((v>>24)>0)pixels[(y+radius)*w+x+radius]=v;}
        return new CartographyRaster(w,h,pixels);
    }
    private static uint Blend(uint a,uint b,float t)
    {
        t=Math.Max(0,Math.Min(1,t));uint r=(uint)((a>>16&255)*(1-t)+(b>>16&255)*t),g=(uint)((a>>8&255)*(1-t)+(b>>8&255)*t),bl=(uint)((a&255)*(1-t)+(b&255)*t);
        return 0xFF000000|r<<16|g<<8|bl;
    }
    internal static bool Marker(List<CartographyPrimitive> shapes,CartographyItem i,float opacity)
    {
        var a=i.Appearance;float size=i.Size*a.Scale;
        if(i.Marker==CartographyMarker.Gate)
        {
            float scale=size/42;uint c=CartographySceneBuilder.Alpha(a.Splitter,opacity);
            // Cornifer's 107 x 64 gate group retains native symbol proportions below the arrows.
            if(a.Shade)shapes.Add(new CartographyPrimitive{Kind=CartographyPrimitiveKind.Line,Rect=new CartographyRect(i.X,i.Y-32*scale,0,64*scale),Color=CartographySceneBuilder.Alpha(a.ShadeColor,opacity),Stroke=5*scale+a.Outline*2});
            shapes.Add(new CartographyPrimitive{Kind=CartographyPrimitiveKind.Line,Rect=new CartographyRect(i.X,i.Y-32*scale,0,64*scale),Color=c,Stroke=5*scale});
            Karma(shapes,a.LeftKarma,i.X,i.Y+12*scale,scale,-1,a.LeftColor,a,opacity);
            Karma(shapes,a.RightKarma,i.X,i.Y+12*scale,scale,1,a.RightColor,a,opacity);
            AddSprite(shapes,CartographyAssets.Sprite("Misc_ArrowRight"),i.X-33.5f*scale,i.Y-25.5f*scale,22*scale,a.RightArrow,a,opacity);
            AddSprite(shapes,CartographyAssets.Sprite("Misc_ArrowLeft"),i.X+33.5f*scale,i.Y-25.5f*scale,22*scale,a.LeftArrow,a,opacity);return true;
        }
        string name=SpriteName(i);
        if(i.Marker==CartographyMarker.Diamond){Diamond(shapes,i.X,i.Y,size,i.Color,false,opacity);return true;}
        CartographyRaster sprite=CartographyAssets.Sprite(name);
        if(sprite==null)return false;
        AddSprite(shapes,sprite,i.X,i.Y,size*2,i.Color,a,opacity);return true;
    }
    internal static string SpriteName(CartographyItem i)
    {
        var a=i.Appearance;
        // Correct old generated names at presentation time; retain custom choices and author data.
        if((a.Category=="Pickup"||a.Category=="Object")&&a.Icon=="Symbol_"+i.Text)
            return CartographyIconCatalog.IconFor(i.Text);
        return a.Icon.Length>0?a.Icon:i.Marker switch
        {CartographyMarker.Shelter=>"ShelterMarker",CartographyMarker.AncientShelter=>"ShelterMarker",CartographyMarker.Trader or CartographyMarker.Outpost or CartographyMarker.Treasury=>"ChieftainA",CartographyMarker.Echo=>"Object_GhostSpot",CartographyMarker.Broadcast=>"Symbol_Satellite",CartographyMarker.Token=>"Sandbox_Unlock",CartographyMarker.Slugcat=>"Kill_Slugcat",CartographyMarker.Pearl=>"Symbol_Pearl",_=>""};
    }
    private static void Karma(List<CartographyPrimitive> shapes,string value,float x,float y,float scale,int side,uint color,CartographyAppearance a,float opacity)
    {
        string name=int.TryParse(value,out int n)?"karma"+Math.Max(0,n-1):value=="R"?"Misc_KarmaR":"karma9";
        CartographyRaster sprite=CartographyAssets.Sprite(name)??CartographyAssets.Sprite(name+"-9");
        if(sprite!=null)AddSprite(shapes,sprite,x+side*(14.5f+sprite.Width/2f)*scale,y,sprite.Width*scale,color,a,opacity);
        else
        {
            CartographyItem label=new(){Kind=CartographyItemKind.Text,Text=value,Size=30*scale,Color=color,Appearance=a.Clone()};
            CartographyRaster text=CartographyText.Render(label,"Arial");shapes.Add(new CartographyPrimitive{Kind=CartographyPrimitiveKind.Image,Rect=new CartographyRect(x-text.Width/4f,y-text.Height/4f,text.Width/2f,text.Height/2f),Color=CartographySceneBuilder.Alpha(0xFFFFFFFF,opacity),Raster=text});
        }
    }
    private static void AddSprite(List<CartographyPrimitive> shapes,CartographyRaster sprite,float x,float y,float width,uint color,CartographyAppearance a,float opacity)
    {
        float k=width/sprite.Width;sprite=sprite.Tint(color);if(a.Shade)sprite=Outline(sprite,a.Outline/k,a.ShadeColor);
        float w=sprite.Width*k,h=sprite.Height*k;
        shapes.Add(new CartographyPrimitive{Kind=CartographyPrimitiveKind.Image,Rect=new CartographyRect(x-w/2,y-h/2,w,h),Color=CartographySceneBuilder.Alpha(0xFFFFFFFF,opacity),Raster=sprite});
    }
    private static void Arrow(List<CartographyPrimitive> shapes,float x,float y,int dir,float size,uint color,CartographyAppearance a,float opacity)
    {
        var segments=new[]{new CartographyRect(x-dir*size,y,dir*size*2,0),new CartographyRect(x+dir*size,y,-dir*size*.6f,-size*.6f),new CartographyRect(x+dir*size,y,-dir*size*.6f,size*.6f)};
        if(a.Shade)foreach(var r in segments)shapes.Add(new CartographyPrimitive{Kind=CartographyPrimitiveKind.Line,Rect=r,Color=CartographySceneBuilder.Alpha(a.ShadeColor,opacity),Stroke=5+a.Outline*2});
        foreach(var r in segments)shapes.Add(new CartographyPrimitive{Kind=CartographyPrimitiveKind.Line,Rect=r,Color=CartographySceneBuilder.Alpha(color,opacity),Stroke=5});
    }
    private static void Diamond(List<CartographyPrimitive> shapes,float x,float y,float size,uint color,bool hollow,float opacity)
    {
        foreach(var r in new[]{new CartographyRect(x,y-size,size,size),new CartographyRect(x+size,y,-size,size),new CartographyRect(x,y+size,-size,-size),new CartographyRect(x-size,y,size,-size)})
            shapes.Add(new CartographyPrimitive{Kind=CartographyPrimitiveKind.Line,Rect=r,Color=CartographySceneBuilder.Alpha(color,opacity),Stroke=hollow?1.5f:3});
    }
    internal static void Availability(List<CartographyPrimitive> shapes,CartographyItem i,CartographyOptions options,float opacity)
    {
        string[] available=i.Appearance.Availability.Split(',');int ordinal=0;
        foreach(string campaign in CartographyRegionLoader.Campaigns)
        {
            bool present=available.Contains(campaign);if(!present&&!options.HollowDiamonds)continue;
            float x=i.X+(ordinal++-2)*9,y=i.Y+i.Size+12;
            uint color=campaign switch{"Yellow"=>0xFFFFFF55,"Red"=>0xFFFF6666,"Saint"=>0xFFAAFF88,"Rivulet"=>0xFF88DDEE,"Artificer"=>0xFFCC5555,"Spear"=>0xFF563B60,_=>0xFFFFFFFF};
            if(!options.Diamonds&&CartographyAssets.Sprite("Slugcat_"+campaign) is CartographyRaster sprite)AddSprite(shapes,sprite,x,y,12,color,i.Appearance,opacity);
            else Diamond(shapes,x,y,3.5f,color,!present,opacity);
        }
    }
}
