using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using DryCycle.Debugging.AI;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.AIObservatory.RWImGui;

[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency("Anno", BepInDependency.DependencyFlags.HardDependency)]
public sealed class BridgePlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.AIObservatory.RWImGui";
    public const string PluginName = "DryCycle AI Observatory RWImGUI Bridge";
    public const string PluginVersion = "0.1.2";

    private static ManualLogSource log;
    private static bool callbackRegistered;
    private static Delegate managedCallbackLifetime;

    private void OnEnable()
    {
        log = Logger;
        ProbeMenu.SetLogger(Logger);
        AIDebugPresentationBridgeStatus.MarkBridgeLoaded(PluginVersion);
        On.RainWorld.OnModsInit += RainWorld_OnModsInit;
        Logger.LogInfo("DryCycle RWImGUI bridge loaded. Waiting for RainWorld.OnModsInit before registering the frame callback.");
    }

    private void Update()
    {
        // Unity/Rain World state is sampled only on the Unity main thread. The RWImGUI
        // callback consumes this copied bool and never reads Unity input or Rain World
        // live objects from the Present hook.
        ProbeMenu.Visible = AIDebuggerRuntime.Visible;
    }

    private void OnDisable()
    {
        On.RainWorld.OnModsInit -= RainWorld_OnModsInit;
        // Keep the function pointer valid for process lifetime. The API does expose
        // removal methods in current releases, but disabling the callback is safer than
        // unregistering while the graphics hook may be active.
        ProbeMenu.Enabled = false;
        ProbeMenu.Visible = false;
        AIDebugPresentationBridgeStatus.MarkFailure("RWImGUI bridge disabled");
    }

    private static void RainWorld_OnModsInit(On.RainWorld.orig_OnModsInit orig, RainWorld self)
    {
        orig(self);
        TryRegisterCallback();
    }

    private static void TryRegisterCallback()
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

            // A menu callback is intentionally NOT used here. AddMenuCallback is only
            // dispatched while RWImGUI's own menu is open. AI Observatory must be able to
            // open directly with F7, so prefer AddAlwaysCallback, which is dispatched on
            // every RWImGUI frame regardless of menu visibility.
            MethodInfo addFrameCallback = FindFrameCallback(apiAssembly);
            RegisterCallback(addFrameCallback);

            callbackRegistered = true;
            ProbeMenu.Enabled = true;
            AIDebugPresentationBridgeStatus.MarkCallbackRegistered(apiVersion?.ToString(), imguiVersion?.ToString());

            ParameterInfo callbackParameter = addFrameCallback.GetParameters()[0];
            log?.LogInfo(
                "DryCycle RWImGUI frame callback registered through " +
                addFrameCallback.DeclaringType?.FullName + "." + addFrameCallback.Name +
                "(" + DescribeType(callbackParameter.ParameterType) + "). Press F7 to show the minimal proof window.");

            if (string.Equals(addFrameCallback.Name, "AddMenuCallback", StringComparison.OrdinalIgnoreCase))
            {
                log?.LogWarning(
                    "DryCycle RWImGUI: this API revision has no AddAlwaysCallback; fell back to AddMenuCallback. " +
                    "On that old API revision the RWImGUI menu may need to be open for the Observatory callback to run.");
            }
        }
        catch (Exception error)
        {
            ProbeMenu.Enabled = false;
            Exception report = error is TargetInvocationException invocation && invocation.InnerException != null
                ? invocation.InnerException
                : error;
            AIDebugPresentationBridgeStatus.MarkFailure(report.GetType().Name + ": " + report.Message);
            log?.LogError("DryCycle RWImGUI callback registration failed. DryCycle gameplay systems remain active. " + report);
        }
    }

    private static MethodInfo FindFrameCallback(Assembly apiAssembly)
    {
        MethodInfo[] methods = GetLoadableTypes(apiAssembly)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            .Where(method => !method.ContainsGenericParameters && method.GetParameters().Length == 1)
            .ToArray();

        MethodInfo always = methods
            .Where(method => string.Equals(method.Name, "AddAlwaysCallback", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(method => string.Equals(method.DeclaringType?.Name, "ImGUIAPI", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(method => method.IsPublic)
            .FirstOrDefault();

        if (always != null)
            return always;

        MethodInfo menuFallback = methods
            .Where(method => string.Equals(method.Name, "AddMenuCallback", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(method => string.Equals(method.DeclaringType?.Name, "ImGUIAPI", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(method => method.IsPublic)
            .FirstOrDefault();

        if (menuFallback != null)
            return menuFallback;

        throw new MissingMethodException(
            "The loaded rain-world-imgui-api assembly exposes neither AddAlwaysCallback nor AddMenuCallback with one parameter. " +
            "RWImGUI may have changed its callback API; check the runtime API inventory log.");
    }

    private static void RegisterCallback(MethodInfo addCallback)
    {
        ParameterInfo parameter = addCallback.GetParameters()[0];
        MethodInfo callbackMethod = typeof(ProbeMenu).GetMethod(
            nameof(ProbeMenu.FrameCallback),
            BindingFlags.Public | BindingFlags.Static);

        if (callbackMethod == null)
            throw new MissingMethodException(typeof(ProbeMenu).FullName, nameof(ProbeMenu.FrameCallback));

        if (typeof(Delegate).IsAssignableFrom(parameter.ParameterType))
        {
            Delegate callback = Delegate.CreateDelegate(parameter.ParameterType, callbackMethod);
            addCallback.Invoke(null, new object[] { callback });
            managedCallbackLifetime = callback;
            return;
        }

        // Reflection cannot box a function-pointer parameter. Emit a tiny adapter that
        // pushes the exact managed callback pointer and invokes the reflected API method.
        DynamicMethod registerThunk = new(
            "DryCycle_RWImGUI_RegisterFrameCallback",
            typeof(void),
            Type.EmptyTypes,
            typeof(BridgePlugin),
            true);

        ILGenerator il = registerThunk.GetILGenerator();
        il.Emit(OpCodes.Ldftn, callbackMethod);
        il.Emit(OpCodes.Call, addCallback);
        if (addCallback.ReturnType != typeof(void))
            il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ret);

        Action register = (Action)registerThunk.CreateDelegate(typeof(Action));
        register();
    }

    private static Type[] GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException partial)
        {
            return partial.Types.Where(type => type != null).ToArray();
        }
    }

    private static string DescribeType(Type type)
    {
        return type.FullName ?? type.ToString();
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
                .Where(type =>
                    string.Equals(type.Name, "ImGUIAPI", StringComparison.OrdinalIgnoreCase) ||
                    type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                        .Any(method => method.Name.IndexOf("Callback", StringComparison.OrdinalIgnoreCase) >= 0))
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
                    .Select(m => m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => DescribeType(p.ParameterType))) + ")")
                    .Distinct());
                log?.LogInfo($"DryCycle RWImGUI API type discovered: {type.FullName}; relevantMethods=[{methods}].");
            }
        }
        catch (Exception error)
        {
            log?.LogWarning("DryCycle RWImGUI runtime API inventory failed: " + error.Message);
        }
    }
}

