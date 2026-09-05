# Peach Lizard Watcher 扩展

本目录负责 Watcher 桃子蜥蜴（`PeachLizard`）与 DryCycle 沙漠生态的兼容。目前包含两部分：

- `QuicksandZone` 非流沙材质的原生掘沙适配。
- 对 `DesertBatfly` 的原生捕食、舌头捕捉与可选沙下接近。

统一生命周期入口是 `PeachLizardRuntime`。具体功能仍拆在独立文件中，避免 Quicksand 与捕食逻辑互相耦合。

---

## QuicksandZone 掘沙适配

桃子蜥蜴原生具备 `AItile.Accessibility.Sand` 可达性，并由 `Lizard.Update` 根据即将进入的 Sand tile 自动驱动：

- `burrowUpcoming`
- `BodyChunk.burrow / buried`
- `TerrainManager` 掘地穿行
- 原生 SandPuff
- 原生 scrape / dig loop
- 从沙层自然出土

因此适配**不实现传送、不模拟管道、不直接移动 BodyChunk、不替换 LizardAI**。

`QuicksandZone` 本身是一条 `TerrainCurve + TerrainManager.ITerrain`。材质由 `QuicksandZoneData.MaterialBoundaries` 在普通沙地与真实流沙之间交替：

- 非流沙材质：`BurrowAllowed` 曲面地形，可成为原生 `AItile.Accessibility.Sand`。
- 流沙材质：不作为安全可钻地形，并继续由 DryCycle 的 Quicksand hazard / sink 系统处理。

本适配只向 Peach Lizard 暴露第一类。

### 安全边界

`PeachLizardQuicksandSandMap` 建立一次性的房间缓存：

1. 只扫描真正的 active `QuicksandZone`。
2. 只接受 AI bake 后已经是 `AItile.Accessibility.Sand` 的 tile。
3. 只接受该 tile 确实由当前 QuicksandZone 的非流沙地形覆盖。
4. 每个 20px tile 横向进行多点材质采样，并额外检查 8px 安全边距。
5. 任意采样进入 `Data.IsQuicksand(u)`，整格都不会作为 Peach 可用沙地。
6. 若另一个重叠的 active QuicksandZone 在同一 tile 内存在真实流沙，同样排除。
7. 不使用 QuicksandZone 为房间边缘渲染而延伸出去的 TerrainCurve mesh seal 作为栖息/出土点。

所以材质切换点附近不会因为“tile 中心刚好落在普通沙”而让桃蜥蜴从半流沙 tile 钻出。

### TravelPreference

Peach Lizard 同时满足：

- `lizard.Swimmer == true`
- `lizard.Burrower == true`

原版 `LizardAI.TravelPreference` 会优先进入 Swimmer 分支，因此 Peach 在所有非水目的地得到 `+5` resistance，并不会执行后面的通用 Burrower 偏好。

适配只对 DryCycle 已验证的安全 Sand tile 撤销这一个 `+5` swimmer surcharge，并补回通用 Burrower 中有价值的“远距离移动时偏好在沙层内部而不是贴着表面走”的深度倾向。

不会修改：

- PathCost legality
- ThreatTracker
- prey / fear / rain 行为
- 真流沙 path hazard
- 其他蜥蜴
- Watcher 原生其他 Sand 地形

### LurkTracker

原版 Peach 的 `LurkPosScore` 会进入 swimmer 分支，其中拒绝既不是 `Floor`、又不是 `DeepWater` 的位置，所以 `Accessibility.Sand` 会直接得到 `-100000`。

适配只针对 DryCycle 安全 Sand：

- 保留原版可达 / 可返回 / slope / narrow-space 等安全检查。
- 给安全 Sand 一个与原版干燥 Floor lurk 点相近的正分数。
- 中等掩埋深度略优于紧贴表面或最深底部。
- 保留 visibility 与附近大型生物的原版式修正。

此外每约 120 tick，`LurkTracker` 只从缓存中抽样最多 8 个候选列，允许 Peach 发现房间另一侧的安全沙区；这只是更新原生 `lurkPosition`，不会强制切换 Behavior。是否真的进入 `Lurk` 仍由原版 UtilityComparer 决定。

---

## Desert Batfly 捕食

### 生态关系

`DesertBatflyDefinition.EstablishRelationships()` 在 Watcher 启用时建立：

```text
PeachLizard -> DesertBatfly : Eats 0.32
DesertBatfly -> PeachLizard : Afraid 0.90
```

`0.32` 接近 Watcher 原生 Peach -> Frog 的捕食强度，目的是让 Desert Batfly 成为真正猎物，但不会因为是一只很小的飞行生物就压过房间内所有其他更有价值的猎物。

反向 `Afraid` 会直接进入现有 DesertBatflyAI 的捕食者判断：即使是恶劣、高 Nerve 的沙漠蝠蝇，也不会把 Peach 当成可骚扰的小目标。

### 平地捕食是默认能力

捕食**不要求沙地**。

只要原版 Peach `PreyTracker` 选中 Desert Batfly，行为继续使用 Watcher 自己的：

```text
PreyTracker
-> LizardAI.Behavior.Hunt
-> AggressiveBehavior
-> ShootTongue / Bite
-> Grasp
-> ShakePrey / ReturnPrey
```

平地、普通 Terrain、房间内没有任何 QuicksandZone 时，这条捕食链仍然完整有效。

没有新建：

