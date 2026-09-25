using System;
using System.Reflection;
using ImGuiNET;
using RWIMGUI.API;
using RWIMGUI.Interop;

public static partial class MapRenderIsolationTests
{
    private static IntPtr backendNewFrame;

    private static unsafe void InitializeFontIntegration()
    {
        // Exercise the actual compatibility hook and Cdecl callback, not a direct font-register call.
        // The isolated editor already loaded RWImGUI's native library onto Unity's D3D11 device.
        typeof(ImGUIBackendInterface).GetField("_libcimguiHandle", Flags).SetValue(null, native);
        backendNewFrame = GetProcAddress(native, "ImGui_ImplDX11_NewFrame");
        ImGUIBackendInterface.ImGui_ImplDX11_NewFrame = (delegate* unmanaged[Cdecl]<void>)backendNewFrame;
        object logger = Activator.CreateInstance(Type.GetType("BepInEx.Logging.ManualLogSource, BepInEx", true), new object[] { "Font regression" });
        api.GetType("RWIMGUI.Core.Globals", true).GetField("mls", Flags).SetValue(null, logger);
        Front("DevToolFontAtlasIntegration").GetMethod("Enable", Flags).Invoke(null, new[] { logger });
        Check(Front("DevToolFontAtlasIntegration").GetField("hook", Flags).GetValue(null) != null,
            "The installed MonoMod can attach the font initialization boundary without importing Present's function-pointer locals.");
        var getHandle = typeof(ImGUIBackendInterface).GetMethod("GetCImGUIAIOLibHandle");
        Check((IntPtr)getHandle.Invoke(null, null) == native, "Font integration preserves the RWImGUI native library handle.");
        Check((IntPtr)ImGUIBackendInterface.ImGui_ImplDX11_NewFrame != backendNewFrame,
            "Resolving native exports arms the pre-upload callback before the first backend frame.");
    }

    private static unsafe void ShutdownFontIntegration()
    {
        Front("DevToolFontAtlasIntegration").GetMethod("Disable", Flags).Invoke(null, null);
        Check((IntPtr)ImGUIBackendInterface.ImGui_ImplDX11_NewFrame == backendNewFrame,
            "Font integration restores the original native backend callback on shutdown.");
    }

    private static unsafe void ExerciseFontLifecycle()
    {
        Type catalog = Front("DevToolFontCatalog"), frontendType = Front("DevToolFrontend"), settings = Front("DevToolUiSettings");
        object Read(string name) => catalog.GetProperty(name, Flags).GetValue(null);
        void Visible(bool value) => frontendType.GetMethod("SetVisibleFromMainThread", Flags).Invoke(null, new object[] { value });
        void Present() => frontendType.GetMethod("FrameCallback", Flags).Invoke(null, new object[] { IntPtr.Zero, 0u, 0u });
        frontendType.GetMethod("ResetNativeReadinessFromMainThread", Flags).Invoke(null, null);
        Visible(true);
        Check(!ImGUIAPI.HasContext && !(bool)frontendType.GetProperty("NativeBackendReady", Flags).GetValue(null),
            "Unity visibility publication does not touch native input/context state before a healthy Present.");
        Check(!(bool)Read("RegistrationAttempted") && ImGui.GetIO().Fonts.TexID == 0,
            "Fonts are not registered or uploaded during plugin startup.");

        newFrame(); ImGui.NewFrame();
        Present();
        Check(ImGUIAPI.HasContext && (bool)frontendType.GetProperty("NativeBackendReady", Flags).GetValue(null),
            "A healthy Present acquires DevTool input and marks the new UI ready.");
        Check((bool)Read("RegistrationSucceeded") && (int)Read("RegisteredLocalFaceCount") == 1,
            "The production callback registers exactly one HarmonyOS font before native DX11 upload.");
        ulong texture = ImGui.GetIO().Fonts.TexID;
        int fontCount = ImGui.GetIO().Fonts.Fonts.Size;
        Check(texture != 0, "The real DX11 backend uploads the combined atlas successfully.");
        settings.GetMethod("SetLanguage", Flags).Invoke(null, new[] { Enum.Parse(Front("DevToolUiLanguage"), "Chinese") });
        Check((bool)frontendType.GetMethod("TryPushActiveFont", Flags).Invoke(null, null),
            "The new UI selects the registered fixed Chinese font.");
        ImFontPtr chinese = ImGui.GetFont();
        foreach (char character in "浏览器房间设置地图缩略图连接编辑区域")
            Check(chinese.FindGlyphNoFallback(character).NativePtr != null,
                "The uploaded HarmonyOS atlas contains UI glyph " + character + " (U+" + ((int)character).ToString("X4") + ").");
        ImGui.Begin("Font lifecycle regression"); ImGui.TextUnformatted("浏览器 / 房间设置 / 中文地图"); ImGui.End();
        ImGui.PopFont(); ImGui.Render();

        for (int cycle = 0; cycle < 5; cycle++)
        {
            newFrame(); ImGui.NewFrame();
            Visible(false); Present();
            Check(!ImGUIAPI.HasContext, "Closing DevTool releases its input context on Present (cycle " + cycle + ").");
            Visible(true);
            Check(!ImGUIAPI.HasContext, "Reopening from Unity waits for Present (cycle " + cycle + ").");
            Present();
            bool pushed = (bool)frontendType.GetMethod("TryPushActiveFont", Flags).Invoke(null, null);
            Check(pushed && ImGui.GetFont().NativePtr == chinese.NativePtr,
                "Reopening keeps the same valid Chinese font pointer (cycle " + cycle + ").");
            if (pushed) ImGui.PopFont();
            Check((bool)Read("RegistrationSucceeded") && ImGui.GetIO().Fonts.Fonts.Size == fontCount && ImGui.GetIO().Fonts.TexID == texture,
                "Context switches neither duplicate fonts nor rebuild the GPU atlas (cycle " + cycle + ").");
            ImGui.Render();
        }

        newFrame(); ImGui.NewFrame();
        Visible(false);
        frontendType.GetMethod("RenderFromContext", Flags).Invoke(null, new object[] { IntPtr.Zero, 0u, 0u });
        Check(!ImGUIAPI.HasContext, "A hidden context releases itself even after its always callback is removed.");
        var other = new IMGUIContext(); ImGUIAPI.SwitchContext(other); Present();
        Check(ReferenceEquals(ImGUIAPI.CurrentContext, other), "DevTool shutdown does not clear another plugin's input context.");
        ImGUIAPI.SwitchContext(null);
        settings.GetMethod("SetLanguage", Flags).Invoke(null, new[] { Enum.Parse(Front("DevToolUiLanguage"), "English") });
        ImGui.Render();
    }
}
