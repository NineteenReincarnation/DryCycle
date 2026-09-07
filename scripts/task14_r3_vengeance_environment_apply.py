from pathlib import Path


def once(text: str, old: str, new: str, label: str) -> str:
    n = text.count(old)
    if n != 1:
        raise RuntimeError(f"{label}: expected 1 match, got {n}")
    return text.replace(old, new, 1)

root = Path('.')
intimidation_path = root / 'src/Creatures/DesertBatfly/DesertBatflyIntimidation.cs'
bat_path = root / 'src/Creatures/DesertBatfly/DesertBatfly.cs'
environment_path = root / 'src/Creatures/DesertBatfly/Environmental/DesertBatflyEnvironmentalBehavior.cs'
hooks_path = root / 'src/Creatures/DesertBatfly/DesertBatflyHooks.cs'
test_path = root / 'tests/DesertBatfly/Program.Task14R3.cs'
status_path = root / 'docs/Discussion/Task_14_R3_FrameArbiterStatus.txt'

# ------------------------------------------------------------------
# Vengeance: state/fear tick is independent; movement progression is owned.
# ------------------------------------------------------------------
intimidation = intimidation_path.read_text(encoding='utf-8')
intimidation = once(intimidation,
'''    internal static void Update(DesertBatfly bat)\n    {\n''',
'''    internal static void UpdateState(DesertBatfly bat)\n    {\n''', 'rename Intimidation.Update to UpdateState')
intimidation = once(intimidation,
'''        UpdateVengeance(bat, state);\n        TryDeactivate(bat, state);\n    }\n\n    internal static void BroadcastPlayerKill(\n''',
'''        // R3: Vengeance phase timers/contact/movement are frozen while another owner wins.\n        // Fear memory, trauma and collapse checks above continue to tick independently.\n        TryDeactivate(bat, state);\n    }\n\n    internal static bool ExecuteVengeanceOwned(DesertBatfly bat)\n    {\n        if (bat == null || !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.Vengeance) ||\n            !states.TryGetValue(bat, out State state) || !state.Active ||\n            state.Vengeance == VengeanceMode.None)\n            return false;\n\n        UpdateVengeance(bat, state);\n        TryDeactivate(bat, state);\n        return true;\n    }\n\n    // Compatibility/readability surface: state tick only, never Vengeance locomotion.\n    internal static void Update(DesertBatfly bat) => UpdateState(bat);\n\n    internal static void BroadcastPlayerKill(\n''', 'split Vengeance executor')
intimidation = once(intimidation,
'''    private static void ForceFlight(\n        DesertBatfly bat,\n        Vector2 goal,\n        float speed)\n    {\n        if (bat?.room == null) return;\n\n''',
'''    private static void ForceFlight(\n        DesertBatfly bat,\n        Vector2 goal,\n        float speed)\n    {\n        if (bat?.room == null ||\n            !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.Vengeance))\n            return;\n\n''', 'guard ForceFlight by owner')
intimidation_path.write_text(intimidation, encoding='utf-8', newline='\n')

# Tick Intimidation facts before FlyAI arbitration; never run Vengeance movement post-base.
bat = bat_path.read_text(encoding='utf-8')
bat = once(bat,
'''        if (DesertState.Cooldown > 0) DesertState.Cooldown--;\n        DesertAI.TickMemory();\n\n        Room currentRoom = room;\n''',
'''        if (DesertState.Cooldown > 0) DesertState.Cooldown--;\n        DesertAI.TickMemory();\n        if (!dead)\n            DesertBatflyIntimidation.UpdateState(this);\n\n        Room currentRoom = room;\n''', 'pre-arbitration Intimidation state tick')
bat = once(bat,
'''        // DB_EventHub owns one-shot mortality facts. Corpses must not recreate a runtime\n        // morale state merely because persistent Trauma remains in CreatureState.\n        bool extremeVengeance = false;\n        if (!dead)\n        {\n            DesertBatflyIntimidation.Update(this);\n            extremeVengeance = DesertBatflyIntimidation.IsExtremeVengeanceActive(this);\n            if (extremeVengeance)\n                DesertAI.CancelAttack();\n        }\n\n''',
'''        // Vengeance state/fear was refreshed before base.Update so the R3 arbiter saw the\n        // current facts. Movement itself can only have run through DB_VengeanceExecutor.\n        bool extremeVengeance = !dead && DesertBatflyIntimidation.IsExtremeVengeanceActive(this);\n        if (extremeVengeance) DesertAI.CancelAttack();\n\n''', 'remove post-base Intimidation movement update')
bat_path.write_text(bat, encoding='utf-8', newline='\n')

