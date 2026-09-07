from pathlib import Path

ROOT = Path('.')


def read(path):
    return (ROOT / path).read_text(encoding='utf-8')


def write(path, text):
    p = ROOT / path
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(text, encoding='utf-8')


def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise SystemExit(f'{label}: expected 1 match, found {count}')
    return text.replace(old, new, 1)


# 1) Lifecycle: remove bridges whose responsibilities now have direct R3/R4 paths.
hooks_path = 'src/Creatures/DesertBatfly/DesertBatflyHooks.cs'
hooks = read(hooks_path)
hooks = replace_once(hooks, '        DesertBatflyEnvironmentalDenseFogBridge.Enable();\n', '', 'remove DenseFog enable')
hooks = replace_once(hooks, '        DesertBatflyThreatVengeanceBridge.Enable();\n', '', 'remove ThreatVengeance enable')
hooks = replace_once(hooks, '        DesertBatflyThreatVengeanceBridge.Disable();\n', '', 'remove ThreatVengeance disable')
hooks = replace_once(hooks, '        DesertBatflyEnvironmentalDenseFogBridge.Disable();\n', '', 'remove DenseFog disable')
write(hooks_path, hooks)

integration_path = 'src/Creatures/DesertBatfly/Environmental/DesertBatflyEnvironmentalIntegration.cs'
integration = read(integration_path)
integration = replace_once(
    integration,
    '        DesertBatflyEnvironmentalVengeanceBridge.Enable();\n',
    '',
    'remove environmental vengeance enable')
integration = replace_once(
    integration,
    '        DesertBatflyEnvironmentalVengeanceBridge.Disable();\n',
    '',
    'remove environmental vengeance disable')
write(integration_path, integration)

# 2) Replace Threat->Vengeance RuntimeDetour with an explicit tactical modifier call at the
#    authorized Vengeance flight boundary.
intimidation_path = 'src/Creatures/DesertBatfly/DesertBatflyIntimidation.cs'
intimidation = read(intimidation_path)
old_force = '''    private static void ForceFlight(\n        DesertBatfly bat,\n        Vector2 goal,\n        float speed)\n    {\n        if (bat?.room == null ||\n            !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.Vengeance))\n            return;\n\n        Vector2 direction = Custom.DirVec(bat.mainBodyChunk.pos, goal);\n'''
new_force = '''    private static void ForceFlight(\n        DesertBatfly bat,\n        Vector2 goal,\n        float speed)\n    {\n        if (bat?.room == null ||\n            !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.Vengeance))\n            return;\n\n        // R5: Task11 is a tactical modifier, not an internal RuntimeDetour. Vengeance owns\n        // the frame and explicitly asks Threat Signature to refine the already-authorized\n        // goal/speed before the single FlightMotor write boundary.\n        if (TryGetVengeanceTarget(bat, out Creature vengeanceTarget) && vengeanceTarget is Player player)\n            goal = DesertBatflyThreatTactics.AdjustExtremeVengeanceGoal(bat, player, goal, ref speed);\n\n        Vector2 direction = Custom.DirVec(bat.mainBodyChunk.pos, goal);\n'''
intimidation = replace_once(intimidation, old_force, new_force, 'direct Threat Vengeance modifier')
write(intimidation_path, intimidation)

# 3) Delete behavior-neutral / superseded bridge files. No compatibility shells.
for path in [
    'src/Creatures/DesertBatfly/Environmental/DesertBatflyEnvironmentalDenseFogBridge.cs',
    'src/Creatures/DesertBatfly/Environmental/DesertBatflyEnvironmentalVengeanceBridge.cs',
    'src/Creatures/DesertBatfly/ThreatSignature/DesertBatflyThreatVengeanceBridge.cs',
]:
    p = ROOT / path
    if not p.exists():
        raise SystemExit(f'expected bridge file missing before R5 deletion: {path}')
    p.unlink()

# 4) R5 regression guard.
test_path = 'tests/DesertBatfly/Program.Task14R5.cs'
write(test_path, r'''using System;\nusing System.Reflection;\n\ninternal static partial class Program\n{\n    private static void RunTask14R5()\n    {\n        Type hooks = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyHooks", true);\n        Type integration = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalIntegration", true);\n        Type intimidation = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyIntimidation", true);\n        Type tactics = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyThreatTactics", true);\n\n        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalDenseFogBridge", false) == null,\n            "R5 removes behavior-neutral DenseFog RuntimeDetour shim");\n        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalVengeanceBridge", false) == null,\n            "R5 removes hard-survival Vengeance detour; Arbiter owns preemption");\n        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyThreatVengeanceBridge", false) == null,\n            "R5 removes Threat/Vengeance ForceFlight RuntimeDetour");\n\n        MethodInfo forceFlight = intimidation.GetMethod("ForceFlight", Flags);\n        Check(forceFlight != null &&\n              MethodCallOffset(forceFlight, tactics, "AdjustExtremeVengeanceGoal") >= 0,\n            "R5 Vengeance explicitly consumes Threat tactical geometry through formal API");\n\n        Check(MethodCallOffset(hooks.GetMethod("Enable", Flags), intimidation, "Reset") >= 0,\n            "R5 species lifecycle remains wired after deleting obsolete bridges");\n        Check(integration.GetField("steerHook", Flags) != null &&\n              integration.GetField("scanCreaturesHook", Flags) != null,\n            "R5 B1 intentionally leaves EnvironmentalIntegration debt visible for the next migration batch");\n\n        Console.WriteLine("Task14 R5 B1: DenseFog and Vengeance bridge shims are physically removed; Threat Vengeance uses a direct tactical API.");\n    }\n}\n'''.replace('\\n', '\n'))

