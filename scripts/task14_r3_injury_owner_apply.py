from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected exactly one match, got {count}")
    return text.replace(old, new, 1)

root = Path('.')
ai_path = root / 'src/Creatures/DesertBatfly/DesertBatflyAI.cs'
hooks_path = root / 'src/Creatures/DesertBatfly/DesertBatflyHooks.cs'
test_path = root / 'tests/DesertBatfly/Program.Task14R3.cs'
status_path = root / 'docs/Discussion/Task_14_R3_FrameArbiterStatus.txt'

ai = ai_path.read_text(encoding='utf-8')
ai = replace_once(
    ai,
'''    internal bool TryInjuryRecovery()\n    {\n        DesertBatflyInjury injury = fly.Injury;\n        if (!injury.IsSeverelyInjured || fly.dead || !fly.Consious || fly.room == null ||\n            RestrainedByNonFly() || fly.inShortcut || fly.Emergence?.Active == true || HasImmediateDanger)\n        {\n            ClearRecoveryNavigation();\n            injury.SetRecovery(InjuryRecoveryState.None, null, "not severe or immediate survival priority");\n            if (Mode == Activity.InjuryRecovery) SetMode(Activity.Flight);\n            return false;\n        }\n\n        CancelPhysicalAttack();\n        SetMode(Activity.InjuryRecovery);\n''',
'''    internal bool ExecuteInjuryRecoveryOwned()\n    {\n        DesertBatflyInjury injury = fly.Injury;\n        if (!DB_BehaviorArbiter.IsPrimaryOwner(fly, DB_BehaviorOwner.InjuryRecovery))\n            return false;\n\n        // Recovery-state cleanup is allowed here only when the physical injury itself\n        // dropped below the severe threshold. Higher-priority preemption (danger,\n        // restraint, shortcut, native special state) must not erase recovery intent.\n        if (!injury.IsSeverelyInjured)\n        {\n            ClearRecoveryNavigation();\n            injury.SetRecovery(InjuryRecoveryState.None, null, "recovered below severe threshold");\n            if (Mode == Activity.InjuryRecovery) SetMode(Activity.Flight);\n            return false;\n        }\n\n        if (fly.dead || !fly.Consious || fly.room == null || RestrainedByNonFly() ||\n            fly.inShortcut || fly.Emergence?.Active == true || HasImmediateDanger)\n            return false;\n\n        CancelPhysicalAttack();\n        SetMode(Activity.InjuryRecovery);\n''',
    'rename/guard injury executor')
ai = replace_once(
    ai,
'''        if (Mode == Activity.Escape) SetMode(Activity.Flight);\n        if (TryInjuryRecovery()) return;\n        if (fly.Injury.BlocksCombat)\n''',
'''        if (Mode == Activity.Escape) SetMode(Activity.Flight);\n        // R3: severe InjuryRecovery movement is executed only by\n        // DB_InjuryRecoveryExecutor after central owner selection.\n        if (fly.Injury.BlocksCombat)\n''',
    'remove inline injury execution')
ai_path.write_text(ai, encoding='utf-8', newline='\n')

hooks = hooks_path.read_text(encoding='utf-8')
hooks = replace_once(
    hooks,
'''        DB_BehaviorResolution ownership = DB_BehaviorArbiter.ResolveFrame(desert);\n        if (ownership.PrimaryOwner == DB_BehaviorOwner.Travel)\n''',
'''        DB_BehaviorResolution ownership = DB_BehaviorArbiter.ResolveFrame(desert);\n        if (ownership.PrimaryOwner == DB_BehaviorOwner.InjuryRecovery)\n        {\n            if (DB_InjuryRecoveryExecutor.TryExecute(desert, ownership))\n            {\n                if (AIDebugTrace.IsWatched(desert.abstractCreature))\n                    AIDebugTrace.RecordChange(\n                        desert.abstractCreature,\n                        AIDebugEventCategory.Decision,\n                        "PrimaryOwner",\n                        ownership.PrimaryOwner,\n                        ownership.Reason);\n                DesertBatflySocialLife.SampleTrace(desert);\n                DesertBatflyDebugTrace.Sample(desert);\n                return;\n            }\n\n            // The physical injury can cross the severe threshold between snapshot and\n            // execution. Clear only the stale recovery state, then allow lower owners\n            // (including a preserved Travel intent) to compete again this same frame.\n            ownership = DB_BehaviorArbiter.ResolveFrame(\n                desert,\n                DB_BehaviorOwner.InjuryRecovery,\n                "InjuryRecovery executor yielded after current physical recheck");\n        }\n\n        if (ownership.PrimaryOwner == DB_BehaviorOwner.Travel)\n''',
    'insert injury owner execution before travel')
hooks = replace_once(
    hooks,
'''        // R3 foundation currently centralizes ownership classification and Travel execution.\n        // Injury/Vengeance/Environment/Social are still being migrated behind proposals;\n        // keep their existing execution order until each domain has a dedicated executor.\n''',
'''        // R3 currently has dedicated owner-gated executors for InjuryRecovery and Travel.\n        // Vengeance/Environment/Social are still being migrated behind proposals; keep\n        // their legacy execution order only when neither migrated owner won this frame.\n''',
    'update R3 pipeline comment')
