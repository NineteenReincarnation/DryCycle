from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8")


def write(path, text):
    (ROOT / path).write_text(text, encoding="utf-8", newline="\n")


def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected exactly one match, got {count}")
    return text.replace(old, new, 1)


# -----------------------------------------------------------------------------
# Observatory: expose the Arbiter's real winner and rejected proposal stack.
# This reads debug state only; it must never resolve/re-run behavior from the UI.
# -----------------------------------------------------------------------------
source_path = "src/Debug/AIDebugger/Sources/DesertBatflyDebugSource.cs"
source = read(source_path)
source = replace_once(
    source,
    "        BuildDecisionStack(snapshot, bat);\n        return snapshot;",
    "        BuildArbiterPresentation(snapshot, bat);\n        BuildDecisionStack(snapshot, bat);\n        return snapshot;",
    "Capture arbiter presentation")

marker = "    private static void BuildDecisionStack(AIDebugSnapshot snapshot, DesertBatfly bat)\n"
if source.count(marker) != 1:
    raise RuntimeError("BuildDecisionStack marker mismatch")
method = r'''    private static void BuildArbiterPresentation(AIDebugSnapshot snapshot, DesertBatfly bat)
    {
        var section = new AIDebugSection("section.arbiter");
        int currentClock = bat?.room?.game?.clock ?? int.MinValue;
        if (bat == null ||
            !DB_BehaviorArbiter.TryGetDebugState(bat, out DB_BehaviorArbiterDebugState debug) ||
            !debug.Resolution.Resolved || debug.Resolution.Clock != currentClock)
        {
            section
                .Add("field.arbiter_tick", "DB_BehaviorResolution.Clock", "unresolved")
                .Add("field.arbiter_winner", "DB_BehaviorResolution.PrimaryOwner", "—")
                .Add("field.arbiter_rejected_count", "DB_BehaviorArbiterDebugState.Rejected", 0);
            snapshot.Sections.Add(section);
            snapshot.Decisions.Add(new AIDebugDecisionNode(
                "decision.arbiter",
                AIDebugDecisionState.Warning,
                "current tick has no resolved R3 arbiter snapshot",
                "DB_BehaviorArbiter.TryGetDebugState"));
            return;
        }

        DB_BehaviorResolution resolution = debug.Resolution;
        DB_BehaviorProposal winner = resolution.WinningProposal;
        section
            .Add("field.arbiter_tick", "DB_BehaviorResolution.Clock", resolution.Clock)
            .Add("field.arbiter_winner", "DB_BehaviorResolution.PrimaryOwner", resolution.PrimaryOwner)
            .Add("field.arbiter_behavior", "DB_BehaviorProposal.BehaviorKind", winner.BehaviorKind)
            .Add("field.arbiter_priority", "DB_BehaviorProposal.Priority", winner.Priority)
            .Add("field.arbiter_commitment", "DB_BehaviorProposal.Commitment", winner.Commitment)
            .Add("field.arbiter_reason", "DB_BehaviorResolution.Reason", resolution.Reason)
            .Add("field.arbiter_goal", "DB_BehaviorResolution.FinalGoal",
                resolution.FinalGoal.HasValue ? resolution.FinalGoal.Value.ToString() : "—")
            .Add("field.arbiter_speed", "DB_BehaviorResolution.FinalSpeed", resolution.FinalSpeed)
            .Add("field.arbiter_native_behavior", "DB_BehaviorResolution.FinalNativeBehavior", resolution.FinalNativeBehavior)
            .Add("field.arbiter_dijkstra", "DB_BehaviorResolution.FinalDijkstraMap", resolution.FinalDijkstraMap)
            .Add("field.arbiter_special_physics", "DB_BehaviorResolution.SpecialPhysicsOwner", resolution.SpecialPhysicsOwner)
            .Add("field.arbiter_rejected_count", "DB_BehaviorArbiterDebugState.Rejected", debug.Rejected.Length);

        snapshot.Decisions.Add(new AIDebugDecisionNode(
            "decision.arbiter",
            AIDebugDecisionState.Active,
            $"tick={resolution.Clock}; rejected={debug.Rejected.Length}",
            "DB_BehaviorArbiter"));
        snapshot.Decisions.Add(new AIDebugDecisionNode(
            "decision.arbiter_winner",
            AIDebugDecisionState.Active,
            $"{resolution.PrimaryOwner} / {winner.BehaviorKind} / P{winner.Priority}; {resolution.Reason}",
            "DB_BehaviorResolution.WinningProposal",
            1));

        for (int i = 0; i < debug.Rejected.Length; i++)
        {
            DB_BehaviorRejection rejected = debug.Rejected[i];
            string value = $"{rejected.Owner} / P{rejected.Priority}: {rejected.Reason}";
            section.Add(
                "field.arbiter_rejected",
                $"DB_BehaviorArbiterDebugState.Rejected[{i}]",
                value);
            snapshot.Decisions.Add(new AIDebugDecisionNode(
                "decision.arbiter_rejected",
                AIDebugDecisionState.Blocked,
                value,
                $"DB_BehaviorArbiterDebugState.Rejected[{i}]",
                1));
        }

        snapshot.Sections.Add(section);
    }

'''
source = source.replace(marker, method + marker, 1)
write(source_path, source)


