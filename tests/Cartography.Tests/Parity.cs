using System;
using System.Drawing;
using System.IO;
using System.Linq;
using DryCycle.DevUI.DevTool.Map.Cartography;

internal static partial class Program
{
    private static void Parity()
    {
        var source=Source();var d=Annotated();var route=d.Items.First(i=>i.Kind==CartographyItemKind.Connection).Clone();
        route.Appearance.Route=CartographyRouteMode.Manual;route.Points.Add(new CartographyPoint{X=95,Y=-80});route.Appearance.Dashed=true;route.Color=0xFFFF8822;
        d=CartographyEditing.Apply(d,source,new CartographyCommand{Kind=CartographyCommandKind.UpdateItem,Item=route});
        var scene=CartographySceneBuilder.Build(d,source);var drawn=scene.Nodes.First(n=>n.Id==route.Id);
        Check(drawn.Points.Length==3&&drawn.Points[1].Y==-80,"Manual routes retain authored bends.");
        var aligned=CartographyEditing.Apply(d,source,new CartographyCommand{Kind=CartographyCommandKind.AlignPorts,Ids=new[]{route.Id},Integer=0});
        var link=CartographySceneBuilder.Build(aligned,source).Nodes.First(n=>n.Id==route.Id);
        Check(Math.Abs(link.FromY-link.ToY)<.0001f,"Align exits aligns the actual ports, not room centers.");
        Check(d.Items.First(i=>i.Kind==CartographyItemKind.Connection).Points.Count==1,"Alignment does not mutate the undo snapshot.");
        var child=new CartographyItem{Kind=CartographyItemKind.Text,Text="[c:f80][b]GATE[/b][/c] [ic:karma4]",X=12,Y=-55};child.Appearance.ParentId="room:SU_A01";
        d=CartographyEditing.Apply(d,source,new CartographyCommand{Kind=CartographyCommandKind.Add,Item=child});
        float before=CartographySceneBuilder.Resolve(d,child).X;
        var moved=CartographyEditing.Apply(d,source,new CartographyCommand{Kind=CartographyCommandKind.Move,Ids=new[]{"room:SU_A01",child.Id},X=20,Y=10});
        Check(CartographySceneBuilder.Resolve(moved,moved.Items.Find(i=>i.Id==child.Id)).X==before+20,"Parent and selected child move once, not twice.");
        moved=CartographyEditing.Apply(moved,source,new CartographyCommand{Kind=CartographyCommandKind.Delete,Ids=new[]{"room:SU_A01"}});
        Check(!moved.Items.Any(i=>i.Id==child.Id),"Deleting a parent removes attached author objects atomically.");
        var room=d.Items.First(i=>i.Kind==CartographyItemKind.Room).Clone();room.Appearance.OverridePalette=true;room.Appearance.Background=0xFFFF0000;room.Appearance.WaterLevel=99;room.Appearance.Acid=true;room.Appearance.AcidColor=0xFF00FF00;
        d=CartographyEditing.Apply(d,source,new CartographyCommand{Kind=CartographyCommandKind.UpdateItem,Item=room});
        var raster=CartographyDrawing.Room(d,room,source.Rooms[room.Room]);
        Check(raster.Pixels.Any(c=>(c>>8&255)>0&&(c>>16&255)>0),"Room background and acid water both affect room pixels.");
        string xml=CartographyStorage.Serialize(d);var round=CartographyStorage.Deserialize(xml,d.Identity);
        Check(round.Items.First(i=>i.Id==route.Id).Points[0].Y==-80&&round.Items.First(i=>i.Id==room.Id).Appearance.Acid,"Routes, room appearance and parent references round trip.");
        scene=CartographySceneBuilder.Build(d,source);d.Options.ExportArea=true;d.Options.AreaX=-100;d.Options.AreaY=-150;d.Options.AreaWidth=120;d.Options.AreaHeight=80;d.ExportScale=1;
        CartographyExporter.Dimensions(d,scene,out int w,out int h);Check(w==120&&h==80,"Export selection controls exact output dimensions.");
        foreach(var format in new[]{CartographyExportFormat.Psd,CartographyExportFormat.ImageMap})
        {string path=Path.Combine(output,format==CartographyExportFormat.Psd?"cartography.psd":"cartography-map.json");CartographyExporter.Export(d,scene,path,format,CartographyStorage.HashFile(path));Check(new FileInfo(path).Length>100,"Layered/map export produces content.");}
        Check(System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(output,"cartography.psd")),0,4)=="8BPS","PSD has a valid signature.");
        var map=CartographyJson.Parse(File.ReadAllText(Path.Combine(output,"cartography-map.json")));Check(map.Number("width")==120&&map.Objects("objects").Any(),"Image map JSON contains coordinates and identity.");
        using Bitmap bitmap=new(24,24);bitmap.SetPixel(5,6,Color.FromArgb(80,25,80,220));using MemoryStream png=new();bitmap.Save(png,System.Drawing.Imaging.ImageFormat.Png);
        var image=new CartographyItem{Kind=CartographyItemKind.Image,Width=24,Height=24};image.Appearance.Image=Convert.ToBase64String(png.ToArray());
        d=CartographyEditing.Apply(d,source,new CartographyCommand{Kind=CartographyCommandKind.Add,Item=image});
        var decoded=CartographyAssets.Image(CartographyStorage.Deserialize(CartographyStorage.Serialize(d),d.Identity).Items.Last().Appearance.Image);
        Check(decoded.Pixels[6*24+5]>>24==80,"Embedded image alpha survives project round trip.");
    }
    private static void CorniferRegion(string directory)
    {
        CartographyAssets.LoadDirectory(directory);var imported=CartographyCorniferImport.Load(Path.Combine(directory,"state.json"));var d=imported.Document;var source=imported.Source;
        Check(source.Rooms.Count>40&&source.Rooms.Values.Count(r=>r.Ready)>40,"Real CC region terrain is decoded from Cornifer state.");
        Check(d.Items.Count(i=>i.Kind==CartographyItemKind.Connection)>40,"Real region imports editable connections.");
        Check(d.Palettes.Any(p=>p.Name=="The Gutter"),"Subregion palette is imported.");
        Check(d.Items.Any(i=>i.Marker==CartographyMarker.Gate)&&CartographyAssets.Sprite("karma4")!=null,"Gate markers use real karma atlas glyphs.");
        Check(CartographyAssets.BitmapFonts.ContainsKey("RodondoExt20M"),"Local pixel font is loaded.");
        var scene=CartographySceneBuilder.Build(d,source);Check(scene.Errors.Length==0,"Full CC scene has complete visible terrain.");
        d.ExportScale=.55f;d.Background=0xFF6495ED;
        string path=Path.Combine(output,"CC-cornifer-import.png");var result=CartographyExporter.Export(d,scene,path,CartographyExportFormat.Png,CartographyStorage.HashFile(path));
        File.WriteAllText(Path.Combine(output,"CC-import-report.txt"),source.Rooms.Count+" rooms; "+d.Items.Count+" objects; "+scene.Warnings.Length+" route warnings; "+imported.Warnings.Length+" import warnings\n"+string.Join("\n",imported.Warnings)+"\n"+result.Width+" x "+result.Height);
        File.WriteAllText(Path.Combine(output,"CC-import-project.xml"),CartographyStorage.Serialize(d));
        Console.WriteLine("Full region preview: "+path);
    }
}
