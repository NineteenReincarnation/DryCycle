from pathlib import Path

ROOT = Path('.')


def read(path):
    return (ROOT / path).read_text(encoding='utf-8')


def write(path, text):
    p = ROOT / path
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(text, encoding='utf-8', newline='\n')


def replace_once(path, old, new):
    text = read(path)
    count = text.count(old)
    if count != 1:
        raise SystemExit(f'{path}: expected exactly one replacement target, found {count}')
    write(path, text.replace(old, new, 1))


# -----------------------------------------------------------------------------
# B4 semantic-retention corrections discovered by B5 audit.
# -----------------------------------------------------------------------------
replace_once(
    'src/Creatures/DesertBatfly/Signals/DesertBatflySignalRuntime.cs',
    '    private const int NeutralSignalTtl = 90;\n',
    '    private const int NeutralSignalTtl = 90;\n'
    '    // Retained from the retired SignalIntegration/SignalThreatBridge split:\n'
    '    // a concrete Creature alarm may trigger Escape at 0.30, while an anonymous\n'
    '    // hazard needs the old stricter 0.34 confidence before creating Escape.\n'
    '    private const float ThreatAlarmEscapeThreshold = 0.30f;\n'
    '    private const float AnonymousAlarmEscapeThreshold = 0.34f;\n')
replace_once(
    'src/Creatures/DesertBatfly/Signals/DesertBatflySignalRuntime.cs',
    '        if (receiver == null || packet == null || response < 0.30f || receiver.dead ||\n',
    '        if (receiver == null || packet == null || response < ThreatAlarmEscapeThreshold || receiver.dead ||\n')
replace_once(
    'src/Creatures/DesertBatfly/Signals/DesertBatflySignalRuntime.cs',
    '        Creature threat = packet.Threat;\n'
    '        if (threat != null && (threat.dead || threat.room != receiver.room))\n'
    '            return;\n',
    '        Creature threat = packet.Threat;\n'
    '        if (threat == null && response < AnonymousAlarmEscapeThreshold) return;\n'
    '        if (threat != null && (threat.dead || threat.room != receiver.room))\n'
    '            return;\n')

# Preserve the pre-B4 DB_EventHub subscriber order: canonical Intimidation/direct experience
# runs first; capture social packets are published afterward. This avoids DistressInterest
# changing initial vengeance candidate scoring merely because the old SignalIntegration
# subscriber was folded into DB_EventConsumers.
event_path = 'src/Creatures/DesertBatfly/Runtime/DB_EventConsumers.cs'
event_text = read(event_path)
start = event_text.index('    private static void OnCapture(DB_CaptureEvent capture)\n')
end = event_text.index('    private static void OnMortality(DB_MortalityEvent mortality)\n')
new_capture = '''    private static void OnCapture(DB_CaptureEvent capture)\n    {\n        DesertBatfly victim = capture.Victim;\n        if (victim == null || victim.dead) return;\n\n        DesertBatflySocialLife.CancelForPriority(victim, "semantic capture event");\n\n        if (capture.Captor is Lizard predator &&\n            DesertBatflyIntimidation.IsSupportedLethalThreat(predator))\n        {\n            // Preserve the historical subscriber order from before R5-B4: direct\n            // Intimidation/fear/vengeance processing commits first. Capture Distress/Alarm\n            // is published below, so it cannot retroactively boost the initial vengeance\n            // candidate score for the same capture event.\n            DesertBatflyIntimidation.BroadcastPredatorCapture(\n                victim,\n                predator,\n                capture.Tongue);\n\n            // A tongue capture precedes Fly.Grabbed; preserve the immediate native danger\n            // response that the old tongue hooks supplied. The later explicit capture\n            // Alarm is the sole root, so this direct danger fact does not rebroadcast.\n            if (capture.CaptureKind == DB_CaptureKind.Tongue)\n                victim.DesertAI.Threatened(predator, true, false);\n        }\n\n        Creature signalThreat = capture.Captor;\n        if (signalThreat != null && signalThreat.room == victim.room)\n        {\n            bool tongueSignal = capture.CaptureKind == DB_CaptureKind.Tongue;\n            DesertBatflySignalRuntime.EmitDistress(\n                victim,\n                signalThreat,\n                tongueSignal ? 0.94f : 0.88f,\n                tongueSignal\n                    ? "semantic tongue capture emits one DistressCall"\n                    : "semantic non-Fly grasp emits one DistressCall");\n            Vector2 signalOrigin = signalThreat.mainBodyChunk?.pos ?? capture.Position;\n            DesertBatflySignalRuntime.EmitAlarm(\n                victim,\n                signalThreat,\n                signalOrigin,\n                Custom.DirVec(victim.mainBodyChunk.pos, signalOrigin),\n                tongueSignal ? 0.90f : 0.82f,\n                tongueSignal\n                    ? "semantic tongue capture emits one AlarmFlutter"\n                    : "semantic grasp emits one AlarmFlutter alongside DistressCall");\n        }\n    }\n\n'''
write(event_path, event_text[:start] + new_capture + event_text[end:])

