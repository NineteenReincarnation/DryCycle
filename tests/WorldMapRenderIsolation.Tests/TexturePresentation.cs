using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using ImGuiNET;
using UnityEngine;
using Num = System.Numerics;

public static partial class MapRenderIsolationTests
{
    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern IntPtr LoadLibrary(string path);
    [DllImport("kernel32", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr library, string name);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern bool SetDllDirectory(string path);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void GetDevice(IntPtr texture, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte BackendInit(IntPtr device, IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void BackendVoid();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void BackendDraw(IntPtr data);
    private static IntPtr d3dDevice, d3dContext, native;
    private static object deviceObject, contextObject;
    private static Assembly api;
    private static BackendVoid newFrame, shutdown;
    private static BackendDraw renderDrawData;

    private static void InitializeTextureDevice()
    {
        string apiDir = Path.GetFullPath(Path.Combine(game, "../../workshop/content/312520/3417372413/plugins"));
        api = Assembly.LoadFrom(Path.Combine(apiDir, "rain-world-imgui-api.dll"));
        var probe = new RenderTexture(4, 4, 0, RenderTextureFormat.ARGB32);
        probe.Create();
        IntPtr resource = probe.GetNativeTexturePtr();
        // ID3D11DeviceChild::GetDevice. This obtains Unity's actual device, never a mock one.
        var getDevice = (GetDevice)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(resource), 3 * IntPtr.Size), typeof(GetDevice));
        getDevice(resource, out d3dDevice);
        Check(d3dDevice != IntPtr.Zero, "The RWImGUI test adapter shares Unity's real D3D11 device.");
        deviceObject = Activator.CreateInstance(api.GetType("RWIMGUI.Windows.DirectX.ID3D11Device", true), new object[] { d3dDevice });
        var globals = api.GetType("RWIMGUI.Core.SharedGlobals", true);
        globals.GetField("cached_ID3D11Device", Flags).SetValue(globals.GetProperty("Instance", Flags).GetValue(null), deviceObject);
        object[] args = { IntPtr.Zero };
        Invoke(deviceObject, "GetImmediateContext", args); d3dContext = (IntPtr)args[0];
        contextObject = Activator.CreateInstance(api.GetType("RWIMGUI.Windows.DirectX.ID3D11DeviceContext", true), new object[] { d3dContext });
        probe.Release(); UnityEngine.Object.Destroy(probe);
        SetDllDirectory(Path.Combine(apiDir, "x86_64"));
        native = LoadLibrary(Path.Combine(apiDir, "x86_64/cimguiaio.dll"));
        Check(native != IntPtr.Zero, "The installed RWImGUI native renderer loads.");
    }

    private static T Native<T>(string name) where T : Delegate =>
        (T)Marshal.GetDelegateForFunctionPointer(GetProcAddress(native, name), typeof(T));

    private static unsafe void ExerciseTexturePresentation()
    {
        ImGuiNative.LoadFunctionPointers(&GetProcAddress, native);
        IntPtr context = ImGui.CreateContext();
        ImGuiIOPtr io = ImGui.GetIO();
        io.NativePtr->IniFilename = null;
        io.DisplaySize = new Num.Vector2(128, 128); io.DeltaTime = 1f / 60;
        io.Fonts.AddFontDefault();
        Check(Native<BackendInit>("ImGui_ImplDX11_Init")(d3dDevice, d3dContext) != 0, "Actual ImGui DX11 backend initializes.");
        InitializeFontIntegration();
        newFrame = () => RWIMGUI.Interop.ImGUIBackendInterface.ImGui_ImplDX11_NewFrame();
        shutdown = Native<BackendVoid>("ImGui_ImplDX11_Shutdown");
        renderDrawData = Native<BackendDraw>("ImGui_ImplDX11_RenderDrawData");
        try
        {
            ExerciseFontLifecycle();
            object bridge = New("WorldMapTextureBridge");
            // Top-left red, top-right green, bottom-left blue, bottom-right white. This detects
            // inverted rows as well as empty image IDs, bad channels and incorrect clipping.
            uint[] pixels = { 0xFFFF0000, 0xFF00FF00, 0xFF0000FF, 0xFFFFFFFF };
            var upload = bridge.GetType().GetMethod("Upload", Flags, null, new[] { typeof(int), typeof(int), typeof(uint[]) }, null);
            Check((bool)upload.Invoke(bridge, new object[] { 2, 2, pixels }), "ARGB raster obtains a real RWImGUI SRV.");
            object entry = Get(bridge, "raster");
            Check((ulong)Get(entry, "Handle") != 0, "The published ImGui image ID is nonzero.");
            newFrame(); ImGui.NewFrame();
            ImDrawListPtr draw = ImGui.GetBackgroundDrawList();
            var present = bridge.GetType().GetMethod("TryPresent", Flags, null,
                new[] { typeof(ImDrawListPtr), typeof(Num.Vector2), typeof(Num.Vector2), typeof(uint) }, null);
            draw.PushClipRect(new Num.Vector2(16), new Num.Vector2(80), true);
            Check((bool)present.Invoke(bridge, new object[] { draw, new Num.Vector2(16), new Num.Vector2(80), uint.MaxValue }), "The production bridge appends an ImGui image command.");
            draw.PopClipRect();
            ImGui.Render();
            // Evict before RenderDrawData: the production frame lease must keep its COM view alive.
            Invoke(bridge, "Reset");
            var target = RenderGui(128, 128, "cartography-texture-gpu.png");
            Check(target[32 * 128 + 32].r > 220 && target[32 * 128 + 32].b < 40, "The top-left raster pixel renders red, with correct Y orientation.");
            Check(target[64 * 128 + 32].b > 220 && target[64 * 128 + 32].r < 40, "The bottom-left raster pixel renders blue.");
            Check(target[8 * 128 + 8].a == 0 && target[96 * 128 + 96].a == 0, "Raster commands stay within the UI rectangle.");
            Front("WorldMapTextureFrame").GetMethod("Begin", Flags).Invoke(null, null);
            ExerciseCartographyCanvas();
        }
        finally
        {
            ShutdownFontIntegration();
            shutdown(); ImGui.DestroyContext(context);
            Marshal.Release(d3dContext); Marshal.Release(d3dDevice);
        }
    }

    private static unsafe Color32[] RenderGui(int width, int height, string name)
    {
        var target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        target.Create();
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = target; GL.Clear(true, true, Color.clear);
        IntPtr texture = target.GetNativeTexturePtr();
        object textureObject = Activator.CreateInstance(api.GetType("RWIMGUI.Windows.DirectX.ID3D11Texture2D", true), new object[] { texture });
        object[] description = { null }; Invoke(textureObject, "GetDesc", description);
        uint format = Convert.ToUInt32(Get(description[0], "Format"));
        // Unity allocates typeless resources. A render-target view must choose the compatible
        // typed UNORM format (as the installed RWImGUI SRV adapter also does).
        uint* viewDescription = stackalloc uint[5];
        for (int i = 0; i < 5; i++) viewDescription[i] = 0;
        viewDescription[0] = format == 27 ? 28u : format == 90 ? 87u : format;
        viewDescription[1] = 4; // D3D11_RTV_DIMENSION_TEXTURE2D
        object[] create = { texture, (IntPtr)viewDescription, IntPtr.Zero };
        int hr = (int)Invoke(deviceObject, "CreateRenderTargetView", create);
        Check(hr == 0, "The UI test target has a real D3D11 render-target view (HRESULT 0x" + hr.ToString("X8") + ", format " + format + ").");
        IntPtr view = (IntPtr)create[2];
        try
        {
            Invoke(contextObject, "OmSetRenderTargets", 1u, view, IntPtr.Zero);
            renderDrawData((IntPtr)ImGui.GetDrawData().NativePtr);
            var read = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            read.ReadPixels(new Rect(0, 0, width, height), 0, 0); read.Apply();
            // The native backend writes D3D screen rows directly, without Unity's camera-target
            // inversion. ReadPixels therefore returns these rows in native top-down order. Only
            // invert the PNG's Unity bottom-up pixel buffer, never the production image or UVs.
            Color32[] topDown = read.GetPixels32(), pngRows = new Color32[topDown.Length];
            for (int y = 0; y < height; y++) Array.Copy(topDown, (height - y - 1) * width, pngRows, y * width, width);
            read.SetPixels32(pngRows); read.Apply();
            File.WriteAllBytes(Path.Combine(output, name), read.EncodeToPNG());
            UnityEngine.Object.Destroy(read);
            return topDown;
        }
        finally
        {
            Invoke(contextObject, "OmSetRenderTargets", 0u, IntPtr.Zero, IntPtr.Zero);
            Marshal.Release(view);
            RenderTexture.active = previous; target.Release(); UnityEngine.Object.Destroy(target);
        }
    }

    private static Type Core(string name) => AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "DryCycle")
        .GetType("DryCycle.DevUI.DevTool." + name, true);

