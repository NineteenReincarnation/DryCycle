# Lance Scavenger 首版实现

依据 [ZIP 任务书](Documentation/LanceScavenger_Codex_Task.md) 实现冲锋型拾荒者与独立长枪。已完成首版代码和 Release 构建；游戏内动作、外观、房间转换及平衡仍待实际验收。

## 使用

启用 DryCycle 与 DevConsole 后：

```text
spawn LanceScavenger
spawn LanceScavenger disarmed
spawn ScavengerLance
spawn ScavengerLance length=90
```

生物默认仅在出生时配置一把真实长枪；`disarmed` 生成无枪个体。长枪掉落后靠实际寻路、拾取恢复装备，不会重新发枪或瞬移回手。武器长度默认 80，可指定 75～90；无效参数明确拒绝生成。

玩家双手持枪，投掷键短刺，向下加投掷键抛出。脱手武器继续作为独立物品模拟；投掷不提供完整冲锋伤害。

## 行为与边界

- 普通 Scavenger 身体、社会关系与基础寻路；明确敌意才进入冲锋决策，轻度威胁先警告，放弃已失去行动能力或正在远离的目标。
- 通道判断结合水平距离、目标预测、身体和枪尖路径、落脚地面、水、狭窄区域与友军；不满足条件时寻找可返回的拉距位置。
- 架枪后施加一次低弧水平跃冲冲量；命中、落空、受击和撞墙进入恢复。贴脸只有低威力短刺或枪杆推挡，失枪后降低威胁。
- 枪尖扫掠负责伤害，枪杆和尾端只推挡。同次攻击不会反复伤害同一目标；伤害与速度、方向、有效冲锋距离相关，质量差影响推力和反作用。
- 长枪独立注册、序列化和实体化；生物保存发枪标记，保留原版健康、社会记忆和其他模组的存档字段。
- 注册、Hook 与 DevConsole 提供成对清理；共享注册器只作必要接线和注册清理。

初始数值集中在 `Combat/LanceCombatState.cs`、`Combat/LanceMotor.cs`、`AI/ChargeLane.cs` 和 `src/Items/ScavengerLance/LanceCombatMath.cs`。架枪 32 帧，普通恢复 44 帧、撞墙恢复 82 帧；完整冲锋最高伤害 1.35，短刺最高 0.42。数值尚未经过游戏内平衡验证。

## 外观与资源

复用原版 `VultureMaskGraphics` 的方向、镜像、锚点和调色逻辑，使用现有 `LanceScavengerMask0`～`8`。资源从启用模组的 `atlas/LanceScavenger/LanceScavengerMask.png` 与同名 `.txt` 加载；当前用户资源位于 Ancient Site 模组目录，没有重画、改名或重新打包。

原 atlas 的逻辑尺寸由元数据决定，不能用 PNG 的物理帧尺寸再次放大。身体保持原版程序化结构，添加单侧护肩、赭黄斜胸带、少量绑带、两条短战带、非对称短珍珠串和肩部小挂饰；原生 Eartlers 缩小以突出面具主长角。

## 构建与验证记录

在仓库根目录执行：

```powershell
dotnet build src/DryCycle.csproj -c Release -p:DeployToGame=false -v quiet
dotnet build tests/LanceScavenger.Tests/LanceScavenger.Tests.csproj -c Release -p:DeployToGame=false -v quiet
& tests/LanceScavenger.Tests/bin/Release/net48/LanceScavenger.Tests.exe
```

2026-09-15：主项目 Release 构建通过，0 警告、0 错误；本次构建输出 `src/bin/Release/DryCycle.dll`，未部署到游戏目录。

保留现有测试代码。最近一次运行 6/8 组通过、108 个断言，未通过的两组停在托管测试环境：

- `SaveRoundTrips`：游戏静态房间名称映射未初始化，`WorldCoordinate.ResolveRoomName` 抛出空引用，尚未完成存档往返验证。
- `MasksAndMeshes`：原版面具绘制调用 Unity 原生 `Vector3.Slerp_Injected`，当前测试环境不支持，尚未完成绘制验证或生成有效预览。

已通过的组覆盖预警与恢复、弱点与中断、伤害与扫掠、冲锋通道、真实武器碰撞入口和 DevConsole 注册清理。托管检查不能替代游戏内生成、存读档、过洞进窝、群体交战及面具遮挡验收；实际游玩验证尚未进行。
