using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using DryCycle.Debugging.AI;
using ImGuiNET;
using RWIMGUI;
using Num = System.Numerics;

namespace DryCycle.AIObservatory.RWImGui;

[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency("Anno", BepInDependency.DependencyFlags.HardDependency)]
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
        AIDebugPresentationBridgeStatus.MarkBridgeLoaded(PluginVersion);
        On.RainWorld.OnModsInit += RainWorld_OnModsInit;
        Logger.LogInfo("DryCycle RWImGUI bridge loaded. Waiting for RainWorld.OnModsInit before registering the Present callback.");
    }

    private void Update()
    {
        // Unity/Rain World state is sampled only on the Unity main thread. The Present
        // callback consumes this copied bool and never calls UnityEngine.Input or touches
        // RainWorld objects.
        ProbeMenu.Visible = AIDebuggerRuntime.Visible;
    }

    private void OnDisable()
    {
        On.RainWorld.OnModsInit -= RainWorld_OnModsInit;
        // The currently verified public API sample documents AddMenuCallback but not a
        // corresponding removal call. Keep the registered function pointer valid for the
        // process lifetime and make it a no-op when this plugin is disabled.
        ProbeMenu.Enabled = false;
        ProbeMenu.Visible = false;
        AIDebugPresentationBridgeStatus.MarkFailure("RWImGUI bridge disabled");
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
            string reason = "rain-world-imgui-api.dll and/or RWImGUI's ImGui.NET.dll is not loaded";
            AIDebugPresentationBridgeStatus.MarkFailure(reason);
            log?.LogWarning("DryCycle RWImGUI bridge did not register: " + reason + ". DryCycle itself remains unaffected.");
            return;
        }

        try
        {
            Version apiVersion = apiAssembly.GetName().Version;
            Version imguiVersion = imguiAssembly.GetName().Version;
            log?.LogInfo($"DryCycle RWImGUI API inventory (runtime): api={apiAssembly.GetName().Name} {apiVersion}, imgui={imguiAssembly.GetName().Name} {imguiVersion}.");
            LogInstalledApiInventory(apiAssembly);

            // Verified integration shape from the RWImGUI public usage example:
            //   ImGUIAPI.AddMenuCallback(&MenuCallback)
            //   void MenuCallback(ref nint swapChain, ref uint syncInterval, ref uint flags)
            // RWImGUI owns NewFrame/Render and the Win32 + DX11 backend. This callback only
            // emits ImGui widgets; it never touches Unity/Rain World objects.
            ImGUIAPI.AddMenuCallback(&ProbeMenu.MenuCallback);
            callbackRegistered = true;
            ProbeMenu.Enabled = true;
            AIDebugPresentationBridgeStatus.MarkCallbackRegistered(apiVersion?.ToString(), imguiVersion?.ToString());
            log?.LogInfo("DryCycle RWImGUI Present callback registered. Press F7 to show the minimal proof window.");
        }
        catch (Exception error)
        {
            ProbeMenu.Enabled = false;
            AIDebugPresentationBridgeStatus.MarkFailure(error.GetType().Name + ": " + error.Message);
            log?.LogError("DryCycle RWImGUI callback registration failed. DryCycle gameplay systems remain active. " + error);
        }
    }

    private static void LogInstalledApiInventory(Assembly apiAssembly)
    {
        try
        {
            Type[] types;
            try
            {
                types = apiAssembly.GetTypes();
            }
            catch (ReflectionTypeLoadException partial)
            {
                types = partial.Types.Where(t => t != null).ToArray();
                string loaderErrors = string.Join(" | ", partial.LoaderExceptions
                    .Where(e => e != null)
                    .Select(e => e.GetType().Name + ": " + e.Message));
                log?.LogWarning("DryCycle RWImGUI inventory loaded only part of the API assembly: " + loaderErrors);
            }

            List<string> pluginMetadata = new();
            foreach (Type type in types)
            {
                foreach (CustomAttributeData attribute in CustomAttributeData.GetCustomAttributes(type))
                {
                    if (!string.Equals(attribute.AttributeType.FullName, "BepInEx.BepInPlugin", StringComparison.Ordinal))
                        continue;
                    string args = string.Join(", ", attribute.ConstructorArguments.Select(a => a.Value?.ToString() ?? "null"));
                    pluginMetadata.Add(type.FullName + " => " + args);
                }
            }
            if (pluginMetadata.Count > 0)
                log?.LogInfo("DryCycle RWImGUI installed BepInPlugin metadata: " + string.Join(" || ", pluginMetadata));

            Type[] apiCandidates = types
                .Where(t => string.Equals(t.Name, "ImGUIAPI", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            foreach (Type type in apiCandidates)
            {
                string methods = string.Join(", ", type
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .Where(m => m.Name.IndexOf("Callback", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("Context", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("Font", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("Texture", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                m.Name.IndexOf("Dock", StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(m => m.Name + "(" + m.GetParameters().Length + ")")
                    .Distinct());
                log?.LogInfo($"DryCycle RWImGUI API type discovered: {type.FullName}; relevantMethods=[{methods}].");
            }
        }
        catch (Exception error)
        {
            // Inventory failure is diagnostic only and must never stop callback registration.
            log?.LogWarning("DryCycle RWImGUI runtime API inventory failed: " + error.Message);
        }
    }
}

[SuppressUnmanagedCodeSecurity]
internal static unsafe class ProbeMenu
{
    private static ManualLogSource log;
    private static int firstPresentLogged;
    private static int firstVisibleDrawLogged;
    private static int drawFailureLogged;
    private static volatile bool visible;

    internal static bool Enabled { get; set; }
    internal static bool Visible
    {
        get => visible;
        set => visible = value;
    }

    internal static void SetLogger(ManualLogSource value) => log = value;

    public static void MenuCallback(ref nint idxgiSwapChain, ref uint syncInterval, ref uint flags)
    {
        if (!Enabled) return;

        try
        {
            AIDebugPresentationBridgeStatus.MarkPresentSeen();
            if (Interlocked.Exchange(ref firstPresentLogged, 1) == 0)
                log?.LogInfo($"DryCycle RWImGUI callback reached Present. swapChain=0x{idxgiSwapChain:X}, syncInterval={syncInterval}, flags={flags}.");

            if (!Visible) return;

            if (Interlocked.Exchange(ref firstVisibleDrawLogged, 1) == 0)
                log?.LogInfo("DryCycle RWImGUI F7-visible frame reached Present; drawing minimal proof window.");

            ImGui.SetNextWindowPos(new Num.Vector2(24f, 24f), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSize(new Num.Vector2(420f, 150f), ImGuiCond.FirstUseEver);
            if (ImGui.Begin("DryCycle AI Observatory - RWImGUI Probe###DryCycleRWImGuiProbe",
                    ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings))
            {
                ImGui.TextColored(new Num.Vector4(0.35f, 0.9f, 0.48f, 1f), "RWImGUI backend connected");
                ImGui.Separator();
                ImGui.Text("F7 visibility state: ON");
                ImGui.Text("Present callback: OK");
                ImGui.Text("Renderer owner: RWImGUI Win32 + DX11");
                ImGui.Text("Legacy DryCycle Unity/Futile renderer: DISABLED");
            }
            ImGui.End();
        }
        catch (Exception error)
        {
            AIDebugPresentationBridgeStatus.MarkFailure(error.GetType().Name + ": " + error.Message);
            if (Interlocked.Exchange(ref drawFailureLogged, 1) == 0)
                log?.LogError("DryCycle RWImGUI probe draw failed. The callback will remain registered for diagnostics. " + error);
        }
    }
}