    private static unsafe void ExerciseCartographyCanvas()
    {
        string assets = Path.Combine(game, "RainWorld_Data/StreamingAssets"), mod = Path.Combine(assets, "mods/Ancient Site");
        string[] roots = { Path.Combine(assets, "mergedmods"), Path.Combine(mod, "newest"), mod, Path.Combine(assets, "mods/moreslugcats"),
            Path.Combine(assets, "mods/watcher"), Path.Combine(assets, "consolefiles/moreslugcatswatcher"), assets };
        Func<string, string> read = relative =>
        {
            string path = roots.Select(r => Path.Combine(r, relative)).FirstOrDefault(File.Exists);
            return path == null ? null : File.ReadAllText(path);
        };
        // Real B5 files, using explicit fixture precedence. Production still uses game AssetManager.
        object source = Core("Map.Cartography.CartographyRegionLoader").GetMethod("Load", Flags).Invoke(null, new object[] { "B5", "White", read });
        object document = Invoke(source, "CreateDocument", "gpu-test|B5", "B5");
        object Scene(object doc) => Core("Map.Cartography.CartographySceneBuilder").GetMethod("Build", Flags).Invoke(null, new object[] { doc, source, null });
        object snapshot = Activator.CreateInstance(Core("Map.Cartography.CartographyPresentation"), true);
        Set(snapshot, "Document", document); Set(snapshot, "Scene", Scene(document)); Set(snapshot, "Source", source);
        Set(snapshot, "Identity", "gpu-test|B5"); Set(snapshot, "Revision", 1L);
        Set(snapshot, "ProjectPath", Path.Combine(output, "fixture.xml"));
        Core("Map.Cartography.CartographyRuntime").GetField("presentation", Flags).SetValue(null, snapshot);
        object picker = Activator.CreateInstance(Core("Map.Cartography.CartographySourcePicker"), true);
        Set(picker, "Region", "B5"); Set(picker, "Campaign", "White");
        Type optionType = Core("Map.Cartography.CartographyRegionOption");
        Array regions = Array.CreateInstance(optionType, 1);
        regions.SetValue(Activator.CreateInstance(optionType, Flags, null, new object[] { "B5", "Ancient Site", "古代遗址" }, null), 0);
        Set(picker, "Regions", regions);
        Core("Map.Cartography.CartographyRuntime").GetField("picker", Flags).SetValue(null, picker);
        object editor = Activator.CreateInstance(Core("Core.EditorPresentationSnapshot"));
        foreach (string name in new[] { "BrowserOpen", "InspectorOpen", "Available" }) editor.GetType().GetProperty(name).SetValue(editor, true);
        ImGuiIOPtr io = ImGui.GetIO(); io.DisplaySize = new Num.Vector2(1920, 1080);
        Front("DevToolUiTheme").GetMethod("Apply", Flags).Invoke(null, null);
        for (int frame = 0; frame < 36; frame++)
        {
            Front("WorldMapTextureFrame").GetMethod("Begin", Flags).Invoke(null, null);
            newFrame(); ImGui.NewFrame();
            ImGui.SetNextWindowPos(Num.Vector2.Zero); ImGui.SetNextWindowSize(io.DisplaySize);
            ImGui.SetNextWindowBgAlpha(1);
            ImGui.Begin("Cartography GPU validation", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings);
            Front("CartographyView").GetMethod("Draw", Flags).Invoke(null, new[] { editor });
            ImGui.End(); ImGui.Render();
            if (frame == 35) RenderGui(1920, 1080, "B5-cartography-ui.png");
            Front("CartographyCanvasImages").GetMethod("UpdateMainThread", Flags).Invoke(null, null);
        }
        var images = (IDictionary)Front("CartographyCanvasImages").GetField("Entries", Flags).GetValue(null);
        Check(images.Count > 100, "The real B5 canvas requests terrain and labels without selecting rooms.");
        Check(images.Values.Cast<object>().All(e => (bool)Get(e, "Uploaded")), "Every visible B5 raster reaches a real GPU texture handle.");
        Check((string)Front("CartographyCanvasImages").GetProperty("Error", Flags).GetValue(null) == "", "The displayed B5 canvas has no texture adapter error.");
        ExerciseRouteInput(snapshot, source, Scene);
    }

