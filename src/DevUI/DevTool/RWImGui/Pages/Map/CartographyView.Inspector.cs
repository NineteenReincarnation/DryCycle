using System;
using System.IO;
using System.Linq;
using DryCycle.DevUI.DevTool.Map.Cartography;
using ImGuiNET;
using Num=System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal static partial class CartographyView
{
    private static string importPath = "", imagePath = "", iconSearch = "", regionSearch = "";
    private static bool sourcePanel;

    private static void SourcePicker()
    {
        CartographySourcePicker state = CartographyRuntime.SourcePicker;
        bool chinese = DevToolUiSettings.Language == DevToolUiLanguage.Chinese;
        // Custom region names can contain Chinese even when the surrounding editor is English.
        ImFontPtr font = default;
        bool pushedFont = !chinese && state.Regions.Any(region => region.Name.Any(c => c > 255)) &&
            DevToolFontCatalog.TryResolveRegisteredFace(DevToolUiSettings.ChineseFontFamily, DevToolUiSettings.FontWeight,
                true, out font, out _, out _, out _);
        if (pushedFont) ImGui.PushFont(font);
        try { SourcePickerContent(state, chinese); }
        finally { if (pushedFont) ImGui.PopFont(); }
    }

    private static void SourcePickerContent(CartographySourcePicker state, bool chinese)
    {
        CartographyRegionOption selected = state.Regions.FirstOrDefault(region => region.Code == state.Region);
        string label = selected?.Label(chinese) ?? (state.Region.Length == 0 ? T("正在获取当前区域…", "Finding current region…") : state.Region);
        ImGui.SetNextItemWidth(Math.Max(180, Math.Min(360, ImGui.GetContentRegionAvail().X - 20)));
        if (ImGui.BeginCombo(T("区域##AtlasRegion", "Region##AtlasRegion"), label))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##AtlasRegionSearch", T("搜索缩写或全称", "Search code or full name"), ref regionSearch, 160);
            foreach (CartographyRegionOption region in state.Regions)
            {
                string text = region.Label(chinese);
                if (regionSearch.Length > 0 && text.IndexOf(regionSearch, StringComparison.OrdinalIgnoreCase) < 0 &&
                    region.Name.IndexOf(regionSearch, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (ImGui.Selectable(text + "##" + region.Code, region.Code == state.Region))
                    SelectSource(region.Code, state.Campaign);
            }
            ImGui.EndCombo();
        }
        DevToolWidgets.SameLineIfFits(DevToolWidgets.ButtonWidth(T("当前玩家区域", "Player region")));
        if (ImGui.Button(T("当前玩家区域##AtlasPlayerRegion", "Player region##AtlasPlayerRegion")))
            SelectSource(null, null);
        DevToolWidgets.SameLineIfFits(DevToolWidgets.ButtonWidth(T("刷新", "Refresh")));
        if (ImGui.Button(T("刷新##AtlasRefresh", "Refresh##AtlasRefresh")))
            SelectSource(state.Region, state.Campaign, true);
        DevToolWidgets.SameLineIfFits(DevToolWidgets.ButtonWidth(T("高级", "Advanced")));
        if (ImGui.Button(T("高级##AtlasSourceOptions", "Advanced##AtlasSourceOptions"))) sourcePanel = !sourcePanel;
        if (state.Loading)
            ImGui.TextDisabled(T("正在后台准备地图，可继续选择区域…", "Preparing map in background; you can select another region…"));
        if (state.Failed)
            ImGui.TextWrapped(T("区域载入失败，原有制图已保留：", "Region load failed; existing composition retained: ") + state.Status);

        if (sourcePanel)
        {
            ImGui.TextDisabled(T("角色影响区域布局；切换区域会保留未保存的修改。", "Campaign affects layouts. Switching regions retains unsaved edits."));
            ImGui.SetNextItemWidth(180);
            if (ImGui.BeginCombo(T("角色##AtlasCampaign", "Campaign##AtlasCampaign"), CampaignLabel(state.Campaign)))
            {
                foreach (string campaign in CartographyRegionLoader.Campaigns)
                    if (ImGui.Selectable(CampaignLabel(campaign) + "##" + campaign, campaign == state.Campaign))
                        SelectSource(state.Region, campaign);
                ImGui.EndCombo();
            }
            ImGui.SetNextItemWidth(Math.Min(500, ImGui.GetContentRegionAvail().X));
            ImGui.InputTextWithHint("##AtlasImport", T("可选：Cornifer 存档 state.json 的路径", "Optional: path to a Cornifer state.json"), ref importPath, 1024);
            if (ImGui.Button(T("导入 Cornifer 存档", "Import Cornifer state")))
            {
                LeaveDrafts();
                CartographyRuntime.Enqueue(new CartographyCommand { Kind = CartographyCommandKind.ImportCornifer, Path = importPath });
            }
        }
        ImGui.Separator();
    }

    private static string CampaignLabel(string campaign) => campaign switch
    {
        "White" => T("求生者", "Survivor"), "Yellow" => T("僧侣", "Monk"), "Red" => T("猎手", "Hunter"),
        "Gourmand" => T("饕餮", "Gourmand"), "Artificer" => T("工匠", "Artificer"), "Rivulet" => T("溪流", "Rivulet"),
        "Spear" => T("矛大师", "Spearmaster"), "Saint" => T("圣徒", "Saint"), "Inv" => T("？？？", "???"),
        "Watcher" => T("观望者", "Watcher"), _ => campaign
    };

    private static void SelectSource(string region, string campaign, bool refresh = false)
    {
        LeaveDrafts();
        CartographyRuntime.Enqueue(new CartographyCommand { Kind = CartographyCommandKind.SelectRegion,
            LayerId = region, Item = campaign == null ? null : new CartographyItem { Text = campaign }, RefreshSource = refresh });
    }

    private static void LeaveDrafts() { CommitDraft(); CommitLayer(); SaveStyle(); FinishGesture(); }
    private static void ExtraToolbar()
    {
        ImGui.SameLine();if(ImGui.Button(T("导出选区","Export area"))){CommitDraft();tool=Tool.ExportArea;}
    }
    private static void ExtendedItemInspector(CartographyPresentation snapshot)
    {
        var a=draft.Appearance;
        void B(string cn,string en,ref bool v){if(ImGui.Checkbox(T(cn,en),ref v)){draftDirty=true;CommitDraft();}}
        void C(string cn,string en,ref uint v){if(EditColor(T(cn,en),ref v))draftDirty=true;if(ImGui.IsItemDeactivatedAfterEdit())CommitDraft();}
        void F(string cn,string en,ref float v,float min,float max){if(ImGui.DragFloat(T(cn,en),ref v,.25f,min,max))draftDirty=true;if(ImGui.IsItemDeactivatedAfterEdit())CommitDraft();}
        void S(string cn,string en,ref string v,int length=160){if(ImGui.InputText(T(cn,en),ref v,(uint)length))draftDirty=true;if(ImGui.IsItemDeactivatedAfterEdit())CommitDraft();}
        F("对象透明度","Object opacity",ref a.Opacity,0,1);
        if(draft.Kind==CartographyItemKind.Room)
        {
            if(ImGui.BeginCombo(T("子区域","Subregion"),a.Subregion.Length==0?T("主区域","Main region"):a.Subregion))
            {foreach(var p in snapshot.Document.Palettes)if(ImGui.Selectable(p.Name.Length==0?T("主区域","Main region"):p.Name,a.Subregion==p.Name)){a.Subregion=p.Name;draftDirty=true;CommitDraft();}ImGui.EndCombo();}
            B("使用房间独立配色","Override room palette",ref a.OverridePalette);
            if(a.OverridePalette){C("房间背景色","Room background",ref a.Background);C("墙体色","Wall color",ref a.Wall);C("房间水色","Room water",ref a.Water);}
            B("优化轮廓裁剪","Preserve narrow walls",ref a.BetterCutout);B("裁去所有实心格","Cut all solid tiles",ref a.CutAllSolid);
            B("房间内部捷径","In-room passages",ref a.Shortcuts);B("死亡深坑","Deathpit",ref a.Deathpit);B("酸水","Acid water",ref a.Acid);
            if(a.Acid)C("酸水颜色","Acid color",ref a.AcidColor);
            if(ImGui.InputInt(T("水位（-2 原始，-1 无水）","Water level (-2 source, -1 dry)"),ref a.WaterLevel))draftDirty=true;
            if(ImGui.IsItemDeactivatedAfterEdit())CommitDraft();B("水覆盖地形","Water in front",ref a.WaterFront);
        }
        if(draft.Kind==CartographyItemKind.Connection)
        {
            ImGui.TextWrapped(a.From+" ["+a.FromPort+"] → "+a.To+" ["+a.ToPort+"]");
            int mode=(int)a.Route;
            if(ImGui.Combo(T("线路方式","Routing"),ref mode,T("直线\0先水平\0先垂直\0手动控制点\0","Straight\0Horizontal first\0Vertical first\0Manual points\0"))){a.Route=(CartographyRouteMode)mode;draftDirty=true;CommitDraft();}
            B("虚线","Dashed",ref a.Dashed);B("出口红色端点","Red exit endpoints",ref a.WhiteRed);
            if(ImGui.Button(T("出口水平对齐","Align exit Y")))Send(CartographyCommandKind.AlignPorts,c=>{c.Ids=new[]{draft.Id};c.Integer=0;});
            ImGui.SameLine();if(ImGui.Button(T("出口垂直对齐","Align exit X")))Send(CartographyCommandKind.AlignPorts,c=>{c.Ids=new[]{draft.Id};c.Integer=1;});
            ImGui.TextWrapped(T("选中线路后双击加点，拖动圆点改线；Alt+点击删点。Shift 拖动保持水平或垂直。","Double-click a selected line to add a point; drag handles to reroute; Alt-click removes a point; Shift constrains the axis."));
            for(int n=0;n<draft.Points.Count;n++)
            {
                ImGui.PushID(n);var p=draft.Points[n];Num.Vector2 v=new(p.X,p.Y);
                if(ImGui.DragFloat2("##RoutePoint",ref v,1)){p.X=v.X;p.Y=v.Y;draftDirty=true;}
                if(ImGui.IsItemDeactivatedAfterEdit())CommitDraft();ImGui.SameLine();
                if(ImGui.SmallButton("×")){draft.Points.RemoveAt(n--);draftDirty=true;CommitDraft();}ImGui.PopID();
            }
            if(ImGui.SmallButton(T("清除控制点","Clear points"))){draft.Points.Clear();a.Route=CartographyRouteMode.Straight;draftDirty=true;CommitDraft();}
        }
        if(draft.Kind==CartographyItemKind.Text)
        {
            if(ImGui.BeginCombo(T("字体","Font"),a.Font.Length==0?snapshot.Document.FontFamily:a.Font))
            {foreach(string font in CartographyRuntime.Fonts)if(ImGui.Selectable(font,a.Font==font)){a.Font=font;draftDirty=true;CommitDraft();}ImGui.EndCombo();}
            B("粗体","Bold",ref a.Bold);ImGui.SameLine();B("斜体","Italic",ref a.Italic);ImGui.SameLine();B("下划线","Underline",ref a.Underline);
            F("基线对齐","Baseline alignment",ref a.Alignment,0,1);B("投影","Drop shadow",ref a.DropShadow);
            if(ImGui.TreeNode(T("富文本格式帮助","Text formatting guide")))
            {ImGui.TextWrapped("[c:ff8800]color[/c]  [s:000]outline[/s]  [ns]no outline[/ns]\n[b]bold[/b] [i]italic[/i] [u]underline[/u]\n[sc:2]scale[/sc] [a:.6]align[/a]\n[ic:ShelterMarker] [ic:karma4:ff0]\n[ds:555]shadow[/ds]  \\[ = [");ImGui.TreePop();}
        }
        if(draft.Kind==CartographyItemKind.Marker)
        {
            int m=(int)draft.Marker;if(ImGui.Combo(T("标志类型","Marker type"),ref m,string.Join("\0",Enum.GetNames(typeof(CartographyMarker)))+"\0")){draft.Marker=(CartographyMarker)m;draftDirty=true;CommitDraft();}
            if(draft.Marker==CartographyMarker.Gate)
            {
                S("左业力要求","Left karma",ref a.LeftKarma);S("右业力要求","Right karma",ref a.RightKarma);S("目标区域","Target region",ref a.TargetRegion);
                C("左符号色","Left symbol",ref a.LeftColor);C("右符号色","Right symbol",ref a.RightColor);C("左箭头色","Left arrow",ref a.LeftArrow);C("右箭头色","Right arrow",ref a.RightArrow);C("分隔线颜色","Splitter",ref a.Splitter);
                if(ImGui.SmallButton(T("交换符号色","Swap symbol colors"))){(a.LeftColor,a.RightColor)=(a.RightColor,a.LeftColor);draftDirty=true;CommitDraft();}
                ImGui.SameLine();if(ImGui.SmallButton(T("交换箭头色","Swap arrow colors"))){(a.LeftArrow,a.RightArrow)=(a.RightArrow,a.LeftArrow);draftDirty=true;CommitDraft();}
            }
            else
            {
                S("图集图标","Atlas sprite",ref a.Icon);
                if(ImGui.BeginCombo(T("选择图标","Choose icon"),a.Icon))
                {ImGui.InputTextWithHint("##AtlasIconFilter",T("搜索图标","Search icons"),ref iconSearch,100);foreach(string icon in CartographyRuntime.Icons.Where(n=>n.IndexOf(iconSearch,StringComparison.OrdinalIgnoreCase)>=0))if(ImGui.Selectable(icon,a.Icon==icon)){a.Icon=icon;draft.Marker=CartographyMarker.Sprite;draftDirty=true;CommitDraft();}ImGui.EndCombo();}
                S("适用角色（逗号分隔）","Available campaigns (comma separated)",ref a.Availability,512);
            }
        }
        if(draft.Kind==CartographyItemKind.Text||draft.Kind==CartographyItemKind.Marker||draft.Kind==CartographyItemKind.Connection)
        {B("描边","Outline",ref a.Shade);if(a.Shade){C("描边颜色","Outline color",ref a.ShadeColor);F("描边宽度","Outline width",ref a.Outline,0,32);}}
        if(draft.Kind==CartographyItemKind.Image)
        {Num.Vector2 extent=new(draft.Width,draft.Height);if(ImGui.DragFloat2(T("图片尺寸","Image size"),ref extent,1,1,100000)){draft.Width=extent.X;draft.Height=extent.Y;draftDirty=true;}if(ImGui.IsItemDeactivatedAfterEdit())CommitDraft();}
        if(draft.Kind!=CartographyItemKind.Room&&draft.Kind!=CartographyItemKind.Connection)
        {
            string parentLabel=snapshot.Document.Items.Find(i=>i.Id==a.ParentId)?.Room??T("独立对象","Independent");
            if(ImGui.BeginCombo(T("跟随房间","Attach to room"),parentLabel))
            {
                if(ImGui.Selectable(T("独立对象","Independent"),a.ParentId.Length==0))SetParent(snapshot,"");
                foreach(var room in snapshot.Document.Items.Where(i=>i.Kind==CartographyItemKind.Room))if(ImGui.Selectable(room.Room,a.ParentId==room.Id))SetParent(snapshot,room.Id);
                ImGui.EndCombo();
            }
        }
    }
    private static void SetParent(CartographyPresentation s,string id)
    {
        CartographyItem world=CartographySceneBuilder.Resolve(s.Document,draft);var parent=s.Document.Items.Find(i=>i.Id==id);
        draft.X=world.X-(parent?.X??0);draft.Y=world.Y-(parent?.Y??0);draft.Appearance.ParentId=id;draftDirty=true;CommitDraft();
    }
    private static void ExtendedWorkspaceInspector(CartographyPresentation snapshot)
    {
        styleDraft??=snapshot.Document.Clone();var o=styleDraft.Options;
        void B(string cn,string en,ref bool v){if(ImGui.Checkbox(T(cn,en),ref v)){styleDirty=true;SaveStyle();}}
        void C(string cn,string en,ref uint v){if(EditColor(T(cn,en),ref v))styleDirty=true;if(ImGui.IsItemDeactivatedAfterEdit())SaveStyle();}
        if(CartographyCanvasImages.Error.Length>0)ImGui.TextWrapped(CartographyCanvasImages.Error);
        if(ImGui.CollapsingHeader(T("子区域配色##AtlasSubregions","SUBREGION COLORS##AtlasSubregions")))
        {
            foreach(var palette in styleDraft.Palettes)
            {
                ImGui.PushID(palette.Name);if(ImGui.TreeNode(palette.Name.Length==0?T("主区域","Main region"):palette.Name))
                {B("显示子区域","Show subregion",ref palette.Visible);C("房间背景颜色","Room background",ref palette.Background);C("墙体颜色","Walls",ref palette.Wall);C("水体颜色","Water",ref palette.Water);ImGui.TreePop();}ImGui.PopID();
            }
            if(ImGui.SmallButton(T("重置子区域颜色","Reset subregion colors"))){foreach(var p in styleDraft.Palettes){p.Background=CartographyRegionLoader.DefaultColor(snapshot.Document.Region,p.Name);p.Water=0xFF0000FF;}styleDirty=true;SaveStyle();}
        }
        if(ImGui.CollapsingHeader(T("可见内容##AtlasVisibility","VISIBILITY##AtlasVisibility")))
        {
            B("房间背景墙","Tile walls",ref o.TileWalls);B("房间外描边","Room borders",ref o.Borders);B("捷径出口标记","Shortcut markers",ref o.MarkShortcuts);B("仅显示房间出口","Only room exits",ref o.ExitsOnly);B("捷径使用房间底色","Shortcut room background",ref o.ShortcutBackground);
            B("房间内捷径连线","In-room passages",ref o.InRoomShortcuts);B("房间对象","Placed objects",ref o.Objects);B("可拾取物","Pickups",ref o.Pickups);B("特殊房间标志","Special rooms",ref o.SpecialRooms);B("角色可用性","Campaign availability",ref o.Slugcats);B("菱形角色标记","Diamond markers",ref o.Diamonds);B("不适用角色空心菱形","Unavailable hollow diamonds",ref o.HollowDiamonds);
            if(ImGui.InputText(T("隐藏对象类型","Hidden object types"),ref o.HiddenTypes,2048))styleDirty=true;if(ImGui.IsItemDeactivatedAfterEdit())SaveStyle();
            if(ImGui.SliderFloat(T("水体不透明度","Water opacity"),ref o.WaterOpacity,0,1))styleDirty=true;if(ImGui.IsItemDeactivatedAfterEdit())SaveStyle();
            C("编辑画布颜色","Editor canvas",ref o.Canvas);C("房间名称颜色","Room name color",ref o.RoomNameColor);
        }
        if(ImGui.CollapsingHeader(T("图片与叠加层##AtlasImages","IMAGES & OVERLAYS##AtlasImages")))
        {
            ImGui.InputTextWithHint("##AtlasImagePath",T("PNG / JPG / BMP 文件路径","PNG / JPG / BMP path"),ref imagePath,1024);
            if(ImGui.Button(T("导入图片到活动图层","Import image into active layer")))Send(CartographyCommandKind.AddImage,c=>{c.Path=imagePath;c.Item=new CartographyItem{Kind=CartographyItemKind.Image,LayerId=activeLayer,X=snapshot.Scene.Bounds.X,Y=snapshot.Scene.Bounds.Y,Color=0xFFFFFFFF};});
            ImGui.TextWrapped(T("选择图片可调整位置、尺寸和透明度；图层顺序决定前景或背景叠加。","Select an image to change position, size and opacity. Layer order controls foreground/background placement."));
        }
        if(ImGui.CollapsingHeader(T("导出范围##AtlasArea","EXPORT AREA##AtlasArea")))
        {
            B("使用画布选区","Use selected area",ref o.ExportArea);
            Num.Vector4 r=new(o.AreaX,o.AreaY,o.AreaWidth,o.AreaHeight);
            if(ImGui.DragFloat4("X / Y / W / H",ref r,1)){o.AreaX=r.X;o.AreaY=r.Y;o.AreaWidth=Math.Max(1,r.Z);o.AreaHeight=Math.Max(1,r.W);styleDirty=true;}if(ImGui.IsItemDeactivatedAfterEdit())SaveStyle();
        }
        if(ImGui.CollapsingHeader(T("快捷键##AtlasKeys","KEYBINDINGS##AtlasKeys")))
        {
            void Key(string label,ref string value){if(ImGui.InputText(label,ref value,64))styleDirty=true;if(ImGui.IsItemDeactivatedAfterEdit())SaveStyle();}
            Key(T("删除","Delete"),ref o.DeleteKey);Key(T("复制对象","Duplicate"),ref o.DuplicateKey);Key(T("复制","Copy"),ref o.CopyKey);Key(T("剪切","Cut"),ref o.CutKey);Key(T("粘贴","Paste"),ref o.PasteKey);
            ImGui.TextWrapped(T("例如 Ctrl+D。保存 Ctrl+S、撤销 Ctrl+Z / Ctrl+Y 由编辑器统一管理。","Example: Ctrl+D. Save and Undo use the editor-wide Ctrl+S / Ctrl+Z / Ctrl+Y bindings."));
        }
        if(styleDirty)Stage(CartographyCommandKind.Style,"style",c=>c.Style=styleDraft);
    }
}
