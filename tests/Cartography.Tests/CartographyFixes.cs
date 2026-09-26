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
        document=Annotated();document.Transparent=true;document.ExportScale=1.25f;
        var scene=CartographySceneBuilder.Build(document,source);
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
