# Iterator Framework 开发进度

更新日期：2026-09-14。

范围依据：《Iterator Framework 迭代器轮子开发任务书》第 81–83 节。前三阶段实现与托管验证已完成，真实游戏内验收待做；第四阶段尚未开始。本轮按用户要求仅增加第三阶段必要的集成检查。

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

## 已运行的验证

主项目及独立测试项目 Release 编译：**0 个警告，0 个错误**。

检查共 **35/35 组通过，1947 项断言，0 个失败**：第一阶段 13 组 / 1564 项，第二阶段 18 组 / 331 项，第三阶段 4 组 / 52 项。

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

复现命令（仓库根目录）：

```powershell
dotnet build .\tests\IteratorFramework.Tests\IteratorFramework.Tests.csproj -c Release -p:DeployToGame=false -v minimal
& .\tests\IteratorFramework.Tests\bin\Release\net48\IteratorFramework.Tests.exe "D:/Application/Steam/steamapps/common/Rain World"
```

该构建会同时生成主项目。其他机器可传入 `-p:RainWorldDir="游戏目录"`，运行测试时传入同一个目录。测试项目默认关闭游戏部署；只编译主项目时同样应传入 `-p:DeployToGame=false` 以生成本地产物。

本轮 DLL 输出：`src/bin/Release/DryCycle.dll`；检查日志：`artifacts/iterator-framework/phase3-tests.log`。两者均为本地生成产物，不提交游戏程序集、测试生成的 CoreModule 或反编译代码。没有覆盖游戏目录的 DLL 或资源。

## 验证边界

- 没有启动 Unity 游戏进程，也没有部署到游戏。真实房间流式加载、存档进入/退出、原版迭代器共存和其他 Mod 兼容仍需游戏内验收。
- 托管夹具和循环检查验证状态、绑定和释放行为，不等于真实游戏的场景或 Session 重启。
- 当前宿主已具备身体移动和碰撞，但没有 Graphics 绘制；身体与姿势状态可通过 Context.Body 查询。
- 尚无图形、行为、对话或存档模块；Runtime 回调异常处理不代表未来各模块已具备独立隔离。
- 外部注册者须按文档保留并注销自己的 Descriptor；不提供按外部 Mod 所有权自动清理或文件热重载。

## Public API 检查点

公共接口无需传递 Hook；Builder 不拥有注册状态；Descriptor 不暴露可变集合；Registry 不缓存游戏实体；ID 映射不依赖可变 Index。前三阶段保持已有 Descriptor 构造签名，通过具名类型的 Runtime / Body / Arm 工厂扩展。BodyProfile 和 IteratorPose 可安全共享，Body / Arm 实例不可复用；游戏引用和部分初始化清理语义已记录。

## 后续阶段

| 阶段 | 范围 | 状态 |
| --- | --- | --- |
| 1 | ID、Descriptor、Builder、Registry、Validation、Logger | 已完成本阶段实现和托管验收 |
| 2 | Runtime、Context、Lifecycle、Oracle 与 Room 绑定、创建销毁 | 实现与托管验证完成；游戏内验收待做 |
| 3 | Body、Arm 抽象、基础移动、Pose | 实现与托管验证完成；游戏内验收待做 |
| 4 | Graphics、SpriteRegistry、Profile、Halo、Face、Gown、Cable | 未开始 |
| 5 | Brain、Action、StateMachine、Sensors、玩家观察 | 未开始 |
| 6 | Conversation、Sequence、Conditions、Commands、Branch、Interrupt | 未开始 |
| 7 | Player/Item/Creature/Pearl/Weapon Interaction | 未开始 |
| 8 | Environment、Gravity、Neuron、Music、Projection、Room Effects | 未开始 |
| 9 | Module、Dependency、Save、Persistent State、Migration | 未开始 |
| 10 | DevConsole、Debug、Diagnostics、Compatibility、完整文档与示例 | 未开始 |

下一开发阶段为 Graphics、SpriteRegistry、GraphicsProfile 及标准外观组件。完整框架尚未达到任务书第 100 节最终验收标准。