# Explicitly classify the generic reflection helper. It is retained because deleting it
# would remove Sandbox icon support and the optional Warp integration; it is not a behavior
# bridge and no Task09-13 domain is allowed to call it.
replace_once(
    'src/Creatures/DesertBatfly/DesertBatflyRuntimePatch.cs',
    '// Optional integrations should not become hard compile/runtime dependencies.\n'
    '// BepInEx already ships Harmony; resolve it reflectively so DryCycle continues to\n'
    '// load even in unusual installs where that assembly or the target mod is absent.\n',
    '// R5-B5 classification: RETAIN as integration-only infrastructure. This is not an\n'
    '// internal Desert Batfly behavior bridge. Its only production consumers are Sandbox\n'
    '// symbol integration and optional Warp compatibility; Task09-13 domains must not use it.\n'
    '// BepInEx already ships Harmony, so resolve it reflectively to avoid hard dependencies.\n')

# -----------------------------------------------------------------------------
# Permanent managed regression guard.
# -----------------------------------------------------------------------------
retention_test = r'''using System;
using System.Reflection;

internal static partial class Program
{
    private static void RunTask14R5Retention()
    {
        Type hooks = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyHooks", true);
        Type intimidation = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyIntimidation", true);
        Type tactics = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyThreatTactics", true);
        Type policy = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentalPolicy", true);
        Type ai = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyAI", true);
        Type combat = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CombatRuntime", true);
        Type social = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySocialLife", true);
        Type behavior = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalBehavior", true);
        Type roomEnvironment = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalRoomRuntime", true);
        Type colony = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyColonyRuntime", true);
        Type travel = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyTravelNavigation", true);
        Type refuge = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyRefuge", true);
        Type signal = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalRuntime", true);
        Type threat = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyThreatRuntime", true);
        Type consumers = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EventConsumers", true);
        Type bond = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySocialBond", true);
        Type runtimePatch = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyRuntimePatch", true);
        Type sandbox = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySandbox", true);
        Type warp = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyWarpCompatibility", true);

        foreach (string removed in new[]
                 {
                     "DesertBatflyEnvironmentalDenseFogBridge",
                     "DesertBatflyEnvironmentalVengeanceBridge",
                     "DesertBatflyThreatVengeanceBridge",
                     "DesertBatflyEnvironmentalIntegration",
                     "DesertBatflyEnvironmentalSocialBridge",
                     "DesertBatflyEnvironmentalTask09Bridge",
                     "DesertBatflyEnvironmentalSurvivalBridge",
                     "DesertBatflySignalIntegration",
                     "DesertBatflySignalAcuteBridge",
                     "DesertBatflySignalDirectWitnessBridge",
                     "DesertBatflySignalThreatBridge",
                     "DesertBatflySignalVengeanceBridge"
                 })
            Check(mod.GetType("DryCycle.Creatures.DesertBatfly." + removed, false) == null,
                "R5 retention: retired adapter stays physically absent: " + removed);

        // B1: tactical semantics moved to the real Vengeance owner.
        Check(MethodCallOffset(intimidation.GetMethod("ForceFlight", Flags), tactics,
                  "AdjustExtremeVengeanceGoal") >= 0,
            "R5 retention B1: Vengeance still consumes Task11 tactical geometry directly");

        // B2: Environmental integration was deleted only after every consumer gained a
        // direct read-only policy path.
        Check(MethodCallOffset(combat.GetMethod("CompleteCandidateScan", Flags), policy,
                  "CombatMotivation") >= 0 &&
              MethodCallOffset(combat.GetMethod("CanHarass", Flags), policy,
                  "AllowsHarassCandidate") >= 0,
            "R5 retention B2: Combat still consumes environmental motivation/permission");
        Check(MethodCallOffset(ai.GetMethod("TryPlanRoost", Flags), policy,
                  "RoostChanceScale") >= 0 &&
              MethodCallOffset(ai.GetMethod("ExecuteRoostOwned", Flags), policy,
                  "AdjustRoostDuration") >= 0,
            "R5 retention B2: Roost chance/duration still consume Task13 policy");
        Check(MethodCallOffset(social.GetMethod("RefreshState", Flags), policy,
                  "SocialDriveScale") >= 0 &&
              MethodCallOffset(social.GetMethod("TryScheduleInteraction", Flags), policy,
                  "GroupCohesionScale") >= 0,
            "R5 retention B2: neutral social drive/group cohesion still consume Task13 policy");

        // B3: the two removed Environment bridges are now on active R3/Task09 paths.
        Check(MethodCallOffset(behavior.GetMethod("RefreshInfluence", Flags), behavior,
                  "ApplySecondaryLightRainMoisture") >= 0,
            "R5 retention B3: secondary LightRain moisture remains on live RefreshInfluence path");
        Check(MethodCallOffset(behavior.GetMethod("ApplyOwnedBehavior", Flags), behavior,
                  "ApplyNativeHomeAndBurrow") >= 0,
            "R5 retention B3: same-room Home/Hive/Burrow remains on Environment-owned execution");
        Check(MethodCallOffset(roomEnvironment.GetMethod("Update", Flags), roomEnvironment,
                  "ObserveLocalShelterFailure") >= 0,
            "R5 retention B3: LocalShelterFailure remains sampled by room runtime");
        Check(MethodCallOffset(colony.GetMethod("ScheduleMigrationBatch", Flags), policy,
                  "ShouldSuppressNewMigration") >= 0,
            "R5 retention B3: Task09 migration start still consumes Task13 timing veto");
        Check(MethodCallOffset(travel.GetMethod("EvaluateColonyWeather", Flags), policy,
                  "ShouldRecallHomeForSandstorm") >= 0,
            "R5 retention B3: Task09 still performs early Sandstorm Home recall");
        MethodInfo findRefuge = refuge.GetMethod("TryFindEmergencyRefuge", Flags);
        Check(MethodCallOffset(findRefuge, policy, "CanConsiderSandstormOutwardRefuge") >= 0 &&
              MethodCallOffset(findRefuge, policy, "AcceptSandstormEmergencyRefuge") >= 0,
            "R5 retention B3: narrow Sandstorm outward-refuge exception remains filtered");

        // B4: Task12 detours were folded into explicit owners. Preserve both behavior and
        // the subtle thresholds/order that existed before deletion.
        Check(MethodCallOffset(ai.GetMethod("RaiseLocalAlarm", Flags), signal, "EmitAlarm") >= 0,
            "R5 retention B4: AI local alarm still enters Task12 explicitly");
        Check(MethodCallOffset(signal.GetMethod("ReceivePacket", Flags), signal, "ApplyAlarm") >= 0,
            "R5 retention B4: received Alarm still applies bounded Escape through SignalRuntime");
        Check(Math.Abs(Convert.ToSingle(signal.GetField("ThreatAlarmEscapeThreshold", Flags)
                          .GetRawConstantValue()) - 0.30f) < 0.0001f &&
              Math.Abs(Convert.ToSingle(signal.GetField("AnonymousAlarmEscapeThreshold", Flags)
                          .GetRawConstantValue()) - 0.34f) < 0.0001f,
            "R5 retention B4: concrete/anonymous Alarm escape thresholds remain 0.30/0.34");
        Type threatMemory = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyThreatMemoryStore", true);
        Check(MethodCallOffset(signal.GetMethod("ResponseStrength", Flags), threatMemory, "For") >= 0,
            "R5 retention B4: Signal response still reads only receiver-owned Task11 memory");
        Check(MethodCallOffset(threat.GetMethod("ReportExplosion", Flags), signal, "EmitAcuteAlarm") >= 0 &&
              MethodCallOffset(threat.GetMethod("BroadcastStartle", Flags), signal, "EmitAcuteAlarm") >= 0 &&
              MethodCallOffset(threat.GetMethod("BroadcastMassCasualty", Flags), signal, "EmitAcuteAlarm") >= 0,
            "R5 retention B4: explosion/startle/mass-casualty still publish real-position acute roots");

        MethodInfo onCapture = consumers.GetMethod("OnCapture", Flags);
        int captureFear = MethodCallOffset(onCapture, intimidation, "BroadcastPredatorCapture");
        int captureDistress = MethodCallOffset(onCapture, signal, "EmitDistress");
        int captureAlarm = MethodCallOffset(onCapture, signal, "EmitAlarm");
        Check(captureFear >= 0 && captureDistress > captureFear && captureAlarm > captureFear,
            "R5 retention B4: capture direct experience commits before social Distress/Alarm publication");
        Check(MethodCallOffset(combat.GetMethod("FindSocialHarassTarget", Flags), signal,
                  "TryGetInfluence") >= 0,
            "R5 retention B4: Combat still consumes HarassSignal interest");
        Check(MethodCallOffset(social.GetMethod("FindRoostSource", Flags), signal,
                  "TryGetInfluence") >= 0,
            "R5 retention B4: Task10 still consumes RoostCall interest");
        Check(MethodCallOffset(bond.GetMethod("OnBondPartnerDeath", Flags), bond,
                  "IsDirectDeathWitness") >= 0,
            "R5 retention B4: persistent grief still requires direct death witness");
        Check(MethodCallOffset(intimidation.GetMethod("ReceiveFear", Flags), signal, "EmitAlarm") >= 0 &&
              MethodCallOffset(intimidation.GetMethod("ArmVengeance", Flags), signal, "EmitRally") >= 0,
            "R5 retention B4: direct fear Alarm and synchronous Vengeance Rally remain explicit");

        // B5 classification: reflection is retained only for integration surfaces whose
        // removal would itself lose Sandbox/Warp functionality. Internal behavior does not
        // depend on this utility.
        Check(MethodCallOffset(sandbox.GetMethod("Enable", Flags), runtimePatch, "Create") >= 0 &&
              MethodCallOffset(sandbox.GetMethod("Enable", Flags), runtimePatch, "Patch") >= 0 &&
              MethodCallOffset(sandbox.GetMethod("Disable", Flags), runtimePatch, "UnpatchSelf") >= 0,
            "R5 retention B5: RuntimePatch remains required by Sandbox symbol integration");
        Check(MethodCallOffset(warp.GetMethod("Enable", Flags), runtimePatch, "Create") >= 0 &&
              MethodCallOffset(warp.GetMethod("Enable", Flags), runtimePatch, "Patch") >= 0 &&
              MethodCallOffset(warp.GetMethod("Disable", Flags), runtimePatch, "UnpatchSelf") >= 0,
            "R5 retention B5: RuntimePatch remains required by optional Warp compatibility");
        Check(MethodCallOffset(hooks.GetMethod("RainWorld_OnModsInit", Flags), sandbox, "Enable") >= 0 &&
              MethodCallOffset(hooks.GetMethod("RainWorld_OnModsInit", Flags), warp, "Enable") >= 0 &&
              MethodCallOffset(hooks.GetMethod("Disable", Flags), sandbox, "Disable") >= 0 &&
              MethodCallOffset(hooks.GetMethod("Disable", Flags), warp, "Disable") >= 0,
            "R5 retention B5: retained integrations remain wired into species lifecycle");

        Console.WriteLine(
            "Task14 R5 retention: B1-B4 deleted adapter semantics remain reachable from direct owners; B5 keeps only Sandbox/Warp reflection integration.");
    }
}
'''
write('tests/DesertBatfly/Program.Task14R5Retention.cs', retention_test)

