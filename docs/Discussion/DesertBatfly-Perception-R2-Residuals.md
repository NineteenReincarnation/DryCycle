# DesertBatfly Perception R2 重构残留

本文件记录 DesertBatfly Perception R2 与 Signals 统合后仍保留的兼容层和后续清理项。

当前主架构已经完成：Creature / Player / Held Item / Projectile / Signal 的接收与认知事实统一由 `DB_PerceptionRuntime` 管理；Signals 负责发射、传播和显示；Threat 负责历史事件、威胁记忆和战术解释。

## 待清理残留

1. 删除 `DB_SignalRuntime.ReceivePacket(...)`。
   - 当前仅转发到 `DB_PerceptionRuntime.ReceiveSignal(...)`。
   - 不再拥有任何 Receiver 状态或行为语义。

2. 删除 `DB_SignalRuntime.TryGetInfluence(...)`。
   - 当前仅将 `DB_PerceptionSignalContext` 转换为旧的 `DB_SignalInfluence`。
   - 正式消费者应直接读取 Perception snapshot / signal context。

3. 淘汰 `DB_SignalPerception`。
   - 统一使用 `DB_PerceptionModality`。

4. 尽量淘汰 `DB_SignalInfluence`。
   - Debug / Observatory 应直接读取 `DB_PerceptionSignalContext`。
   - 若仍存在正式消费端，先迁移消费端，再删除该 DTO。

5. 更新相关调试和守卫。
   - Signal Debug / Observatory 改为直接展示 Perception R2 数据。
   - 更新 R6 Architecture Guard。
   - 更新 R6 Function Retention。
   - 更新 R7 Performance Guard。

6. 清理完成后重新执行并要求全部通过：
   - R6 Architecture Guard
   - R6 Function Retention
   - R7 Performance Guard

## 明确保留，不属于残留

`DB_SwarmLifecycleRuntime` 不得作为重构残留删除。它仍负责阻止原版非 virtual `FlyAI.Swarm` / `SwarmFlight` 重新夺取 DesertBatfly 行为控制，是当前架构中的正式 Hook 适配层。

## 清理原则

- 不重新引入第二套 Signal receiver state。
- 不允许 Signals 直接触发 Escape、取消 Social 或决定 BehaviorOwner。
- 不允许 Threat 重新镜像当前可见武器、投射物几何或 Signal 接收状态。
- Perception 只负责“个体认为世界是什么样”，行为域负责“如何响应这些事实”。