# ------------------------------------------------------------------
# Environment: refresh influence before arbitration; apply only if owner wins.
# ------------------------------------------------------------------
environment = environment_path.read_text(encoding='utf-8')
environment = once(environment,
'''    internal static void Update(DesertBatfly bat)\n    {\n        if (bat?.room == null || bat.AI == null || bat.dead || bat.slatedForDeletetion)\n            return;\n\n        State state = states.GetValue(bat, _ => new State());\n        int tick = bat.room.game?.clock ?? 0;\n        int interval = DecisionBaseInterval + StableBucket(bat.Personality.VisualSeed, 7);\n        if (state.LastDecisionTick == int.MinValue || tick - state.LastDecisionTick >= interval)\n        {\n            state.LastDecisionTick = tick;\n            Recompute(bat, state, tick);\n        }\n\n        ApplyLocalBehavior(bat, state, tick);\n    }\n''',
'''    internal static void Update(DesertBatfly bat)\n    {\n        RefreshInfluence(bat);\n        ApplyOwnedBehavior(bat);\n    }\n\n    internal static void RefreshInfluence(DesertBatfly bat)\n    {\n        if (bat?.room == null || bat.AI == null || bat.dead || bat.slatedForDeletetion)\n            return;\n\n        State state = states.GetValue(bat, _ => new State());\n        int tick = bat.room.game?.clock ?? 0;\n        int interval = DecisionBaseInterval + StableBucket(bat.Personality.VisualSeed, 7);\n        if (state.LastDecisionTick == int.MinValue || tick - state.LastDecisionTick >= interval)\n        {\n            state.LastDecisionTick = tick;\n            Recompute(bat, state, tick);\n        }\n    }\n\n    internal static bool ApplyOwnedBehavior(DesertBatfly bat)\n    {\n        if (bat?.room == null || bat.AI == null || bat.dead || bat.slatedForDeletetion ||\n            !states.TryGetValue(bat, out State state))\n            return false;\n\n        bool owns = DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.EnvironmentHardSurvival) ||\n                    DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.EnvironmentLocalSurvival);\n        if (!owns) return false;\n\n        ApplyLocalBehavior(bat, state, bat.room.game?.clock ?? 0);\n        return true;\n    }\n''', 'split Environment refresh/apply')
environment_path.write_text(environment, encoding='utf-8', newline='\n')

# ------------------------------------------------------------------
# Hook: refresh environment facts, arbitrate, then execute Vengeance/Environment winners.
# ------------------------------------------------------------------
hooks = hooks_path.read_text(encoding='utf-8')
hooks = once(hooks,
'''        if (self.fly is not DesertBatfly desert) return;\n        DesertBatflyEnvironmentalIntegration.Register(desert);\n\n        DB_BehaviorResolution ownership = DB_BehaviorArbiter.ResolveFrame(desert);\n''',
'''        if (self.fly is not DesertBatfly desert) return;\n        DesertBatflyEnvironmentalIntegration.Register(desert);\n        DesertBatflyEnvironmentalBehavior.RefreshInfluence(desert);\n\n        DB_BehaviorResolution ownership = DB_BehaviorArbiter.ResolveFrame(desert);\n''', 'refresh Environment before arbiter')
hooks = once(hooks,
'''        // R3 has dedicated owner-gated executors for InjuryRecovery and Travel.\n        // Vengeance/Environment/Social remain in the legacy pipeline until migrated.\n        desert.DesertAI.Update();\n        DesertBatflyThreatRuntime.Update(desert);\n        DesertBatflyThreatTactics.TryApplyOrdinaryProjectileEvade(desert);\n        DesertBatflyThreatTrace.Sample(desert);\n        DesertBatflySignalRuntime.Update(desert);\n        DesertBatflyEnvironmentalBehavior.Update(desert);\n        DesertBatflySocialLife.Update(desert);\n''',
'''        if (ownership.PrimaryOwner is DB_BehaviorOwner.EnvironmentHardSurvival or\n            DB_BehaviorOwner.EnvironmentLocalSurvival)\n        {\n            if (DB_EnvironmentExecutor.TryExecute(desert, ownership))\n            {\n                if (AIDebugTrace.IsWatched(desert.abstractCreature))\n                    AIDebugTrace.RecordChange(desert.abstractCreature, AIDebugEventCategory.Decision,\n                        "PrimaryOwner", ownership.PrimaryOwner, ownership.Reason);\n                DesertBatflySocialLife.SampleTrace(desert);\n                DesertBatflyDebugTrace.Sample(desert);\n                return;\n            }\n\n            ownership = DB_BehaviorArbiter.ResolveFrame(\n                desert, ownership.PrimaryOwner,\n                "Environment executor yielded after current state recheck");\n        }\n\n        if (ownership.PrimaryOwner == DB_BehaviorOwner.Vengeance)\n        {\n            if (DB_VengeanceExecutor.TryExecute(desert, ownership))\n            {\n                if (AIDebugTrace.IsWatched(desert.abstractCreature))\n                    AIDebugTrace.RecordChange(desert.abstractCreature, AIDebugEventCategory.Decision,\n                        "PrimaryOwner", ownership.PrimaryOwner, ownership.Reason);\n                DesertBatflySocialLife.SampleTrace(desert);\n                DesertBatflyDebugTrace.Sample(desert);\n                return;\n            }\n\n            ownership = DB_BehaviorArbiter.ResolveFrame(\n                desert, DB_BehaviorOwner.Vengeance,\n                "Vengeance executor yielded after current state recheck");\n        }\n\n        // R3 owner-gated executors now cover InjuryRecovery, Travel, Environment and\n        // Vengeance. Social/combat/projectile/ordinary movement remain to be migrated.\n        desert.DesertAI.Update();\n        DesertBatflyThreatRuntime.Update(desert);\n        DesertBatflyThreatTactics.TryApplyOrdinaryProjectileEvade(desert);\n        DesertBatflyThreatTrace.Sample(desert);\n        DesertBatflySignalRuntime.Update(desert);\n        DesertBatflySocialLife.Update(desert);\n''', 'insert Environment/Vengeance executors and remove late Environment update')
hooks_path.write_text(hooks, encoding='utf-8', newline='\n')

