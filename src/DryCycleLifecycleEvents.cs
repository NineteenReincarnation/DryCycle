using System;

namespace DryCycle;

/// <summary>
/// Cross-assembly lifecycle surface for optional DryCycle frontends.
///
/// The core plugin owns RainWorld lifecycle hooks. Optional assemblies subscribe here instead of
/// stacking their own PreModsInit/OnModsInit HookGen detours on the same external methods.
/// </summary>
internal static class DryCycleLifecycleEvents
{
    internal static event Action<RainWorld> BeforePreModsInit;
    internal static event Action<RainWorld> AfterPreModsInit;
    internal static event Action<RainWorld> BeforeModsInit;
    internal static event Action<RainWorld> AfterModsInit;

    internal static void RaiseBeforePreModsInit(RainWorld rainWorld) =>
        Invoke(BeforePreModsInit, rainWorld, nameof(BeforePreModsInit));

    internal static void RaiseAfterPreModsInit(RainWorld rainWorld) =>
        Invoke(AfterPreModsInit, rainWorld, nameof(AfterPreModsInit));

    internal static void RaiseBeforeModsInit(RainWorld rainWorld) =>
        Invoke(BeforeModsInit, rainWorld, nameof(BeforeModsInit));

    internal static void RaiseAfterModsInit(RainWorld rainWorld) =>
        Invoke(AfterModsInit, rainWorld, nameof(AfterModsInit));

    private static void Invoke(Action<RainWorld> handlers, RainWorld rainWorld, string phase)
    {
        if (handlers == null) return;

        Delegate[] invocationList = handlers.GetInvocationList();
        for (int i = 0; i < invocationList.Length; i++)
        {
            Delegate handler = invocationList[i];
            string owner = handler?.Method?.DeclaringType?.FullName ?? "<unknown>";
            string method = handler?.Method?.Name ?? "<unknown>";
            StartupDiagnostics.Optional(
                "Lifecycle/" + phase + "/" + owner + "." + method,
                () => ((Action<RainWorld>)handler)(rainWorld));
        }
    }
}
