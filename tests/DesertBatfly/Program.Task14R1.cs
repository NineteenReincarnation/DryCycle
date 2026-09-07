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

        Type creature = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DesertBatfly", true);
        Type intimidation = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DesertBatflyIntimidation", true);
        Check(creature.GetField("recentLethalDamager", Flags) == null &&
              creature.GetField("recentLethalDamageTicks", Flags) == null &&
              creature.GetField("recentLethalThreatScale", Flags) == null,
            "Task14 R1 removes Creature-local mortality attribution cache");
        Check(MethodCallOffset(creature.GetMethod("Die", Flags), intimidation, "BroadcastPlayerKill") < 0 &&
              MethodCallOffset(creature.GetMethod("Die", Flags), intimidation, "BroadcastPredatorKill") < 0 &&
              MethodCallOffset(creature.GetMethod("Grabbed", Flags), intimidation, "BroadcastPredatorCapture") < 0,
            "Task14 R1 Creature no longer publishes mortality/predator-capture semantics directly");

        Type peach = mod.GetType(
            "DryCycle.WatcherExts.PeachLizard.PeachLizardDesertBatflyPredation", true);
        Check(MethodCallOffset(peach.GetMethod("LizardTongue_Update", Flags), intimidation, "BroadcastPredatorCapture") < 0,
            "Task14 R1 Watcher Peach adapter owns tongue mechanics but no longer publishes fear semantics");

        MethodInfo captureConsumer = consumers.GetMethod("OnCapture", Flags);
        MethodInfo mortalityConsumer = consumers.GetMethod("OnMortality", Flags);
        Type colony = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DesertBatflyColonyRuntime", true);
        Check(MethodCallOffset(captureConsumer, intimidation, "BroadcastPredatorCapture") >= 0,
            "Task14 R1 Peach fear consumes canonical CaptureEvent");
        Check(MethodCallOffset(mortalityConsumer, colony, "ReportDeath") >= 0 &&
              MethodCallOffset(mortalityConsumer, intimidation, "BroadcastPlayerKill") >= 0 &&
              MethodCallOffset(mortalityConsumer, intimidation, "BroadcastPredatorKill") >= 0,
            "Task14 R1 Colony and Intimidation consume the same canonical MortalityEvent killer");

        Type signalIntegration = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DesertBatflySignalIntegration", true);
        MethodInfo signalEnable = signalIntegration.GetMethod("Enable", Flags);
        MethodInfo signalDisable = signalIntegration.GetMethod("Disable", Flags);
        Check(signalIntegration.GetMethod("FlyGrabbed", Flags) == null &&
              signalIntegration.GetMethod("TongueUpdate", Flags) == null &&
              signalIntegration.GetMethod("CaptureEvent", Flags) != null,
            "Task14 R1 Task12 signals no longer observe raw grasp/tongue roots independently");
        Check(MethodCallOffset(signalEnable, hub, "add_Capture") >= 0 &&
              MethodCallOffset(signalDisable, hub, "remove_Capture") >= 0,
            "Task14 R1 Task12 capture signals subscribe to the canonical CaptureEvent lifecycle");

        Check(hub.Name.StartsWith("DB_", StringComparison.Ordinal) &&
              hub.Name.IndexOf("Task", StringComparison.OrdinalIgnoreCase) < 0,
            "Task14 R1 new production authority follows DB_ domain naming without TaskXX architecture");

        Console.WriteLine(
            "Task14 R1: EventHub roots, capture-session exactly-once, canonical mortality attribution, Creature/Watcher root removal, Colony/Fear/Signal consumers and DB_ naming verified.");
    }
}
