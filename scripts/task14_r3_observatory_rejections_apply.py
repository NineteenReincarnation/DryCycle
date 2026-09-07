from pathlib import Path

SOURCE = Path('src/Debug/AIDebugger/Sources/DesertBatflyDebugSource.cs')
LOC = Path('src/Debug/AIDebugger/AIDebugLocalization.cs')
TEST = Path('tests/DesertBatfly/Program.Task14R3.cs')
STATUS = Path('docs/Discussion/Task_14_R3_FrameArbiterStatus.txt')
OBSOLETE_SCRIPT = Path('scripts/task14_r3_core_ai_owner_apply.py')
OBSOLETE_WORKFLOW = Path('.github/workflows/task14-r3-core-ai-owner-one-shot.yml')
SELF = Path('scripts/task14_r3_observatory_rejections_apply.py')
SELF_WORKFLOW = Path('.github/workflows/task14-r3-observatory-rejections-one-shot.yml')


def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise SystemExit(f'{label}: expected 1 occurrence, found {count}')
    return text.replace(old, new, 1)

src = SOURCE.read_text(encoding='utf-8')
src = replace_once(src,
'''        BuildDecisionStack(snapshot, bat);\n        return snapshot;''',
'''        BuildArbiterSection(snapshot, bat);\n        BuildDecisionStack(snapshot, bat);\n        return snapshot;''',
'Capture arbiter section call')

insert_anchor = '''    private static void BuildDecisionStack(AIDebugSnapshot snapshot, DesertBatfly bat)\n    {'''
method = r'''    private static void BuildArbiterSection(AIDebugSnapshot snapshot, DesertBatfly bat)
    {
        var section = new AIDebugSection("section.arbiter");
        if (bat?.room == null ||
            !DB_BehaviorArbiter.TryGetDebugState(bat, out DB_BehaviorArbiterDebugState debug) ||
            debug.Resolution.Clock != (bat.room.game?.clock ?? int.MinValue))
        {
            section.Add("field.arbiter_owner", "DB_BehaviorArbiter.PrimaryOwner", "R3 / unresolved")
                .Add("field.arbiter_rejected_count", "DB_BehaviorArbiter.Rejected.Count", 0);
            snapshot.Sections.Add(section);
            return;
        }

        DB_BehaviorResolution resolution = debug.Resolution;
        section.Add("field.arbiter_owner", "DB_BehaviorResolution.PrimaryOwner", resolution.PrimaryOwner)
            .Add("field.arbiter_priority", "DB_BehaviorProposal.Priority", resolution.WinningProposal.Priority)
            .Add("field.arbiter_kind", "DB_BehaviorProposal.BehaviorKind", resolution.WinningProposal.BehaviorKind)
            .Add("field.arbiter_reason", "DB_BehaviorResolution.Reason", resolution.Reason)
            .Add("field.arbiter_goal", "DB_BehaviorResolution.FinalGoal",
                resolution.FinalGoal.HasValue ? resolution.FinalGoal.Value.ToString() : "—")
            .Add("field.arbiter_native_behavior", "DB_BehaviorResolution.FinalNativeBehavior",
                resolution.FinalNativeBehavior?.ToString() ?? "—")
            .Add("field.arbiter_special_physics", "DB_BehaviorResolution.SpecialPhysicsOwner",
                resolution.SpecialPhysicsOwner)
            .Add("field.arbiter_rejected_count", "DB_BehaviorArbiterDebugState.Rejected.Length",
                debug.Rejected?.Length ?? 0);

        if (debug.Rejected != null)
        {
            for (int i = 0; i < debug.Rejected.Length; i++)
            {
                DB_BehaviorRejection rejected = debug.Rejected[i];
                section.Add(
                    "field.arbiter_rejected",
                    $"DB_BehaviorArbiter.Rejected[{i}]",
                    $"{rejected.Owner} / P{rejected.Priority} / {rejected.Reason}");
            }
        }

        snapshot.Sections.Add(section);
    }

'''
src = replace_once(src, insert_anchor, method + insert_anchor, 'BuildArbiterSection insertion')
SOURCE.write_text(src, encoding='utf-8')