replace_once(
    'tests/DesertBatfly/Program.RejectedTask02.cs',
    '        RunTask14R5();\n',
    '        RunTask14R5();\n        RunTask14R5Retention();\n')

# -----------------------------------------------------------------------------
# Permanent source-level audit. This complements managed IL tests and catches imports/files.
# -----------------------------------------------------------------------------
audit_script = r'''#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

mapfile -t bridges < <(find src/Creatures/DesertBatfly -type f -name '*Bridge.cs' -print | sort)
test "${#bridges[@]}" -eq 0

mapfile -t detours < <(grep -RIl --include='*.cs' 'MonoMod.RuntimeDetour' src/Creatures/DesertBatfly | sort || true)
test "${#detours[@]}" -eq 0

mapfile -t reflection < <(grep -RIl --include='*.cs' 'System.Reflection' src/Creatures/DesertBatfly | sort || true)
test "${#reflection[@]}" -eq 3
printf '%s\n' "${reflection[@]}" | grep -qx 'src/Creatures/DesertBatfly/DesertBatflyRuntimePatch.cs'
printf '%s\n' "${reflection[@]}" | grep -qx 'src/Creatures/DesertBatfly/DesertBatflySandbox.cs'
printf '%s\n' "${reflection[@]}" | grep -qx 'src/Creatures/DesertBatfly/DesertBatflyWarpCompatibility.cs'

for name in \
  DesertBatflyEnvironmentalDenseFogBridge \
  DesertBatflyEnvironmentalVengeanceBridge \
  DesertBatflyThreatVengeanceBridge \
  DesertBatflyEnvironmentalIntegration \
  DesertBatflyEnvironmentalSocialBridge \
  DesertBatflyEnvironmentalTask09Bridge \
  DesertBatflyEnvironmentalSurvivalBridge \
  DesertBatflySignalIntegration \
  DesertBatflySignalAcuteBridge \
  DesertBatflySignalDirectWitnessBridge \
  DesertBatflySignalThreatBridge \
  DesertBatflySignalVengeanceBridge; do
  ! grep -RIn --include='*.cs' "$name" src/Creatures/DesertBatfly
 done

grep -q 'ApplySecondaryLightRainMoisture(bat, state, tick)' src/Creatures/DesertBatfly/Environmental/DesertBatflyEnvironmentalBehavior.cs
grep -q 'ApplyNativeHomeAndBurrow(bat, state.Influence)' src/Creatures/DesertBatfly/Environmental/DesertBatflyEnvironmentalBehavior.cs
grep -q 'ObserveLocalShelterFailure(state)' src/Creatures/DesertBatfly/Environmental/DesertBatflyEnvironmentalRoomRuntime.cs
grep -q 'DB_EnvironmentalPolicy.ShouldSuppressNewMigration(world, source)' src/Creatures/DesertBatfly/DesertBatflyColonyRuntime.cs
grep -q 'DB_EnvironmentalPolicy.ShouldRecallHomeForSandstorm' src/Creatures/DesertBatfly/DesertBatflyTravelNavigation.cs
grep -q 'DB_EnvironmentalPolicy.CanConsiderSandstormOutwardRefuge' src/Creatures/DesertBatfly/DesertBatflyRefuge.cs
grep -q 'DB_EnvironmentalPolicy.AcceptSandstormEmergencyRefuge' src/Creatures/DesertBatfly/DesertBatflyRefuge.cs
! grep -RIn --include='*.cs' 'LeaveRoom(' src/Creatures/DesertBatfly/Environmental
! grep -RIn --include='*.cs' 'new TravelIntent\|RequestPermanentMigration\|ConvertToReturnHome' src/Creatures/DesertBatfly/Environmental

grep -q 'AnonymousAlarmEscapeThreshold = 0.34f' src/Creatures/DesertBatfly/Signals/DesertBatflySignalRuntime.cs
grep -q 'ThreatAlarmEscapeThreshold = 0.30f' src/Creatures/DesertBatfly/Signals/DesertBatflySignalRuntime.cs
grep -q 'DesertBatflyThreatMemoryStore.For(receiver.DesertState, slot)' src/Creatures/DesertBatfly/Signals/DesertBatflySignalRuntime.cs
test "$(grep -c 'DesertBatflySignalRuntime.EmitAcuteAlarm' src/Creatures/DesertBatfly/ThreatSignature/DesertBatflyThreatRuntime.cs)" -ge 3
grep -q 'DesertBatflySignalRuntime.EmitDistress' src/Creatures/DesertBatfly/Runtime/DB_EventConsumers.cs
grep -q 'DesertBatflySignalRuntime.EmitRally' src/Creatures/DesertBatfly/DesertBatflyIntimidation.cs
grep -q 'IsDirectDeathWitness(observer, victim, killer)' src/Creatures/DesertBatfly/DesertBatflySocialBond.cs
! grep -RIn --include='*.cs' 'DesertBatflyThreatMemoryStore.AddEvidence' src/Creatures/DesertBatfly/Signals
! grep -RIn --include='*.cs' 'LeaveRoom(' src/Creatures/DesertBatfly/Signals

mapfile -t runtime_patch_users < <(grep -RIl --include='*.cs' 'DesertBatflyRuntimePatch' src/Creatures/DesertBatfly | sort)
test "${#runtime_patch_users[@]}" -eq 3
printf '%s\n' "${runtime_patch_users[@]}" | grep -qx 'src/Creatures/DesertBatfly/DesertBatflyRuntimePatch.cs'
printf '%s\n' "${runtime_patch_users[@]}" | grep -qx 'src/Creatures/DesertBatfly/DesertBatflySandbox.cs'
printf '%s\n' "${runtime_patch_users[@]}" | grep -qx 'src/Creatures/DesertBatfly/DesertBatflyWarpCompatibility.cs'

echo 'R5 retention audit passed: no internal Bridge/RuntimeDetour debt; reflection is limited to Sandbox/Warp integration.'
'''
write('scripts/check-desertbatfly-r5-retention.sh', audit_script)

