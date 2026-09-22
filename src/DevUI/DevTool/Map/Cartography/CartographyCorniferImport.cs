using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DryCycle.DevUI.DevTool.Map.Cartography;

internal sealed class CartographyImport
{
    internal CartographySource Source;
    internal CartographyDocument Document;
    internal string[] Warnings=Array.Empty<string>();
}

internal static class CartographyCorniferImport
{
    internal static CartographyImport Load(string path)
    {
        var state=CartographyJson.Parse(File.ReadAllText(path));var region=state.Object("region");string id=region.Text("id"),campaign=state.Text("slugcat","White");
        var rooms=region.Objects("rooms").ToDictionary(r=>r.Text("id"),StringComparer.OrdinalIgnoreCase);
        var source=CartographyRegionLoader.Parse(id,campaign,region.Text("world"),region.Text("map"),region.Text("properties"),region.Text("locks"),name=>rooms.TryGetValue(name,out var r)?(r.Text("data"),r.Text("settings")):(null,""));
        string identity="cornifer:"+Path.GetFullPath(path).ToLowerInvariant()+"|"+CartographyStorage.HashFile(path);
        CartographyDocument d=source.CreateDocument(identity,id);d.Options.Campaign=campaign;d.Options.SpriteDirectory=Path.GetDirectoryName(path);
        string font=CartographyAssets.BitmapFonts.ContainsKey("RodondoExt20M")?"RodondoExt20M":"Arial";d.FontFamily=font;
        var colors=state.Object("colors");
        uint Color(string value,uint fallback)
        {
            if(colors.ContainsKey(value))value=colors.Text(value);
            return CartographyText.Color(value,fallback);
        }
        foreach(var sub in region.Objects("subregions"))
        {string name=sub.Text("name");var palette=d.Palettes.Find(p=>p.Name==name);if(palette==null){palette=new CartographyPalette{Name=name};d.Palettes.Add(palette);}palette.Background=Color(sub.Text("background"),palette.Background);palette.Water=Color(sub.Text("water"),palette.Water);}
        var settings=state.Object("interface");
        d.CropSolid=!settings.Flag("disableRoomCropping");d.Options.TileWalls=settings.Flag("tileWalls",true);d.Options.Objects=settings.Flag("placedObjects",true);d.Options.Pickups=settings.Flag("placedPickups");
        d.Options.Slugcats=settings.Flag("slugcatIcons");d.Options.Diamonds=settings.Flag("slugcatDiamond",true);d.Options.HollowDiamonds=settings.Flag("hollowSlugcatDiamond");d.Options.Borders=settings.Flag("borders",true);
        d.Options.MarkShortcuts=settings.Flag("markShortcuts",true);d.Options.ExitsOnly=settings.Flag("markExitsOnly",true);d.Options.ShortcutBackground=settings.Flag("regionBGShortcuts",true);d.Options.WaterOpacity=1-settings.Number("waterTransparency",.3f);
        Dictionary<string,string> layerMap=new(StringComparer.OrdinalIgnoreCase){{"connections","links"},{"texts","notes"}};
        foreach(var layer in state.Objects("layers"))
        {
            string name=layer.Text("id");string target=layerMap.TryGetValue(name,out string existing)?existing:"cornifer:"+name;
            layerMap[name]=target;if(d.Layer(target)==null)d.Layers.Add(new CartographyLayer{Id=target,Name=layer.Text("name",name)});d.Layer(target).Visible=layer.Flag("visible",true);
        }
        if(layerMap.TryGetValue("rooms",out string roomLayer))foreach(var item in d.Items.Where(i=>i.Kind==CartographyItemKind.Room))item.LayerId=roomLayer;
        if(layerMap.TryGetValue("icons",out string icons))foreach(var item in d.Items.Where(i=>i.Kind==CartographyItemKind.Marker))item.LayerId=icons;
        List<string> warnings=new();
        void ImportObject(Dictionary<string,object> obj,CartographyItem parent)
        {
            string name=obj.Text("name"),type=obj.Text("type");var data=obj.Object("data");var pos=obj.Object("pos");
            CartographyItem item=parent==null?d.Items.Find(i=>i.Id=="room:"+name):d.Items.Find(i=>i.Id==parent.Room+"/"+name);
            if(item==null&&name.StartsWith("SubregionText_"))item=d.Items.Find(i=>i.Id.StartsWith("subregion:")&&name.EndsWith(i.Text.ToLowerInvariant().Replace(' ','-'),StringComparison.OrdinalIgnoreCase));
            if(item==null&&parent!=null&&name.Contains("@"))item=d.Items.Where(i=>i.Appearance.ParentId==parent.Id&&i.Kind==CartographyItemKind.Marker&&name.StartsWith(i.Text+"@",StringComparison.Ordinal)).OrderBy(i=>Math.Abs(i.X-pos.Number("x")*3)+Math.Abs(i.Y-pos.Number("y")*3)).FirstOrDefault();
            if(item==null)
            {
                if(type.Contains("MapText"))item=new CartographyItem{Kind=CartographyItemKind.Text,Text=data.Text("text",name),Size=30,Color=0xFFFFFFFF};
                else if(type.Contains("Icon")||type.Contains("PlacedObject"))item=new CartographyItem{Kind=CartographyItemKind.Marker,Marker=CartographyMarker.Sprite,Size=20,Color=0xFFFFFFFF};
                else if(type.Contains("MapImage"))item=new CartographyItem{Kind=CartographyItemKind.Image};
                else{warnings.Add("Unsupported Cornifer object: "+name+" "+type);return;}
                item.Id="import:"+(parent?.Id??"")+"/"+name;item.Appearance.ParentId=parent?.Id??"";d.Items.Add(item);
            }
            if(item.Kind==CartographyItemKind.Room&&source.Rooms.TryGetValue(item.Room,out var room))
            {
                item.X=pos.Number("x")*3+room.Width*1.5f;item.Y=pos.Number("y")*3+room.Height*1.5f;
                item.Appearance.Subregion=data.Text("subregion",item.Appearance.Subregion);
                item.Appearance.Deathpit=data.Flag("deathpit",item.Appearance.Deathpit);item.Appearance.BetterCutout=data.Flag("betterCutout",true);item.Appearance.CutAllSolid=data.Flag("cutAllSolid");
                item.Appearance.Shortcuts=data.Flag("inRoomShortcuts",true);item.Appearance.Acid=data.Flag("acidWater");item.Appearance.AcidColor=Color(data.Text("acidColor"),item.Appearance.AcidColor);item.Appearance.WaterLevel=(int)data.Number("waterLevel",-2);
                string target=data.Object("gateData").Text("targetName");CartographyItem label=d.Items.Find(i=>i.Id==room.Name+"/TargetRegionText");if(label!=null&&target.Length>0)label.Text="TO "+target.ToUpperInvariant();
            }
            else
            {
                // Cornifer child positions are relative to the parent's top-left, our children use its center.
                float ox=0,oy=0;if(parent!=null&&source.Rooms.TryGetValue(parent.Room,out var r)){ox=r.Width*1.5f;oy=r.Height*1.5f;}
                item.X=pos.Number("x")*3-ox;item.Y=pos.Number("y")*3-oy;
                item.Text=data.Text("text",item.Text);item.Color=Color(data.Text("color"),item.Color);item.Appearance.Shade=data.Flag("shade",true);item.Appearance.ShadeColor=Color(data.Text("shadeColor"),0xFF000000);
                item.Appearance.Font=data.Text("font",font);item.Size=Math.Max(4,Math.Min(256,item.Size*data.Number("scale",1)));
                item.Appearance.Icon=data.Text("sprite",data.Text("texture",item.Appearance.Icon));
                if(item.Marker==CartographyMarker.Gate)
                {
                    item.X+=160.5f;item.Y+=96;item.Size=96;
                    item.Appearance.Splitter=Color(data.Text("splitter"),item.Appearance.Splitter);item.Appearance.LeftColor=Color(data.Text("leftSymbol"),item.Appearance.LeftColor);item.Appearance.RightColor=Color(data.Text("rightSymbol"),item.Appearance.RightColor);
                    item.Appearance.LeftArrow=Color(data.Text("leftArrow"),item.Appearance.LeftArrow);item.Appearance.RightArrow=Color(data.Text("rightArrow"),item.Appearance.RightArrow);
                }
            }
            item.Visible=obj.Flag("active",item.Visible);string layer=obj.Text("layer");if(layerMap.TryGetValue(layer,out string selectedLayer))item.LayerId=selectedLayer;
            foreach(var child in obj.Objects("children"))ImportObject(child,item);
        }
        foreach(var obj in state.Objects("objects"))ImportObject(obj,null);
        foreach(var pair in state.Object("connections"))
        {
            if(pair.Value is not Dictionary<string,object> value)continue;
            if(pair.Key.StartsWith("#"))continue; // In-room passages are reconstructed from the shared shortcut compiler.
            string[] endpoints=pair.Key.Split('~');if(endpoints.Length<2)continue;
            var routes=d.Items.Where(i=>i.Kind==CartographyItemKind.Connection&&((i.Appearance.From==endpoints[0]&&i.Appearance.To==endpoints[1])||(i.Appearance.To==endpoints[0]&&i.Appearance.From==endpoints[1]))).ToArray();
            if(routes.Length!=1){warnings.Add("Ambiguous imported connection: "+pair.Key);continue;}
            var route=routes[0];route.Appearance.Route=CartographyRouteMode.Manual;route.Appearance.WhiteRed=value.Flag("whiteToRed",true);
            foreach(var p in value.Objects("points"))route.Points.Add(new CartographyPoint{X=p.Number("x")*3,Y=p.Number("y")*3});
            if(route.Appearance.From!=endpoints[0])route.Points.Reverse();
        }
        d.Validate();return new CartographyImport{Source=source,Document=d,Warnings=warnings.ToArray()};
    }
}
