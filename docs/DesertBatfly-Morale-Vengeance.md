# Desert Batfly：群体恐惧、捕食威慑与极端报复

本文描述 `main` 当前实现的 Desert Batfly 群体士气系统。实现主体：

- `src/Creatures/DesertBatfly/DesertBatflyIntimidation.cs`
- `src/Creatures/DesertBatfly/DesertBatfly.cs`
- `src/Creatures/DesertBatfly/DesertBatflyState.cs`
- `src/WatcherExts/PeachLizard/PeachLizardDesertBatflyPredation.cs`

## 设计原则

死亡/捕食事件不再只是普通 `Alarm`。群体会区分：

1. 亲眼看到同类被杀/捕获；
2. 距离很近但没有直接看见；
3. 看到附近同类突然恐慌逃跑而被带动；
4. 极少数性格极端的个体，在先逃离后反向报复。

恐惧可以传播，但传播有限，不做全房间心灵感应；极端报复可以造成真实伤害，但不会替代普通 Desert Batfly 的非伤害反击，也不会让整群变成敢死队。

## 支持的死亡/捕食来源

### 玩家

真实的玩家致死攻击会建立对**具体玩家编号**的威慑记忆。

- Spear / ExplosiveSpear 等矛类真实伤害：完整威慑权重。
- 其他真实玩家伤害：略低权重。
- Rock：仍然严格 `stun-only`，只产生普通直接威胁反应，不进入死亡威慑。
- 多人游戏不会把 Player 1 的杀戮错误归到 Player 2。

### Peach Lizard

桃子蜥蜴的捕食分两次可能触发群体事件：

- **捕获**：舌头第一次黏住 Desert Batfly，或 Peach 直接 Bite/Grasp 成功；此时即使猎物还活着，附近群体已经知道捕食发生，并可能出现救援。
- **死亡**：之后 Peach 的真实 Bite/Violence 杀死猎物，再形成一次更强的死亡确认。

舌头捕获与随后嘴部 `Bite/Grasp` 有 90 tick 去重，同一次捕获不会重复广播。

## Fly Chain 的直接目击

玩家用致命攻击击中倒挂链成员、或 Peach 从链上用舌头/嘴部捕获成员时，会在拆链前快照 `FirstInChain()`。

因此原本处于同一条真实原版 Fly Chain 的成员全部视为直接目击者，即使它们与死亡点之间的普通视线判断不理想。

随后仍使用已有 Chain 解散逻辑，避免下层成员悬挂在已经离开的上层成员下面。

## 有限链式恐惧

一次事件先建立四个强度层级：

| 层级 | 来源 | 特征 |
| --- | --- | --- |
| Tier 0 | 直接目击 / 同一 Fly Chain | 最强 DeathShock 与记忆 |
| Tier 1 | 事件附近约 180px 的非直接目击者 | 较弱但明确的灾难反应 |
| Tier 2 | 被 Tier 0/1 恐慌个体在约 150px 内带动 | 第一跳社会传播 |
| Tier 3 | 再向外一跳，范围进一步缩小 | 最弱的第二跳传播 |

只允许两次社会传播 hop。Tier 2/3 不要求直接看见尸体，因为它们响应的是邻居突然逃跑，而不是拥有死亡现场信息。

传播只在死亡/捕获事件发生的那一刻遍历当前 Desert colony；不会每帧维护邻接图，也不会无限链式传播。

## 两类独立恐惧记忆

每只实现中的 Desert Batfly 最多维护两份固定槽位：

- `PlayerFear`：当前具体杀手玩家；
- `PredatorFear`：当前具体 Peach 捕食者。

不使用 `Dictionary<Creature,...>`，所以不会随着房间内玩家/捕食者数量增加而扩展。

直接目击的短期 DeathShock 当前约 200～500 tick（5～12.5 秒）；普通长期威慑约 800～2400 tick（20～60 秒）。链式传播层级会显著缩短这两个效果。

`Temperament` 和 `Nerve` 会降低恐惧强度、缩短恢复时间，但不会让直接目击者完全无视刚刚发生的死亡/捕食。

## 极端报复个体

新增稳定个体因子：

```text
VengeanceAffinity
```

它由个体 `EntityID.RandomSeed` 稳定生成：

- 55% 先天随机；
- 30% Temperament；
- 15% Nerve。

只有同时满足：

```text
Temperament >= 0.70
Nerve >= 0.58
VengeanceAffinity >= 0.78
```

才具备极端报复资格。

10,000 个连续 Seed 的离线统计中约 2.9% 个体满足资格，因此普通约 11+3 的群落往往没有或只有 1 个，极少同时出现多个。

每次死亡/捕食事件最多挑选 **2 个**直接目击的合格个体；优先选择 `VengeanceAffinity` 更高者。

即使符合资格，个体也不会立即冲上去。它首先经历事件自身的恐惧/退离，然后才可能让复仇欲压过恐惧。

## 三种极端报复行为

### 1. 真实伤害冲撞

死亡后的主要流程：

```text
DeathShock / Retreat
→ Observe
→ Circle
→ Feint
→ Charge
→ （高驱动力时最多再来一次）
→ Withdraw
```

真实 Charge 命中目标头部/主 BodyChunk 时调用原版：

```text
Creature.Violence(..., DamageType.Blunt, damage, stun)
```

因此它不是视觉假动作，而是真实游戏伤害。

