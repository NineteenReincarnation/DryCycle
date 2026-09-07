from pathlib import Path


def once(text, old, new, label):
    n = text.count(old)
    if n != 1:
        raise RuntimeError(f'{label}: expected 1 match, got {n}')
    return text.replace(old, new, 1)

tactics_path = Path('src/Creatures/DesertBatfly/ThreatSignature/DesertBatflyThreatTactics.cs')
hooks_path = Path('src/Creatures/DesertBatfly/DesertBatflyHooks.cs')
test_path = Path('tests/DesertBatfly/Program.Task14R3.cs')
status_path = Path('docs/Discussion/Task_14_R3_FrameArbiterStatus.txt')

tactics = tactics_path.read_text(encoding='utf-8')
tactics = once(tactics,
'''    internal static bool TryApplyOrdinaryProjectileEvade(DesertBatfly bat)\n    {\n        if (bat?.room == null || bat.AI == null || bat.dead || !bat.Consious || bat.inShortcut ||\n            DesertBatflyIntimidation.IsExtremeVengeanceActive(bat))\n            return false;\n''',
'''    internal static bool TryApplyOrdinaryProjectileEvade(DesertBatfly bat)\n    {\n        if (bat?.room == null || bat.AI == null || bat.dead || !bat.Consious || bat.inShortcut ||\n            DesertBatflyIntimidation.IsExtremeVengeanceActive(bat) ||\n            !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.ImmediateProjectileEvade))\n            return false;\n''', 'owner guard old projectile surface')
tactics = once(tactics,
'''        bat.AI.localGoal = evade;\n        DesertBatflySocialLife.CancelForPriority(bat, "Task11 real incoming projectile evade");\n        TraceAdjustment(\n            bat,\n            "ThreatEvadeStarted",\n            evade,\n            "real projectile trajectory owns this local goal; native Fly locomotion executes the dodge");\n        return true;\n    }\n\n    internal static Vector2 AdjustExtremeVengeanceGoal(\n''',
'''        return ApplyProjectileEvadeOwned(bat, evade);\n    }\n\n    internal static bool ApplyProjectileEvadeOwned(DesertBatfly bat, Vector2 evade)\n    {\n        if (bat?.room == null || bat.AI == null || bat.dead || !bat.Consious || bat.inShortcut ||\n            !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.ImmediateProjectileEvade))\n            return false;\n\n        bat.AI.localGoal = evade;\n        bat.Injury.NominalFlightSpeed = Mathf.Max(bat.Injury.NominalFlightSpeed, 9f);\n        DesertBatflySocialLife.CancelForPriority(bat, "R3 PrimaryOwner=ImmediateProjectileEvade");\n        TraceAdjustment(\n            bat,\n            "ThreatEvadeStarted",\n            evade,\n            "real projectile trajectory owns this local goal; native Fly locomotion executes the dodge");\n        return true;\n    }\n\n    internal static Vector2 AdjustExtremeVengeanceGoal(\n''', 'add projectile owned apply')
tactics_path.write_text(tactics, encoding='utf-8', newline='\n')

hooks = hooks_path.read_text(encoding='utf-8')
hooks = once(hooks,
'''        if (ownership.PrimaryOwner == DB_BehaviorOwner.Social)\n        {\n''',
'''        if (ownership.PrimaryOwner == DB_BehaviorOwner.ImmediateProjectileEvade)\n        {\n            if (DB_ProjectileEvadeExecutor.TryExecute(desert, ownership))\n            {\n                if (AIDebugTrace.IsWatched(desert.abstractCreature))\n                    AIDebugTrace.RecordChange(desert.abstractCreature, AIDebugEventCategory.Decision,\n                        "PrimaryOwner", ownership.PrimaryOwner, ownership.Reason);\n                DesertBatflySocialLife.SampleTrace(desert);\n                DesertBatflyDebugTrace.Sample(desert);\n                return;\n            }\n\n            ownership = DB_BehaviorArbiter.ResolveFrame(\n                desert, DB_BehaviorOwner.ImmediateProjectileEvade,\n                "Projectile evade executor yielded after current projectile recheck");\n        }\n\n        if (ownership.PrimaryOwner == DB_BehaviorOwner.Social)\n        {\n''', 'insert projectile executor before Social')
hooks = once(hooks,
'''        // R3 owner-gated executors now cover InjuryRecovery, Travel, Environment,\n        // Vengeance and Social. Combat/projectile/ordinary movement remain to migrate.\n        desert.DesertAI.Update();\n        DesertBatflyThreatRuntime.Update(desert);\n        DesertBatflyThreatTactics.TryApplyOrdinaryProjectileEvade(desert);\n        DesertBatflyThreatTrace.Sample(desert);\n''',
'''        // R3 owner-gated executors now cover InjuryRecovery, Travel, Environment,\n        // Vengeance, ImmediateProjectileEvade and Social. Combat/ordinary remain.\n        desert.DesertAI.Update();\n        DesertBatflyThreatRuntime.Update(desert);\n        DesertBatflyThreatTrace.Sample(desert);\n''', 'remove legacy projectile call')
hooks_path.write_text(hooks, encoding='utf-8', newline='\n')