# -----------------------------------------------------------------------------
# Permanent migration/retention ledger.
# -----------------------------------------------------------------------------
ledger = '''Desert Batfly Task 14 — R5 Deleted Adapter Function-Retention Ledger\nRevision: R5-B5 / 2026-09-07\nStatus: 【代码侧守卫已建立 / 待 Rain World 实机总验收】\n\nPurpose\n=======\n\nThis ledger exists because deleting a Bridge/Integration file is not itself a successful\nrefactor. Every behavior that mattered in the deleted file must have a live direct owner,\nand the new call path must remain reachable after R1-R4 changed the update pipeline.\n\nR5 uses two permanent guards:\n- tests/DesertBatfly/Program.Task14R5Retention.cs — managed IL call-path/ordering guards.\n- scripts/check-desertbatfly-r5-retention.sh — source/file/import ownership guards.\n\nLive Rain World validation is still required later. These guards prove code-side reachability\nand ownership, not final in-game tuning.\n\n======================================================================\nB1 — early detour retirement\n======================================================================\n\nDesertBatflyEnvironmentalDenseFogBridge.cs [DELETED]\nOld responsibility:\n- behavior-neutral DenseFog integration shim.\nCurrent owner:\n- Task13 weather ecology/context + DB_FogGoalModifier / shared perception.\nRetention rule:\n- no replacement compatibility shell; DenseFog remains normal Task13 state.\n\nDesertBatflyEnvironmentalVengeanceBridge.cs [DELETED]\nOld responsibility:\n- block Vengeance movement during hard environmental survival.\nCurrent owner:\n- DB_BehaviorArbiter single PrimaryOwner. Intimidation state may tick, Vengeance phase\n  locomotion advances only when PrimaryOwner=Vengeance.\n\nDesertBatflyThreatVengeanceBridge.cs [DELETED]\nOld responsibility:\n- Task11 tactical adjustment around Vengeance flight.\nCurrent owner:\n- DesertBatflyIntimidation.ForceFlight directly calls\n  DesertBatflyThreatTactics.AdjustExtremeVengeanceGoal before DB_FlightMotor.\n\n======================================================================\nB2 — Environment Integration/Social Bridge retirement\n======================================================================\n\nDesertBatflyEnvironmentalIntegration.cs [DELETED]\nDesertBatflyEnvironmentalSocialBridge.cs [DELETED]\nOld responsibilities and direct owners:\n- weather aggression permission -> DB_EnvironmentalPolicy.AggressionAuthorized.\n- combat motivation -> DB_EnvironmentalPolicy.CombatMotivation.\n- Harass candidate weighting -> DB_EnvironmentalPolicy.AllowsHarassCandidate.\n- Roost chance/duration -> DesertBatflyAI reads RoostChanceScale/AdjustRoostDuration.\n- neutral social drive/group/play/activity radius -> DesertBatflySocialLife reads policy.\n- Fog Home/Hive familiarity -> DB_FogGoalModifier via FogNavigationFamiliarityScale.\n- temporary Thirst spoofing -> REMOVED, not migrated; it was integration debt (HB-06).\n- old environment Steer hook -> REMOVED; DB_FlightMotor/DB_FogGoalModifier own flight.\n\nDeletion effect audit:\n- No current behavior domain depends on reflection or RuntimeDetour to read Task13.\n- Removing temporary Thirst mutation does not remove actual thirst/dehydration behavior; it\n  only removes mutation used to impersonate combat motivation.\n\n======================================================================\nB3 — Task09 / native survival bridge retirement\n======================================================================\n\nDesertBatflyEnvironmentalTask09Bridge.cs [DELETED]\nOld responsibility -> direct owner:\n- suppress starting new migration in severe Sandstorm ->\n  DesertBatflyColonyRuntime.ScheduleMigrationBatch queries DB_EnvironmentalPolicy.\n- early Sandstorm Home recall ->\n  DesertBatflyTravelNavigation.EvaluateColonyWeather queries policy and Task09 itself creates\n  ReturnHome routes/intents.\n- narrow outward emergency refuge exception ->\n  DesertBatflyRefuge.TryFindEmergencyRefuge applies policy before accepting a target.\n- LocalShelterFailure -> DesertBatflyEnvironmentalRoomRuntime owns realized observation and\n  reports only bounded evidence to ColonyRuntime.\n\nDesertBatflyEnvironmentalSurvivalBridge.cs [DELETED]\nOld responsibility -> direct owner:\n- same-room Home/Hive/Burrow -> DesertBatflyEnvironmentalBehavior.ApplyOwnedBehavior.\n- secondary LightRain moisture -> DesertBatflyEnvironmentalBehavior.RefreshInfluence.\n\nImportant deletion effect found and fixed during B3:\n- the old SurvivalBridge detoured EnvironmentalBehavior.Update, but R3 production had already\n  moved to RefreshInfluence + DB_EnvironmentExecutor. The old bridge could therefore be off\n  the active path. Migration deliberately connected both behaviors to the live R3 calls.\n\nOwnership guard:\n- Environment has no LeaveRoom, TravelIntent, RequestPermanentMigration or ReturnHome intent\n  creation. Task09 remains the sole cross-room owner.\n\n======================================================================\nB4 — Signal detour family retirement\n======================================================================\n\nDesertBatflySignalIntegration.cs [DELETED]\nDesertBatflySignalAcuteBridge.cs [DELETED]\nDesertBatflySignalDirectWitnessBridge.cs [DELETED]\nDesertBatflySignalThreatBridge.cs [DELETED]\nDesertBatflySignalVengeanceBridge.cs [DELETED]\n\nOld responsibility -> direct owner:\n- AI local alarm -> DesertBatflyAI.RaiseLocalAlarm -> SignalRuntime.EmitAlarm.\n- exact alarm escape origin -> DesertBatflyAI.ThreatenedAt.\n- anonymous hazard escape -> SignalRuntime.ApplyAlarm -> ThreatenedAt(origin).\n- receiver-private Task11 caution -> SignalRuntime.ResponseStrength reads only receiver memory.\n- Explosion/Startle/MassCasualty acute roots -> ThreatRuntime -> EmitAcuteAlarm(real position).\n- capture Distress/Alarm -> DB_EventConsumers canonical Capture consumer.\n- HarassSignal target contagion -> DB_CombatRuntime.FindSocialHarassTarget.\n- RoostCall contagion -> DesertBatflySocialLife.FindRoostSource.\n- direct-witness grief gate -> DesertBatflySocialBond.IsDirectDeathWitness.\n- tier-1+/chain legacy persistent fear suppression -> Intimidation.ReceiveFear direct gate.\n- direct witness Alarm -> Intimidation.ReceiveFear -> SignalRuntime.EmitAlarm.\n- Vengeance Rally -> Intimidation.ArmVengeance -> SignalRuntime.EmitRally before supporter scoring.\n\nDeletion effect audit found two semantic drifts and corrected them in B5:\n1. Anonymous Alarm escape threshold\n   - old ThreatBridge: anonymous hazard required response >= 0.34.\n   - first B4 direct implementation accidentally used the ordinary >= 0.30 threshold.\n   - B5 restores explicit 0.30 concrete / 0.34 anonymous thresholds.\n2. Capture event ordering\n   - before B4, DB_EventConsumers handled Intimidation first and SignalIntegration published\n     capture Distress/Alarm afterward because of DB_EventHub subscription order.\n   - first B4 direct fold published Distress/Alarm before Intimidation, which could let the\n     same event's DistressInterest alter initial Vengeance candidate scoring.\n   - B5 restores direct-experience/Intimidation commit first, social signal publication second.\n\n======================================================================\nB5 — RuntimePatch classification\n======================================================================\n\nDesertBatflyRuntimePatch.cs [RETAIN]\nClassification: integration-only infrastructure, NOT an internal behavior bridge.\n\nExact production reflection whitelist under src/Creatures/DesertBatfly:\n1. DesertBatflyRuntimePatch.cs\n   - generic optional Harmony reflection helper.\n2. DesertBatflySandbox.cs\n   - patches CreatureSymbol sprite/color for DesertBatfly sandbox representation.\n3. DesertBatflyWarpCompatibility.cs\n   - optional Warp 1.9.x RoomInfo/category compatibility without a hard dependency.\n\nWhy deletion would be wrong:\n- deleting RuntimePatch would lose Sandbox symbol support and Warp compatibility.\n- neither consumer owns AI, travel, environment, social, signal, threat or locomotion logic.\n- Hooks explicitly enables/disables both integration surfaces at lifecycle boundaries.\n\nFinal code-side repository audit:\n- *Bridge.cs under DesertBatfly: 0.\n- MonoMod.RuntimeDetour imports under DesertBatfly: 0.\n- System.Reflection files under DesertBatfly: exactly the three integration files above.\n- RuntimePatch production users: exactly RuntimePatch itself + Sandbox + WarpCompatibility.\n\n======================================================================\nAcceptance boundary\n======================================================================\n\nR5 is code-side complete only when both permanent guards pass. This does NOT substitute for\nRain World live validation. Final Task14 validation still needs gameplay scenarios covering\nweather survival, Task09 travel/refuge, Signals, Vengeance, Sandbox registration and Warp\ncompatibility when Warp is installed.\n'''
write('docs/Discussion/Task_14_R5_FunctionRetentionLedger.txt', ledger)