# -----------------------------------------------------------------------------
# Localization for the new Arbiter inspector/decision fields.
# -----------------------------------------------------------------------------
loc_path = "src/Debug/AIDebugger/AIDebugLocalization.cs"
loc = read(loc_path)
loc = replace_once(
    loc,
    '        ["section.generic_ai"] = "Vanilla AI",\n',
    '        ["section.generic_ai"] = "Vanilla AI",\n'
    '        ["section.arbiter"] = "R3 Behavior Arbiter",\n',
    "English arbiter section")
loc = replace_once(
    loc,
    '        ["field.roost_ratio"] = "Roost ratio",\n',
    '        ["field.roost_ratio"] = "Roost ratio",\n'
    '        ["field.arbiter_tick"] = "Arbiter tick",\n'
    '        ["field.arbiter_winner"] = "Winning owner",\n'
    '        ["field.arbiter_behavior"] = "Winning behavior",\n'
    '        ["field.arbiter_priority"] = "Priority",\n'
    '        ["field.arbiter_commitment"] = "Commitment",\n'
    '        ["field.arbiter_reason"] = "Winner reason",\n'
    '        ["field.arbiter_goal"] = "Resolved goal",\n'
    '        ["field.arbiter_speed"] = "Nominal speed",\n'
    '        ["field.arbiter_native_behavior"] = "Resolved native behavior",\n'
    '        ["field.arbiter_dijkstra"] = "Resolved Dijkstra map",\n'
    '        ["field.arbiter_special_physics"] = "Special physics owner",\n'
    '        ["field.arbiter_rejected_count"] = "Rejected proposals",\n'
    '        ["field.arbiter_rejected"] = "Rejected proposal",\n',
    "English arbiter fields")
loc = replace_once(
    loc,
    '        ["decision.roost"] = "Roost / chain",\n',
    '        ["decision.roost"] = "Roost / chain",\n'
    '        ["decision.arbiter"] = "R3 behavior arbitration",\n'
    '        ["decision.arbiter_winner"] = "Winning proposal",\n'
    '        ["decision.arbiter_rejected"] = "Rejected proposal",\n',
    "English arbiter decisions")

loc = replace_once(
    loc,
    '        ["section.generic_ai"] = "原版 AI",\n',
    '        ["section.generic_ai"] = "原版 AI",\n'
    '        ["section.arbiter"] = "R3 行为仲裁器",\n',
    "Chinese arbiter section")
loc = replace_once(
    loc,
    '        ["field.roost_ratio"] = "栖息比例",\n',
    '        ["field.roost_ratio"] = "栖息比例",\n'
    '        ["field.arbiter_tick"] = "仲裁 Tick",\n'
    '        ["field.arbiter_winner"] = "获胜控制者",\n'
    '        ["field.arbiter_behavior"] = "获胜行为",\n'
    '        ["field.arbiter_priority"] = "优先级",\n'
    '        ["field.arbiter_commitment"] = "承诺度",\n'
    '        ["field.arbiter_reason"] = "获胜原因",\n'
    '        ["field.arbiter_goal"] = "最终目标",\n'
    '        ["field.arbiter_speed"] = "名义速度",\n'
    '        ["field.arbiter_native_behavior"] = "最终原版行为",\n'
    '        ["field.arbiter_dijkstra"] = "最终 Dijkstra 图",\n'
    '        ["field.arbiter_special_physics"] = "特殊物理控制者",\n'
    '        ["field.arbiter_rejected_count"] = "被拒提案数",\n'
    '        ["field.arbiter_rejected"] = "被拒提案",\n',
    "Chinese arbiter fields")
