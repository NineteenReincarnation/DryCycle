# Iterator Framework 开发进度

更新日期：2026-09-15。

范围依据：《Iterator Framework 迭代器轮子开发任务书》第 81–86 节。前六阶段实现与托管验证已完成，真实游戏内验收待做。本轮完成独立对话、脚本、条件、命令、分支与打断，只增加三组必要检查，未进入第七阶段。按用户要求，本轮没有修改 PWN_AI，后续也不再给它追加阶段样例。

## 第一阶段已实现

- `IteratorID`：验证、Parse/TryParse、注册查询、Ordinal 值语义，创建 ID 无注册副作用。
- `IteratorDescriptor`：创建后不可变、输入集合防御性复制、默认名称、精确多房间绑定、只读字符串 Metadata。
- `IteratorBuilder` 与 `Iterator.Create`：Fluent API、独立 Build 快照、批量房间验证、外部扩展方法入口。
- `IteratorRegistry`：ID/房间/Oracle ID 查询，保留注册顺序，稳定只读快照，同一 Descriptor 重复注册幂等，冲突预检，按 Descriptor 身份安全注销。
- 游戏 ID 适配：使用 `Oracle.OracleID`，保护原版、未启用 MSC 和其他 Mod 已登记的 ID，注销后释放框架拥有的 ExtEnum。
- 基础 Validation：null、空白、非法 ID、缺失房间、重复房间、元数据和全局冲突检查。
- 基础 Logger：自动 ID/Module/Phase 上下文、BepInEx 适配、Trace 回退、日志接收器及重入保护。
- 公开 API 使用说明、源码 XML 注释、独立外部调用测试项目。

## 第二阶段已实现

- `IteratorRuntime`：统一状态转换、生命周期回调、幂等销毁、初始化与更新重入保护、回调异常处理及销毁原因。
- `IteratorContext`：Descriptor、Runtime、Oracle、Room、World、Game、StorySession、当前房间玩家视图、PrimaryPlayer 与阶段日志；销毁后清空游戏引用。
- `IteratorLifecycle`：RuntimeCreated、Initializing、Initialized、Active、Destroying、Destroyed。
- Runtime 工厂：Builder 的 `.Runtime(...)`、Descriptor 的只读 RuntimeFactory 与完整构造重载；保留第一阶段原构造签名，默认工厂可直接使用。
- `IteratorRuntimes`：房间就绪后生成、按实际 Room / Oracle 查询、活动实例快照、显式 TrySpawn；支持同名房间跨 Session 隔离与房间重新实体化。
- 内部 Oracle 宿主：实际 Oracle 实体与基础 BodyChunk，绑定到房间更新列表；第二阶段建立基础实体，第三阶段接入身体物理。
- 集中 Hook：Oracle 构造、Room.ReadyForAI、Room.Unloaded、RainWorldGame.ShutDownProcess、RemoveFromRoom；安装失败回滚，启停幂等。
- 销毁协调：房间卸载、Session 结束、实体移除、定义注销与插件停用均释放实例绑定。注销先销毁实例再释放游戏 ID，并保留销毁回调对其他定义的合法修改。
- 生成保护：拒绝未就绪或失效房间、复用或外来 Runtime；限制自动重试，处理工厂期间的注销、停用与卸载。
- [Runtime API 与使用说明](RUNTIME.md)，以及更新后的注册、快速开始和仓库入口文档。

## 第三阶段已实现

- `IteratorBody`：可替换组件、身体结构配置、位置/速度、MoveTo / Stop / ReleaseMovement、注视与姿势输入、手脚目标、组件清理。
- `StandardIteratorBody`：双 BodyChunk、限速与加速度控制、接近目标时减速、姿势朝向控制，使用游戏身体连接与碰撞。
- `BodyProfile`：不可变尺寸、质量、速度、加速度、重力、摩擦和浮力配置；默认零重力悬停，不改写房间重力。
- `IteratorPose`：内置姿势与自定义姿势，身体方向及手脚局部偏移；无 Sprite 或对话依赖。
- `IteratorArm`：可替换约束接口、默认 NoArm、FixedArm 固定锚点/最大伸长约束；位置修正优先保留地形碰撞。
- Builder 的 `.Body(...)` / `.Arm(...)` 与 Descriptor 工厂；保留前两阶段四参数、五参数构造签名，新增完整七参数重载。
- Runtime 在 OnCreate 前初始化 Body / Arm，按规定顺序推进控制、游戏物理和后处理；销毁时先清理 Arm、Body，再释放宿主。
- 内部物理适配：一次生成非虚调用，直接执行游戏 `PhysicalObject.Update`；不改动原版 Oracle.Update，不复制游戏物理源码，不执行原版 Oracle AI。
- 身体、机械臂工厂归属及复用保护，非法配置/坐标/初始结构检查，失败和创建中途销毁后的清理。
- [Body、Arm 与 Pose 使用文档](BODY.md)，包含默认用法、替换工厂、生命周期与自定义身体示例。

