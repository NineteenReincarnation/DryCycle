using System;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using MonoMod.RuntimeDetour;
using RWIMGUI.Interop;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// RWImGUI 1.12 has one native ImGui context; IMGUIContext only selects an input/render callback.
/// Its API has no pre-atlas-build event. Extend that external boundary immediately before the
/// DX11 backend builds/uploads the atlas, on the same Present thread that owns it.
/// </summary>
internal static unsafe class DevToolFontAtlasIntegration
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void BackendNewFrame();

    // Keep the reverse-P/Invoke thunk rooted for as long as the native renderer can call it.
    private static readonly BackendNewFrame BeforeFrame = BeforeBackendFrame;
    private static readonly IntPtr BeforeFramePointer = Marshal.GetFunctionPointerForDelegate(BeforeFrame);
    private static Hook hook;
    private static delegate* unmanaged[Cdecl]<void> originalNewFrame;
    private static ManualLogSource log;
    private static volatile bool enabled;
    private static bool failureLogged;

    internal static void Enable(ManualLogSource logger)
    {
        if (hook != null) return;
        log = logger;
        enabled = true;
        failureLogged = false;
        try
        {
            // RWImGUI asks for this handle after loading its backend exports, before installing
            // Present. Hook only this ordinary IntPtr-returning method: Rain World's MonoMod cannot
            // copy Present's C# function-pointer locals (its reflection importer stack-overflows).
            MethodInfo getHandle = typeof(ImGUIBackendInterface).GetMethod("GetCImGUIAIOLibHandle", BindingFlags.Public | BindingFlags.Static);
            if (getHandle == null) throw new MissingMethodException(typeof(ImGUIBackendInterface).FullName, "GetCImGUIAIOLibHandle");
            hook = new Hook(getHandle, (Func<Func<IntPtr>, IntPtr>)GetBackendHandle);
            log?.LogInfo("DevTool font integration installed; waiting for RWImGUI's backend exports.");
        }
        catch (Exception error)
        {
            // Typography is optional; failing to extend a newer backend must not disable DevTool.
            log?.LogError("DevTool font integration could not be installed; the existing backend font remains available: " + error);
        }
    }

    internal static void Disable()
    {
        enabled = false;
        if ((IntPtr)ImGUIBackendInterface.ImGui_ImplDX11_NewFrame == BeforeFramePointer)
            ImGUIBackendInterface.ImGui_ImplDX11_NewFrame = originalNewFrame;
        hook?.Dispose();
        hook = null;
    }

    private static IntPtr GetBackendHandle(Func<IntPtr> original)
    {
        IntPtr handle = original();
        if (enabled && handle != IntPtr.Zero) AttachBeforeBackendFrame();
        return handle;
    }

    private static void AttachBeforeBackendFrame()
    {
        var current = ImGUIBackendInterface.ImGui_ImplDX11_NewFrame;
        if (current == null || (IntPtr)current == BeforeFramePointer) return;
        originalNewFrame = current;
        ImGUIBackendInterface.ImGui_ImplDX11_NewFrame = (delegate* unmanaged[Cdecl]<void>)BeforeFramePointer;
        log?.LogInfo("DevTool font registration attached before RWImGUI's atlas upload on the Present thread.");
    }

    private static void BeforeBackendFrame()
    {
        try
        {
            if (enabled) DevToolFontCatalog.RegisterBeforeBackendFrame(log);
        }
        catch (Exception error)
        {
            // Never propagate a managed exception through RWImGUI's native callback boundary.
            if (!failureLogged) log?.LogError("DevTool font registration failed before atlas upload: " + error);
            failureLogged = true;
        }
        originalNewFrame();
    }
}
