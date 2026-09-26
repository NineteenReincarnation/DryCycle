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

        CartographyRaster raster = new(width, height, pixels);
        int curveOffset = 0;
        if (d.Options.Borders)
        {
            curveOffset = (int)Math.Ceiling(d.Options.BorderSize);
            raster = Outline(raster, d.Options.BorderSize, wall);
        }

        // Continuous terrain is a surface boundary in the map, not a filled rectangular band.
        // Draw the authored spline itself after room outlining so the room border cannot dilate the
        // curve into a thick block. This also keeps ordinary room geometry and curved geometry
        // visually consistent: CurvedSlope uses Solid color, LocalTerrain uses Structure color.
        return PaintCurvedTerrainCurves(
            d,
            a,
            room,
            kinds,
            water,
            raster,
            curveOffset,
            bg,
            wall,
            waterColor);
    }

    private static CartographyRaster PaintCurvedTerrainCurves(
        CartographyDocument document,
        CartographyAppearance appearance,
        CartographyRoomSource room,
        byte[] kinds,
        bool[] water,
        CartographyRaster source,
        int offset,
        uint background,
        uint wall,
        uint waterColor)
    {
        EditorMapPolylineSnapshot[] curves =
            room.CurvedTerrainCurves ??
            Array.Empty<EditorMapPolylineSnapshot>();
        if (curves.Length == 0)
            return source;

        uint[] pixels = (uint[])source.Pixels.Clone();
        for (int curveIndex = 0; curveIndex < curves.Length; curveIndex++)
        {
            EditorMapPolylineSnapshot curve = curves[curveIndex];
            EditorMapPointSnapshot[] points =
                curve?.Points ??
                Array.Empty<EditorMapPointSnapshot>();
            if (points.Length < 2)
                continue;

            uint color = CurveTerrainColor(curve.Kind, background, wall);
            for (int pointIndex = 1; pointIndex < points.Length; pointIndex++)
            {
                DrawCurveSegment(
                    pixels,
                    source.Width,
                    source.Height,
                    room,
                    kinds,
                    water,
                    appearance,
                    document,
                    offset,
                    points[pointIndex - 1],
                    points[pointIndex],
                    curve.Kind,
                    color,
                    waterColor);
            }

            if (curve.Closed && points.Length > 2)
            {
                DrawCurveSegment(
                    pixels,
                    source.Width,
                    source.Height,
                    room,
                    kinds,
                    water,
                    appearance,
                    document,
                    offset,
                    points[points.Length - 1],
                    points[0],
                    curve.Kind,
                    color,
                    waterColor);
            }
        }

        return new CartographyRaster(source.Width, source.Height, pixels);
    }

    private static uint CurveTerrainColor(
        EditorMapGeometryKind kind,
        uint background,
        uint wall) =>
        kind switch
        {
            // Same palette rules as ordinary room tiles: Solid is wall; Structure is the
            // 35%-background blend used by kind==3 in Room().
            EditorMapGeometryKind.LocalTerrain => Blend(wall, background, .35f),
            EditorMapGeometryKind.CurvedSlope => wall,
            _ => wall
        };

    private static void DrawCurveSegment(
        uint[] pixels,
        int rasterWidth,
        int rasterHeight,
        CartographyRoomSource room,
        byte[] kinds,
        bool[] water,
        CartographyAppearance appearance,
        CartographyDocument document,
        int offset,
        EditorMapPointSnapshot a,
        EditorMapPointSnapshot b,
        EditorMapGeometryKind kind,
        uint baseColor,
        uint waterColor)
    {
        int x0 = offset + (int)Math.Round(a.X * 3f);
        int y0 = offset + (int)Math.Round((room.Height - a.Y) * 3f);
        int x1 = offset + (int)Math.Round(b.X * 3f);
        int y1 = offset + (int)Math.Round((room.Height - b.Y) * 3f);

        int dx = Math.Abs(x1 - x0);
        int sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0);
        int sy = y0 < y1 ? 1 : -1;
        int error = dx + dy;

        while (true)
        {
            PaintCurvePixel(
                pixels,
                rasterWidth,
                rasterHeight,
                room,
                kinds,
                water,
                appearance,
                document,
                offset,
                x0,
                y0,
                kind,
                baseColor,
                waterColor);

            if (x0 == x1 && y0 == y1)
                break;

            int doubled = error * 2;
            if (doubled >= dy)
            {
                error += dy;
                x0 += sx;
            }
            if (doubled <= dx)
            {
                error += dx;
                y0 += sy;
            }
        }
    }

    private static void PaintCurvePixel(
        uint[] pixels,
        int rasterWidth,
        int rasterHeight,
        CartographyRoomSource room,
        byte[] kinds,
        bool[] water,
        CartographyAppearance appearance,
        CartographyDocument document,
        int offset,
        int x,
        int y,
        EditorMapGeometryKind kind,
        uint baseColor,
        uint waterColor)
    {
        if (x < 0 || y < 0 || x >= rasterWidth || y >= rasterHeight)
            return;

        int roomX = x - offset;
        int roomY = y - offset;
        if (roomX < 0 || roomY < 0 || roomX >= room.Width * 3 || roomY >= room.Height * 3)
            return;

        int tileColumn = Math.Max(0, Math.Min(room.Width - 1, roomX / 3));
        int tileRow = Math.Max(0, Math.Min(room.Height - 1, roomY / 3));
        int tileIndex = tileRow * room.Width + tileColumn;

        // Preserve shortcut/transport markers exactly as ordinary terrain rendering does.
        if (kinds[tileIndex] >= 4)
            return;

        uint color = baseColor;
        bool solid =
            kind == EditorMapGeometryKind.CurvedSlope ||
            kind == EditorMapGeometryKind.Solid;
        bool wet =
            appearance.WaterLevel == -2
                ? water[tileIndex]
                : appearance.WaterLevel >= 0 &&
                  room.Height - 1 - tileRow <= appearance.WaterLevel;

        if (wet && (!solid || appearance.WaterFront))
            color = Blend(color, waterColor, document.Options.WaterOpacity);

        pixels[y * rasterWidth + x] = color;
    }
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