## 第四阶段已实现

- `IteratorGraphics`：每实例视觉组件、更新与相机插值分离、公开 Render 数据入口、组件归属及清理保护。
- `GraphicsProfile`：不可变颜色、尺寸、Face / Gown / Mark 开关、0–8 个 Halo、Cable、Shader / Atlas 元素、调色和发光外观参数。
- `SpriteRegistry` / `SpriteHandle`：按名称注册，初始化后冻结布局；内部管理相机下标，拒绝重复名称和跨 Sprite / 实例的网格共享。
- `IteratorMesh` / `IteratorMeshPart`：拓扑和 UV 防御性复制、只读数据视图、有限坐标检查、多边形 / 椭圆 / 描边及局部变形；可完全替换标准人形。
- 标准部件：身体 / 袖子 / 手脚、Face、Gown、Halo、Cable；姿势与注视输入、确定性眨眼和袍摆，FixedArm 的可视线缆。
- 内部相机适配器：独立 TriangleMesh、世界坐标转相机坐标、每相机调色、绘制层级、退出观看 / 重建 / 销毁清理。
- 可选图形错误隔离：部件异常只停用该部件；工厂与整体初始化异常尝试标准外观；渲染器或整体图形失败停用绘制并保留 Runtime。
- Atlas 引用计数与借用保护、缺失元素 / Shader 的安全回退。标准外观和样例仅借用游戏 Futile_White，无新图集或 Shader Bundle。
- `.Graphics(...)` / GraphicsFactory / Context.Graphics，保留既有 Descriptor 构造签名；Runtime 统一初始化、更新及清理。
- PWN_AI 自动登记样例：白色面部、额头大小圆环、金色扭转头饰、白紫长袍，不含左右蓝球。实际房间 48×36 格，当前安全位置 `(470, 350)`，没有修改房间或世界文件。
- [Graphics API 与样例文档](GRAPHICS.md)，以及同一套编译后网格生成的角色、透明背景和房间放置预览。

## 第五阶段已实现

- `IteratorBrain`：每实例行为中心、被动 Idle 回退、统一生命周期与异常处理；`.Brain(...)`、BrainFactory、Context.Brain；保留所有既有 Descriptor 构造签名。
- `IteratorAction`：初始化、进入 / 更新 / 退出、独立进入 / 保持条件、优先级、可打断性、完成状态与清理。
- `IteratorStateMachine`：具名动作、优先级仲裁、同优先级稳定排序、待处理请求、不可打断动作、完成和条件失效后的切换；每帧最多进入一个新动作，回调请求延后处理。
- `IteratorBehaviorModule`：工厂 / 初始化阶段组合模块，OnSense、OnPlayerEvent、OnUpdate、模块拥有的动作及局部故障隔离；无需扩展者安装全局 Hook。
- `PlayerSensor` / `IteratorSensorProfile`：当前房间玩家、距离、实际地形视线、生命和捷径状态、接近阈值与目标切换阈值；默认 5 帧采样，复用记录和集合。
- 集中感知通知：Entered / Left / Approached / MovedAway / Died / Revived；离开和销毁时释放保留记录中的 Player 引用。
- `StandardIteratorBrain` / `ObservePlayer` / `IdleAction` / `ObservePlayerAction`：基础 Idle 与观察可见存活玩家，不追逐、不攻击、不触发对话；保留 Body 已有姿势与移动目标。
- Runtime 在 OnUpdate 前推进 Brain，让 Runtime 可以覆盖同帧身体输入；Brain 在 Graphics / Body 清理前结束动作和模块。
- PWN_AI 接入默认观察，维持第四阶段参考造型、展示姿势和悬停位置；玩家离开、死亡、在捷径中或被墙挡住时结束观察。
- [行为 API 与扩展示例](BEHAVIOR.md)，并同步已有生命周期、Body、Graphics、快速开始与仓库入口文档。

