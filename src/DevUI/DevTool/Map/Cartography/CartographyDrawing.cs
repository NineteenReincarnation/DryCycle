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

        CartographyRaster raster = PaintCurvedTerrainBands(
            d,
            a,
            room,
            kinds,
            water,
            new CartographyRaster(width, height, pixels),
            bg,
            wall,
            waterColor);

        return d.Options.Borders
            ? Outline(raster, d.Options.BorderSize, wall)
            : raster;
    }

    private readonly struct CurveFillInterval
    {
        internal readonly float MinY;
        internal readonly float MaxY;

        internal CurveFillInterval(float minY, float maxY)
        {
            MinY = minY;
            MaxY = maxY;
        }
    }

    private static CartographyRaster PaintCurvedTerrainBands(
        CartographyDocument document,
        CartographyAppearance appearance,
        CartographyRoomSource room,
        byte[] kinds,
        bool[] water,
        CartographyRaster source,
        uint background,
        uint wall,
        uint waterColor)
    {
        EditorMapPolylineSnapshot[] curves =
            room.CurvedTerrainCurves ??
            Array.Empty<EditorMapPolylineSnapshot>();
        EditorMapRectSnapshot[] fills =
            room.CurvedTerrainFills ??
            Array.Empty<EditorMapRectSnapshot>();

        if (curves.Length == 0 || fills.Length == 0)
            return source;

        uint[] pixels = (uint[])source.Pixels.Clone();

        // CurvedTerrainFills are topology hints only. They tell us which side of the authored
        // surface is solid and how far that solid body extends; they are never drawn as rectangles.
        // The visible body is rebuilt into a mask from the real curve, then composited using the
        // same material colors/rules as ordinary room terrain.
        for (int curveIndex = 0; curveIndex < curves.Length; curveIndex++)
        {
            EditorMapPolylineSnapshot curve = curves[curveIndex];
            EditorMapPointSnapshot[] points =
                curve?.Points ??
                Array.Empty<EditorMapPointSnapshot>();
            if (points.Length < 2)
                continue;

            EditorMapGeometryKind fillKind = CurveFillKind(curve.Kind);
            bool[] mask = new bool[source.Width * source.Height];

            for (int pointIndex = 1; pointIndex < points.Length; pointIndex++)
            {
                AddCurveSegmentToMask(
                    mask,
                    source.Width,
                    source.Height,
                    room,
                    fills,
                    fillKind,
                    points[pointIndex - 1],
                    points[pointIndex]);
            }

            if (curve.Closed && points.Length > 2)
            {
                AddCurveSegmentToMask(
                    mask,
                    source.Width,
                    source.Height,
                    room,
                    fills,
                    fillKind,
                    points[points.Length - 1],
                    points[0]);
            }

            PaintCurvedTerrainMask(
                pixels,
                mask,
                source.Width,
                source.Height,
                room,
                kinds,
                water,
                appearance,
                document,
                fillKind,
                TerrainColor(fillKind, background, wall),
                wall,
                waterColor);
        }

        return new CartographyRaster(source.Width, source.Height, pixels);
    }

    private static EditorMapGeometryKind CurveFillKind(EditorMapGeometryKind kind) =>
        kind switch
        {
            EditorMapGeometryKind.LocalTerrain => EditorMapGeometryKind.Structure,
            EditorMapGeometryKind.CurvedSlope => EditorMapGeometryKind.Solid,
            _ => kind
        };

    private static uint TerrainColor(
        EditorMapGeometryKind kind,
        uint background,
        uint wall)
    {
        // "Normal floor" in Cartography is ordinary Solid terrain (kind == 2), whose visible
        // color is the room/palette wall color. Curved terrain changes geometry only, so every
        // authored curve uses that exact same floor color. The original semantic kind is still
        // retained for topology, water/deathpit behavior and masking.
        return wall;
    }

    private static void AddCurveSegmentToMask(
        bool[] mask,
        int rasterWidth,
        int rasterHeight,
        CartographyRoomSource room,
        EditorMapRectSnapshot[] fills,
        EditorMapGeometryKind fillKind,
        EditorMapPointSnapshot a,
        EditorMapPointSnapshot b)
    {
        float ax = a.X * 3f;
        float bx = b.X * 3f;
        float span = bx - ax;

        if (Math.Abs(span) < 0.0001f)
        {
            // Vertical authored segments are uncommon but valid. Sample them densely enough that
            // the topology lookup still produces one connected body instead of a pinhole.
            float ay = a.Y * 3f;
            float by = b.Y * 3f;
            int samples = Math.Max(1, (int)Math.Ceiling(Math.Abs(by - ay)));
            int x = Math.Max(0, Math.Min(rasterWidth - 1, (int)Math.Round(ax)));
            for (int sample = 0; sample <= samples; sample++)
            {
                float t = samples == 0 ? 0f : (float)sample / samples;
                float surfaceY = a.Y + (b.Y - a.Y) * t;
                AddCurveColumnToMask(
                    mask,
                    rasterWidth,
                    rasterHeight,
                    room,
                    fills,
                    fillKind,
                    x,
                    surfaceY);
            }
            return;
        }

        int minX = Math.Max(0, (int)Math.Floor(Math.Min(ax, bx)));
        int maxX = Math.Min(rasterWidth - 1, (int)Math.Ceiling(Math.Max(ax, bx)));

        for (int x = minX; x <= maxX; x++)
        {
            float sampleX = x + .5f;
            float t = (sampleX - ax) / span;
            if (t < -0.001f || t > 1.001f)
                continue;

            t = Math.Max(0f, Math.Min(1f, t));
            float surfaceY = a.Y + (b.Y - a.Y) * t;

            AddCurveColumnToMask(
                mask,
                rasterWidth,
                rasterHeight,
                room,
                fills,
                fillKind,
                x,
                surfaceY);
        }
    }

    private static void AddCurveColumnToMask(
        bool[] mask,
        int rasterWidth,
        int rasterHeight,
        CartographyRoomSource room,
        EditorMapRectSnapshot[] fills,
        EditorMapGeometryKind fillKind,
        int x,
        float surfaceY)
    {
        float tileX = (x + .5f) / 3f;
        if (!TryResolveSolidBoundary(
                fills,
                fillKind,
                tileX,
                surfaceY,
                out float solidBoundaryY))
            return;

        float surfacePixel = (room.Height - surfaceY) * 3f;
        float boundaryPixel = (room.Height - solidBoundaryY) * 3f;
        int minY = Math.Max(
            0,
            (int)Math.Floor(Math.Min(surfacePixel, boundaryPixel)));
        int maxY = Math.Min(
            rasterHeight - 1,
            (int)Math.Ceiling(Math.Max(surfacePixel, boundaryPixel)));

        for (int y = minY; y <= maxY; y++)
            mask[y * rasterWidth + x] = true;
    }

    private static bool TryResolveSolidBoundary(
        EditorMapRectSnapshot[] fills,
        EditorMapGeometryKind fillKind,
        float tileX,
        float surfaceY,
        out float solidBoundaryY)
    {
        solidBoundaryY = surfaceY;
        List<CurveFillInterval> intervals = new();

        // Merge every topology run intersecting this sample column. The old renderer picked a
        // single tiny rectangle and could leave gaps between simplified curve samples; the merged
        // interval represents the complete solid body at this x position.
        for (int i = 0; i < fills.Length; i++)
        {
            EditorMapRectSnapshot fill = fills[i];
            if (fill.Kind != fillKind ||
                fill.Width <= 0f ||
                fill.Height <= 0f)
                continue;

            const float xTolerance = .08f;
            if (tileX < fill.X - xTolerance ||
                tileX > fill.X + fill.Width + xTolerance)
                continue;

            intervals.Add(
                new CurveFillInterval(
                    fill.Y,
                    fill.Y + fill.Height));
        }

        if (intervals.Count == 0)
            return false;

        intervals.Sort((left, right) => left.MinY.CompareTo(right.MinY));
        List<CurveFillInterval> merged = new(intervals.Count);
        CurveFillInterval current = intervals[0];

        for (int i = 1; i < intervals.Count; i++)
        {
            CurveFillInterval next = intervals[i];
            if (next.MinY <= current.MaxY + .08f)
            {
                current = new CurveFillInterval(
                    current.MinY,
                    Math.Max(current.MaxY, next.MaxY));
                continue;
            }

            merged.Add(current);
            current = next;
        }
        merged.Add(current);

        int bestIndex = -1;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < merged.Count; i++)
        {
            CurveFillInterval interval = merged[i];
            float distance =
                surfaceY < interval.MinY
                    ? interval.MinY - surfaceY
                    : surfaceY > interval.MaxY
                        ? surfaceY - interval.MaxY
                        : 0f;

            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
            }
        }

        // Fill geometry and its surface originate from the same authored object, so a large
        // separation means this is another curve of the same material rather than our solid side.
        if (bestIndex < 0 || bestDistance > .75f)
            return false;

        CurveFillInterval best = merged[bestIndex];
        float toMin = Math.Abs(surfaceY - best.MinY);
        float toMax = Math.Abs(surfaceY - best.MaxY);

        // The farther side of the topology interval is the interior/back side. This works for
        // floors, ceilings, reversed slopes and finite-thickness bands without assuming "down".
        solidBoundaryY = toMin >= toMax
            ? best.MinY
            : best.MaxY;
        return true;
    }

    private static void PaintCurvedTerrainMask(
        uint[] pixels,
        bool[] mask,
        int rasterWidth,
        int rasterHeight,
        CartographyRoomSource room,
        byte[] kinds,
        bool[] water,
        CartographyAppearance appearance,
        CartographyDocument document,
        EditorMapGeometryKind fillKind,
        uint baseColor,
        uint wall,
        uint waterColor)
    {
        bool solid = fillKind == EditorMapGeometryKind.Solid;

        for (int y = 0; y < rasterHeight; y++)
        {
            for (int x = 0; x < rasterWidth; x++)
            {
                int pixelIndex = y * rasterWidth + x;
                if (!mask[pixelIndex])
                    continue;

                int tileColumn = Math.Max(
                    0,
                    Math.Min(room.Width - 1, x / 3));
                int tileRow = Math.Max(
                    0,
                    Math.Min(room.Height - 1, y / 3));
                int tileIndex = tileRow * room.Width + tileColumn;

                // Shortcut/transport pixels are authored overlays and keep precedence over terrain.
                if (kinds[tileIndex] >= 4)
                    continue;

                uint color = baseColor;
                bool wet =
                    appearance.WaterLevel == -2
                        ? water[tileIndex]
                        : appearance.WaterLevel >= 0 &&
                          room.Height - 1 - tileRow <= appearance.WaterLevel;

                if (wet && (!solid || appearance.WaterFront))
                    color = Blend(
                        color,
                        waterColor,
                        document.Options.WaterOpacity);

                if (appearance.Deathpit &&
                    tileRow >= room.Height - 5 &&
                    IsAirTile(
                        kinds,
                        room.Width,
                        room.Height,
                        tileColumn,
                        room.Height - 1))
                {
                    color = Blend(
                        wall,
                        color,
                        (room.Height - tileRow - .5f) / 5f);
                }

                pixels[pixelIndex] = color;
            }
        }
    }

    private static bool IsAirTile(
        byte[] kinds,
        int width,
        int height,
        int x,
        int y) =>
        x >= 0 &&
        y >= 0 &&
        x < width &&
        y < height &&
        kinds[y * width + x] != 2;

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