# ------------------------------------------------------------------
# Regression guards.
# ------------------------------------------------------------------
test = test_path.read_text(encoding='utf-8')
test = once(test,
'''        Type injuryExecutor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_InjuryRecoveryExecutor", true);\n        Type desertAI = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyAI", true);\n''',
'''        Type injuryExecutor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_InjuryRecoveryExecutor", true);\n        Type vengeanceExecutor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_VengeanceExecutor", true);\n        Type environmentExecutor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentExecutor", true);\n        Type environmentBehavior = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalBehavior", true);\n        Type desertAI = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyAI", true);\n        Type desertBat = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatfly", true);\n''', 'test executor types')
test = once(test,
'''        Check(MethodCallOffset(vengeanceBridge.GetMethod("IntimidationUpdateHook", Flags), arbiter, "IsPrimaryOwner") >= 0 &&\n              MethodCallOffset(vengeanceBridge.GetMethod("ForceFlightHook", Flags), intimidation, "TryGetVengeanceTarget") >= 0,\n            "Task14 R3 Vengeance bridge consumes actual PrimaryOwner and explicit target facts");\n''',
'''        Check(vengeanceBridge.GetMethod("IntimidationUpdateHook", Flags) == null &&\n              vengeanceBridge.GetField("intimidationUpdateHook", Flags) == null &&\n              MethodCallOffset(vengeanceBridge.GetMethod("ForceFlightHook", Flags), intimidation, "TryGetVengeanceTarget") >= 0,\n            "Task14 R3 Vengeance bridge is tactic-only and no longer intercepts state lifecycle");\n        Check(intimidation.GetMethod("UpdateState", Flags) != null &&\n              intimidation.GetMethod("ExecuteVengeanceOwned", Flags) != null &&\n              MethodCallOffset(intimidation.GetMethod("ExecuteVengeanceOwned", Flags), arbiter, "IsPrimaryOwner") >= 0 &&\n              MethodCallOffset(intimidation.GetMethod("ForceFlight", Flags), arbiter, "IsPrimaryOwner") >= 0,\n            "Task14 R3 Vengeance state tick is split from owner-gated movement/contact execution");\n        Check(MethodCallOffset(desertBat.GetMethod("Update", Flags), intimidation, "UpdateState") >= 0,\n            "Task14 R3 refreshes Vengeance/fear facts before FlyAI arbitration");\n        Check(environmentBehavior.GetMethod("RefreshInfluence", Flags) != null &&\n              environmentBehavior.GetMethod("ApplyOwnedBehavior", Flags) != null &&\n              MethodCallOffset(environmentBehavior.GetMethod("ApplyOwnedBehavior", Flags), arbiter, "IsPrimaryOwner") >= 0,\n            "Task14 R3 Environment influence refresh is split from owner-gated local apply");\n''', 'replace Vengeance bridge test with R3 split guards')
test = once(test,
'''        Check(MethodCallOffset(updateAI, arbiter, "ResolveFrame") >= 0 &&\n              MethodCallOffset(updateAI, injuryExecutor, "TryExecute") >= 0 &&\n              MethodCallOffset(updateAI, travel, "TryDriveRealized") >= 0,\n            "Task14 R3 InjuryRecovery and Travel enter through central owner resolution");\n''',
'''        Check(MethodCallOffset(updateAI, arbiter, "ResolveFrame") >= 0 &&\n              MethodCallOffset(updateAI, environmentBehavior, "RefreshInfluence") >= 0 &&\n              MethodCallOffset(updateAI, injuryExecutor, "TryExecute") >= 0 &&\n              MethodCallOffset(updateAI, travel, "TryDriveRealized") >= 0 &&\n              MethodCallOffset(updateAI, environmentExecutor, "TryExecute") >= 0 &&\n              MethodCallOffset(updateAI, vengeanceExecutor, "TryExecute") >= 0,\n            "Task14 R3 migrated owner executors enter through central owner resolution");\n''', 'test hook migrated executors')
test = once(test,
'''            "Task14 R3: FrameContext/Arbiter plus owner-gated InjuryRecovery and Travel verified. Vengeance/Environment/Social single-writer migration remains open.");\n''',
'''            "Task14 R3: InjuryRecovery, Travel, Vengeance and Environment now have owner-gated execution. Social/combat/projectile/ordinary migration remains open.");\n''', 'test summary')
test_path.write_text(test, encoding='utf-8', newline='\n')