## 第六阶段已实现

- `ConversationController` / `EmptyConversation`：每实例独立生命周期、排队控制、默认不自动说话；`.Conversation(...)`、ConversationFactory、Context.Conversation，保留此前构造签名。
- `Dialogue` / `DialogueBuilder` / `DialogueSequence`：可共享不可变脚本，独占 DialogueRun 与命令状态；名称、长度、帧数、队列、树深度和每帧处理预算验证。
- `DialogueCondition`、When、Conditional、Branch、Then、Random：条件进入时求值，分支返回父脚本，Controller 私有随机源不改动游戏随机状态。
- Say、Wait、WaitUntil、Pause、Look、MoveTo、Gesture、Sound、Action、Callback 与可扩展 DialogueCommand / DialogueCommandExecution。
- 暂停、打断栈、恢复、Cancel / CancelCurrent、替换、目标玩家失效取消；保留未完成的等待进度，只重显被撤回的未完成台词。
- 原版 HUD.DialogBox 适配、按房间相机选择输出、游戏翻译器、等待 HUD 可用；按消息身份清理，保留其他系统的台词和顺序。
- 对话身体输入在 Brain 之后、Runtime.OnUpdate 之前应用；暂停及结束时只回收自身仍持有的输入，保留外部覆盖。
- 命令/条件/输出异常结束单次脚本，控制器异常只停用对话；工厂回退、重入销毁、迟到输出回收、当前/打断/排队引用清理。
- [对话 API 与时序约定](CONVERSATION.md)，同步定义、生命周期及框架入口文档。没有添加对话示例或第七阶段 Interaction。

## 已运行的验证

主项目及独立测试项目 Release 编译成功。全量编译存在 **3 条已有 CS0162 警告，0 个错误**，均位于本阶段未修改的 LanceScavengerGraphics.cs；对话代码没有新增编译警告。

第四阶段曾将两个 DevTool Compatibility 文件中的三处类型引用限定为 `global::DevInterface.DevUI`，以解决命名冲突；本阶段没有修改这些文件。

检查共 **47/47 组通过，2123 项断言，0 个失败**：第一阶段 13 组 / 1564 项，第二阶段 18 组 / 331 项，第三阶段 4 组 / 52 项，第四阶段 4 组 / 42 项，第五阶段 5 组 / 70 项，第六阶段 3 组 / 64 项。

测试项目引用实际编译的 `DryCycle.dll`，运行时加载本机安装的 `Assembly-CSharp.dll`，没有源文件链接副本或 Oracle 模拟类型。第二阶段用托管房间夹具准备游戏对象图，执行实际的 Room.ReadyForAI、Oracle 构造补丁、实体增删与 Runtime 更新。卸载和关闭处理使用替代原方法委托检查框架清理顺序，未调用完整 Unity 关闭流程。

第三阶段执行实际 PhysicalObject / BodyChunk 物理路径。独立 CLR 无法执行 Unity 原生入口，因此测试在自身输出目录生成 CoreModule 夹具，将其原生入口替换成“调用即抛异常”的方法；未替换游戏物理算法，也未改动游戏安装目录。测试通过意味着所测路径未触及这些原生入口，不代表原生水体、天气、音画路径已验证。

第一阶段覆盖内容：

1. 任务书最小示例、名称默认值、ID/房间/Oracle ID 查询。
2. ID 格式、null、边界长度、大小写、值相等和哈希契约。
3. Descriptor 输入防御、只读集合，以及 Builder 后续修改不影响已注册定义。
4. 批量添加房间时的重复或非法输入不会部分写入。
5. 重复 ID、大小写房间冲突及失败后无残留、可再次注册。
6. 注册顺序、旧快照稳定性、注销再注册的顺序。
7. 原版/MSC/外部 Mod 的 ID 保护。
8. 重复注销、显式重载、旧 Descriptor 不能移除新注册。
9. 真实 ExtEnum Index 变化时映射保持正确。
10. 无效查询、参数异常与直接构造时的输入校验。
11. 日志作用域、接收器异常、递归日志、异常格式化失败、Trace 失败隔离。
12. 外部程序集直接构造 Descriptor 和使用 Builder 扩展方法。
13. 40 轮、每轮 4 个定义的注册/注销循环，检查所有 ID 与房间释放。

