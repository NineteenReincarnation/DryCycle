# Desert Batfly V4 实现与验收

实现位于 `src/Creatures/DesertBatfly/`，只在 `Plugin.cs` 增加注册、启用和禁用入口。没有改写 Quicksand、Curved Terrain、玩家饮水系统或普通 Batfly Graphics。

## 源码调查与设计依据

开发前核对了本机 Rain World 参考源码：`Fly.cs`、`FlyAI.cs`、`FlyGraphics.cs`、`FliesRoomAI.cs`、`WorldLoader.cs`、`AbstractRoom.cs`、`Creature.cs`、`Rock.cs`、`ScavengerAI.cs`、`SlugcatStats.cs`、`Player.cs`、`TerrainManager.cs`、`TerrainCurve.cs` 和 `CurvedSlope.cs`，以及项目的注册、饮水和流沙实现。

- 原版 Batfly 的实际类名是 `Fly`。其 `FlyAI` 不是 `ArtificialIntelligence`；生物自行调用 AI，所以模板保留 `AI = false`，仍使用 Fly 的预烘焙导航槽。
- `FliesRoomAI` 提供草巢节点、雨前回巢、危险判断、出巢音效与随机出巢。它内部写死了普通 Fly 的生成类型，所以沙漠种群由专属管理器创建，其余 Hive 操作复用原版。
- `WorldLoader` 已通过 `AbstractRoom.AddTag` 保存未知房间标签。`DESERTSWARMROOM` 无需改写解析器，也不会进入普通 `swarmRoomIndex`。
- 原版 `Fly` 在飞行和回巢时直接访问 `room.fliesRoomAi`。仅在单只 Desert Batfly 的同步更新期间，用 `try/finally` 切换到私有同物种池；调用结束立即恢复。普通 Fly 的种群、标签和世界 Swarm 注册表不变。
- Rock 经 `Creature.Violence` 传入石头 BodyChunk。针对精确普通 `Rock` 类型保留冲量和眩晕、跳过伤害。不能简单调用“零伤害 base”，因为受伤 HealthState 仍可能触发 quickDeath。
- 拾荒者的 `Attacks` 关系进入原版 prey tracker，并由原版动态关系决定致命武器行为；没有伪造投矛或手动生成矛。
- 原版营养值使用四分之一食物单位。以原版对当前角色返回的营养倍率区分普通／肉食，不新增角色名单。保留原版禁止食用或不能获得营养的角色限制。竞技场直接读取 FoodPoints，完整食用调用期间也做相同换算。
- 玩家饮水以格数保存，1 格 = `ThirstConstants.WaterValuePerPip`；所有扣水以该常量换算，不能把 200 直接当作 200 格。
- 性格、花纹和突刺种子从稳定 EntityID 派生；状态存储种子、简单脱水、冷却、剩余咬数、完整食用标记和 Hive 状态。存档保留其他系统的未知字段。
- 曲面候选来自当前 `TerrainManager.ITerrain`，每次重新随机选择，不保留巢点。沿真实法线检查从隐藏位置到离开表面的完整路径；流沙通过现有 `QuicksandZoneData` 与 `QuicksandSurface` 几何排除。

## 地图与生成

在区域 `world.txt` 中使用：

```text
DC_A01 : DC_A02, DC_A03 : DESERTSWARMROOM
```

草巢出巢需要房间实际具有 BatHive / grass hive 节点；标签本身不制造地形或巢穴。有效 Curved Terrain 可提供额外出巢。曲面出巢属于沙漠群落的额外数量预算，初始限制在 `DESERTSWARMROOM` 中，避免普通区域凭空出现沙漠生物。种群迁移后可以出现在相邻房间。

普通 `SWARMROOM` 保持原意。若地图作者同时添加两个标签，该房间会分别拥有两种群落；这属于显式双标签配置。

项目已有 Dev Console 注册器会自动提供：

```text
spawn DesertBatfly
```

也可以在普通 world 生物配置中使用 `DesertBatfly` 类型。本次没有擅自给现有区域房间添加标签。

## 当前参数

OPEN 参数集中在 `DesertBatflyState.cs` 的 `DesertBatflyTuning` 中。40 tick 约为一秒。

