using System;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using ImGuiNET;
using RWIMGUI;
using Num = System.Numerics;

namespace DryCycle.AIObservatory.RWImGui;

[BepInPlugin(PluginId, PluginName, PluginVersion)]
public sealed class BridgePlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.AIObservatory.RWImGui";
    public const string PluginName = "DryCycle AI Observatory RWImGUI Bridge";
    public const string PluginVersion = "0.1.0";

    private static ManualLogSource log;
    private static bool callbackRegistered;

    private void OnEnable()
    {
        log = Logger;
        ProbeMenu.SetLogger(Logger);
        On.RainWorld.OnModsInit += RainWorld_OnModsInit;
        Logger.LogInfo("DryCycle RWImGUI bridge loaded. Waiting for RainWorld.OnModsInit before registering the Present callback.");
    }

    private void OnDisable()
    {
        On.RainWorld.OnModsInit -= RainWorld_OnModsInit;
        // The currently verified public API sample documents AddMenuCallback but not a
        // corresponding removal call. Keep the registered function pointer valid for the
        // process lifetime and make it a no-op when this plugin is disabled.
        ProbeMenu.Enabled = false;
    }

    private static void RainWorld_OnModsInit(On.RainWorld.orig_OnModsInit orig, RainWorld self)
    {
        orig(self);
        TryRegisterCallback();
    }

    private static unsafe void TryRegisterCallback()
    {
        if (callbackRegistered)
        {
            ProbeMenu.Enabled = true;
            return;
        }

        Assembly apiAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, "rain-world-imgui-api", StringComparison.OrdinalIgnoreCase));
        Assembly imguiAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, "ImGui.NET", StringComparison.OrdinalIgnoreCase));

        if (apiAssembly == null || imguiAssembly == null)
        {
            log?.LogWarning("DryCycle RWImGUI bridge did not register: rain-world-imgui-api.dll and/or RWImGUI's ImGui.NET.dll is not loaded. DryCycle itself remains unaffected.");
            return;
        }

        try
        {
            log?.LogInfo($"DryCycle RWImGUI API inventory (runtime): api={apiAssembly.GetName().Name} {apiAssembly.GetName().Version}, imgui={imguiAssembly.GetName().Name} {imguiAssembly.GetName().Version}.");

            // Verified integration shape from the RWImGUI public usage example:
            //   ImGUIAPI.AddMenuCallback(&MenuCallback)
            //   void MenuCallback(ref nint swapChain, ref uint syncInterval, ref uint flags)
            // RWImGUI owns NewFrame/Render and the Win32 + DX11 backend. This callback only
            // emits ImGui widgets; it never touches Unity/Rain World objects.
            ImGUIAPI.AddMenuCallback(&ProbeMenu.MenuCallback);
            callbackRegistered = true;
            ProbeMenu.Enabled = true;
            log?.LogInfo("DryCycle RWImGUI Present callback registered. Minimal proof window is enabled.");
        }
        catch (Exception error)
        {
            ProbeMenu.Enabled = false;
            log?.LogError("DryCycle RWImGUI callback registration failed. The legacy DryCycle systems remain active. " + error);
        }
    }
}

[SuppressUnmanagedCodeSecurity]
internal static unsafe class ProbeMenu
{
    private static ManualLogSource log;
    private static int firstDrawLogged;

    internal static bool Enabled { get; set; }

    internal static void SetLogger(ManualLogSource value) => log = value;

    public static void MenuCallback(ref nint idxgiSwapChain, ref uint syncInterval, ref uint flags)
    {
        if (!Enabled) return;

        try
        {
            if (Interlocked.Exchange(ref firstDrawLogged, 1) == 0)
                log?.LogInfo($"DryCycle RWImGUI callback reached Present. swapChain=0x{idxgiSwapChain:X}, syncInterval={syncInterval}, flags={flags}.");

            ImGui.SetNextWindowPos(new Num.Vector2(24f, 24f), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSize(new Num.Vector2(420f, 150f), ImGuiCond.FirstUseEver);
            bool open = Enabled;
            if (ImGui.Begin("DryCycle AI Observatory - RWImGUI Probe###DryCycleRWImGuiProbe", ref open,
                    ImGuiWindowFlags.NoCollapse))
            {
                ImGui.TextColored(new Num.Vector4(0.35f, 0.9f, 0.48f, 1f), "RWImGUI backend connected");
                ImGui.Separator();
                ImGui.Text("Present callback: OK");
                ImGui.Text("Renderer owner: RWImGUI Win32 + DX11");
                ImGui.Text("DryCycle Unity/Futile renderer: not used by this probe");
            }
            ImGui.End();
            Enabled = open;
        }
        catch (Exception error)
        {
            Enabled = false;
            if (Interlocked.Exchange(ref firstDrawLogged, 2) != 2)
                log?.LogError("DryCycle RWImGUI probe draw failed and was disabled for this session. " + error);
        }
    }
}
