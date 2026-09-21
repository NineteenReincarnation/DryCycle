using System;

namespace DryCycle;

/// <summary>
/// Fail-open wrapper for auxiliary BepInEx plugin entrypoints that belong to optional editor/runtime
/// surfaces. Each independent plugin gets a deterministic startup source label and, if Enable fails
/// after partially installing hooks, its paired cleanup is attempted immediately.
///
/// Core gameplay/plugin bootstrap does not use this wrapper; failures there remain owned by Plugin's
/// larger startup transaction.
/// </summary>
internal static class AuxiliaryPluginStartupGuard
{
    internal static bool Enable(string source, Action enable, Action rollback)
    {
        source = string.IsNullOrWhiteSpace(source) ? "AuxiliaryPlugin.OnEnable" : source;

        if (Plugin.BootstrapFailed)
        {
            StartupDiagnostics.Marker(
                source,
                "SKIP-PRIMARY-FAILED",
                "primary DryCycle OnEnable rolled back; auxiliary startup suppressed");
            StartupDiagnostics.Marker(
                source,
                "ISOLATED",
                "auxiliary plugin remained disabled after primary bootstrap failure");
            return false;
        }

        bool succeeded = StartupDiagnostics.Optional(source, enable);
        if (succeeded)
            return true;

        StartupDiagnostics.RollbackStep(source + "/rollback", rollback);
        StartupDiagnostics.Marker(
            source,
            "ISOLATED",
            "auxiliary startup failed; Rain World startup will continue");
        return false;
    }

    internal static void Disable(string source, Action disable)
    {
        source = string.IsNullOrWhiteSpace(source) ? "AuxiliaryPlugin.OnDisable" : source;
        StartupDiagnostics.RollbackStep(source, disable);
    }
}
