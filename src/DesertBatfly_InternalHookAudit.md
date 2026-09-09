# DesertBatfly 内部挂钩与冗余代码排查记录

本文件用于标记当前 `src/Creatures/DesertBatfly` 中仍需后续清理的内部架构问题。这里只记录问题，不在本次提交中继续修改行为代码。

## 已确认的问题

### 1. Observatory 对项目自身代码使用 Reflection

文件：`src/Debug/AIDebugger/Sources/DB_ObservatorySource.cs`

当前通过 Reflection 读取 `DB_AI` 私有字段：

- `retreat`
- `escapeFrom`
- 旧字段 `pursuit`

问题：这些类型都属于 DryCycle 自己的代码，可以直接提供只读查询接口，没有必要通过 Reflection 绕行。尤其 `pursuit` 已迁移到 `DB_CombatRuntime`，当前反射读取已经失真。

重构目标：给正式 owner 提供只读 debug/query surface，Observatory 直接调用，不再反射自己的类。

### 2. Observatory 重复实现非 Fly 抓取判定

`DB_ObservatorySource` 自己又实现了一份 `RestrainedByNonFly()`。

正式事实源已经是：

`DB_RestraintPolicy.IsRestrainedByNonFly(DB_Creature)`

重构目标：删除 Observatory 的重复实现，统一使用 `DB_RestraintPolicy`。

### 3. DB_AI 仍有纯转发 facade

当前已确认的候选：

- `DB_AI.ExecuteCombatOwned()` → 只转发 `DB_CombatRuntime.TryExecuteOwned()`，生产执行链已经直接调用 Combat runtime。
- `DB_AI.ExecuteInjuryRecoveryOwned()` → 只转发 `DB_InjuryRecovery.ExecuteOwned()`。
- `DB_AI.RestrainedByNonFly()` → 只转发 `DB_RestraintPolicy.IsRestrainedByNonFly()`。

问题：这些入口会让职责 owner 看起来仍然属于 `DB_AI`，增加后续误用和重复控制的概率。

重构目标：调用者直接进入正式 domain owner，删除没有协调语义的转发壳。

### 4. `On.FlyAI.FleeFromRainUpdate` 可能已被 R3 顶层所有权覆盖

当前顶层 `On.FlyAI.Update` 已先通过 `DB_BehaviorArbiter` 决定 `Travel` 或 `NativeSpecial`：

- Travel 获得所有权时，不进入 vanilla `FlyAI.Update`；
- Travel 不能拥有时，才允许 vanilla rain 行为执行。

因此 `FleeFromRainUpdate` 内再次检查 Travel owner 很可能已经成为重复保险。

重构目标：核对完整调用链后，若确认无独立语义，删除该子 Hook，只保留顶层 owner 控制。

### 5. Integration Hook 中仍混有部分物种业务判断

`DB_RainWorldHooks` 中 `Idle / Swarm / Follow` 的 Hook 本身可能仍然需要保留，因为它们对应 Rain World 非 virtual 入口；但 Hook 内部包含 DesertBatfly 自己的 swarm 生命周期和规则判断。

重构目标：Integration 只负责拦截外部入口和转发，物种规则下沉到对应 domain runtime。不要删除确实无法 override 的 Rain World Hook。

## 已确认应保留的外部 Hook

以下目前属于合理外部接入点，不应因本轮“清 Hook”而直接删除：

- `Fly.ReportToFliesRoomAI`
- `Fly.Burrowed`
- `FliesRoomAI.FlyEmergeFromHive`
- `FlyAI.Update`
- `FlyAI.UpdateThreats`
- `FlyAI.IdleUpdate`
- `FlyAI.SwarmUpdate`
- `FlyAI.UpdateFollowDijsktra`
- `Room.Update`
- `Player.Collide`
- `Player.SlugcatGrab`
- `Weapon.Thrown`
- `Explosion.Update`
- `FirecrackerPlant.PopLump`
- `LizardTongue.Update`
- `SlugcatStats.NourishmentOfObjectEaten`

Sandbox/Warp 的 Harmony Patch 当前也只针对 Rain World、Warp 或系统外部类型，没有直接 Patch DryCycle 自身类型。

## 后续重构原则

自己的生命周期、状态和行为逻辑直接修改自己的 owner；只有 Rain World、第三方 Mod 或确实无法 override 的外部入口才使用 Hook/Reflection/Patch。任何新内部转发层都必须有明确协调语义，不能只是为了兼容旧调用而长期保留。