# Update R5 status rather than leaving B4 as the apparent endpoint.
status_path = 'docs/Discussion/Task_14_R5_BridgeDebtStatus.txt'
status = read(status_path)
status = status.replace('Revision: R5-B4 / 2026-09-07', 'Revision: R5-B5 / 2026-09-07', 1)
status = status.replace('Status: 【R5 进行中 / B4 Signal detour family removed】',
                        'Status: 【R5 代码侧完成 / Bridge debt closed，待实机总验收】', 1)
if '8. R5-B5 final audit and retention closure' not in status:
    status += '''\n\n======================================================================\n8. R5-B5 final audit and retention closure\n======================================================================\n\n- B1-B4 deleted-file behavior was re-audited against current live owners, not only against\n  file/type absence. Permanent mapping is recorded in Task_14_R5_FunctionRetentionLedger.txt.\n- Two subtle B4 semantic drifts were found and corrected: anonymous Alarm Escape restores\n  the historical 0.34 threshold (concrete threat remains 0.30), and Capture restores the\n  historical ordering where direct Intimidation experience commits before Distress/Alarm\n  social publication.\n- DesertBatflyRuntimePatch is RETAINED. It is integration-only infrastructure required by\n  DesertBatflySandbox symbol patches and optional Warp compatibility. Deleting it would be\n  a functional regression, not debt cleanup.\n- Final source audit: zero *Bridge.cs files, zero MonoMod.RuntimeDetour imports, and exactly\n  three Reflection files: RuntimePatch, Sandbox and WarpCompatibility.\n- Added permanent managed retention tests plus scripts/check-desertbatfly-r5-retention.sh.\n\nR5 internal bridge/detour debt is code-side closed. Rain World live validation remains\ndeferred to the final Task14 validation stage; no live-play pass is claimed here.\n'''
write(status_path, status)

# Remove temporary B5 scaffolding from the final commit. The permanent audit script/test stay.
for obsolete in [
    '.github/workflows/task14-r5-b5-audit.yml',
    '.github/workflows/task14-r5-b5-finalize.yml',
    'scripts/task14_r5_b5_finalize.py',
    'scripts/.task14-r5-b5-trigger'
]:
    p = ROOT / obsolete
    if p.exists():
        p.unlink()
