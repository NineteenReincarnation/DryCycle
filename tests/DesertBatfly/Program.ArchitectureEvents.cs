using System;
using System.Linq;
using System.Reflection;

internal static partial class Program
{
    private static void RunArchitectureEvents()
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
        Type damageEventType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_DamageEvent", true);
        Type captureEventType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_CaptureEvent", true);
        Type mortalityEventType = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_MortalityEvent", true);

        Check(Enum.GetNames(eventKind).Length == 3 &&
              Enum.IsDefined(eventKind, "Damage") &&
              Enum.IsDefined(eventKind, "Capture") &&
              Enum.IsDefined(eventKind, "Mortality"),
            "Architecture event foundation EventHub exposes exactly Damage/Capture/Mortality semantic roots");
        Check(Enum.GetNames(captureKind).Length == 2 &&
              Enum.IsDefined(captureKind, "Grasp") &&
              Enum.IsDefined(captureKind, "Tongue"),
            "Architecture event foundation capture semantics distinguish grasp from tongue without creating behavior kinds");
        Check(Enum.GetNames(mortalityAttribution).Length == 3 &&
              Enum.IsDefined(mortalityAttribution, "Unattributed") &&
              Enum.IsDefined(mortalityAttribution, "RecentDamage") &&
              Enum.IsDefined(mortalityAttribution, "ExplicitConsumption") &&
              !Enum.IsDefined(mortalityAttribution, "ActiveCapture"),
            "Architecture event foundation mortality attribution uses real damage/consume facts and never equates an active grasp with a kill");

        Check(hub.GetEvent("Damage", Flags) != null &&
              hub.GetEvent("Capture", Flags) != null &&
              hub.GetEvent("Mortality", Flags) != null,
            "Architecture event foundation EventHub exposes semantic event subscriptions");
        Check(hub.GetMethod("BeginViolence", Flags) != null &&
              hub.GetMethod("EndViolence", Flags) != null &&
              hub.GetMethod("PrepareMortality", Flags) != null &&
              hub.GetMethod("CompleteMortality", Flags) != null &&
              hub.GetMethod("ReportGraspCapture", Flags) != null &&
              hub.GetMethod("TongueUpdate", Flags) != null,
            "Architecture event foundation owned Creature lifecycle enters EventHub through explicit transactions while tongue remains an external observer");
        Check(hub.GetMethod("CreatureViolence", Flags) == null &&
              hub.GetMethod("CreatureDie", Flags) == null &&
              hub.GetMethod("FlyGrabbed", Flags) == null,
            "Architecture event foundation no longer detours base Creature/Fly lifecycle solely to rediscover DB_Creature");
        Check(hub.GetMethod("RecordConsumptionAttribution", Flags) != null,
            "Architecture event foundation exposes an explicit vanilla-eating attribution fact instead of inferring killer from a grasp");