# ------------------------------------------------------------------
# Status record.
# ------------------------------------------------------------------
status = status_path.read_text(encoding='utf-8')
status = once(status,
'''R3-OPEN-03 — Vengeance proposal executor\n----------------------------------------\n\n当前已解决：\n\n- private reflection；\n- Travel frame stamp；\n- owner 可观测性。\n\n但 Intimidation.Update / ForceFlight 仍是旧 executor。\n\n后续要让 Vengeance 只在 PrimaryOwner=Vengeance 时执行 movement 部分；\nfear / trauma / state decay 可继续独立更新。\n''',
'''R3-CLOSED-03 — Vengeance proposal executor（代码侧已收敛）\n----------------------------------------------------------\n\n已完成状态与执行拆分：\n\n- `UpdateState` 只更新 fear / trauma / collapse / lifecycle；\n- Vengeance Observe/Circle/Feint/Charge/Withdraw 阶段只由 `ExecuteVengeanceOwned` 推进；\n- `DB_VengeanceExecutor` 要求同 tick `PrimaryOwner=Vengeance`；\n- `ForceFlight` 自身还有 owner guard，防止旁路写 movement；\n- 被 Injury / Travel / HardSurvival / fear 抢占时，Vengeance movement/timer 冻结但 fear/trauma 继续更新；\n- Task11 bridge 只剩 ForceFlight tactic modifier，不再 hook Intimidation.Update。\n\n仍需 Rain World 实机验证 preemption/resume 与 rescue charge/contact。\n''', 'close Vengeance item')
status = once(status,
'''R3-OPEN-04 — Environment decision / apply split\n-----------------------------------------------\n\nDesertBatflyEnvironmentalBehavior.Update 当前仍同时：\n\n- refresh influence；\n- ApplyLocalBehavior；\n- 写 localGoal。\n\n需要拆成：\n\nRefreshInfluence / BuildProposal / ApplyOwnedBehavior\n\nTask13 ownership 边界不变：\n\n- 不获得 cross-room authority；\n- 不新增 velocity controller；\n- Task09 仍独占跨房间 travel。\n''',
'''R3-CLOSED-04 — Environment decision / apply split（代码侧已收敛）\n-------------------------------------------------------------------\n\n`DesertBatflyEnvironmentalBehavior` 已拆为：\n\n- `RefreshInfluence`：只刷新授权天气/暴露/anchor/influence；\n- Arbiter 从最新 influence 构建 Environment proposal；\n- `ApplyOwnedBehavior`：只有 `EnvironmentHardSurvival` 或 `EnvironmentLocalSurvival` 为当前 PrimaryOwner 才调用本地 apply；\n- `DB_EnvironmentExecutor` 是唯一 R3 环境执行入口。\n\nTask13 边界不变：\n\n- 不获得 cross-room authority；\n- 不新增第二 velocity controller；\n- 不新增 world planner；\n- Task09 仍独占跨房间 Travel。\n\n仍需 Rain World 实机验证 HeavyRain/DeathRain/DenseFog/Heat/Sandstorm 的 owner 切换。\n''', 'close Environment item')
status = once(status,
'''【R3 foundation 已建立；InjuryRecovery 与 Travel 都必须先获得 Arbiter PrimaryOwner 才执行。】\n【R3 尚未完成；下一阶段继续拆 Vengeance / Environment / Social，并最终消除 ordinary localGoal 多 writer。】\n''',
'''【R3 foundation 已建立；InjuryRecovery / Travel / Vengeance / Environment 都已进入 owner-gated executor。】\n【R3 尚未完成；下一阶段继续拆 Social、projectile/combat/ordinary，并最终消除 ordinary localGoal 多 writer。】\n''', 'status conclusion')
status_path.write_text(status, encoding='utf-8', newline='\n')

# Remove temporary migration scaffolding from the resulting tree.
Path('scripts/task14_r3_vengeance_environment_apply.py').unlink()
Path('.github/workflows/task14-r3-vengeance-environment-one-shot.yml').unlink()
