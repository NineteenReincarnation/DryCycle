from pathlib import Path


def once(text, old, new, label):
    n = text.count(old)
    if n != 1:
        raise RuntimeError(f'{label}: expected 1 match, got {n}')
    return text.replace(old, new, 1)

ai_path = Path('src/Creatures/DesertBatfly/DesertBatflyAI.cs')
hooks_path = Path('src/Creatures/DesertBatfly/DesertBatflyHooks.cs')
test_path = Path('tests/DesertBatfly/Program.Task14R3.cs')
status_path = Path('docs/Discussion/Task_14_R3_FrameArbiterStatus.txt')

ai = ai_path.read_text(encoding='utf-8')
ai = once(ai,
'''    internal bool TryInjuryRecovery()\n    {\n        DesertBatflyInjury injury = fly.Injury;\n        if (!injury.IsSeverelyInjured || fly.dead || !fly.Consious || fly.room == null ||\n            RestrainedByNonFly() || fly.inShortcut || fly.Emergence?.Active == true || HasImmediateDanger)\n        {\n            ClearRecoveryNavigation();\n            injury.SetRecovery(InjuryRecoveryState.None, null, "not severe or immediate survival priority");\n            if (Mode == Activity.InjuryRecovery) SetMode(Activity.Flight);\n            return false;\n        }\n\n        CancelPhysicalAttack();\n        SetMode(Activity.InjuryRecovery);\n''',
'''    internal bool ExecuteInjuryRecoveryOwned()\n    {\n        DesertBatflyInjury injury = fly.Injury;\n        if (!DB_BehaviorArbiter.IsPrimaryOwner(fly, DB_BehaviorOwner.InjuryRecovery))\n            return false;\n\n        if (!injury.IsSeverelyInjured)\n        {\n            ClearRecoveryNavigation();\n            injury.SetRecovery(InjuryRecoveryState.None, null, "recovered below severe threshold");\n            if (Mode == Activity.InjuryRecovery) SetMode(Activity.Flight);\n            return false;\n        }\n\n        // Higher-priority preemption must not erase recovery state or Task09 intent.\n        if (fly.dead || !fly.Consious || fly.room == null || RestrainedByNonFly() ||\n            fly.inShortcut || fly.Emergence?.Active == true || HasImmediateDanger)\n            return false;\n\n        CancelPhysicalAttack();\n        SetMode(Activity.InjuryRecovery);\n''', 'injury executor')
ai = once(ai,
'''        if (Mode == Activity.Escape) SetMode(Activity.Flight);\n        if (TryInjuryRecovery()) return;\n        if (fly.Injury.BlocksCombat)\n''',
'''        if (Mode == Activity.Escape) SetMode(Activity.Flight);\n        // R3: severe InjuryRecovery movement is owner-gated outside this legacy pipeline.\n        if (fly.Injury.BlocksCombat)\n''', 'remove inline injury')
ai_path.write_text(ai, encoding='utf-8', newline='\n')

hooks = hooks_path.read_text(encoding='utf-8')
hooks = once(hooks,
'''        DesertBatflyEnvironmentalIntegration.Register(desert);\n\n        DB_BehaviorResolution ownership = DB_BehaviorArbiter.ResolveFrame(desert);\n        if (ownership.PrimaryOwner == DB_BehaviorOwner.Travel)\n''',
'''        DesertBatflyEnvironmentalIntegration.Register(desert);\n\n        DB_BehaviorResolution ownership = DB_BehaviorArbiter.ResolveFrame(desert);\n        if (ownership.PrimaryOwner == DB_BehaviorOwner.InjuryRecovery)\n        {\n            if (DB_InjuryRecoveryExecutor.TryExecute(desert, ownership))\n            {\n                if (AIDebugTrace.IsWatched(desert.abstractCreature))\n                    AIDebugTrace.RecordChange(desert.abstractCreature, AIDebugEventCategory.Decision,\n                        "PrimaryOwner", ownership.PrimaryOwner, ownership.Reason);\n                DesertBatflySocialLife.SampleTrace(desert);\n                DesertBatflyDebugTrace.Sample(desert);\n                return;\n            }\n\n            ownership = DB_BehaviorArbiter.ResolveFrame(\n                desert, DB_BehaviorOwner.InjuryRecovery,\n                "InjuryRecovery executor yielded after current physical recheck");\n        }\n\n        if (ownership.PrimaryOwner == DB_BehaviorOwner.Travel)\n''', 'UpdateAI injury before travel')
hooks = once(hooks,
'''        // R3 foundation currently centralizes ownership classification and Travel execution.\n        // Injury/Vengeance/Environment/Social are still being migrated behind proposals;\n        // keep their existing execution order until each domain has a dedicated executor.\n''',
'''        // R3 has dedicated owner-gated executors for InjuryRecovery and Travel.\n        // Vengeance/Environment/Social remain in the legacy pipeline until migrated.\n''', 'pipeline comment')
hooks_path.write_text(hooks, encoding='utf-8', newline='\n')

