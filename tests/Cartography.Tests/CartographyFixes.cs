using System;
using System.Drawing;
using System.IO;
using System.Linq;
using DryCycle.DevUI.DevTool.Map.Cartography;

internal static partial class Program
{
    private static void CartographyFixes()
    {
        var source=Source();var document=source.CreateDocument("fixes","SU");
        Check(document.Transparent&&(document.Options.Canvas>>24)==0,"New cartography documents use a transparent composition and editor canvas by default.");
        var route=document.Items.First(i=>i.Kind==CartographyItemKind.Connection);
        var diagonal=CartographySceneBuilder.Route(document,source,route,document.Layer(route.LayerId));
        Check(diagonal.Primitives.Where(p=>p.Kind==CartographyPrimitiveKind.Line).All(p=>p.GuideOnly),"Diagonal Cornifer connections are editor guides.");
        var aligned=CartographyEditing.Apply(document,source,new CartographyCommand{Kind=CartographyCommandKind.AlignPorts,Ids=new[]{route.Id},Integer=0});
        var pipe=CartographySceneBuilder.Route(aligned,source,aligned.Items.Find(i=>i.Id==route.Id),aligned.Layer(route.LayerId));
        Check(pipe.Primitives.Any(p=>p.Dashed&&!p.GuideOnly&&p.DashLength==3&&p.DashGap==3&&p.PixelPerfect),"Exactly aligned exits produce the crisp alternating Cornifer pipe pattern, including old Dashed=true documents.");
        var horizontal=route.Clone();horizontal.Appearance.Route=CartographyRouteMode.HorizontalFirst;
        var horizontalNode=CartographySceneBuilder.Route(document,source,horizontal,document.Layer(horizontal.LayerId));
        Check(horizontalNode.Points.Length==3&&Math.Abs(horizontalNode.Points[1].X-horizontalNode.ToX)<.0001f&&Math.Abs(horizontalNode.Points[1].Y-horizontalNode.FromY)<.0001f,"Horizontal-first cartography routes use one Cornifer-style right-angle control point.");
        var vertical=route.Clone();vertical.Appearance.Route=CartographyRouteMode.VerticalFirst;
        var verticalNode=CartographySceneBuilder.Route(document,source,vertical,document.Layer(vertical.LayerId));
        Check(verticalNode.Points.Length==3&&Math.Abs(verticalNode.Points[1].X-verticalNode.FromX)<.0001f&&Math.Abs(verticalNode.Points[1].Y-verticalNode.ToY)<.0001f,"Vertical-first cartography routes use one Cornifer-style right-angle control point.");
        Check(diagonal.Primitives.Where(p=>p.Kind==CartographyPrimitiveKind.Line).Any(p=>p.GuideOnly&&Math.Abs(p.DashLength-11)<.0001f&&Math.Abs(p.DashGap-5)<.0001f),"Diagonal guides use Cornifer's black 11/5 dash silhouette.");

        var curvedRoom=new CartographyRoomSource
        {
            Name="CURVE_TEST",
            Width=20,
            Height=12,
            Settings=
                "PlacedObjects: "+
                "TerrainHandle><0><120><-40~0~40~0~20,"+
                "TerrainHandle><400><140><-40~0~40~0~20,"+
                "LocalTerrain><40><60><40~100^0~0^100~100^0~,"+
                "CurvedSlope><120><80><24~120^0~0^80~120^-40~,"+
                "SuperSlope><240><40><120~80~20"
        };
        CartographyRegionLoader.DecodeCurvedTerrain(curvedRoom);
        Check(curvedRoom.CurvedTerrainFills.Length>0&&curvedRoom.CurvedTerrainCurves.Length>0,"Cartography decodes TerrainHandle, LocalTerrain, CurvedSlope and SuperSlope authored geometry.");
        Check(curvedRoom.CurvedTerrainFills.Any(r=>r.Kind==DryCycle.DevUI.DevTool.Map.EditorMapGeometryKind.Solid)&&curvedRoom.CurvedTerrainFills.Any(r=>r.Kind==DryCycle.DevUI.DevTool.Map.EditorMapGeometryKind.Structure),"Curved cartography terrain retains ordinary Solid/Structure material semantics.");
        var curvedDocument=new CartographyDocument{Terrain=0xFF8899AA,CropSolid=false};
        curvedDocument.Options.Borders=false;
        curvedDocument.Options.Wall=0xFF112233;
        var curvedItem=new CartographyItem{Kind=CartographyItemKind.Room,Appearance=new CartographyAppearance()};
        var curvedRaster=CartographyDrawing.Room(curvedDocument,curvedItem,curvedRoom);
        Check(curvedRaster.Pixels.Any(p=>p==curvedDocument.Options.Wall),"Curved solid terrain uses the same wall color as ordinary solid terrain.");
        var localOnly=new CartographyRoomSource{Name="LOCAL_CURVE_TEST",Width=12,Height=10,Settings="PlacedObjects: LocalTerrain><40><60><40~100^0~0^100~100^0~"};
        CartographyRegionLoader.DecodeCurvedTerrain(localOnly);
        var localRaster=CartographyDrawing.Room(curvedDocument,curvedItem,localOnly);
        Check(localRaster.Pixels.Any(p=>p==curvedDocument.Options.Wall),"Local/custom curved terrain also uses ordinary terrain color with no special tint.");
        var returned=CartographyEditing.Apply(aligned,source,new CartographyCommand{Kind=CartographyCommandKind.Move,Ids=new[]{"room:SU_A02"},Y=12});
        Check(CartographySceneBuilder.Route(returned,source,returned.Items.Find(i=>i.Id==route.Id),returned.Layer(route.LayerId)).Primitives.Any(p=>p.GuideOnly),"Moving a room out of alignment restores guides using current port positions.");

        foreach(string type in new[]{"KarmaFlower","SeedCob","GhostSpot","BlueToken","GoldToken","RedToken","GreenToken","DataPearl","UniqueDataPearl"})
            Check(CartographyAssets.Sprite(CartographyRegionLoader.IconFor(type))?.Pixels.Any(p=>(p>>24)>0)==true,type+" has a packaged real sprite without a Cornifer installation.");
        var old=new CartographyItem{Kind=CartographyItemKind.Marker,Marker=CartographyMarker.Sprite,Text="KarmaFlower",Appearance=new CartographyAppearance{Category="Pickup",Icon="Symbol_KarmaFlower"}};
        string original=CartographyRecord.Key(old.Appearance);
        var shapes=new System.Collections.Generic.List<CartographyPrimitive>();
        Check(CartographyDrawing.Marker(shapes,old,1)&&shapes.All(p=>p.Kind==CartographyPrimitiveKind.Image),"Existing saved pickup names resolve to real sprites rather than question triangles.");
        Check(original==CartographyRecord.Key(old.Appearance),"Resolving old icons does not rewrite author data.");
        old.Appearance.Icon="my-mod-icon";
        Check(CartographyDrawing.SpriteName(old)=="my-mod-icon","Explicit mod icon choices remain intact.");

        // Compare the streaming encoder against one ordinary render across several strip boundaries.
        document=Annotated();document.Transparent=false;document.ExportScale=1.25f;
        var scene=CartographySceneBuilder.Build(document,source);
        CartographyExporter.Dimensions(document,scene,out int transparentWidth,out int transparentHeight);
        using(var transparentProbe=CartographyExporter.RenderBitmap(document,scene,transparentWidth,transparentHeight,null))
            Check((transparentProbe.GetPixel(0,0).ToArgb()>>24&255)==0,"Cartography bitmap export stays transparent even if a legacy document carries Transparent=false.");
        CartographyExporter.Dimensions(document,scene,out int width,out int height);
        string path=Path.Combine(output,"band-parity.png");
        CartographyExporter.Export(document,scene,path,CartographyExportFormat.Png,CartographyStorage.HashFile(path));
        using(var expected=CartographyExporter.RenderBitmap(document,scene,width,height,null))
        using(var actual=new Bitmap(path))
        {
            var expectedPixels=CartographyRaster.FromBitmap(expected).Pixels;
            var actualPixels=CartographyRaster.FromBitmap(actual).Pixels;
            if(!expectedPixels.SequenceEqual(actualPixels))
            {
                expected.Save(Path.Combine(output,"band-expected.png"));
                Console.WriteLine("Band mismatch: "+string.Join("; ",Enumerable.Range(0,expectedPixels.Length).Where(n=>expectedPixels[n]!=actualPixels[n]).Take(12).Select(n=>(n%width)+","+(n/width)+":"+expectedPixels[n].ToString("X8")+"/"+actualPixels[n].ToString("X8"))));
            }
            Check(expectedPixels.SequenceEqual(actualPixels),"Banded PNG matches full-frame pixels exactly, including alpha and strip boundaries.");
        }
        var large=new CartographyScene{Bounds=new CartographyRect(0,0,24000,8000)};
        document.Padding=0;document.ExportScale=1;
        CartographyExporter.Dimensions(document,large,out width,out height);
        Check(width==24000&&height==8000,"Normal large maps are accepted by the bounded-memory PNG exporter.");
        Throws(()=>CartographyExporter.Dimensions(document,large,out _,out _,CartographyExportFormat.Psd),"PSD retains its own full-bitmap allocation limit.");
        CartographyExporter.Dimensions(document,large,out width,out height,CartographyExportFormat.Svg);
        Check(width==24000,"Vector export does not inherit bitmap allocation limits.");
    }
}
