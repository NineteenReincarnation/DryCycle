using System;
using System.Reflection;

internal static partial class Program
{
    private static void RunTask14R1()
    {
        Type hub = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_EventHub", true);
        Type consumers = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_EventConsumers", true);
        Type eventKind = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_EventKind", true);
        Type captureKind = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_CaptureKind", true);
        Type mortalityAttribution = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_MortalityAttribution", true);

        Check(Enum.GetNames(eventKind).Length == 3 &&
              Enum.IsDefined(eventKind, "Damage") &&
              Enum.IsDefined(eventKind, "Capture") &&
              Enum.IsDefined(eventKind, "Mortality"),
            "Task14 R1 EventHub exposes exactly Damage/Capture/Mortality semantic roots");
        Check(Enum.GetNames(captureKind).Length == 2 &&
              Enum.IsDefined(captureKind, "Grasp") &&
              Enum.IsDefined(captureKind, "Tongue"),
            "Task14 R1 capture semantics distinguish grasp from tongue without creating behavior kinds");
        Check(Enum.GetNames(mortalityAttribution).Length == 3 &&
              Enum.IsDefined(mortalityAttribution, "Unattributed") &&
              Enum.IsDefined(mortalityAttribution, "RecentDamage") &&
              Enum.IsDefined(mortalityAttribution, "ActiveCapture"),
            "Task14 R1 mortality attribution has explicit bounded sources");

        Check(hub.GetEvent("Damage", Flags) != null &&
              hub.GetEvent("Capture", Flags) != null &&
              hub.GetEvent("Mortality", Flags) != null,
            "Task14 R1 EventHub exposes semantic event subscriptions");
        Check(hub.GetMethod("CreatureViolence", Flags) != null &&
              hub.GetMethod("CreatureDie", Flags) != null &&
              hub.GetMethod("FlyGrabbed", Flags) != null &&
              hub.GetMethod("TongueUpdate", Flags) != null,
            "Task14 R1 EventHub is the new Rain World fact observer for damage/capture/mortality");

        Type captureSession = hub.GetNestedType("CaptureSession", Flags);
        object session = Activator.CreateInstance(captureSession, true);
        MethodInfo accept = captureSession.GetMethod("Accept", Flags);
        object captorA = new object();
        object captorB = new object();
        bool first = (bool)accept.Invoke(session, new object[] { captorA, false });
        bool transferDuplicate = (bool)accept.Invoke(session, new object[] { captorA, true });
        bool recaptureAfterRelease = (bool)accept.Invoke(session, new object[] { captorA, false });
        bool differentCaptor = (bool)accept.Invoke(session, new object[] { captorB, true });
        Check(first && !transferDuplicate && recaptureAfterRelease && differentCaptor,
            "Task14 R1 capture session is exactly-once across tongue->grasp transfer but permits true recapture");

        Type victimState = hub.GetNestedType("VictimState", Flags);
        object mortalityState = Activator.CreateInstance(victimState, true);
        MethodInfo markMortality = victimState.GetMethod("TryMarkMortality", Flags);
        bool firstDeath = (bool)markMortality.Invoke(mortalityState, Array.Empty<object>());
        bool duplicateDeath = (bool)markMortality.Invoke(mortalityState, Array.Empty<object>());
        Check(firstDeath && !duplicateDeath,
            "Task14 R1 mortality semantic event can publish only once per victim runtime state");

        MethodInfo recent = hub.GetMethod("WithinAttributionWindow", Flags);
        bool atBoundary = (bool)recent.Invoke(null, new object[] { 360, 100, 260 });
        bool expired = (bool)recent.Invoke(null, new object[] { 361, 100, 260 });
        bool timeReversed = (bool)recent.Invoke(null, new object[] { 99, 100, 260 });
        bool unavailable = (bool)recent.Invoke(null, new object[] { int.MinValue, 100, 260 });
        Check(atBoundary && !expired && !timeReversed && !unavailable,
            "Task14 R1 mortality attribution window is bounded and monotonic");

        Type hooks = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DesertBatflyHooks", true);
        MethodInfo hooksEnable = hooks.GetMethod("Enable", Flags);
        MethodInfo hooksDisable = hooks.GetMethod("Disable", Flags);
        Check(MethodCallOffset(hooksEnable, hub, "Enable") >= 0 &&
              MethodCallOffset(hooksDisable, hub, "Disable") >= 0 &&
              MethodCallOffset(hooksEnable, consumers, "Enable") >= 0 &&
              MethodCallOffset(hooksDisable, consumers, "Disable") >= 0,
            "Task14 R1 EventHub and first consumers share the species hook lifecycle");
        Check(hooks.GetMethod("CreatureDie", Flags) == null &&
              hooks.GetMethod("TongueUpdate", Flags) == null,
            "Task14 R1 removes Core Hooks duplicate mortality and tongue semantic roots");

        Type colony = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DesertBatflyColonyRuntime", true);
        MethodInfo mortalityConsumer = consumers.GetMethod("OnMortality", Flags);
        Check(MethodCallOffset(mortalityConsumer, colony, "ReportDeath") >= 0,
            "Task14 R1 colony death accounting consumes canonical MortalityEvent attribution");

        Check(hub.Name.StartsWith("DB_", StringComparison.Ordinal) &&
              hub.Name.IndexOf("Task", StringComparison.OrdinalIgnoreCase) < 0,
            "Task14 R1 new production authority follows DB_ domain naming without TaskXX architecture");

        Console.WriteLine(
            "Task14 R1: semantic EventHub, capture-session exactly-once, mortality one-shot/attribution window, Core Hook root removal and colony mortality consumer verified.");
    }
}