dispatch_path = 'tests/DesertBatfly/Program.RejectedTask02.cs'
dispatch = read(dispatch_path)
dispatch = replace_once(
    dispatch,
    '        RunTask14R4();\n',
    '        RunTask14R4();\n        RunTask14R5();\n',
    'wire R5 regression dispatcher')
write(dispatch_path, dispatch)

# 5) Status document.
status_path = 'docs/Discussion/Task_14_R5_BridgeDebtStatus.txt'
write(status_path, '''Desert Batfly Task 14 — R5 Internal Bridge / Detour Debt Status\nRevision: R5-B1 / 2026-09-07\nBranch: task14-r5-bridge-debt\nBase: R4 code-side complete at a461631633360fff30c721fb774acec99fc23889\nStatus: 【R5 已正式启动 / B1 obsolete bridge removal implemented】\n\n======================================================================\n1. R5 objective\n======================================================================\n\nR5 removes internal Desert Batfly integration debt that was acceptable while Tasks 09–13\nwere developed independently but is no longer acceptable after R1–R4 established formal\nevents, shared perception, frame ownership and a single flight boundary.\n\nR5 is not R6. It does not perform mass DB_ renames or final directory migration. A bridge\nis deleted only after its unique behavior has a direct formal API/owner path. Empty\ncompatibility shells are forbidden.\n\n======================================================================\n2. B1 closed\n======================================================================\n\nR5-B1-A — DesertBatflyEnvironmentalDenseFogBridge DELETED.\nIt was behavior-neutral; DenseFog ecology already lives in weather ecology / Task13 and\nFog movement uncertainty now lives in DB_FogGoalModifier.\n\nR5-B1-B — DesertBatflyEnvironmentalVengeanceBridge DELETED.\nR3 already freezes Vengeance movement when another owner wins. Fear/trauma state may still\nupdate, while Vengeance phase movement progresses only under PrimaryOwner=Vengeance. A\nRuntimeDetour around Intimidation.Update is therefore both redundant and semantically worse.\n\nR5-B1-C — DesertBatflyThreatVengeanceBridge DELETED.\nDesertBatflyIntimidation.ForceFlight now directly calls\nDesertBatflyThreatTactics.AdjustExtremeVengeanceGoal before DB_FlightMotor.TrySteer. Task11\nremains a tactical modifier and never becomes a locomotion owner.\n\n======================================================================\n3. Remaining R5 debt after B1\n======================================================================\n\nEnvironmental:\n- DesertBatflyEnvironmentalIntegration.cs — MAJOR: Reflection + RuntimeDetour; includes\n  Personality/Roost/Harass/ScanCreatures hooks, temporary Thirst mutation (HB-06), and a\n  legacy Steer fog hook superseded by DB_FogGoalModifier.\n- DesertBatflyEnvironmentalSocialBridge.cs\n- DesertBatflyEnvironmentalSurvivalBridge.cs\n- DesertBatflyEnvironmentalTask09Bridge.cs\n\nSignals:\n- DesertBatflySignalAcuteBridge.cs\n- DesertBatflySignalDirectWitnessBridge.cs\n- DesertBatflySignalThreatBridge.cs\n- DesertBatflySignalVengeanceBridge.cs\n\nOther historical integration:\n- DesertBatflyRuntimePatch.cs — inspect and classify before deletion/retention.\n\n======================================================================\n4. Next batch\n======================================================================\n\nR5-B2 will remove EnvironmentalIntegration detours and EnvironmentalSocialBridge by moving\nweather aggression/Harass weighting, Roost scaling, social scaling/activity radius and\nweather priority suppression into explicit domain APIs. This batch must close HB-06: no\nproduction path may temporarily mutate DesertBatflyState.Thirst merely to influence a\ndecision. Fog SteerHook must also disappear because DB_FogGoalModifier is authoritative.\n\nLive Rain World validation remains deferred to the final Task14 validation stage by project\ndecision.\n''')

# 6) Guard source-level intent before allowing commit.
assert 'DesertBatflyEnvironmentalDenseFogBridge' not in read(hooks_path)
assert 'DesertBatflyThreatVengeanceBridge' not in read(hooks_path)
assert 'DesertBatflyEnvironmentalVengeanceBridge' not in read(integration_path)
assert 'AdjustExtremeVengeanceGoal(bat, player, goal, ref speed)' in read(intimidation_path)
assert not (ROOT / 'src/Creatures/DesertBatfly/Environmental/DesertBatflyEnvironmentalDenseFogBridge.cs').exists()
assert not (ROOT / 'src/Creatures/DesertBatfly/Environmental/DesertBatflyEnvironmentalVengeanceBridge.cs').exists()
assert not (ROOT / 'src/Creatures/DesertBatfly/ThreatSignature/DesertBatflyThreatVengeanceBridge.cs').exists()

# Remove migration machinery from final tree.
this = ROOT / 'scripts/task14_r5_b1_apply.py'
if this.exists(): this.unlink()
wf = ROOT / '.github/workflows/task14-r5-b1.yml'
if wf.exists(): wf.unlink()

print('R5 B1 obsolete bridge removal prepared successfully')