test = test_path.read_text(encoding='utf-8')
test = once(test,
'''        Type socialExecutor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SocialExecutor", true);\n''',
'''        Type socialExecutor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_SocialExecutor", true);\n        Type projectileExecutor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_ProjectileEvadeExecutor", true);\n        Type threatTactics = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyThreatTactics", true);\n''', 'test projectile types')
test = once(test,
'''        Check(socialLife.GetMethod("RefreshState", Flags) != null &&\n              socialLife.GetMethod("ApplyOwnedBehavior", Flags) != null &&\n              MethodCallOffset(socialLife.GetMethod("ApplyOwnedBehavior", Flags), arbiter, "IsPrimaryOwner") >= 0 &&\n              MethodCallOffset(socialLife.GetMethod("SocialSteer", Flags), arbiter, "IsPrimaryOwner") >= 0,\n            "Task14 R3 Social scheduling/state refresh is split from owner-gated movement");\n''',
'''        Check(socialLife.GetMethod("RefreshState", Flags) != null &&\n              socialLife.GetMethod("ApplyOwnedBehavior", Flags) != null &&\n              MethodCallOffset(socialLife.GetMethod("ApplyOwnedBehavior", Flags), arbiter, "IsPrimaryOwner") >= 0 &&\n              MethodCallOffset(socialLife.GetMethod("SocialSteer", Flags), arbiter, "IsPrimaryOwner") >= 0,\n            "Task14 R3 Social scheduling/state refresh is split from owner-gated movement");\n        Check(threatTactics.GetMethod("ApplyProjectileEvadeOwned", Flags) != null &&\n              MethodCallOffset(threatTactics.GetMethod("ApplyProjectileEvadeOwned", Flags), arbiter, "IsPrimaryOwner") >= 0,\n            "Task14 R3 real projectile dodge has an owner-gated apply surface while Threat memory remains a modifier");\n''', 'test projectile owned apply')
test = once(test,
'''              MethodCallOffset(updateAI, socialLife, "RefreshState") >= 0 &&\n              MethodCallOffset(updateAI, socialExecutor, "TryExecute") >= 0,\n            "Task14 R3 migrated owner executors enter through central owner resolution");\n''',
'''              MethodCallOffset(updateAI, socialLife, "RefreshState") >= 0 &&\n              MethodCallOffset(updateAI, projectileExecutor, "TryExecute") >= 0 &&\n              MethodCallOffset(updateAI, socialExecutor, "TryExecute") >= 0,\n            "Task14 R3 migrated owner executors enter through central owner resolution");\n        Check(MethodCallOffset(updateAI, threatTactics, "TryApplyOrdinaryProjectileEvade") < 0,\n            "Task14 R3 legacy pipeline no longer applies projectile dodge outside PrimaryOwner");\n''', 'test hook projectile')
test = once(test,
'''            "Task14 R3: InjuryRecovery, Travel, Vengeance, Environment and Social now have owner-gated execution. Combat/projectile/ordinary migration remains open.");\n''',
'''            "Task14 R3: InjuryRecovery, Travel, Vengeance, Environment, ProjectileEvade and Social are owner-gated. Combat/ordinary migration remains open.");\n''', 'test projectile summary')
test_path.write_text(test, encoding='utf-8', newline='\n')

status = status_path.read_text(encoding='utf-8')
status = once(status,
'''R3-OPEN-06 — projectile modifier migration\n-------------------------------------------\n\n当前 ImmediateProjectileEvade 已进入 proposal priority model，\n但旧 DesertBatflyThreatTactics.TryApplyOrdinaryProjectileEvade 仍在 pipeline 直接调用。\n\n后续要决定：\n\n- real incoming projectile 作为短时 movement proposal；\n- learned Threat memory 仍只改变几何/风险，不成为 owner。\n''',
'''R3-CLOSED-06 — projectile modifier migration（代码侧已收敛）\n--------------------------------------------------------------\n\n- real incoming projectile 继续由 current-frame projectile fact 产生 `ImmediateProjectileEvade` proposal；\n- `DB_ProjectileEvadeExecutor` 只在该 owner 获胜时消费 Arbiter resolved goal；\n- `ApplyProjectileEvadeOwned` 与旧 compatibility surface 都有 same-tick owner guard；\n- Hooks 已删除无条件 `TryApplyOrdinaryProjectileEvade` 调用；\n- learned Threat Signature memory 仍只参与风险/几何，不进入 `DB_BehaviorOwner`。\n\n仍需实机验证 spear/rock near-miss 与 Vengeance geometry override 的连续性。\n''', 'close projectile item')
status = once(status,
'''【R3 foundation 已建立；InjuryRecovery / Travel / Vengeance / Environment / Social 都已进入 owner-gated executor。】\n【R3 尚未完成；下一阶段处理 projectile、Combat/Ordinary 的 DesertBatflyAI split，并最终证明 ordinary localGoal 单 writer。】\n''',
'''【R3 foundation 已建立；InjuryRecovery / Travel / Vengeance / Environment / ImmediateProjectileEvade / Social 已进入 owner-gated executor。】\n【R3 尚未完成；下一阶段集中处理 Combat/Ordinary 的 DesertBatflyAI split、Threat tactical apply 与 ordinary localGoal 单 writer。】\n''', 'status projectile conclusion')
status_path.write_text(status, encoding='utf-8', newline='\n')

Path('scripts/task14_r3_projectile_owner_apply.py').unlink()
Path('.github/workflows/task14-r3-projectile-owner-one-shot.yml').unlink()