        foreach (Type semanticType in new[] { damageEventType, captureEventType, mortalityEventType })
            Check(semanticType.GetField("Sequence", Flags) != null,
                "Architecture event foundation semantic event has a unique sequence: " + semanticType.Name);
        Check(damageEventType.GetField("Lethal", Flags) != null,
            "Architecture event foundation DamageEvent records the confirmed post-vanilla lethal result");
        Check(mortalityEventType.GetField("DamageType", Flags) != null &&
              mortalityEventType.GetField("Damage", Flags) != null &&
              mortalityEventType.GetField("Stun", Flags) != null &&
              mortalityEventType.GetField("WasConsumed", Flags) != null,
            "Architecture event foundation MortalityEvent carries the canonical causal facts needed by downstream domains");

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
            "Architecture event foundation capture session is exactly-once across tongue->grasp transfer but permits true recapture");

        Type victimState = hub.GetNestedType("VictimState", Flags);
        object mortalityState = Activator.CreateInstance(victimState, true);
        MethodInfo markMortality = victimState.GetMethod("TryMarkMortality", Flags);
        bool firstDeath = (bool)markMortality.Invoke(mortalityState, Array.Empty<object>());
        bool duplicateDeath = (bool)markMortality.Invoke(mortalityState, Array.Empty<object>());
        Check(firstDeath && !duplicateDeath,
            "Architecture event foundation mortality semantic event can publish only once per victim runtime state");
        Check(victimState.GetField("PendingMortality", Flags) != null &&
              victimState.GetField("ViolenceDepth", Flags) != null,
            "Architecture event foundation buffers re-entrant death so lethal damage is delivered Damage -> Mortality rather than double-interpreted");

        MethodInfo recent = hub.GetMethod("WithinAttributionWindow", Flags);
        int mortalityWindow = (int)hub.GetField("MortalityAttributionTicks", Flags).GetRawConstantValue();
        bool atBoundary = (bool)recent.Invoke(null, new object[] { 100 + mortalityWindow, 100, mortalityWindow });
        bool expired = (bool)recent.Invoke(null, new object[] { 101 + mortalityWindow, 100, mortalityWindow });
        bool timeReversed = (bool)recent.Invoke(null, new object[] { 99, 100, mortalityWindow });
        bool unavailable = (bool)recent.Invoke(null, new object[] { int.MinValue, 100, mortalityWindow });
        Check(atBoundary && !expired && !timeReversed && !unavailable,
            "Architecture event foundation mortality attribution window is bounded and monotonic");

        Type hooks = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_RainWorldHooks", true);
        MethodInfo hooksEnable = hooks.GetMethod("Enable", Flags);
        MethodInfo hooksDisable = hooks.GetMethod("Disable", Flags);
        Check(MethodCallOffset(hooksEnable, hub, "Enable") >= 0 &&
              MethodCallOffset(hooksDisable, hub, "Disable") >= 0 &&
              MethodCallOffset(hooksEnable, consumers, "Enable") >= 0 &&
              MethodCallOffset(hooksDisable, consumers, "Disable") >= 0,
            "Architecture event foundation EventHub and first consumers share the species hook lifecycle");
        Check(hooks.GetMethod("FlyNewRoom", Flags) == null &&
              hooks.GetMethod("FlyGrabbed", Flags) == null &&
              hooks.GetMethod("CreatureDie", Flags) == null &&
              hooks.GetMethod("TongueUpdate", Flags) == null,
            "Architecture event foundation Integration no longer wraps owned Creature room/grasp/death lifecycle");

        Type creature = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_Creature", true);
        Type runtime = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_Runtime", true);
        Type intimidation = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_FearRuntime", true);
        Check(creature.GetField("recentLethalDamager", Flags) == null &&
              creature.GetField("recentLethalDamageTicks", Flags) == null &&
              creature.GetField("recentLethalThreatScale", Flags) == null,
            "Architecture event foundation removes Creature-local mortality attribution cache");
        Check(MethodCallOffset(creature.GetMethod("NewRoom", Flags), runtime, "BeforeNewRoom") >= 0 &&
              runtime.GetMethod("BeforeNewRoom", Flags) != null,
            "Architecture event foundation room-transition cleanup is called directly by DB_Creature before vanilla NewRoom");
        Check(MethodCallOffset(creature.GetMethod("Grabbed", Flags), hub, "ReportGraspCapture") >= 0,
            "Architecture event foundation DB_Creature reports owned grasp lifecycle directly before vanilla Grabbed");
        Check(MethodCallOffset(creature.GetMethod("Violence", Flags), hub, "BeginViolence") >= 0 &&
              MethodCallOffset(creature.GetMethod("Violence", Flags), hub, "EndViolence") >= 0,
            "Architecture event foundation DB_Creature wraps vanilla Violence in one direct semantic transaction");
        Check(MethodCallOffset(creature.GetMethod("Die", Flags), hub, "PrepareMortality") >= 0 &&
              MethodCallOffset(creature.GetMethod("Die", Flags), hub, "CompleteMortality") >= 0,
            "Architecture event foundation DB_Creature wraps accepted vanilla Die in one direct mortality transaction");
        Check(MethodCallOffset(creature.GetMethod("Die", Flags), intimidation, "BroadcastPlayerKill") < 0 &&
              MethodCallOffset(creature.GetMethod("Die", Flags), intimidation, "BroadcastPredatorKill") < 0 &&
              MethodCallOffset(creature.GetMethod("Grabbed", Flags), intimidation, "BroadcastPredatorCapture") < 0,
            "Architecture event foundation Creature reports facts but does not publish mortality/predator-capture domain reactions directly");

        InterfaceMapping edible = creature.GetInterfaceMap(typeof(IPlayerEdible));
        MethodInfo bitByPlayer = null;
        for (int i = 0; i < edible.InterfaceMethods.Length; i++)
            if (edible.InterfaceMethods[i].Name == "BitByPlayer")
                bitByPlayer = edible.TargetMethods[i];
        Check(bitByPlayer != null &&
              MethodCallOffset(bitByPlayer, hub, "RecordConsumptionAttribution") >= 0,
            "Architecture event foundation player eating reports its explicit lethal action to the canonical mortality authority");

        Type peach = mod.GetType(
            "DryCycle.WatcherExts.PeachLizard.PeachLizardDesertBatflyPredation", true);
        Check(MethodCallOffset(peach.GetMethod("LizardTongue_Update", Flags), intimidation, "BroadcastPredatorCapture") < 0,
            "Architecture event foundation Watcher Peach adapter owns tongue mechanics but no longer publishes fear semantics");

        MethodInfo captureConsumer = consumers.GetMethod("OnCapture", Flags);
        MethodInfo mortalityConsumer = consumers.GetMethod("OnMortality", Flags);
        Type colony = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_ColonyRuntime", true);
        Check(MethodCallOffset(captureConsumer, intimidation, "BroadcastPredatorCapture") >= 0,
            "Architecture event foundation Peach fear consumes canonical CaptureEvent");
        Check(MethodCallOffset(mortalityConsumer, colony, "ReportDeath") >= 0 &&
              MethodCallOffset(mortalityConsumer, intimidation, "BroadcastPlayerKill") >= 0 &&
              MethodCallOffset(mortalityConsumer, intimidation, "BroadcastPredatorKill") >= 0,
            "Architecture event foundation Colony and Intimidation consume the same canonical MortalityEvent killer");

        Type threatRuntime = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_ThreatRuntime", true);
        Type threatState = threatRuntime.GetNestedType("RuntimeState", Flags);
        Check(threatRuntime.GetMethod("DamageEvent", Flags) != null &&
              threatRuntime.GetMethod("CaptureEvent", Flags) != null &&
              threatRuntime.GetMethod("MortalityEvent", Flags) != null,
            "Architecture event foundation ThreatRuntime consumes canonical damage/capture/mortality facts");
        Check(threatRuntime.GetMethod("CreatureViolence", Flags) == null &&
              threatRuntime.GetMethod("CreatureDie", Flags) == null &&
              threatRuntime.GetMethod("FlyGrabbed", Flags) == null,
            "Architecture event foundation ThreatRuntime owns no duplicate raw damage/death/grasp hooks");
        Check(threatState.GetField("RecentDamagePlayer", Flags) == null &&
              threatState.GetField("RecentDamageSourceObject", Flags) == null &&
              threatState.GetField("RecentDamagePlayerSlot", Flags) != null &&
              threatState.GetField("RecentDamageTick", Flags) != null,
            "Architecture event foundation ThreatRuntime keeps only short-lived learning context, not an independent killer attribution cache");
        Check((int)threatRuntime.GetField("RecentDamageMemoryTicks", Flags).GetRawConstantValue() == mortalityWindow,
            "Architecture event foundation Threat kill-evidence window shares the EventHub mortality attribution duration");
        Check(MethodCallOffset(threatRuntime.GetMethod("MortalityEvent", Flags), threatRuntime, "Forget") >= 0,
            "Architecture event foundation ThreatRuntime clears its own transient state only after consuming canonical mortality");

        Type signalRuntime = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_SignalRuntime", true);
        MethodInfo consumerEnable = consumers.GetMethod("Enable", Flags);
        MethodInfo consumerDisable = consumers.GetMethod("Disable", Flags);
        Check(captureConsumer != null &&
              MethodCallOffset(captureConsumer, signalRuntime, "EmitDistress") >= 0 &&
              MethodCallOffset(captureConsumer, signalRuntime, "EmitAlarm") >= 0,
            "Architecture event foundation canonical CaptureEvent directly publishes Distress/Alarm through SignalRuntime");
        Check(MethodCallOffset(consumerEnable, hub, "add_Capture") >= 0 &&
              MethodCallOffset(consumerDisable, hub, "remove_Capture") >= 0,
            "Architecture event foundation event consumers own the canonical CaptureEvent subscription lifecycle");

        Type corpseWarnings = mod.GetType(
            "DryCycle.Creatures.DesertBatfly.DB_CorpseWarningRuntime", true);
        Check(corpseWarnings.GetMethod("TrackRoom", Flags) != null &&
              corpseWarnings.GetMethod("Reset", Flags) != null &&
              corpseWarnings.GetMethod("IsCorpseWarning", Flags) != null,
            "Architecture event foundation has a dedicated transient CorpseWarning teardown owner");
        Check(MethodCallOffset(mortalityConsumer, corpseWarnings, "TrackRoom") >= 0 &&
              MethodCallOffset(hooksEnable, corpseWarnings, "Reset") >= 0 &&
              MethodCallOffset(hooksDisable, corpseWarnings, "Reset") >= 0,
            "Architecture event foundation mortality rooms are weakly tracked and CorpseWarning teardown runs on enable/disable lifecycle boundaries");
        Check(corpseWarnings.GetField("active", Flags) == null,
            "Architecture event foundation CorpseWarning cleanup does not strongly retain transient warning objects or their Rooms");

        Check(hub.Name.StartsWith("DB_", StringComparison.Ordinal) &&
              corpseWarnings.Name.StartsWith("DB_", StringComparison.Ordinal) &&
              new[] { hub.Name, corpseWarnings.Name }.All(n =>
                  n.IndexOf("Task", StringComparison.OrdinalIgnoreCase) < 0),
            "Architecture event foundation new production authorities follow DB_ domain naming without TaskXX architecture");

        Console.WriteLine(
            "Architecture event foundation: direct owned lifecycle transactions, causal Damage->Mortality ordering, explicit consumption attribution, exactly-once capture, Threat/Colony/Fear/Signal consumers, and bounded CorpseWarning teardown verified.");
    }
}