普通 `RetaliationCharge -> Interfere` 仍保持原设计：**不造成生命伤害**。只有稀有极端报复状态允许真实伤害。

#### 对 Slugcat

当前伤害范围约：

```text
0.30 ～ 1.30 Blunt
```

按 `VengeanceDrive` 线性增加，并有小幅 rage 修正。普通极端个体通常造成受伤/强 Stun；最高端极少数个体可以通过原版 `instantDeathDamageLimit` 路径造成死亡。

#### 对 Peach Lizard

当前伤害范围约：

```text
0.12 ～ 0.44 Blunt
```

使用更保守的平方曲线，并附带冲量与 Stun。目标是让小型蝠蝇能够真正伤害/干扰捕食者，而不是单次撞死大型蜥蜴。

### 2. 舌头 / 嘴部救援

Peach 舌头第一次抓到 Desert Batfly 后，合格个体可以在非常短的退离后进入 `RescueCharge`。

救援者必须先真实冲撞 Peach 头部；命中本身会造成上述伤害和 Stun，然后检查同伴是否仍然：

- 被 `LizardTongue.State.AttachedInSmallObject` 拖拽；或
- 被 Peach 的 grasp[0] 咬住。

舌头救援基础成功率约 18%～48%，随 `VengeanceDrive / Nerve / Rage` 增加。已经进入嘴部 Grasp 后成功率进一步乘 0.62，因此越晚越难救。

成功时：

- 舌头调用原版 `Retract()`；或
- Peach 调用原版 `ReleaseGrasp(0)`；
- 被救同伴获得离开 Peach 的速度；
- 救援者立即撤退，不继续因为成功而无限攻击。

### 3. 围绕、假冲、有限多次真实攻击

如果同伴已经死亡、救援窗口关闭，或者救援失败，极端个体使用：

```text
Observe → Circle → Feint → Charge
```

表现为先远距离确认目标，再绕圈，再做一次明显的接近/拉起假冲，最后才进入真实伤害冲撞。

高 `VengeanceDrive` 个体最多拥有 2 个真实攻击 pass；其他只有 1 个。冲撞超时没有命中也会消耗 pass，避免卡成无限追击。

## 复仇者被反杀

如果正在极端报复的 Desert Batfly 又被原杀手/Peach 杀死：

- 新死亡事件的恐惧强度提高约 28%；
- 当次事件明确**不再招募新的极端报复者**。

这样会形成：

```text
同伴被杀
→ 极少数个体尝试报复
→ 报复者也被杀
→ 群落士气明显崩溃
```

而不是 A 死后 B 上、B 死后 C 上的无限敢死队。

当单个极端个体自身积累的相关恐惧强度达到约 0.84 时，即使它具备 `VengeanceAffinity`，也会放弃继续成为复仇者。

## 与原有攻击系统隔离

极端报复存在期间：

- 释放普通 `AttackSlot`；
- 不执行普通 `Attach / Drain`；
- 不执行普通 `Interfere`；
- 不叠加玩家饮水损失；
- 极端报复状态机独占该个体的攻击移动。

状态结束后才重新交还原 DesertBatflyAI。

这保证“极端报复真实可致命”不会和原来的吸水、移动干扰叠成不可读的双重攻击。

## 性能约束

设计没有新增逐帧全房间 Creature scan：

- 平时每只 Desert Batfly 只有固定状态查询/标量更新；
- PlayerFear / PredatorFear 是固定两个槽，不分配目标字典；
- Chain fear 只在稀有死亡/捕获事件发生时遍历一次当前约十几只 Desert colony；
- 传播最多两跳，候选数量由当前群落天然限制；
- Peach 捕食仍复用原版 `PreyTracker / LizardAI / LizardTongue / Bite`；
- 没有为救援新增房间扫描；
- CorpseWarning 最长 600 tick，只每 40 tick 检查一次附近群落，且杀手离开死亡位置后立即销毁。

## 实机验收重点

1. 玩家用 Spear 杀死单只：直接目击者立刻撤退，外围个体出现有限的链式恐惧。
2. 玩家杀死倒挂链成员：整条原 Chain 应按直接目击强度反应。
3. Rock：只能击晕，不产生死亡威慑，也不能杀死 Desert Batfly。
4. 连续玩家击杀：同一玩家的威慑累积，攻击位不能像敢死队一样立即补满。
5. Peach 舌头抓到活体：捕获瞬间即出现群体恐慌，不需要等猎物死亡。
6. Peach 直接 Bite/Grasp：没有舌头也必须触发同一捕食威慑。
7. Peach 抓倒挂链：同一 Chain 成员按直接目击处理。
8. 极端个体：先退离，再 Observe/Circle/Feint/Charge，不能直接无脑冲。
9. RescueCharge：有机会撞击 Peach 并使舌头/嘴部释放同伴；成功后救援者立即撤退。
10. 极端冲撞 Peach：应造成实际 Damage/Stun，但不能普通一击秒杀 Peach。
11. 极端冲撞 Slugcat：普通极端个体造成伤害/重 Stun；最高端个体应存在通过原版伤害路径致死的可能。
12. 极端个体最多 1～2 个真实 pass；未命中超时也必须消耗 pass。
13. 复仇者被杀：后续群体更恐慌，不能立即产生下一批复仇敢死队。
14. 普通攻击系统：极端报复时不得同时 Attach 吸水或 Interfere。
15. 多人：玩家死亡威慑继续绑定实际杀手玩家，不污染另一位玩家。