hooks_path.write_text(hooks, encoding='utf-8', newline='\n')

test = test_path.read_text(encoding='utf-8')
test = replace_once(
    test,
'''        Type arbiter = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_BehaviorArbiter", true);\n''',
'''        Type arbiter = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_BehaviorArbiter", true);\n        Type injuryExecutor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_InjuryRecoveryExecutor", true);\n        Type desertAI = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyAI", true);\n''',
    'declare injury executor test types')
test = replace_once(
    test,
'''        Check(MethodCallOffset(updateAI, arbiter, "ResolveFrame") >= 0 &&\n              MethodCallOffset(updateAI, travel, "TryDriveRealized") >= 0,\n            "Task14 R3 realized Travel is now entered through the central owner resolution path");\n''',
'''        Check(MethodCallOffset(updateAI, arbiter, "ResolveFrame") >= 0 &&\n              MethodCallOffset(updateAI, injuryExecutor, "TryExecute") >= 0 &&\n              MethodCallOffset(updateAI, travel, "TryDriveRealized") >= 0,\n            "Task14 R3 InjuryRecovery and Travel executors are entered through central owner resolution");\n        Check(injuryExecutor.GetMethod("TryExecute", Flags) != null &&\n              desertAI.GetMethod("ExecuteInjuryRecoveryOwned", Flags) != null &&\n              desertAI.GetMethod("TryInjuryRecovery", Flags) == null,\n            "Task14 R3 severe injury movement has one explicit owner-gated executor surface");\n        Check(MethodCallOffset(desertAI.GetMethod("ExecuteInjuryRecoveryOwned", Flags), arbiter, "IsPrimaryOwner") >= 0,\n            "Task14 R3 InjuryRecovery executor refuses movement without same-tick PrimaryOwner");\n''',
    'add injury owner regression guards')
test = replace_once(
    test,
'''            "Task14 R3 foundation: FrameContext, one-winner priority model, explicit special physics, Travel preflight, Vengeance API and Observatory owner wiring verified. Ordinary single-writer migration remains a later R3 gate.");\n''',
'''            "Task14 R3: FrameContext/Arbiter foundation plus owner-gated InjuryRecovery and Travel execution verified. Vengeance/Environment/Social single-writer migration remains open.");\n''',
    'update task14 r3 console summary')
test_path.write_text(test, encoding='utf-8', newline='\n')

status = status_path.read_text(encoding='utf-8')
status = replace_once(
    status,
'''R3-OPEN-02 — Injury proposal executor\n-------------------------------------\n\n目前 Arbiter 已能识别 Severe Injury / InjuryRecovery 高于 Travel，\n但现有 InjuryRecovery movement 仍在 DesertBatflyAI.Update 内执行。\n\n后续要把该 executor 独立出来，并验证：\n\n- severe Injury beats current Travel；\n- TravelIntent 仍保留，不被清除；\n- recovery 结束后 Travel 可恢复。\n''',
'''R3-CLOSED-02 — Injury proposal executor（代码侧已收敛）\n--------------------------------------------------------\n\n已新增：\n\nsrc/Creatures/DesertBatfly/Runtime/DB_InjuryRecoveryExecutor.cs\n\n当前执行顺序：\n\nFrameContext -> Arbiter -> PrimaryOwner=InjuryRecovery -> DB_InjuryRecoveryExecutor\n\n并完成：\n\n- 旧 DesertBatflyAI.TryInjuryRecovery 入口删除；\n- DesertBatflyAI.Update 不再自行启动 severe InjuryRecovery movement；\n- ExecuteInjuryRecoveryOwned 必须验证同 tick PrimaryOwner；\n- InjuryRecovery 在 Travel 之前执行；\n- Injury executor 因伤势刚降出 severe 阈值而 yield 时，同帧排除 Injury 后重新仲裁；\n- 该清理过程不删除 Task09 TravelIntent，因此 lower-priority Travel 可恢复竞争。\n\n仍需实机验证严重受伤 -> 恢复 -> Travel 恢复的完整行为链。\n''',
    'close R3 injury open item')
status = replace_once(
    status,
'''【R3 foundation 已建立；Travel 已成为第一个真正经 Arbiter 获得 owner 后执行的 domain。】\n【R3 尚未完成；下一阶段是拆 DesertBatflyAI / Injury / Environment / Social 的 decision 与 executor。】\n''',
'''【R3 foundation 已建立；InjuryRecovery 与 Travel 已经必须先获得 Arbiter PrimaryOwner 才执行。】\n【R3 尚未完成；下一阶段继续拆 Vengeance / Environment / Social，并最终消除 ordinary localGoal 多 writer。】\n''',
    'update R3 status conclusion')
status_path.write_text(status, encoding='utf-8', newline='\n')

# Remove one-shot scaffolding from final tree.
Path('scripts/task14_r3_injury_owner_apply.py').unlink()
Path('.github/workflows/task14-r3-injury-owner-one-shot.yml').unlink()