[SuppressUnmanagedCodeSecurity]
internal static class ProbeMenu
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

    public static void FrameCallback(ref nint idxgiSwapChain, ref uint syncInterval, ref uint flags)
    {
        if (!Enabled)
            return;

        try
        {
            AIDebugPresentationBridgeStatus.MarkPresentSeen();
            if (Interlocked.Exchange(ref firstPresentLogged, 1) == 0)
            {
                log?.LogInfo(
                    $"DryCycle RWImGUI always callback reached Present. swapChain=0x{idxgiSwapChain:X}, " +
                    $"syncInterval={syncInterval}, flags={flags}.");
            }

            if (!Visible)
                return;

            if (Interlocked.Exchange(ref firstVisibleDrawLogged, 1) == 0)
                log?.LogInfo("DryCycle RWImGUI F7-visible frame reached Present; drawing minimal proof window.");

            ImGui.SetNextWindowPos(new Num.Vector2(24f, 24f), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSize(new Num.Vector2(420f, 150f), ImGuiCond.FirstUseEver);
            if (ImGui.Begin(
                    "DryCycle AI Observatory - RWImGUI Probe###DryCycleRWImGuiProbe",
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
            {
                log?.LogError(
                    "DryCycle RWImGUI probe draw failed. The callback will remain registered for diagnostics. " + error);
            }
        }
    }
}