    private static void ExerciseRouteInput(object snapshot, object source, Func<object, object> scene)
    {
        Type view = Front("CartographyView");
        object[] nodes = ((IEnumerable)Get(Get(snapshot, "Scene"), "Nodes")).Cast<object>().ToArray();
        object routeNode = nodes.First(n => Get(n, "FromId") != null && !(bool)Get(n, "Locked"));
        string routeId = (string)Get(routeNode, "Id");
        Array points = (Array)Get(routeNode, "Points");
        object a = points.GetValue(0), b = points.GetValue(1);
        Num.Vector2 mouse = new(((float)Get(a, "X") + (float)Get(b, "X")) / 2, ((float)Get(a, "Y") + (float)Get(b, "Y")) / 2);
        view.GetField("tool", Flags).SetValue(null, Enum.Parse(view.GetNestedType("Tool", Flags), "Select"));
        view.GetField("zoom", Flags).SetValue(null, 1f); view.GetField("snap", Flags).SetValue(null, false);
        view.GetField("observed", Flags).SetValue(null, snapshot);
        var commands = (IEnumerable)Core("Map.Cartography.CartographyRuntime").GetField("Commands", Flags).GetValue(null);
        ImGuiIOPtr io = ImGui.GetIO();
        void Input(bool down, Num.Vector2 position)
        {
            io.AddMousePosEvent(position.X, position.Y); io.AddMouseButtonEvent(0, down);
            newFrame(); ImGui.NewFrame();
            view.GetMethod("RouteGesture", Flags).Invoke(null, new object[] { snapshot, true, position, io });
            ImGui.Render();
        }
        Input(true, mouse);
        Check(view.GetField("routeGesture", Flags).GetValue(null) != null, "Real ImGui left-click begins route editing without prior selection or a double-click.");
        Input(true, mouse + new Num.Vector2(25, 40));
        Input(false, mouse + new Num.Vector2(25, 40));
        object command = commands.Cast<object>().Last();
        object edit = Get(command, "Item");
        Check(((IList)Get(edit, "Points")).Count > 0 && (long)Get(command, "Revision") == 1L, "Releasing the mouse produces one versioned command with a dragged bend.");
        object original = Get(snapshot, "Document");
        object changed = Core("Map.Cartography.CartographyEditing").GetMethod("Apply", Flags).Invoke(null, new[] { original, source, command });
        Set(snapshot, "Document", changed); Set(snapshot, "Scene", scene(changed)); Set(snapshot, "Revision", 2L);
        io.AddKeyEvent(ImGuiKey.Delete, true); newFrame(); ImGui.NewFrame();
        view.GetMethod("RouteGesture", Flags).Invoke(null, new object[] { snapshot, true, mouse, io }); ImGui.Render();
        edit = Get(commands.Cast<object>().Last(), "Item");
        Check(((IList)Get(edit, "Points")).Count == 0 && (bool)Get(edit, "Visible"), "Real Delete input removes the selected bend and retains the connection.");
        io.AddKeyEvent(ImGuiKey.Delete, false);
    }
}
