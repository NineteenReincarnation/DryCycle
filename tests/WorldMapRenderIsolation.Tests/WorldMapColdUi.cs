using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using ImGuiNET;
using UnityEngine;
using Num = System.Numerics;

public static partial class MapRenderIsolationTests
{
    private static unsafe void ExerciseIndustrialColdUi(object scene, Dictionary<int, object> visuals)
    {
        void Property(object target, string name, object value) => target.GetType().GetProperty(name, Flags).SetValue(target, value);
        object snapshot = Activator.CreateInstance(Core("Map.EditorMapPresentationSnapshot"));
        Property(snapshot, "Available", true); Property(snapshot, "RegionName", "HI");
        object[] sceneRooms = ((IDictionary)Get(scene, "rooms")).Values.Cast<object>().ToArray();
        object[] sceneLinks = ((IDictionary)Get(scene, "connections")).Values.Cast<object>().ToArray();
        Array rooms = Array.CreateInstance(Core("Map.EditorMapRoomSnapshot"), sceneRooms.Length);
        Array links = Array.CreateInstance(Core("Map.EditorMapConnectionSnapshot"), sceneLinks.Length);
        for (int i = 0; i < sceneRooms.Length; i++)
        {
            object source = sceneRooms[i], room = Activator.CreateInstance(rooms.GetType().GetElementType());
            int index = (int)Get(source, "RoomIndex"); var pos = (Num.Vector2)Get(source, "WorldPosition");
            Property(room, "RoomIndex", index); Property(room, "Name", Get(source, "Name"));
            Property(room, "X", pos.X); Property(room, "Y", -pos.Y);
            var sourceNodes = (Array)visuals[index].GetType().GetProperty("Nodes").GetValue(visuals[index]);
            Array nodes = Array.CreateInstance(Core("Map.EditorMapRoomNodeSnapshot"), sourceNodes.Length);
            for (int n = 0; n < nodes.Length; n++)
            {
                object node = Activator.CreateInstance(nodes.GetType().GetElementType());
                Property(node, "NodeIndex", n); Property(node, "Type", "Exit"); Property(node, "Exit", true);
                nodes.SetValue(node, n);
            }
            Property(room, "Nodes", nodes); rooms.SetValue(room, i);
        }
        for (int i = 0; i < sceneLinks.Length; i++)
        {
            object link = Activator.CreateInstance(links.GetType().GetElementType());
            Property(link, "ConnectionId", Get(sceneLinks[i], "Id"));
            foreach (string name in new[] { "FromRoomIndex", "ToRoomIndex", "FromNodeIndex", "ToNodeIndex", "Direction", "Ambiguous" })
                Property(link, name, Get(sceneLinks[i], name));
            links.SetValue(link, i);
        }
        Property(snapshot, "Rooms", rooms); Property(snapshot, "Connections", links);
        Type hub = Core("Map.MapRoomGeometryPresentationHub");
        var published = (IDictionary)hub.GetField("published", Flags).GetValue(null);
        foreach (var pair in visuals) published[pair.Key] = pair.Value;
        var generation = hub.GetField("publishedGeneration", Flags);
        generation.SetValue(null, (int)generation.GetValue(null) + 1);

        ImGuiNative.LoadFunctionPointers(&GetProcAddress, native);
        IntPtr context = ImGui.CreateContext();
        var io = ImGui.GetIO(); io.NativePtr->IniFilename = null;
        io.DisplaySize = new Num.Vector2(1600, 1000); io.DeltaTime = 1f / 60;
        io.Fonts.AddFontDefault();
        Check(Native<BackendInit>("ImGui_ImplDX11_Init")(d3dDevice, d3dContext) != 0, "Cold HI UI uses the installed native ImGui/DX11 renderer.");
        newFrame = Native<BackendVoid>("ImGui_ImplDX11_NewFrame");
        shutdown = Native<BackendVoid>("ImGui_ImplDX11_Shutdown");
        renderDrawData = Native<BackendDraw>("ImGui_ImplDX11_RenderDrawData");
        Type runtime = Front("WorldMapRetainedV2Runtime"), map = Front("WorldMapView");
        try
        {
            int[] vertexCounts = new int[2];
            for (int mode = 0; mode < 2; mode++)
            {
                map.GetMethod("ResetRetainedState", Flags).Invoke(null, null);
                runtime.GetField("enabled", Flags).SetValue(null, mode == 1);
                var timer = Stopwatch.StartNew();
                for (int frame = 0; frame < 3; frame++)
                {
                    newFrame(); ImGui.NewFrame();
                    ImGui.SetNextWindowPos(Num.Vector2.Zero); ImGui.SetNextWindowSize(io.DisplaySize);
                    ImGui.Begin("Cold HI", ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDecoration);
                    map.GetMethod("Draw", Flags).Invoke(null, new[] { snapshot });
                    ImGui.End(); ImGui.Render();
                    vertexCounts[mode] = Math.Max(vertexCounts[mode], ImGui.GetDrawData().TotalVtxCount);
                }
                results.Add("HI UI " + (mode == 0 ? "detailed fallback" : "cold retained preparation") + ": " +
                    vertexCounts[mode] + " vertices, 3 frames " + timer.ElapsedMilliseconds + " ms (includes first JIT).");
            }
            Check(vertexCounts[1] < vertexCounts[0] / 2, "Cold loading no longer rebuilds the entire HI tile raster in ImGui.");
            RenderGui(1600, 1000, "HI-cold-ui.png");

            // Simulate the public adapter releasing its old SRV while rebuilding the next upload.
            // The bridge must own its publication independently until the replacement is committed.
            object bridge = New("WorldMapTextureBridge");
            bridge.GetType().GetMethod("Upload", Flags, null, new[] { typeof(int), typeof(int), typeof(uint[]) }, null)
                .Invoke(bridge, new object[] { 2, 2, new uint[] { 0xFFFF0000, 0xFFFF0000, 0xFFFF0000, 0xFFFF0000 } });
            object entry = Get(bridge, "raster"); Invoke(Get(entry, "Image"), "ReleaseSRV");
            newFrame(); ImGui.NewFrame();
            var draw = ImGui.GetForegroundDrawList();
            var present = bridge.GetType().GetMethod("TryPresent", Flags, null,
                new[] { typeof(ImDrawListPtr), typeof(Num.Vector2), typeof(Num.Vector2), typeof(uint) }, null);
            Check((bool)present.Invoke(bridge, new object[] { draw, new Num.Vector2(16, 16), new Num.Vector2(96, 96), uint.MaxValue }),
                "The last committed SRV stays presentable during adapter replacement.");
            ImGui.Render(); Invoke(bridge, "Reset");
            Color32[] pixels = RenderGui(128, 128, "map-upload-continuity.png");
            Check(pixels[32 * 128 + 32].r > 220, "An upload/eviction cannot invalidate an already submitted native ImGui image.");
            Front("WorldMapTextureFrame").GetMethod("Begin", Flags).Invoke(null, null);
        }
        finally
        {
            runtime.GetField("enabled", Flags).SetValue(null, false);
            map.GetMethod("ResetRetainedState", Flags).Invoke(null, null);
            published.Clear(); generation.SetValue(null, (int)generation.GetValue(null) + 1);
            shutdown(); ImGui.DestroyContext(context); Marshal.Release(d3dContext); Marshal.Release(d3dDevice);
        }
    }
}
