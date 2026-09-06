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
    public const string PluginVersion = "0.1.1";

    private static ManualLogSource log;
    private static bool callbackRegistered;
    private static Delegate managedCallbackLifetime;

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

            // Do not compile against a fixed RWIMGUI.ImGUIAPI type. The public API has
            // changed namespace/type layout across releases, while AddMenuCallback is the
            // stable integration point we actually need. Discover that entry point from
            // the user's loaded assembly and adapt either a raw function-pointer or a
            // managed-delegate parameter at runtime.
            MethodInfo addMenuCallback = FindAddMenuCallback(apiAssembly);
            RegisterMenuCallback(addMenuCallback);

            callbackRegistered = true;
            ProbeMenu.Enabled = true;
            AIDebugPresentationBridgeStatus.MarkCallbackRegistered(apiVersion?.ToString(), imguiVersion?.ToString());

            ParameterInfo callbackParameter = addMenuCallback.GetParameters()[0];
            log?.LogInfo(
                "DryCycle RWImGUI Present callback registered through " +
                addMenuCallback.DeclaringType?.FullName + "." + addMenuCallback.Name +
                "(" + DescribeType(callbackParameter.ParameterType) + "). Press F7 to show the minimal proof window.");
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

    private static MethodInfo FindAddMenuCallback(Assembly apiAssembly)
    {
        MethodInfo[] candidates = GetLoadableTypes(apiAssembly)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            .Where(method =>
                string.Equals(method.Name, "AddMenuCallback", StringComparison.OrdinalIgnoreCase) &&
                !method.ContainsGenericParameters &&
                method.GetParameters().Length == 1)
            .OrderByDescending(method =>
                string.Equals(method.DeclaringType?.Name, "ImGUIAPI", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(method => method.IsPublic)
            .ToArray();

        if (candidates.Length == 0)
        {
            throw new MissingMethodException(
                "The loaded rain-world-imgui-api assembly exposes no static AddMenuCallback method with one parameter. " +
                "RWImGUI may have changed its callback API; check the runtime API inventory log.");
        }

        return candidates[0];
    }

    private static void RegisterMenuCallback(MethodInfo addMenuCallback)
    {
        ParameterInfo parameter = addMenuCallback.GetParameters()[0];
        MethodInfo callbackMethod = typeof(ProbeMenu).GetMethod(
            nameof(ProbeMenu.MenuCallback),
            BindingFlags.Public | BindingFlags.Static);

        if (callbackMethod == null)
            throw new MissingMethodException(typeof(ProbeMenu).FullName, nameof(ProbeMenu.MenuCallback));

        // Some API revisions may expose a managed delegate instead of the C# function
        // pointer used by the original public sample. Prefer the delegate path when the
        // reflected parameter explicitly derives from Delegate and keep it rooted for the
        // lifetime of the process.
        if (typeof(Delegate).IsAssignableFrom(parameter.ParameterType))
        {
            Delegate callback = Delegate.CreateDelegate(parameter.ParameterType, callbackMethod);
            addMenuCallback.Invoke(null, new object[] { callback });
            managedCallbackLifetime = callback;
            return;
        }

        // The established API shape is a native/function-pointer parameter. Reflection's
        // MethodInfo.Invoke cannot box function-pointer parameters, so emit a tiny adapter:
        // ldftn pushes the exact ProbeMenu.MenuCallback pointer and the following call uses
        // the reflected AddMenuCallback signature directly. This intentionally avoids any
        // compile-time dependency on the API type/namespace.
        DynamicMethod registerThunk = new(
            "DryCycle_RWImGUI_RegisterMenuCallback",
            typeof(void),
            Type.EmptyTypes,
            typeof(BridgePlugin),
            true);

        ILGenerator il = registerThunk.GetILGenerator();
        il.Emit(OpCodes.Ldftn, callbackMethod);
        il.Emit(OpCodes.Call, addMenuCallback);
        if (addMenuCallback.ReturnType != typeof(void))
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
                        .Any(method => string.Equals(method.Name, "AddMenuCallback", StringComparison.OrdinalIgnoreCase)))
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
            // Inventory failure is diagnostic only and must never stop callback registration.
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