loc = replace_once(
    loc,
    '        ["decision.roost"] = "栖息 / 倒挂链",\n',
    '        ["decision.roost"] = "栖息 / 倒挂链",\n'
    '        ["decision.arbiter"] = "R3 行为仲裁",\n'
    '        ["decision.arbiter_winner"] = "获胜提案",\n'
    '        ["decision.arbiter_rejected"] = "被拒提案",\n',
    "Chinese arbiter decisions")
write(loc_path, loc)


# -----------------------------------------------------------------------------
# Architecture regression: Observatory must consume Arbiter debug state and expose rejected.
# -----------------------------------------------------------------------------
test_path = "tests/DesertBatfly/Program.Task14R3.cs"
test = read(test_path)
old = '''        Type observatory = mod.GetType("DryCycle.Debugging.AI.DesertBatflyDebugSource", true);\n        Check(MethodCallOffset(observatory.GetMethod("ControlOwner", Flags), arbiter, "TryGetResolution") >= 0,\n            "Task14 R3 Observatory ControlOwner reads the actual arbiter resolution instead of post-hoc guessing");'''
new = '''        Type observatory = mod.GetType("DryCycle.Debugging.AI.DesertBatflyDebugSource", true);\n        MethodInfo arbiterPresentation = observatory.GetMethod("BuildArbiterPresentation", Flags);\n        Check(MethodCallOffset(observatory.GetMethod("ControlOwner", Flags), arbiter, "TryGetResolution") >= 0,\n            "Task14 R3 Observatory ControlOwner reads the actual arbiter resolution instead of post-hoc guessing");\n        Check(arbiterPresentation != null &&\n              MethodCallOffset(arbiterPresentation, arbiter, "TryGetDebugState") >= 0 &&\n              MethodCallOffset(observatory.GetMethod("Capture", Flags), observatory, "BuildArbiterPresentation") >= 0,\n            "Task14 R3 Observatory presents actual winner plus rejected proposals from Arbiter debug state");'''
test = replace_once(test, old, new, "Observatory R3 test")
test = replace_once(
    test,
    '            "Task14 R3: ordinary locomotion domains are owner-gated; Rain World live validation and rejected-proposal Observatory presentation remain.");',
    '            "Task14 R3: code-side single-owner and Observatory rejected-proposal presentation are closed; managed compile/live validation remain.");',
    "R3 test final message")
write(test_path, test)


# -----------------------------------------------------------------------------
# Status document: close R3-07; keep final R3 pending compile/live acceptance.
# -----------------------------------------------------------------------------
doc_path = "docs/Discussion/Task_14_R3_FrameArbiterStatus.txt"
doc = read(doc_path)
old_open = '''R3-OPEN-07 — Observatory rejected proposal presentation\n------------------------------------------------------\n\nArbiter runtime 已记录 rejected proposals + reasons。\n当前通用 ControlOwner 已读真实 winner；后续再把 rejected proposal stack 正式显示到 Observatory。'''
new_closed = '''R3-CLOSED-07 — Observatory rejected proposal presentation（代码侧已收敛）\n--------------------------------------------------------------------------\n\n`DesertBatflyDebugSource` 现在直接读取 `DB_BehaviorArbiter.TryGetDebugState`，并在 Observatory 中展示：\n\n- Arbiter tick；\n- PrimaryOwner / BehaviorKind；\n- winner priority / commitment / reason；\n- final goal / nominal speed / native behavior / Dijkstra map；\n- SpecialPhysicsOwner；\n- rejected proposal count；\n- 每个 rejected owner / priority / explicit rejection reason。\n\nDecision Stack 顶部同时加入真实 Arbiter winner 与 rejected nodes；原有 injury/fear/vengeance 等 gameplay 诊断节点继续保留，但不再承担 ownership 推断。\n\nDebug capture 只读现有同 tick Arbiter snapshot；不会为了 UI 重新 ResolveFrame，也不会改变 locomotion。'''
doc = replace_once(doc, old_open, new_closed, "close R3-07")
doc = replace_once(
    doc,
    '【R3 尚不标记最终完成：仍需 Observatory rejected proposal 正式展示，以及 Rain World live scenarios 验收。】',
    '【R3 代码侧功能项已全部收敛；尚不标记最终完成，因为 managed regression 仍需在具备 Rain World assemblies 的开发机编译执行，并完成 Rain World live scenarios 验收。】',
    "R3 final pending status")
write(doc_path, doc)


# Self-delete one-shot artifacts.
for transient in [
    ROOT / "scripts/task14_r3_observatory_apply.py",
    ROOT / ".github/workflows/task14-r3-observatory-one-shot.yml",
    ROOT / "scripts/task14_r3_observatory_trigger.txt",
]:
    if transient.exists():
        transient.unlink()