test = test_path.read_text(encoding='utf-8')
test = once(test,
'''        Type arbiter = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_BehaviorArbiter", true);\n''',
'''        Type arbiter = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_BehaviorArbiter", true);\n        Type injuryExecutor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_InjuryRecoveryExecutor", true);\n        Type desertAI = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyAI", true);\n''', 'test types')
test = once(test,
'''        Check(MethodCallOffset(updateAI, arbiter, "ResolveFrame") >= 0 &&\n              MethodCallOffset(updateAI, travel, "TryDriveRealized") >= 0,\n            "Task14 R3 realized Travel is now entered through the central owner resolution path");\n''',
'''        Check(MethodCallOffset(updateAI, arbiter, "ResolveFrame") >= 0 &&\n              MethodCallOffset(updateAI, injuryExecutor, "TryExecute") >= 0 &&\n              MethodCallOffset(updateAI, travel, "TryDriveRealized") >= 0,\n            "Task14 R3 InjuryRecovery and Travel enter through central owner resolution");\n        Check(injuryExecutor.GetMethod("TryExecute", Flags) != null &&\n              desertAI.GetMethod("ExecuteInjuryRecoveryOwned", Flags) != null &&\n              desertAI.GetMethod("TryInjuryRecovery", Flags) == null,\n            "Task14 R3 severe injury movement has one explicit owner-gated executor surface");\n        Check(MethodCallOffset(desertAI.GetMethod("ExecuteInjuryRecoveryOwned", Flags), arbiter, "IsPrimaryOwner") >= 0,\n            "Task14 R3 InjuryRecovery executor requires same-tick PrimaryOwner");\n''', 'test injury owner')
test = once(test,
'''            "Task14 R3 foundation: FrameContext, one-winner priority model, explicit special physics, Travel preflight, Vengeance API and Observatory owner wiring verified. Ordinary single-writer migration remains a later R3 gate.");\n''',
'''            "Task14 R3: FrameContext/Arbiter plus owner-gated InjuryRecovery and Travel verified. Vengeance/Environment/Social single-writer migration remains open.");\n''', 'test summary')
test_path.write_text(test, encoding='utf-8', newline='\n')

status = status_path.read_text(encoding='utf-8')
status = once(status,
'''R3-OPEN-02 — Injury proposal executor\n-------------------------------------\n\n目前 Arbiter 已能识别 Severe Injury / InjuryRecovery 高于 Travel，\n但现有 InjuryRecovery movement 仍在 DesertBatflyAI.Update 内执行。\n\n后续要把该 executor 独立出来，并验证：\n\n- severe Injury beats current Travel；\n- TravelIntent 仍保留，不被清除；\n- recovery 结束后 Travel 可恢复。\n''',
'''R3-CLOSED-02 — Injury proposal executor（代码侧已收敛）\n--------------------------------------------------------\n\n新增 src/Creatures/DesertBatfly/Runtime/DB_InjuryRecoveryExecutor.cs。\n\n执行链已改为：\nFrameContext -> Arbiter -> PrimaryOwner=InjuryRecovery -> DB_InjuryRecoveryExecutor。\n\n- 旧 TryInjuryRecovery 入口删除；\n- DesertBatflyAI.Update 不再自行启动 severe InjuryRecovery movement；\n- ExecuteInjuryRecoveryOwned 必须验证同 tick PrimaryOwner；\n- InjuryRecovery 高于 Travel；\n- 若伤势刚降出 severe 阈值，清理 stale Recovery 后同帧重新仲裁；\n- 不删除 Task09 TravelIntent，因此恢复后 Travel 可重新竞争。\n\n仍需 Rain World 实机验证 severe injury -> recovery -> Travel resume。\n''', 'status injury')
status = once(status,
'''【R3 foundation 已建立；Travel 已成为第一个真正经 Arbiter 获得 owner 后执行的 domain。】\n【R3 尚未完成；下一阶段是拆 DesertBatflyAI / Injury / Environment / Social 的 decision 与 executor。】\n''',
'''【R3 foundation 已建立；InjuryRecovery 与 Travel 都必须先获得 Arbiter PrimaryOwner 才执行。】\n【R3 尚未完成；下一阶段继续拆 Vengeance / Environment / Social，并最终消除 ordinary localGoal 多 writer。】\n''', 'status conclusion')
status_path.write_text(status, encoding='utf-8', newline='\n')

Path('scripts/task14_r3_injury_owner_apply.py').unlink()
Path('.github/workflows/task14-r3-injury-owner-one-shot.yml').unlink()