loc = LOC.read_text(encoding='utf-8')
loc = replace_once(loc,
'''        ["section.movement"] = "Movement",\n        ["section.generic_ai"] = "Vanilla AI",''',
'''        ["section.movement"] = "Movement",\n        ["section.arbiter"] = "R3 Arbiter",\n        ["section.generic_ai"] = "Vanilla AI",''',
'English section localization')
loc = replace_once(loc,
'''        ["field.roost_ratio"] = "Roost ratio",\n\n        ["decision.availability"]''',
'''        ["field.roost_ratio"] = "Roost ratio",\n        ["field.arbiter_owner"] = "Primary owner",\n        ["field.arbiter_priority"] = "Winning priority",\n        ["field.arbiter_kind"] = "Behavior kind",\n        ["field.arbiter_reason"] = "Winner reason",\n        ["field.arbiter_goal"] = "Final goal",\n        ["field.arbiter_native_behavior"] = "Final native behavior",\n        ["field.arbiter_special_physics"] = "Special physics owner",\n        ["field.arbiter_rejected_count"] = "Rejected proposals",\n        ["field.arbiter_rejected"] = "Rejected",\n\n        ["decision.availability"]''',
'English field localization')
loc = replace_once(loc,
'''        ["section.movement"] = "运动",\n        ["section.generic_ai"] = "原版 AI",''',
'''        ["section.movement"] = "运动",\n        ["section.arbiter"] = "R3 仲裁器",\n        ["section.generic_ai"] = "原版 AI",''',
'Chinese section localization')
loc = replace_once(loc,
'''        ["field.roost_ratio"] = "栖息比例",\n\n        ["decision.availability"]''',
'''        ["field.roost_ratio"] = "栖息比例",\n        ["field.arbiter_owner"] = "主控制权",\n        ["field.arbiter_priority"] = "获胜优先级",\n        ["field.arbiter_kind"] = "行为类型",\n        ["field.arbiter_reason"] = "获胜原因",\n        ["field.arbiter_goal"] = "最终目标",\n        ["field.arbiter_native_behavior"] = "最终原生行为",\n        ["field.arbiter_special_physics"] = "特殊物理控制权",\n        ["field.arbiter_rejected_count"] = "被拒提案数",\n        ["field.arbiter_rejected"] = "被拒提案",\n\n        ["decision.availability"]''',
'Chinese field localization')
LOC.write_text(loc, encoding='utf-8')

test = TEST.read_text(encoding='utf-8')
test = replace_once(test,
'''        Check(MethodCallOffset(observatory.GetMethod("ControlOwner", Flags), arbiter, "TryGetResolution") >= 0,\n            "Task14 R3 Observatory ControlOwner reads the actual arbiter resolution instead of post-hoc guessing");''',
'''        Check(MethodCallOffset(observatory.GetMethod("ControlOwner", Flags), arbiter, "TryGetResolution") >= 0,\n            "Task14 R3 Observatory ControlOwner reads the actual arbiter resolution instead of post-hoc guessing");\n        Check(observatory.GetMethod("BuildArbiterSection", Flags) != null &&\n              MethodCallOffset(observatory.GetMethod("BuildArbiterSection", Flags), arbiter, "TryGetDebugState") >= 0,\n            "Task14 R3 Observatory formally presents winner plus rejected arbiter proposals");''',
'Observatory regression')
test = test.replace(
'"Task14 R3: ordinary locomotion domains are owner-gated; Rain World live validation and rejected-proposal Observatory presentation remain."',
'"Task14 R3: code-side owner arbitration and rejected-proposal Observatory presentation are closed; Rain World live validation remains."')
TEST.write_text(test, encoding='utf-8')

status = STATUS.read_text(encoding='utf-8')
status = status.replace(
'''R3-OPEN-07 — Observatory rejected proposal presentation\n------------------------------------------------------\n\nArbiter runtime 已记录 rejected proposals + reasons。\n当前通用 ControlOwner 已读真实 winner；后续再把 rejected proposal stack 正式显示到 Observatory。''',
'''R3-CLOSED-07 — Observatory rejected proposal presentation（代码侧已收敛）\n--------------------------------------------------------------------------\n\nArbiter runtime 的 rejected proposals + reasons 已正式进入 AI Observatory：\n\n- 新增 `R3 Arbiter / R3 仲裁器` Inspector 区块；\n- 显示真实 PrimaryOwner、winning priority、BehaviorKind、winner reason；\n- 显示 FinalGoal、FinalNativeBehavior、SpecialPhysicsOwner；\n- 显示 rejected proposal 数量；\n- 对每条 rejected proposal 显示 Owner / Priority / Reason；\n- 数据来自同 tick `DB_BehaviorArbiter.TryGetDebugState`，不做 post-hoc 猜测。\n\n因此 R3 代码侧 Observatory owner/rejection 可观测性已闭合。''')
status = status.replace(
'''【R3 代码侧单 owner 已收敛：ImmediateDanger / InjuryRecovery / Travel / HardSurvival / Fear / Vengeance / Environment / ProjectileEvade / Combat / Roost / Social / Ordinary 均通过 Arbiter 进入。】\n【R3 尚不标记最终完成：仍需 Observatory rejected proposal 正式展示，以及 Rain World live scenarios 验收。】''',
'''【R3 代码侧已收敛：所有 ordinary locomotion domain 均通过 Arbiter，Observatory 已正式显示 winner + rejected proposal stack。】\n【R3 尚不标记最终完成：只剩 Rain World live scenarios / managed integration 的实机验收。】''')
STATUS.write_text(status, encoding='utf-8')

for p in (OBSOLETE_SCRIPT, OBSOLETE_WORKFLOW, SELF, SELF_WORKFLOW):
    if p.exists():
        p.unlink()
