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
    private static unsafe void ExerciseWorldMapOptimization()
    {
        ExerciseOpenCorridor();
        ExerciseThumbnailReuse();
        ImGuiNative.LoadFunctionPointers(&GetProcAddress, native);
        IntPtr context = ImGui.CreateContext();
        var io = ImGui.GetIO(); io.NativePtr->IniFilename = null;
        io.DisplaySize = new Num.Vector2(920, 520); io.DeltaTime = 1f / 60;
        io.Fonts.AddFontDefault();
        Check(Native<BackendInit>("ImGui_ImplDX11_Init")(d3dDevice, d3dContext) != 0, "World Map uses the real ImGui DX11 backend.");
        newFrame = Native<BackendVoid>("ImGui_ImplDX11_NewFrame");
        shutdown = Native<BackendVoid>("ImGui_ImplDX11_Shutdown");
        renderDrawData = Native<BackendDraw>("ImGui_ImplDX11_RenderDrawData");
        try { ExerciseMapDirections(); }
        finally { shutdown(); ImGui.DestroyContext(context); Marshal.Release(d3dContext); Marshal.Release(d3dDevice); }
    }

    private static void ExerciseOpenCorridor()
    {
        Type router = Front("WorldMapOrthogonalRouter");
        object request = New("WorldMapOrthogonalRouter+Request");
        Set(request, "Id", "open-corridor"); Set(request, "StartRoom", 0); Set(request, "EndRoom", 1);
        Set(request, "StartRoomMin", new Num.Vector2(0, 0)); Set(request, "StartRoomMax", new Num.Vector2(80, 100));
        Set(request, "EndRoomMin", new Num.Vector2(144, 0)); Set(request, "EndRoomMax", new Num.Vector2(224, 100));
        Set(request, "Start", new Num.Vector2(75, 50)); Set(request, "End", new Num.Vector2(149, 50));
        Set(request, "StartDirection", Num.Vector2.UnitX); Set(request, "EndDirection", -Num.Vector2.UnitX);
        Array requests = Array.CreateInstance(request.GetType(), 1); requests.SetValue(request, 0);
        object Obstacle(int id, float x0, float y0, float x1, float y1) =>
            Activator.CreateInstance(Front("WorldMapOrthogonalRouter+Obstacle"), Flags, null,
                new object[] { id, new Num.Vector2(x0, y0), new Num.Vector2(x1, y1) }, null);
        Array obstacles = Array.CreateInstance(Front("WorldMapOrthogonalRouter+Obstacle"), 2);
        obstacles.SetValue(Obstacle(0, 0, 0, 80, 100), 0); obstacles.SetValue(Obstacle(1, 144, 0, 224, 100), 1);
        object Solve(Array obs) => ((Array)router.GetMethod("BuildRoutesCore", Flags)
            .Invoke(null, new object[] { requests, obs, false, null, null })).GetValue(0);
        float Length(Num.Vector2[] p) => p.Zip(p.Skip(1), Num.Vector2.Distance).Sum();
        object aligned = Solve(obstacles);
        var path = (Num.Vector2[])Get(aligned, "Points");
        Check(path.All(p => Math.Abs(p.Y - 50) < .01f) && Math.Abs(Length(path) - 74) < .01f,
            "Facing aligned sockets take the exact straight distance with no overshoot.");
        Set(request, "End", new Num.Vector2(149, 72));
        path = (Num.Vector2[])Get(Solve(obstacles), "Points");
        Check(Math.Abs(Length(path) - 96) < .01f, "Staggered nearby sockets use a shortest orthogonal corridor.");
        Check(path[0] == new Num.Vector2(75, 50) && path[path.Length - 1] == new Num.Vector2(149, 72),
            "Optimized paths still terminate at the real pipe coordinates.");
        var clock = Stopwatch.StartNew();
        object cached = null;
        for (int i = 0; i < 100; i++) cached = Solve(obstacles);
        clock.Stop();
        Check((bool)Get(cached, "Reused"), "A stable route is reused from cache.");
        results.Add("100 cached route lookups: " + clock.Elapsed.TotalMilliseconds.ToString("F2") + " ms.");

        Array blocked = Array.CreateInstance(Front("WorldMapOrthogonalRouter+Obstacle"), 3);
        Array.Copy(obstacles, blocked, 2); blocked.SetValue(Obstacle(2, 104, 20, 120, 95), 2);
        path = (Num.Vector2[])Get(Solve(blocked), "Points");
        bool entersRoom = false;
        for (int i = 0; i + 1 < path.Length; i++)
            entersRoom |= (bool)router.GetMethod("SegmentIntersectsRect", Flags).Invoke(null,
                new object[] { path[i], path[i + 1], new Num.Vector2(104, 20), new Num.Vector2(120, 95) });
        Check(!entersRoom && Length(path) > 96, "An obstructed corridor reroutes without crossing the third room.");
    }

    private static object Geometry(float width, int stamp, float nodeX = 2)
    {
        Type node = Core("Map.EditorMapNodeVisualSnapshot");
        Array nodes = Array.CreateInstance(node, 1);
        nodes.SetValue(Activator.CreateInstance(node, new object[] { 0, nodeX, 3f }), 0);
        return Activator.CreateInstance(Front("RoomGeometryBlob"), Flags, null,
            new object[] { 0, width, 10f, stamp, null, null, null, null, null, nodes }, null);
    }

    private static void ExerciseThumbnailReuse()
    {
        object renderer = New("WorldMapRetainedRoomRenderer"), resources = New("WorldMapRoomResourceStore");
        object scene = New("WorldMapScene"), resource = New("WorldMapRoomResourceStore+RoomResource");
        Invoke(scene, "GetOrCreateRoom", 0);
        ((IDictionary)Get(resources, "rooms"))[0] = resource;
        object geometry = Geometry(20, 1); Set(resource, "Geometry", geometry);
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        texture.SetPixels(new[] { Color.white, Color.gray, Color.gray, Color.white }); texture.Apply();
        object thumbnail = Get(resource, "Thumbnail");
        void Stage(int stamp)
        {
            object source = Activator.CreateInstance(Front("WorldMapLegacyRoomSourceService+RoomTextureSource"), Flags, null,
                new object[] { texture, new Rect(0, 0, 1, 1), 2f, 2f, stamp, "" }, null);
            Invoke(thumbnail, "Stage", source); Invoke(thumbnail, "CommitPending");
        }
        var parent = new GameObject("Map optimization check"); parent.SetActive(false);
        void Sync() => Invoke(renderer, "SynchronizeVisible", scene, resources, new[] { 0 }, parent.transform);
        Stage(1); Sync();
        object room = ((IDictionary)Get(renderer, "roomObjects"))[0];
        var filter = (MeshFilter)Get(room, "BaseFilter"); Mesh original = filter.sharedMesh;
        for (int i = 0; i < 100; i++) Sync();
        Check(ReferenceEquals(original, filter.sharedMesh), "100 unchanged thumbnail frames reuse the same native mesh.");
        object next = Geometry(20, 2); Set(resource, "Geometry", next); Set(resource, "GeometryGeneration", 2L); Sync();
        Check(ReferenceEquals(original, filter.sharedMesh), "A terrain-only refresh reuses the textured room quad.");
        var changed = Front("WorldMapRoomResourceStore").GetMethod("RoutingGeometryChanged", Flags);
        Check(!(bool)changed.Invoke(null, new[] { geometry, next }), "Raster/terrain refresh does not invalidate unchanged pipe paths.");
        Check((bool)changed.Invoke(null, new[] { geometry, Geometry(20, 3, 4) }), "Moving a real pipe still invalidates its route.");
        Set(resource, "Geometry", Geometry(25, 3)); Set(resource, "GeometryGeneration", 3L); Sync();
        Check(!ReferenceEquals(original, filter.sharedMesh) && Math.Abs(filter.sharedMesh.bounds.size.x - 50f) < .01f,
            "A changed room size correctly rebuilds the thumbnail quad.");
        Mesh resized = filter.sharedMesh; Stage(2); Sync();
        Check(!ReferenceEquals(resized, filter.sharedMesh), "A new thumbnail descriptor is actually committed.");
        Invoke(renderer, "Reset"); UnityEngine.Object.Destroy(parent); UnityEngine.Object.Destroy(texture);

        Type runtime = Front("WorldMapRetainedV2Runtime");
        runtime.GetField("enabled", Flags).SetValue(null, true);
        runtime.GetField("canvasSeenAt", Flags).SetValue(null, Stopwatch.GetTimestamp() - Stopwatch.Frequency);
        Check(!(bool)runtime.GetProperty("CanvasVisible", Flags).GetValue(null), "A dormant map canvas does not request off-screen GPU rendering.");
        runtime.GetField("enabled", Flags).SetValue(null, false);
    }

    private static void ExerciseMapDirections()
    {
        Type drawing = Front("WorldMapConnectionDrawing");
        object direction = Enum.Parse(Core("World.WorldConnectionDirection"), "Bidirectional");
        var straight = new[] { new Num.Vector2(25, 45), new Num.Vector2(235, 45) };
        var stepped = new[] { new Num.Vector2(25, 45), new Num.Vector2(105, 45), new Num.Vector2(105, 52), new Num.Vector2(235, 52) };
        foreach (float zoom in new[] { .5f, 1f, 3.25f })
        {
            Num.Vector2[] path = stepped.Select(p => p * zoom).ToArray();
            newFrame(); ImGui.NewFrame();
            drawing.GetMethod("DrawDirectionMarker", Flags).Invoke(null, new object[] { ImGui.GetBackgroundDrawList(), path,
                Num.Vector2.Zero, new Num.Vector2(920, 520), null, direction, 0xFFBFE5FAu });
            ImGui.Render(); Color32[] pixels = RenderGui(920, 520, "worldmap-short-arrow-" + zoom.ToString("F2") + ".png");
            var lit = Enumerable.Range(0, pixels.Length).Where(i => pixels[i].a > 128 && pixels[i].r > 230 && pixels[i].g > 210).ToArray();
            int width = lit.Length == 0 ? 0 : lit.Max(i => i % 920) - lit.Min(i => i % 920) + 1;
            Check(width >= 10 && width <= 15, "Short stepped bidirectional link has a compact visible GPU marker at zoom " + zoom + ".");
        }

        // Actual retained route mesh, presented through the production texture bridge, with the
        // screen-space marker on top. This catches discrepancies between the two render paths.
        object routes = New("WorldMapConnectionResourceStore"), renderer = New("WorldMapRetainedConnectionRenderer");
        object surface = New("WorldMapRenderTextureSurface");
        Num.Vector2[][] paths = { straight, stepped.Select(p => p + new Num.Vector2(0, 100)).ToArray(),
            new[] { new Num.Vector2(25, 280), new Num.Vector2(105, 280), new Num.Vector2(105, 340), new Num.Vector2(235, 340) } };
        string[] ids = { "aligned", "short", "corner" };
        for (int i = 0; i < paths.Length; i++)
        {
            object route = New("ConnectionRouteResource");
            Set(route, "ConnectionId", ids[i]); Set(route, "Points", paths[i]); Set(route, "Revision", 1L); Set(route, "Direction", direction);
            ((IDictionary)Get(routes, "routes"))[ids[i]] = route;
        }
        object view = Activator.CreateInstance(Front("WorldMapViewTransform"), Flags, null,
            new object[] { Num.Vector2.Zero, new Num.Vector2(920, 520), new Num.Vector2(90, 30), 1.5f }, null);
        Check((bool)Invoke(surface, "Render", view, (Action<Transform>)(p => Invoke(renderer, "SynchronizeVisible", routes, ids, true, p))),
            "Actual retained route meshes render the new stroke and corner geometry.");
        newFrame(); ImGui.NewFrame(); var draw = ImGui.GetBackgroundDrawList();
        draw.AddRectFilled(Num.Vector2.Zero, new Num.Vector2(920, 520), 0xFF201811);
        Check((bool)Invoke(surface, "TryPresent", draw, Num.Vector2.Zero, new Num.Vector2(920, 520), view),
            "The actual retained connection surface is presented in ImGui.");
        foreach (var path in paths)
        {
            var points = path.Select(p => p * 1.5f + new Num.Vector2(90, 30)).ToArray();
            drawing.GetMethod("DrawDirectionMarker", Flags).Invoke(null, new object[] { draw, points, Num.Vector2.Zero,
                new Num.Vector2(920, 520), null, direction, 0xFFBFE5FAu });
            foreach (Num.Vector2 endpoint in new[] { points[0], points[points.Length - 1] })
                draw.AddCircleFilled(endpoint, 4, 0xFFE8C285, 12);
        }
        ImGui.Render(); RenderGui(920, 520, "worldmap-connections-gpu.png");
        object[] clipped = { new[] { new Num.Vector2(-200, 50), new Num.Vector2(800, 50) }, new Num.Vector2(0, 0), new Num.Vector2(100, 100),
            new[] { new Num.Vector4(0, 0, 25, 100) }, default(Num.Vector2), default(Num.Vector2), 0f };
        Check((bool)drawing.GetMethod("TryDirectionMarker", Flags).Invoke(null, clipped) && ((Num.Vector2)clipped[4]).X > 30,
            "A visible direction marker stays inside the canvas and outside the room silhouette.");
        Invoke(renderer, "Reset"); Invoke(surface, "Reset");
    }
}
