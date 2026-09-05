# Desert Batfly 当前实现与验收

实现主体位于 `src/Creatures/DesertBatfly/`。本文件描述当前 `main` 的真实行为；后续修改以代码和本文件为准，不再使用旧的 7+2、-200 饮水、单次 -30 吸取等过期参数。

## 当前核心定位

Desert Batfly 是原版 `Fly` 的沙漠生态变种：保持原版 Batfly 的飞行、挂链和 `FlyGraphics` 动画基础，在同一个 CreatureTemplate 内使用稳定 Personality 形成体型、颜色、花纹、突刺、胆量、栖息倾向与攻击倾向差异。

- 单一物种：`DesertBatfly`。
- 物理和绘制尺寸：原版 Fly 的 1.00x～1.25x，随 `Temperament` 连续变化。
- 群落目标：`HivePopulation = 11`，额外曲面预算 `CurvePopulation = 3`。
- `DESERTSWARMROOM` 独立于原版 `SWARMROOM`。
- 额外支持随机 Curved Terrain 出巢，并排除 Quicksand 本体、表面、边缘和出巢路径。
- Graphics 继承 `FlyGraphics`，原版负责基础 Body/Wing/Eyes、飞行、死亡、被抓挣扎和 Chain 倒挂姿态；本物种只叠加沙漠着色、花纹、突刺和尺寸差异。

## Personality

稳定个体因子包括：

- `Temperament`：温和～恶劣。
- `Nerve`：胆量；决定对普通靠近的容忍度，不会屏蔽真实攻击、武器和抓取。
- `RoostAffinity`：栖息/倒挂倾向。
- `VisualSeed / PatternSeed / SpikeSeed`：稳定视觉差异。

`Temperament >= 0.52` 进入恶劣侧，但攻击性不是二元开关。`AggressionDrive` 会连续控制：

- 观察时间（越恶劣越短）。
- 假俯冲概率（越恶劣越低）。
- 真攻击倾向。
- Approach / Circle / Dive 速度。
- 反击冲刺概率、速度、冲击和顶住时长。

## 饮水与食物

- 普通适用角色：2 Food。
- 肉食适用角色：1 Food。
- 完整吃掉一只 Desert Batfly：玩家饮水 **-50 raw hydration points**。
- 吸体液进入实际喝水阶段后：**50 raw hydration points / 秒**。
- Rain World 按 40 simulation ticks / 秒计算，因此吸水阶段每 tick 扣 1.25 raw hydration points。
- 当前附着上限 180 tick；吸水窗口是 tick 20～160。
- 吸水期间持续保持现有饮水 HUD 可见，让水位实时下降。
- Desert Batfly 自身 `Thirst` 仍是独立轻量 AI 欲望值，不接玩家完整饮水系统。

## Rock 与抓取生存

普通 `Rock` 对 Desert Batfly 定义为 stun-only：

- `Rock.HitSomething` 入口先开启短 death guard。
- `DesertBatfly.Violence` 识别 Rock 后只保留冲量和 Stun，完全跳过基础伤害。
- Rock 同一碰撞调用栈中由其他原版路径尝试触发的 `Die()` 也被 guard 拦截。
- Spear、捕食者咬击、溺水等真实致死来源不受保护。

原版 Fly 被玩家长时间拿着存在随机死亡逻辑；Desert Batfly 只屏蔽这条继承行为，因此活体被玩家拿住、放下或扔出后仍应存活。

## 倒挂与 Fly Chain

沙漠蝠蝇的栖息使用真正的原版机制：

`FlyAI.Behavior.Chain + Fly.MovementMode.Hang`

- 使用原版 `ChainTile` 判断合法挂点。
- `FlyGraphics` 自动得到原版倒挂 Body/Wing 姿态。
- 其他 Fly 可以通过原版 Grasp 机制继续接在下面形成链。
- `RoostAffinity` 和 Temperament 共同影响倒挂频率和持续时长。
- 当前自定义范围：`RoostMinTicks = 160`，`RoostMaxTicks = 520`；机会范围 0.012～0.045，再乘个体 RoostAffinity。

Fly-on-Fly 的 `grabbedBy` 是 Chain 结构，不视为“被捕获”。只有 Player / predator 等非 Fly 的 grasp 才暂停普通 AI。

普通靠近时，`Nerve` 高的个体允许表现为“附近其他蝠蝇已经逃了，它仍然继续待着/倒挂”。但一旦某只位于真实 Fly Chain 中的成员决定逃跑，整条链会一起释放，因为上层离开后下层无法继续悬挂。

## 恶劣个体主动攻击

主要攻击流程仍为：

`Observe -> Approach / Circle -> FakeDive 或 Dive -> Attach -> Drain -> Escape / Cooldown`

恶劣程度越高：

- Observe 越短。
- FakeDive 越少。
- 越容易进入真 Dive。
- 真攻击机动性略高。
- 口渴阈值对高恶劣个体略微放宽。

所有正式攻击继续共享 `AttackSlots = 2`，防止整群同时粘住/冲撞同一目标。

## 受击后的反击冲撞

玩家真正攻击一只恶劣 Desert Batfly 后，该个体会记录攻击者，先退离，再重新观察。满足性格和 Attack Slot 条件时可进入：

`Observe -> RetaliationCharge -> Interfere -> Escape`

`RetaliationCharge`：

- 对玩家当前位置和速度做轻量预测。
- 越恶劣速度越高（当前约 11.5～15.5）。
- 命中时给玩家 BodyChunks 一次小冲量。

`Interfere`：

