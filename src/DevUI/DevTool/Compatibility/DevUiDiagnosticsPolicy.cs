namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Runtime policy for expensive DevUI verification tooling.
///
/// Compatibility audits intentionally walk live DevInterface trees, exercise reflection-backed
/// protocol discovery and can scan every loaded DevUINode type. Those checks are valuable while
/// developing the migration layer, but they are diagnostic work rather than editor work and must
/// not share the latency-sensitive O/H activation path.
///
/// Production behavior is therefore opt-in: normal DevTool sessions keep the generic compatibility
/// implementation active while leaving verification disabled. A diagnostics surface or temporary
/// developer harness may explicitly enable this policy when the audit data is actually needed.
/// </summary>
public static class DevUiDiagnosticsPolicy
{
    private static volatile bool enabled;

    public static bool Enabled
    {
        get => enabled;
        set => enabled = value;
    }
}
