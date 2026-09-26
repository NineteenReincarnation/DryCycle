using System;
using System.Collections;
using System.Linq;
using System.Runtime.InteropServices;
using ImGuiNET;
using Num=System.Numerics;

public static partial class MapRenderIsolationTests
{
    private static unsafe void ExerciseCorniferConnections()
    {
        ImGuiNative.LoadFunctionPointers(&GetProcAddress, native);
        IntPtr context=ImGui.CreateContext();
        var io=ImGui.GetIO();io.NativePtr->IniFilename=null;
        io.DisplaySize=new Num.Vector2(920,520);io.DeltaTime=1f/60;
        io.Fonts.AddFontDefault();
        Check(Native<BackendInit>("ImGui_ImplDX11_Init")(d3dDevice,d3dContext)!=0,"Connection check uses the real ImGui DX11 renderer.");
        newFrame=Native<BackendVoid>("ImGui_ImplDX11_NewFrame");
        shutdown=Native<BackendVoid>("ImGui_ImplDX11_Shutdown");
        renderDrawData=Native<BackendDraw>("ImGui_ImplDX11_RenderDrawData");
        try { ExerciseCorniferLinePreview(verifyInput:true); ExerciseCorniferRoomCrossing(); }
        finally
        {
            shutdown();ImGui.DestroyContext(context);
            Marshal.Release(d3dContext);Marshal.Release(d3dDevice);
        }
    }

    private static void ExerciseCorniferLinePreview(bool verifyInput=false)
    {
        object NewCore(string name)=>Activator.CreateInstance(Core("Map.Cartography."+name),true);
        object Rect(float x,float y,float w,float h)=>Activator.CreateInstance(Core("Map.Cartography.CartographyRect"),Flags,null,new object[]{x,y,w,h},null);
        object source=NewCore("CartographySource");
        foreach(string name in new[]{"A","B"})
        {
            object room=NewCore("CartographyRoomSource");
            Set(room,"Name",name);Set(room,"Ready",true);Set(room,"Width",10);Set(room,"Height",10);
            ((IDictionary)Get(room,"Ports"))[0]=Rect(5,5,0,0);
            ((IDictionary)Get(source,"Rooms"))[name]=room;
        }
        object connection=NewCore("CartographyConnectionSource");
        Set(connection,"From","A");Set(connection,"To","B");Set(connection,"FromPort",0);Set(connection,"ToPort",0);
        ((IList)Get(source,"Connections")).Add(connection);
        object document=Invoke(source,"CreateDocument","line-preview","TEST");
        var items=((IEnumerable)Get(document,"Items")).Cast<object>().ToArray();
        object a=items.Single(i=>(string)Get(i,"Room")=="A"),b=items.Single(i=>(string)Get(i,"Room")=="B");
        object link=items.Single(i=>Get(i,"Kind").ToString()=="Connection");
        object layer=Invoke(document,"Layer","links");
        object Node(float ax,float ay,float bx,float by,string mode)
        {
            Set(a,"X",ax);Set(a,"Y",ay);Set(b,"X",bx);Set(b,"Y",by);
            Set(Get(link,"Appearance"),"Route",Enum.Parse(Core("Map.Cartography.CartographyRouteMode"),mode));
            return Core("Map.Cartography.CartographySceneBuilder").GetMethod("Route",Flags).Invoke(null,new[]{document,source,link,layer});
        }
        object[] nodes={Node(80,50,80,220,"Straight"),Node(150,80,410,80,"Straight"),Node(150,130,410,220,"Straight"),Node(470,50,720,220,"HorizontalFirst")};
        var view=Front("CartographyView");
        ImGui.GetIO().DisplaySize=new Num.Vector2(920,520);
        view.GetField("pan",Flags).SetValue(null,Num.Vector2.Zero);
        foreach(float scale in new[]{1f,2f,.5f})
        {
            view.GetField("zoom",Flags).SetValue(null,scale);
            newFrame();ImGui.NewFrame();
            var draw=ImGui.GetBackgroundDrawList();
            draw.AddRectFilled(Num.Vector2.Zero,new Num.Vector2(920,520),0xFFED9564);
            foreach(object node in nodes)
                foreach(object shape in (IEnumerable)Get(node,"Primitives"))
                    view.GetMethod("DrawPrimitive",Flags).Invoke(null,new object[]{draw,shape,Num.Vector2.Zero,Num.Vector2.Zero,Rect(0,0,920/scale,520/scale)});
            ImGui.Render();
            var pixels=RenderGui(920,520,"cornifer-lines-"+(scale*100)+"-gpu.png");
            int center=(int)Math.Round(81.5f*scale),whiteRows=0,blackRows=0;
            for(int y=(int)(65*scale);y<(int)(205*scale);y++)
            {
                if(Enumerable.Range(center-2,5).Any(x=>pixels[y*920+x].r>220&&pixels[y*920+x].g>220&&pixels[y*920+x].b>220))whiteRows++;
                var black=pixels[y*920+center-(int)(10*scale)];
                if(black.r<15&&black.g<15&&black.b<15)blackRows++;
            }
            Check(whiteRows>=(int)(139*scale),"Aligned Cornifer preview keeps a continuous white center at "+scale+" zoom.");
            Check(blackRows>=(int)(139*scale),"Aligned Cornifer preview has the wide black channel at "+scale+" zoom.");
        }
        if(verifyInput)
        {
            Set(Get(link,"Appearance"),"Route",Enum.Parse(Core("Map.Cartography.CartographyRouteMode"),"Straight"));
            object Scene(object doc)=>Core("Map.Cartography.CartographySceneBuilder").GetMethod("Build",Flags).Invoke(null,new[]{doc,source,null});
            object snapshot=NewCore("CartographyPresentation");
            Set(snapshot,"Identity","line-preview");Set(snapshot,"Revision",1L);
            Set(snapshot,"Document",document);Set(snapshot,"Source",source);Set(snapshot,"Scene",Scene(document));
            Core("Map.Cartography.CartographyRuntime").GetField("presentation",Flags).SetValue(null,snapshot);
            ExerciseRouteInput(snapshot,source,Scene);
        }
    }
}
