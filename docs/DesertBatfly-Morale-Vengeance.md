# Desert Batfly：群体恐惧、从众、PTSD 与极端报复

本文描述 `main` 当前的 Desert Batfly 社会行为实现。核心文件：

- `src/Creatures/DesertBatfly/DesertBatflyState.cs`
- `src/Creatures/DesertBatfly/DesertBatflyAI.cs`
- `src/Creatures/DesertBatfly/DesertBatflyIntimidation.cs`
- `src/Creatures/DesertBatfly/DesertBatfly.cs`
- `src/WatcherExts/PeachLizard/PeachLizardDesertBatflyPredation.cs`

## 总体原则

Desert Batfly 不是共享一个群体大脑。每只个体仍保留自己的 `Temperament / Nerve / Thirst / Cooldown / GrabMemory` 和普通攻击逻辑；社会系统只把别的蝠蝇的行为作为额外证据。

一次玩家击杀或 Peach 捕食会经历：

```text
死亡 / 捕获事件
→ 直接目击者恐慌
→ 附近个体受带动
→ 最多再传播两跳社会恐惧
→ 极少数 True Avenger 可能克服恐惧
→ 最多 1～2 个高 Conformity 个体跟随
→ 成功、失败或士气崩溃后撤退
```

普通 `RetaliationCharge / Interfere` 仍然是不造成生命伤害的移动干扰；只有 Extreme Vengeance 状态允许真实伤害。

## Personality

### VengeanceAffinity

`VengeanceAffinity` 仍由稳定 Seed 生成：

```text
55% innate random
30% Temperament
15% Nerve
```

True Avenger 的当前门槛：

```text
Temperament >= 0.70
Nerve >= 0.58
VengeanceAffinity >= 0.715
```

对 Seed `0..9999` 的标定结果约为 **4.94%**。测试要求保持在约 5% 区间，防止以后调 Personality 时无意把极端复仇变成常见行为。

### Conformity

`Conformity` 是独立的稳定人格轴，范围 `0..1`，不由 Temperament 或 Nerve 派生。

它表达的是“同伴行为对自己决策的影响有多大”，而不是状态复制。

高 Conformity 会：

- 放大 Tier 1～3 的社会恐惧；
- 稍微放大直接目击后的群体性反应；
- 更容易受正在骚扰玩家的同伴影响；
- 更容易跟随 True Avenger；
- 稍微提高加入/维持 roost 文化的倾向；
- 在自己盲从后看到 Leader/Follower 被杀时受到更大的士气和 Trauma 打击。

它不会绕过：

- `Aggressive` 门槛；
- Nerve；
- Thirst；
- Cooldown；
- Fear / Trauma；
- 普通 `AttackSlots = 2`；
- 个体自己的目标可达性和状态机。

## 玩家死亡威慑

玩家真正杀死 Desert Batfly 时，威慑绑定到**具体 Player number**。

- Spear 等真实矛伤害使用完整威慑权重；
- 其他玩家真实致死伤害略低；
- Rock 继续严格 `stun-only`，不会制造死亡事件；
- 玩家直接吃掉仍活着的 Desert Batfly 也会被归因为该玩家的真实死亡事件；原版 `Fly.BitByPlayer()` 会在第一口令 Fly 死亡，因此归因必须在进入原版 Bite 前写入；
- 多人模式不会把 Player 1 的杀戮记到 Player 2。

## Peach 捕食威慑

Peach Lizard 有两种群体事件：

1. `PredatorCapture`：舌头第一次黏住，或直接 Bite/Grasp 抓住活体；
2. `PredatorKill`：随后真正杀死猎物。

Tongue → Bite/Grasp 有 90 tick 捕获去重；捕获后短时间内发生死亡时，死亡事件会降低权重，避免同一次捕食被当成两次完全独立的大灾难。

Peach 平地捕食、普通地形追猎和安全普通沙地下接近都仍然有效；沙地只是可选路线。

## Fly Chain 直接目击

死亡前会先 `SnapshotChainWitnesses()`，然后执行原版 `base.Die()`，确认真正死亡后才广播。

这样同时解决两件事：

- 原版 `Creature.Die()` 会拆 Grasp，但同链成员身份不会丢；
- 如果外部兼容 Hook 意外阻止死亡，不会提前制造一次假的死亡恐慌。

Peach 捕获则在 Chain 被 `Threatened()` 拆掉之前直接快照。

## 有限链式恐惧

| Tier | 来源 | 强度 |
| --- | --- | --- |
| 0 | 直接视觉 / 同一 Fly Chain | 最强 |
| 1 | 事件附近约 180 px | 中等 |
| 2 | 跟随附近恐慌蝠蝇的第一跳 | 较弱 |
| 3 | 第二跳 | 最弱 |

只允许两次社会 hop。Tier 2/3 表示看见同伴逃跑，不表示知道完整死亡现场。