第二阶段重点覆盖：

- 实际房间就绪 Hook、宿主构造、绑定查询与生命周期顺序。
- 重复初始化/销毁、回调重入、中途销毁及各阶段异常后的释放。
- 默认与自定义工厂、工厂失败、外来或复用实例、显式重试。
- 注销回调修改其他定义、注销与停用期间的生成保护。
- 房间卸载/重新实体化、Session 隔离、宿主移除和转移、当前房间玩家刷新。
- 原版 Oracle 适配分支不接管、未知构造布局拒绝打补丁、重复安装与卸载。
- 无效房间、无可用生成位置，以及 30 轮实体化/卸载后无活动实例残留。

第三阶段 4 组必要检查：

1. 默认组件、目标移动/到达/停止、注视清除、姿势方向和手脚目标、非法输入、销毁后访问保护。
2. 实际 BodyChunk 墙面接触、地板承托、独立身体重力，确认房间重力不变。游戏在碰撞后求解连接，地板位置检查允许约 1 像素接触误差。
3. 固定机械臂的目标与惯性约束、无需原版 OracleArm、初始不可达时的清理。
4. 单 Chunk 自定义身体、组件初始化/反向销毁顺序、外来工厂结果保护、约束更新和清理异常、工厂中途销毁。

第四阶段 4 组必要检查：

1. 命名网格布局、冻结、别名和有限输入保护；绘制不推进 Runtime；失败部件与正常部件隔离。
2. 图形工厂失败的标准回退、外来组件保护、工厂中途销毁、重复清理和无残留 Drawable。
3. 实际 Futile SpriteLeaser / TriangleMesh 的多相机独立性、相机偏移、调色板、缺失资源回退、退出观看重建、全部视图清理及借用 Atlas 保留。
4. PWN_AI 实际房间地形解析、样例幂等注册、全部部件正常初始化、无 Halo / Cable / 蓝球、有限网格且不穿实墙、样例注销与 CPU 预览导出。

已查看并调整角色预览：收窄青色高光、调整金色头饰和袍摆。预览与游戏读取同一套网格，但不能代替游戏内 Shader / 光照效果或用户最终视觉验收。

第五阶段 5 组必要检查：

1. 条件与优先级仲裁、同优先级顺序、不可打断、完成与保持条件失效、回调内排队和重复请求。
2. 玩家进入 / 离开 / 接近 / 死亡 / 复活，低频缓存、目标切换与接近阈值、实际 Room.VisualContact 墙体遮挡、捷径状态。
3. 行为模块及其动作隔离、动作故障的单次退出与 Idle 回退、整体 Brain 故障、中途销毁停止后续更新。
4. Brain 工厂或部分初始化失败、外来工厂结果、工厂中途销毁、动作 / 模块幂等清理、玩家引用和房间 Drawable 释放。
5. PWN 样例的 Brain → Body 注视 → 编译后面部网格传递，确认姿势和位置保持稳定，离开后回到 Idle。

第六阶段只新增三组关键检查：

1. 脚本快照、计时、条件、分支及随机选择，HUD 缺失等待与输出完成同步。
2. 打断与手动暂停、保留进度、台词恢复、回调取消、Body 姿势/移动控制与输入回收。
3. 条件故障恢复、工厂回退/归属/部分初始化、输出创建期间销毁、目标离开、真实 HUD 队列交接及全部引用清理。

复现命令（仓库根目录）：

```powershell
dotnet build .\tests\IteratorFramework.Tests\IteratorFramework.Tests.csproj -c Release -p:DeployToGame=false -v minimal
New-Item -ItemType Directory -Force .\artifacts\iterator-framework | Out-Null
& .\tests\IteratorFramework.Tests\bin\Release\net48\IteratorFramework.Tests.exe "D:/Application/Steam/steamapps/common/Rain World" > .\artifacts\iterator-framework\phase6-tests.log 2>&1
```

