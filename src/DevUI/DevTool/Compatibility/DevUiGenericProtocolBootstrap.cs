using System.Threading;
using DevInterface;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// One-time registration for base-class protocols that are containers rather than standalone
/// widgets. Registering the base contract keeps coverage generic: every vanilla/RK/DryCycle/mod
/// subclass inherits the same result without naming concrete feature types.
/// </summary>
public static class DevUiGenericProtocolBootstrap
{
    private static int initialized;

    public static void Ensure()
    {
        if (Interlocked.Exchange(ref initialized, 1) != 0) return;

        DevUiMigrationCoverage.RegisterAssignable(
            typeof(Panel),
            DevUiMigrationState.GenericAdapter,
            "Generic Panel container; interactive descendants are mirrored recursively");

        // Handles are scene-space interaction rather than screen-space widgets. FullAudit also
        // registers this contract, but registration is idempotent and keeping it here ensures the
        // universal mirror can bootstrap correctly even if its first frame precedes a full audit.
        DevUiMigrationCoverage.RegisterAssignable(
            typeof(Handle),
            DevUiMigrationState.GenericAdapter,
            "Generic world-space Handle protocol retained as live scene gizmo");
    }
}