Conformity 越高，Tier 1～3 的 Fear/DeathShock 越容易被放大；但传播放大仍然有硬 hop 上限。

## True Avenger 与社会跟随组

一次事件最多产生：

```text
1 True Avenger
+ 0～2 Follower
= 最多 3 个社会复仇参与者
```

True Avenger 由直接目击者中的 `CanExtremeVengeance` 个体选出，优先 `VengeanceAffinity` 较高者。

Follower 自己不需要 True Avenger trait。加入评分综合：

```text
Conformity
Temperament
Nerve
Leader VengeanceDrive
- 当前 Fear
- 当前 Trauma
```

Follower 必须是 Tier 0/1，不能从很远的链式恐惧层突然加入围攻。

### Support follower

承诺较低的 Follower：

```text
Wait → Observe → Circle → Feint → Withdraw
```

它只壮声势，不进行普通死亡伤害 Charge。若 Peach 仍在拖拽同伴，它仍可以用较低威力尝试 RescueCharge。

### Combat follower

承诺较高的 Follower 最多进行一次真实 Charge，伤害缩放约为 True Avenger 的 `35%～65%`。

Follower 不会得到 True Avenger 的第二次攻击 pass。

## Extreme Vengeance 三种方案

### 1. 真实伤害冲撞

True Avenger 的一般流程：

```text
DeathShock / Retreat
→ Observe
→ Circle
→ Feint
→ Charge
→ 可能第二个 pass
→ Withdraw
```

Charge 命中调用原版：

```text
Creature.Violence(..., DamageType.Blunt, damage, stun)
```

对 Slugcat：True Avenger 基础约 `0.30～1.30 Blunt`，极高驱动力少数个体存在通过原版伤害机制致死的可能。

对 Peach：约 `0.12～0.44 Blunt`，并附带 Stun 和冲量，定位为真正能伤害/打断大型捕食者，而不是常规一撞秒杀蜥蜴。

Follower 再乘自己的 `DamageScale`。

### 2. Peach 舌头/嘴部救援

若同伴仍被：

- `LizardTongue.State.AttachedInSmallObject` 拖拽；或
- Peach `grasp[0]` 咬住；

复仇参与者可进入 `RescueCharge`。必须先真实撞到 Peach，随后才检查救援概率。

成功时使用原版：

- `tongue.Retract()`；或
- `lizard.ReleaseGrasp(0)`。

舌头阶段基础救援率约 `18%～48%`；进入嘴部 Grasp 后再乘 `0.62`。Follower 的成功率进一步降低。

成功后立即 Withdraw。

### 3. Circle / Feint / 有限围攻

没有可救援目标时，复仇不会直接锁头冲撞，而是先 Observe、Circle、Feint，再进入真实 Charge。

Charge 超时没有命中也会消耗 pass，避免无限追杀。

## Leader / Follower 士气逻辑

### Leader 被杀

这是最强社会失败事件之一。

跟随这个 Leader 的 Follower：

- 立即退出复仇；
- 获得额外 Trauma；
- Conformity 越高，额外 Trauma 越大；
- 当次死亡事件禁止再招募替补 True Avenger。

### Follower 被杀

同一 Leader 下的其他 Follower 获得较小额外 Trauma；因为死者本身处于 Extreme Vengeance，死亡事件同样不会再补一个新的复仇者。

### Leader 正常撤退

正常 `Withdraw` **不是失败/PTSD 事件**。

Follower 只会同步 Withdraw。它们不会因为 Leader 正常结束行为而获得额外 Trauma，也不会因为 Leader 状态自然清理而把正常收手误判成“Leader 阵亡”。

该分支在 AI 复盘中专门修复过：Follower 的 Withdraw timer 不再每帧被 Leader 重置。

## Persistent Trauma / PTSD

短期 Fear 和长期 Trauma 分离。

CreatureState 固定保存：

```text
PlayerTraumaPlayer
PlayerTraumaStrength
PlayerTraumaTicks

PredatorTraumaId
PredatorTraumaStrength
PredatorTraumaTicks
```

持续时间约：

```text
2400 ～ 12000 tick
约 1 ～ 5 分钟
```

阈值：

```text
Trauma >= 0.42
→ 阻止对这个具体对象的普通骚扰/反击

Trauma >= 0.68
→ 可以打崩正在进行的 Extreme Vengeance
```

所以高 Temperament / 高 Nerve 个体也可能真正被某个玩家或某只 Peach 打怕。

PTSD 优先级高于：

- `GrabMemory`；
- 普通 attacker memory；
- 未消费的 retaliation charges；
- 普通目标扫描；
- AttackSlot 申请；
- Attach / Interfere；
- Extreme Vengeance。

`SuppressHostility()` 会清理针对该创伤对象的旧 Target、attacker memory 和未消费普通 retaliation，避免 Trauma 几分钟后过期时突然执行一笔很久以前的旧仇。