该构建会同时生成主项目。其他机器可传入 `-p:RainWorldDir="游戏目录"`，运行测试时传入同一个目录。测试项目默认关闭游戏部署；只编译主项目时同样应传入 `-p:DeployToGame=false` 以生成本地产物。

本轮主 DLL 输出：`src/bin/Release/DryCycle.dll`；检查日志：`artifacts/iterator-framework/phase6-tests.log`。原有图形回归仍会导出 CPU 预览，没有新增 PWN_AI 样例或绘图测试。不提交游戏程序集、测试生成的 CoreModule 或反编译代码。

沿用上一轮部署安排，主 DLL 与 AIObservatory.RWImGui、DevTool.RWImGui 两个界面 DLL 均已重新构建并更新至 `Ancient Site/newest/plugins`。主 DLL 与通过检查的测试用程序集哈希一致，两个界面构建均为 0 警告/0 错误；三个 DLL 及两个配套 PDB 的 5/5 项 SHA256 校验通过。旧文件备份和部署记录位于 `artifacts/iterator-framework/phase6-release`。本阶段没有修改 Shader 源码或重新构建资源包。

## 验证边界

- 没有启动 Unity 游戏进程。真实房间流式加载、存档进入/退出、原版迭代器共存和其他 Mod 兼容仍需游戏内验收。
- 托管夹具和循环检查验证状态、绑定和释放行为，不等于真实游戏的场景或 Session 重启。
- 当前宿主已具备身体、姿势与 Graphics；相机检查使用未挂载 Stage 的 Futile 容器，没有验证 GPU 上传、实际图集加载或 Shader 执行。
- 行为、感知、图形及对话已实现并独立隔离；尚无物品/伤害交互或存档模块。对话调度使用可控输出验证，真实字体、翻译、逐字显示和多相机效果未在游戏内验证。
- 感知是按间隔采样的地形视线和距离，不包含光照、伪装或完整潜行 AI；两次采样之间的短暂变化可能不被记录。
- 外部注册者须按文档保留并注销自己的 Descriptor；不提供按外部 Mod 所有权自动清理或文件热重载。

## Public API 检查点

公共接口无需传递 Hook；Builder 不拥有注册状态；Descriptor 不暴露可变集合；Registry 不缓存游戏实体；ID 映射不依赖可变 Index。前六阶段保持已有 Descriptor 构造签名，通过具名类型的 Runtime / Body / Arm / Graphics / Brain / Conversation 工厂扩展。Profile、Pose 和 DialogueSequence 可共享，组件、命令执行状态和网格实例不可复用；游戏引用、相机视图和部分初始化清理语义已记录。

## 后续阶段

| 阶段 | 范围 | 状态 |
| --- | --- | --- |
| 1 | ID、Descriptor、Builder、Registry、Validation、Logger | 已完成本阶段实现和托管验收 |
| 2 | Runtime、Context、Lifecycle、Oracle 与 Room 绑定、创建销毁 | 实现与托管验证完成；游戏内验收待做 |
| 3 | Body、Arm 抽象、基础移动、Pose | 实现与托管验证完成；游戏内验收待做 |
| 4 | Graphics、SpriteRegistry、Profile、Halo、Face、Gown、Cable | 实现、托管验证和 CPU 预览完成；游戏内验收待做 |
| 5 | Brain、Action、StateMachine、BehaviorModule、Sensors、玩家观察 | 实现与托管验证完成；游戏内验收待做 |
| 6 | Conversation、Sequence、Conditions、Commands、Branch、Interrupt | 实现与托管验证完成；游戏内验收待做 |
| 7 | Player/Item/Creature/Pearl/Weapon Interaction | 未开始 |
| 8 | Environment、Gravity、Neuron、Music、Projection、Room Effects | 未开始 |
| 9 | Module、Dependency、Save、Persistent State、Migration | 未开始 |
| 10 | DevConsole、Debug、Diagnostics、Compatibility、完整文档与示例 | 未开始 |

下一开发阶段为第七阶段 Player / Item / Creature / Pearl / Weapon Interaction；继续遵守不追加 PWN_AI 样例、非必要测试不写的约定。完整框架尚未达到任务书第 100 节最终验收标准。