- 不造成直接生命伤害。
- 不设置 Player stun。
- 不修改玩家输入。
- 不强制释放玩家手中物品或杆子状态。
- Desert Batfly 短时间贴在接触 BodyChunk 附近，给玩家水平速度施加轻量 drag，并沿冲撞方向施加很小持续推力。
- 持续约 16～36 tick，随恶劣程度增加。
- 被抓、失去意识、进入 shortcut、目标无效或位置被地形阻断都会立即结束。
- 高恶劣个体有有限概率获得第二次反击 pass，但仍受 Attack Slot 和 cooldown 约束。

## 被玩家抓后的 Grudge / Fear Memory

每只 Desert Batfly 只维护一份极轻量玩家记忆：

- `GrabMemoryPlayer`：具体玩家编号。
- `GrabMemoryStrength`：0～1。
- `GrabMemoryTicks`：剩余时间。

这样多人游戏中只针对真正抓过它的那位玩家，不分配每只蝙蝠的字典或复杂关系表。

玩家第一次抓住时：

- 只有被直接抓的个体获得强记忆。
- 同一 Chain 的其他成员只解散/警戒，不继承记仇对象。
- 被拿着期间仍然只挣扎，不反咬、不吸水、不进行反击攻击。

释放后：

- 普通放手保留基础记忆。
- 若释放瞬间速度 >= 6，则判定为明显扔出，额外增加记忆强度。
- 同一玩家重复抓取会累积 Strength，上限 1。
- 记忆持续约 1200～3600 tick，并会衰减消失，不是永久仇恨。
- 记忆写入 CreatureState 存档；旧版 V1 状态没有这些字段时会安全清零，不残留脏数据。

### 温和个体

把被抓记忆解释为恐惧：

- 释放后更长时间逃离。
- 记住该玩家后，在较远距离就可能把其视为 danger。
- Strength 越高，基础回避距离越大。
- `Nerve` 高的温和个体仍会比低 Nerve 个体容忍玩家更近一些。
- 不会因为被抓而突然变成攻击型。

### 恶劣个体

把被抓记忆解释为挑衅/记仇：

- 释放后先拉开短距离，再重新观察抓取者。
- 记忆中的玩家会被优先作为候选目标。
- 越恶劣越容易反击冲撞。
- GrabMemory 越强，假俯冲概率进一步降低。
- 即使当前 Thirst 不高，强记忆也可以让它选择真正的 Attach / Drain 报复性吸水。
- 对记忆中的玩家，仅仅靠近更不容易把它吓跑；真实 Rock/Spear/抓取等仍然立即作为威胁处理。

## 性能约束

新增行为保持轻量：

- 每只蝙蝠只额外保存一个玩家编号、一个 float Strength、一个计时器。
- 没有全局仇恨字典，没有每只蝙蝠的玩家列表。
- 记忆目标选择复用已有每 8 tick 的 `ScanCreatures()`，没有增加逐 tick 全房间 Creature 遍历。
- `RestrainedByNonFly()` 仅遍历本个体通常为 0～1 项的 `grabbedBy`。
- 反击贴身阶段每 tick 只处理目标 Player 的少量 BodyChunks。
- 局部 Alarm 只在受击/抓取等事件发生时遍历 Desert colony。
- Attack Slot 查询只在准备正式攻击时执行。

## 地图、Sandbox、Token、Warp

区域 `world.txt` 使用：

```text
DC_A01 : DC_A02, DC_A03 : DESERTSWARMROOM
```

- 普通 `SWARMROOM` 保持原版 Batfly 语义。
- `DESERTSWARMROOM` 使用独立 Desert colony / Hive 逻辑。
- Curved Terrain 是额外随机出巢源，不保存固定巢点。
- Quicksand 只局部排除曲面候选，不禁用整房间 Desert colony。
- Sandbox 注册独立 `DesertBatfly` Creature unlock。
- 原版蓝色 Creature Token 可解锁该 Sandbox 条目。
- Warp 存在时通过软兼容将 `DESERTSWARMROOM` 显示为独立 Desert Swarmroom 类型；未安装 Warp 不影响 DryCycle 加载。

## 当前需要实机验收

本仓库当前没有 GitHub Actions workflow；本会话环境也没有完整 Rain World `PUBLIC-Assembly-CSharp.dll / HOOKS-Assembly-CSharp.dll` 与游戏安装目录，因此最近这一轮不能在这里声明完成真实插件编译或 Unity 游戏循环测试。

下一轮实机优先检查：

1. Rock 连续命中：只 Stun，不死亡；恢复后的恶劣个体可能重新观察并反击。
2. 玩家抓住活体 30～60 秒再释放：仍存活。
3. 同一只恶劣个体抓一次、抓多次、扔出去：记仇和攻击倾向应逐级明显。
4. 温和个体被抓后：应该主动保持更远距离，而不是回来攻击。
5. Co-op：Player 1 抓过它后，它应优先记 Player 1，不把 Player 2 等同处理。
6. RetaliationCharge：明显冲向玩家；Interfere 让跑跳受到短暂影响，但不能形成输入锁、长时间卡位或无解连续控制。
7. 两只正式攻击位已占用时，第三只不能通过 Retaliation 绕开 Slot。
8. Fly Chain：普通链保持原版挂链；抓/攻击链中一只时整链释放，但只有直接被抓个体建立强 Grab Memory。
9. 吸水：从喝水阶段开始稳定按照 50 raw hydration points / 秒降低，HUD 连续显示。
10. 完整食用：只在最后完成食用时扣 50 raw hydration points。

参数仍集中于 `DesertBatflyTuning`，实机若需要调整只调数值，不应重新拆散上述行为架构。