### 多人/多捕食者固定槽策略

为了保持固定内存，每类 Trauma 只保留一个对象。如果新的不同玩家/Peach只造成了很弱的 Trauma，而当前已有更强的有效 PTSD，新弱事件不会覆盖强记忆。

这避免 co-op 中 Player 2 的一次小事件把 Player 1 已经建立的严重 PTSD 清掉。

## 普通 Conformity 行为

普通攻击扫描仍每 8 tick 工作。高 Conformity 的 Aggressive 个体可以观察附近正在明显 `Observe / Approach / Circle / FakeDive / Dive` 的同类，并把其 Player 目标当成一个额外候选。

这只降低“我是否也过去看看”的动力门槛：

- 不复制 Attach；
- 不绕过 Cooldown；
- 不绕过 Trauma；
- 不绕过普通 AttackSlots；
- 不让非 Aggressive 个体突然成为攻击者。

因此表现是社会影响，不是同步编队。

## AttackSlots 与 Extreme Vengeance 隔离

普通正式攻击仍最多 `AttackSlots = 2`。

Extreme Vengeance 使用独立社会组上限，但该状态活跃时会：

- `CancelAttack()`；
- 释放普通攻击槽；
- 跳过普通 `AfterPhysics`；
- 禁止同时 Attach/Drain/Interfere。

因此不会出现极端伤害冲撞与吸水/普通干扰叠加。

## Runtime 生命周期与性能

正常时间没有群体邻接图。

- Death/Capture 事件发生时才遍历约 `11+3` 的 Desert colony；
- fear propagation 最多两 hop；
- Follower 只在该事件中筛选一次；
- Conformity 普通骚扰复用原来的 8-tick AI scan；
- Trauma threat 只每 20 tick 对有活跃 PTSD 的活体检查一次；
- 无 Trauma/Fear/Vengeance 时 `Intimidation.Update()` 保留 fast return；
- 死亡后 `Die()` 负责一次性广播并 `Forget()` runtime morale state；尸体即使 CreatureState 里仍保留 Trauma，也不会再次进入 `Intimidation.Update()`、重新占用 `activeStates`；
- CorpseWarning 只在死亡后存在最多 600 tick，并每 40 tick 采样一次；
- 不创建自定义寻路器，不重写 Peach tongue/pathfinder。

## 当前自动测试覆盖

`tests/DesertBatfly/Program.cs` 现在验证：

- 10,000 Seed Personality 可重复；
- `Conformity` 独立稳定且范围合法；
- Extreme Vengeance 资格保持约 5%；
- `VengeanceDrive / SocialFearScale` 有界；
- Player Trauma 存档往返；
- Peach/Predator Trauma 存档往返；
- Trauma 每 realized update 只衰减一 tick；
- 旧 V1 payload 不产生幽灵 PTSD；
- 原有 Rock、营养、AttackSlots、Quicksand/Emergence 回归检查继续存在。

这些测试仍需要完整 Rain World `PUBLIC/HOOKS` 程序集和编译后的 DryCycle.dll 才能实际执行。

## 实机验收重点

1. 玩家一矛杀一只：直接目击、同链、外围两 hop 的反应应明显分层。
2. Rock 连续命中：只能 Stun，绝不制造死亡恐惧。
3. 玩家直接抓住并吃掉活体：第一次原版 Bite 导致死亡时应正确建立 PlayerKill 威慑。
4. True Avenger 出现率在大量个体中应接近 5%，单房间仍然不是必出。
5. True Avenger 偶尔带 1～2 个高 Conformity Follower，而不是整群同步冲锋。
6. Support follower 只绕圈/假冲；Combat follower 最多一次低伤 Charge。
7. Leader 正常 Withdraw：Follower 正常同步退出，不额外获得 PTSD。
8. Leader 被杀：Follower 士气明显崩溃，高 Conformity 个体最容易留下 PTSD。
9. Follower 被杀：剩余跟随者受到次一级士气打击，不补新的敢死队。
10. 高 Trauma 恶劣个体面对同一 Player/Peach：不能重新申请普通攻击槽或突然执行旧 retaliation。
11. Co-op：弱的新玩家 Trauma 不应覆盖另一玩家已经建立的严重 PTSD。
12. Peach Tongue → Bite/Grasp：捕获广播只发生一次；随后死亡权重降低但仍确认死亡。
13. RescueCharge：成功后 Peach 原版舌头/Grasp 正确释放，救援者立即撤退。
14. Extreme Vengeance 活跃时不得同时吸水或执行普通 Interfere。
15. 连续杀死复仇者：群体应越来越崩，不应出现无穷替补。
16. 已死亡尸体长时间留在房间：不得重新激活 runtime morale state，也不能破坏无事件时的 `activeStates` fast-return。