- 自定义 Hunt 状态
- 自定义舌头 projectile
- 自定义咬杀
- 自定义拖拽
- 自定义“传送到猎物”行为

### 舌头轻型生物桥接

Watcher Peach 的 AI 使用 `lizardParams.tongueAttackRange = 160` 作为发舌判定。

`LizardTongue` 自身对 `TotalMass < 0.2` 的命中目标使用 `AttachedInSmallObject` 分支。Desert Batfly 的实际质量远低于 0.2，因此原版舌头会像拉轻物件一样把它拖回嘴边；自由飞行的小型生物在这条分支结束时不一定自动转换成蜥蜴 Grasp。

`PeachLizardDesertBatflyPredation` 只修这一处接口缝隙：

1. 舌头发射仍由原版 AI 决定。
2. 飞行轨迹仍是原版 `LizardTongue`。
3. BodyChunk 命中仍使用原版 projectile trace。
4. 拉回过程仍是原版 `AttachedInSmallObject`。
5. 当已经捕获的 Desert Batfly 被拉到嘴边约 18px 内时，调用原版 `Lizard.Bite(caughtChunk)`。
6. 只有原版 Bite 真正建立 `grasps[0]` 后才让舌头 Retract。
7. 后续伤害、ShakePrey、ReturnPrey 全部继续由原版 Lizard 处理。

因此不会发生“自定义代码远距离直接把蝠蝇塞进嘴里”，也不会出现舌头已经物理抓到它、回到嘴边却无条件释放的轻型目标漏洞。

### 蝠蝇链

舌头第一次真正命中活体 Desert Batfly 后，会调用该生物已有的 `DesertAI.Threatened(peach, directAttack: true)`。

这样直接复用现有规则：

- 被命中的个体退出倒挂。
- 如果它属于真实 Fly Chain，整条链释放。
- 附近 Desert Batfly 收到局部 Alarm。
- Peach 被识别为捕食者，只触发逃生，不触发玩家式记仇反击。

不会另写第二套 Fly Chain 解散逻辑。

### 可选沙下接近

沙地只是捕食的**可选移动优势**，绝不是前置条件。

当 Peach 正在 Hunt Desert Batfly 且当前还没有清晰的舌头射击机会时：

- 若 PathFinder 本来就考虑经过 DryCycle 已验证的安全 Sand tile，该连接得到很小的额外 resistance 优惠。
- 已经 buried 的 Peach 获得稍强一点的“继续走沙下”的偏好。
- 越接近适中的沙层深度，偏好略高。
- 如果猎物远高于正常舌头能够解决的高度，沙地下接近奖励大幅衰减，让原版 frustration / retargeting 有机会放弃这个目标。

一旦：

```text
VisualContact == true
并且 preyDistance <= tongueAttackRange * 1.05
```

立刻停止额外沙地偏好。

所以在平地看到低飞 / 倒挂 Desert Batfly 时，Peach 会直接按原版方式接近并吐舌，不会为了“必须伏击”而先钻进附近的沙。

真正流沙仍然：

- 不作为捕食隧道。
- 不作为安全伏击出口。
- 不会因为猎物在其上方就被 Peach 适配层豁免。

---

## 性能

### Quicksand

- 房间缓存通过 `ConditionalWeakTable<Room, ...>` 持有，Room 销毁后自动释放。
- 每个 Room 只构建一次安全 Sand lookup。
- lookup 是 O(1) tile 查询。
- lurk 候选每个 X 列最多保存 1 个，不保存整片沙层的所有点。
- 每 120 tick 最多测试 8 个候选，不逐帧扫描房间。

### 捕食

- 不新增 Creature 扫描；猎物完全复用原版 `PreyTracker.MostAttractivePrey`。
- `TravelPreference` 热路径先检查是否 Peach + Hunt + 当前 prey 是否 DesertBatfly；绝大多数调用立即返回。
- 真正需要沙判断时只做现有 O(1) SafeSand lookup。
- `LizardTongue.Update` 每只 Peach 每 tick 只做常数级类型 / 状态 / 距离检查。
- 不创建每只 Peach 的 Dictionary、候选表、仇恨表或额外 AI module。

---

## 期望实机行为

1. **纯平地、无 QuicksandZone**：Peach 能把 Desert Batfly 作为 prey，接近后使用原版舌头捕捉。
2. **低空飞行**：进入舌头机会后直接吐舌，不要求钻沙。
3. **倒挂个体**：Peach 可在地面接近并吐舌；命中链成员时整条 Fly Chain 解散。
4. **舌头命中**：Desert Batfly 被原版舌头拉回；到嘴边后进入原版 Bite/Grasp，而不是被轻型物件分支无故放掉。
5. **抓住以后**：Desert Batfly 不享受 Player-held 生存保护；Peach 的正常 Bite / predator damage 可以杀死它。
6. **安全普通沙附近**：远距离追猎时 Peach 可以选择原生 burrow 路线靠近。
7. **已经有直接舌头机会**：不再为了沙下伏击绕路。
8. **真实流沙**：仍不是安全 Sand tunnel；Peach 不会为了追 Desert Batfly 穿过它。
9. **高空且长期不可达的 Bat**：沙路线奖励很弱，原版 frustration / retargeting 应能让 Peach 放弃，不长期卡死追逐。
10. **其他猎物**：所有额外路径偏好只针对当前 `MostAttractivePrey` 是 DesertBatfly 的 Hunt，不改变 Peach 对其他生物的原版行为。
