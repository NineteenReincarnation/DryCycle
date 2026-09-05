# Peach Lizard / QuicksandZone 适配

本目录只负责 Watcher 桃子蜥蜴（`PeachLizard`）与 DryCycle `QuicksandZone` 的沙地掘行兼容。

## 目标

桃子蜥蜴原生具备 `AItile.Accessibility.Sand` 可达性，并由 `Lizard.Update` 根据即将进入的 Sand tile 自动驱动：

- `burrowUpcoming`
- `BodyChunk.burrow / buried`
- `TerrainManager` 掘地穿行
- 原生 SandPuff
- 原生 scrape / dig loop
- 从沙层自然出土

因此本适配**不实现传送、不模拟管道、不直接移动 BodyChunk、不替换 LizardAI**。

`QuicksandZone` 本身是一条 `TerrainCurve + TerrainManager.ITerrain`。它的材质由 `QuicksandZoneData.MaterialBoundaries` 在普通沙地与真实流沙之间交替：

- 非流沙材质：`BurrowAllowed` 曲面地形，可成为原生 `AItile.Accessibility.Sand`。
- 流沙材质：不作为可钻 TerrainManager 实体，并继续由 DryCycle 的 Quicksand hazard / sink 系统处理。

本适配只向 Peach Lizard 暴露第一类。

## 安全边界

`PeachLizardQuicksandSandMap` 建立一次性的房间缓存：

1. 只扫描真正的 `QuicksandZone`。
2. 只接受 AI bake 后已经是 `AItile.Accessibility.Sand` 的 tile。
3. 只接受该 tile 确实由当前 QuicksandZone 的非流沙地形覆盖。
4. 每个 20px tile 横向进行多点材质采样，并额外检查 8px 安全边距。
5. 任意一个采样点进入 `Data.IsQuicksand(u)`，整格都不会作为 Peach 可用沙地。
6. 不使用 QuicksandZone 为解决房间边缘渲染而延伸出去的 TerrainCurve mesh seal 作为栖息/出土点。

所以材质切换点附近不会因为“tile 中心刚好落在普通沙”而让桃蜥蜴从半流沙 tile 钻出。

## AI 适配

### TravelPreference

Peach Lizard 同时满足：

- `lizard.Swimmer == true`
- `lizard.Burrower == true`

但原版 `LizardAI.TravelPreference` 的顺序是先处理 `Swimmer`，因此 Peach 在所有非水目的地都会获得 `+5` resistance，并不会进入后面的通用 Burrower 分支。

适配只对 DryCycle 已验证的安全 Sand tile 撤销这一个 `+5` swimmer surcharge，并补回通用 Burrower 中有价值的“远距离移动时偏好在沙层内部而不是贴着表面走”的深度倾向。

不会修改：

- PathCost legality
- ThreatTracker
- prey / fear / rain 行为
- 真流沙 path hazard
- 其他蜥蜴
- Watcher 原生其他 Sand 地形

### LurkTracker

原版 Peach 的 `LurkPosScore` 会进入 swimmer 分支，其中明确拒绝既不是 `Floor`、又不是 `DeepWater` 的位置，所以 `Accessibility.Sand` 会直接得到 `-100000`。

适配只针对 DryCycle 安全 Sand：

- 保留原版可达 / 可返回 / slope / narrow-space 等安全检查。
- 给安全 Sand 一个与原版干燥 Floor lurk 点相近的正分数。
- 中等掩埋深度略优于紧贴表面或最深底部。
- 保留 visibility 与附近大型生物的原版式修正。

此外每约 120 tick，`LurkTracker` 只从缓存中抽样最多 8 个候选列，允许 Peach 发现房间另一侧的安全沙区；这只是更新原生 `lurkPosition`，不会强制切换 Behavior。是否真的进入 `Lurk` 仍由原版 UtilityComparer 决定。

## 性能

- 房间缓存通过 `ConditionalWeakTable<Room, ...>` 持有，Room 销毁后自动释放。
- 每个 Room 只构建一次安全 Sand lookup。
- lookup 是 O(1) tile 查询。
- lurk 候选每个 X 列最多保存 1 个，不保存整片沙层的所有点。
- 每 120 tick 最多测试 8 个候选，不逐帧扫描房间。
- `TravelPreference` 只进行一次 O(1) 缓存查询。

## 期望实机行为

假设一个 QuicksandZone 从左到右是：

```text
普通沙 | 普通沙 | 流沙 | 流沙 | 普通沙 | 普通沙
```

Peach Lizard 应当能够：

1. 将左右两侧普通沙部分作为原生 Sand 路径 / lurk 目标。
2. 进入普通沙时触发 Watcher 原生钻地表现。
3. 在同一连续普通沙部分内部掘行。
4. 根据原生 AI 目标自然从普通沙表面钻出。
5. 不把中间真实流沙视为 Sand tunnel。
6. 不为了到达另一侧普通沙而把真实流沙当作安全地下连接。
7. 若实际跌进流沙，仍交给 DryCycle 现有 Quicksand hazard / escape 逻辑处理。

这保证“桃子蜥蜴能够利用 QuicksandZone 中的非流沙沙层”，同时不会把 QuicksandZone 整体错误地豁免成安全地形。