| 项目 | 初始值 |
| --- | --- |
| 主 BodyChunk | 半径 8.5、质量 0.095，附加稳定个体尺寸差异 |
| 群落 | 最多补至 7 只 Hive 个体，另有最多 2 次曲面新增预算 |
| 恶劣阈值 | Temperament ≥ 0.52 |
| 正式攻击位 | 每目标最多 2 只，从 Approach 起占用 |
| 单次吸取 | 30 饮水值；不施加直接生命伤害 |
| 附着上限 | 18 tick，8 tick 时完成一次吸取 |
| 成功冷却 | 1800 tick，约 45 秒 |
| 失败冷却 | 240 tick，约 6 秒 |
| 观察 | 100 tick，假俯冲概率 0.55 |
| 单轮目标兴趣 | 最多 1000 tick，之后离开并冷却 |
| 反击记忆 | 640 tick，受威胁后先退离 |
| 普通石头眩晕 | 至少 110 tick |
| 轻量目标质量上限 | 0.55；仍排除捕食关系、同种和危险目标 |
| 曲面出巢动画 | 65 tick，前 12 tick 隐藏 |
| 流沙排除缓冲 | 22 像素，沿完整路径检查 |
| 花纹／突刺上限 | 9／6 |
| 完整食用 | 普通适用角色 2 Food，肉食适用角色 1 Food，饮水 −200 |

每个 AbstractRoom 生命周期只初始化一次新增预算，反复进出房间不会重新补满被吃掉的种群。跨周期仍按存活个体数量补齐；原版的季节活跃 Swarm 索引属于普通 Batfly，没有冒用该索引。

## 已执行验证

2026-09-05：Debug 和 Release 编译成功，零警告、零错误。Release 由现有项目构建目标部署到配置的 Ancient Site 模组插件目录。

独立托管验证程序直接加载编译后的 DryCycle 与本机公开化游戏程序集，执行 **30,024 个断言**：

- 10,000 个种子的重复外观一致、花纹和突刺数量不越界。
- 法语区域设置下状态保存／加载一致，保留健康、Hive、完整食用标记与其他模组字段；兼容旧字段数并拒绝 NaN 脱水值。
- 对已受伤但仍存活的生物连续 100 次普通石头命中：生命不变、不死亡、保留眩晕和冲量。
- 调用实际原版营养函数，确认 Survivor 2 Food、Hunter 1 Food、禁止食用值保留、普通 Fly 食物不变；确认派生类接口分派。
- 实际 AI 攻击位最多两个，第三只被拒绝；眩晕释放名额，抓取取消附着且不满足内部脱水欲望。
- 实际流沙几何查询排除内部、表面和边缘，同时允许远处候选。
- 实际 TerrainManager / TerrainCurve 碰撞：正确外法线允许出巢，反向法线、阻挡出口和流沙缓冲拒绝出巢。

复现（PowerShell，替换实际游戏目录）：

```powershell
dotnet build src/DryCycle.csproj -c Release
dotnet build tests/DesertBatfly/DesertBatfly.Tests.csproj -c Release
& tests/DesertBatfly/bin/Release/net48/DesertBatfly.Tests.exe '<RainWorldDir>' '<DryCycle.dll>'
```

测试没有启动 Unity 游戏循环，不能替代以下实机验收。

## 实机验收清单

| 场景 | 期望 |
| --- | --- |
| 温和个体＋玩家普通经过 | 不追逐、不锁定、不凭空恐慌 |
| 追逐／挥矛／投掷／捕食者靠近 | 退离、求生，附近局部警戒 |
| 恶劣个体＋玩家 | Observe → 接近／盘旋 → 假俯冲或真俯冲 |
| 假俯冲 | 接触前张翼拉起，不扣水 |
| 真攻击 | 短暂附着、一次 −30、自动脱离、长冷却 |
| 温和个体＋Rock | 眩晕掉落，恢复后逃跑 |
| 恶劣个体＋Rock | 眩晕掉落，恢复后可能观察攻击者并反击 |
| 连续 Rock／Spear | 石头只晕；矛正常致死 |
| 抓住攻击中的个体 | 中断攻击，只挣扎，不扣水、不反咬 |
| 逐口食用 | 中间咬数不扣水，最后一口恰好 −200；普通／肉食为 2／1 |
| 离房、回房、休眠重载 | 同一 ID 保持外观与性格，已食用标记不重复结算 |
| SWARMROOM | 普通 Batfly 数量与 Hive 行为正常 |
| DESERTSWARMROOM | 独立小群落，草巢出入及雨前回巢正常 |
| 无 Hive 节点的沙漠房间 | 有效曲面可额外出巢；雨前优先沿真实出口离开 |
| 曲面上方／下缘／陡坡 | 沿实际外法线渐进显露，翅膀延迟展开，无闪现 |
| 流沙叠加曲面／边缘 | 不出巢；远处正常曲面仍有效，草巢逻辑保持可用 |
| 拾荒者持矛 | 通过原版目标和武器决策主动投矛处理目标 |
| 合适轻量生物 | 恶劣个体可骚扰，危险捕食者仍优先逃跑 |
| 视觉图层 | 翼在身体后，花纹贴体，突刺随姿态，无贴附／出巢遮挡错误 |

动画重量、实际群落节奏、地图覆盖率与拾荒者投矛频率仍需在目标房间实机观察和调整；上述托管测试没有把这些场景标记为通过。
