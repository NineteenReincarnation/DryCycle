using System;
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
        newFrame = Native<BackendVoid>("ImGui_ImplDX11_NewFrame");
        shutdown = Native<BackendVoid>("ImGui_ImplDX11_Shutdown");
        renderDrawData = Native<BackendDraw>("ImGui_ImplDX11_RenderDrawData");
        try
        {
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
        }
        finally
        {
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
        object[] create = { target.GetNativeTexturePtr(), IntPtr.Zero, IntPtr.Zero };
        int hr = (int)Invoke(deviceObject, "CreateRenderTargetView", create);
        Check(hr == 0, "The UI test target has a real D3D11 render-target view.");
        IntPtr view = (IntPtr)create[2];
        try
        {
            Invoke(contextObject, "OmSetRenderTargets", 1u, view, IntPtr.Zero);
            renderDrawData((IntPtr)ImGui.GetDrawData().NativePtr);
            var read = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            read.ReadPixels(new Rect(0, 0, width, height), 0, 0); read.Apply();
            File.WriteAllBytes(Path.Combine(output, name), read.EncodeToPNG());
            Color32[] bottomUp = read.GetPixels32(), topDown = new Color32[bottomUp.Length];
            for (int y = 0; y < height; y++) Array.Copy(bottomUp, (height - y - 1) * width, topDown, y * width, width);
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
}
