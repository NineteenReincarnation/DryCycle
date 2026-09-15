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

## 2026-09-15 游戏内反馈修复

- 面具分离头部倾斜与左右镜像，仍由原版模块选择九方向并绘制阴影层。按实际 2172×724 贴图重做九帧 TXT 裁切，修复后几帧截到相邻图块、裁掉下颌以及逻辑缩放失真的问题；各帧按脸部位置补偿原版锚点。
- 原样接入用户提供的骨白、赭金彩色图，使用 Basic 着色器和白色基础乘色保留纹样，再叠加原版房间暗度。原白色 PNG 保留，作为没有彩色资源时的回退。
- 架枪和恢复保留原版站立支撑，落脚判定改看髋部下方的地面、单向平台和斜坡。跃冲中不再把离地误判为通道失效，继续检查友军，墙面交由武器和身体碰撞处理。
- 采用原版已确定的敌意，移除重复的强度与声望门槛；压制专属长枪的原版投矛蓄势，战术拉距使用 Travel，避免 Idle 的不适区域限制拦住移动。
- 完整冲锋在速度 12、有效距离 90 时达到 1.35 伤害，短刺最高仍为 0.42；突刺及跃冲锁定攻击方向，转枪动作本身不产生贯刺扫掠。
- 双手持枪不再进入玩家重物拖拽约束；短刺、被动持枪和枪杆碰地不修改持有者速度，完整跃冲保留命中与撞墙的收势。

六项针对性托管回归检查通过：站姿架枪、低强度明确敌意、非致命警告、重物抓持排除、短刺朝向与持有者速度、实际冲锋速度下的伤害。已有测试文件保持原样，下面两项测试环境限制仍存在；这次修复尚未完成游戏内复验。

## 行为与边界

- 普通 Scavenger 身体、社会关系与基础寻路；明确敌意才进入冲锋决策，轻度威胁先警告，放弃已失去行动能力或正在远离的目标。
- 通道判断结合水平距离、目标预测、身体和枪尖路径、落脚地面、水、狭窄区域与友军；不满足条件时寻找可返回的拉距位置。
- 架枪后施加一次低弧水平跃冲冲量；命中、落空、受击和撞墙进入恢复。贴脸只有低威力短刺或枪杆推挡，失枪后降低威胁。
- 枪尖扫掠负责伤害，枪杆和尾端只推挡。同次攻击不会反复伤害同一目标；伤害与速度、方向、有效冲锋距离相关，质量差影响推力和反作用。
- 长枪独立注册、序列化和实体化；生物保存发枪标记，保留原版健康、社会记忆和其他模组的存档字段。
- 注册、Hook 与 DevConsole 提供成对清理；共享注册器只作必要接线和注册清理。

初始数值集中在 `Combat/LanceCombatState.cs`、`Combat/LanceMotor.cs`、`AI/ChargeLane.cs` 和 `src/Items/ScavengerLance/LanceCombatMath.cs`。架枪 32 帧，普通恢复 44 帧、撞墙恢复 82 帧；完整冲锋最高伤害 1.35，短刺最高 0.42。数值尚未经过游戏内平衡验证。

## 外观与资源

复用原版 `VultureMaskGraphics` 的方向、镜像、锚点和环境调色逻辑，优先使用 `LanceScavengerMaskColor0`～`8`，缺少彩色资源时回退到现有 `LanceScavengerMask0`～`8`。资源均从启用模组的 `atlas/LanceScavenger/` 加载，每张 PNG 配合同名 TXT。彩色 PNG 是用户图 2 的原样副本；原白色 PNG 未重画、改名或修改像素。

仓库资源位于 `mod/atlas/LanceScavenger/`，正常 Release 部署会同步这两组 PNG/TXT 到模组目录。彩色图九帧采用一致的物理/逻辑比例，避免侧面帧被强行拉到正面帧的高度；TXT 中保留浮点逻辑尺寸，Futile 原生支持该格式。脸部锚点独立于长角的外接框，避免转头时沿角的长度漂移。

原 atlas 的逻辑尺寸由元数据决定，不能用 PNG 的物理帧尺寸再次放大。身体保持原版程序化结构，添加单侧护肩、赭黄斜胸带、少量绑带、两条短战带、非对称短珍珠串和肩部小挂饰；原生 Eartlers 缩小以突出面具主长角。

## 构建与验证记录

在仓库根目录执行：

```powershell
dotnet build src/DryCycle.csproj -c Release -p:DeployToGame=false -v quiet
dotnet build tests/LanceScavenger.Tests/LanceScavenger.Tests.csproj -c Release -p:DeployToGame=false -v quiet
& tests/LanceScavenger.Tests/bin/Release/net48/LanceScavenger.Tests.exe
```

2026-09-15：主项目 Release 构建通过，0 警告、0 错误；面具修复的资源和 `DryCycle.dll` 已同步到 Ancient Site 模组目录，并核对 SHA256。原 TXT 和 DLL 备份位于 `artifacts/lance-scavenger/mask-backup/`。

面具专项检查验证两组九帧裁切不越界、脸部锚点一致、彩色帧等比缩放、PNG 与用户原图字节一致。使用独立临时托管预览运行原版 `VultureMaskGraphics` 和 `FSprite`，完成 36 次绘制，检查九帧、左右镜像及头部倾斜；修复前后预览见 `artifacts/lance-scavenger/mask-correction-preview.png`。预览仅为 `t=1` 端点提供 Unity 插值的托管替代，不修改游戏 DLL 或已有测试代码，不能替代游戏内整只生物的遮挡、动作与光照验收。

保留现有测试代码。最近一次运行 6/8 组通过、108 个断言，未通过的两组停在托管测试环境：

- `SaveRoundTrips`：游戏静态房间名称映射未初始化，`WorldCoordinate.ResolveRoomName` 抛出空引用，尚未完成存档往返验证。
- `MasksAndMeshes`：原版面具绘制调用 Unity 原生 `Vector3.Slerp_Injected`，该旧测试环境不支持；本次独立面具预览已完成，但没有修改这组旧测试。

已通过的组覆盖预警与恢复、弱点与中断、伤害与扫掠、冲锋通道、真实武器碰撞入口和 DevConsole 注册清理。托管检查不能替代游戏内生成、存读档、过洞进窝、群体交战及面具遮挡验收；实际游玩验证尚未进行。
