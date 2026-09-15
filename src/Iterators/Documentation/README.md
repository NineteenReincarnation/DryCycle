# Iterator Framework

Rain World 自定义迭代器框架，位于 DryCycle 的 `src/Iterators`，公共命名空间为 `DryCycle.Iterators`。

目前完成任务书第 81–86 节的**定义注册、Runtime 与游戏绑定、Body / Arm / Pose、Graphics、Brain 与玩家观察、Conversation 与对话脚本**。定义注册后，指定房间完成加载时会创建独立 Runtime、绑定 Oracle 和默认身体，并在房间卸载、Session 结束、注销或插件停用时销毁。

默认身体可移动、碰撞、接受姿势和机械臂约束，并由标准网格部件绘制。PWN_AI 内置白紫长袍和金色头饰样例，并会观察可见玩家。代码、托管检查和 CPU 网格预览已完成，真实 Unity 游戏内验收待做。

```csharp
using DryCycle.Iterators;

IteratorDescriptor definition = Iterator.Create("TEST")
    .Name("Test Iterator")
    .Room("TEST_AI")
    .Register();

IteratorRegistry.TryGet("TEST", out IteratorDescriptor byID);
IteratorRegistry.TryGetByRoom("TEST_AI", out IteratorDescriptor byRoom);

// 所属 Mod 卸载时，先销毁该定义的所有实例，再释放定义和游戏 ID。
IteratorRegistry.Unregister(definition);
```

## 文档入口

- [快速开始](QUICKSTART.md)：程序集引用、注册时机、示例、卸载。
- [定义与注册 API](API.md)：输入规则、冲突、不可变性、工厂、ID 映射、日志和错误语义。
- [Runtime 与生命周期](RUNTIME.md)：实例工厂、上下文、房间生成、回调时序、查询和销毁。
- [Graphics 与 PWN_AI 样例](GRAPHICS.md)：命名 Sprite、网格、Profile、视觉部件、资源和相机生命周期。
- [Brain、动作与玩家观察](BEHAVIOR.md)：优先级、条件、不可打断动作、行为模块、感知与默认行为。
- [Conversation 与对话脚本](CONVERSATION.md)：命令、条件、分支、HUD 输出、打断、恢复与清理。
- [Body、Arm 与 Pose](BODY.md)：移动、配置、自定义身体、机械臂约束及姿势输入。
- [开发进度与验证](PROGRESS.md)：本次交付、测试结果、后续阶段和未验证内容。

## 当前结构

```text
Iterators/
├─ Core/
│  ├─ Iterator.cs              公开入口
│  ├─ IteratorID.cs            不可变 ID 值对象
│  ├─ IteratorDescriptor.cs    不可变定义快照
│  ├─ IteratorBuilder.cs       独立配置收集器
│  ├─ IteratorRegistry.cs      定义与房间索引
│  ├─ IteratorRuntimes.cs      实例查询与生成、按实体隔离的绑定
│  ├─ IteratorRuntime.cs       生命周期与回调保护
│  ├─ IteratorContext.cs       当前实例的游戏引用与玩家视图
│  ├─ IteratorLifecycle.cs     生命周期及销毁原因
│  ├─ IteratorValidation.cs    内部输入校验
│  ├─ IteratorOracleIds.cs     内部游戏 ID 适配
│  └─ IteratorLogger.cs        日志上下文
├─ Body/
│  ├─ IteratorBody.cs          可替换身体与物理结构入口
│  ├─ StandardIteratorBody.cs  默认双 Chunk 移动控制
│  ├─ BodyProfile.cs           不可变身体配置
│  ├─ IteratorPose.cs          姿势与手脚目标
│  ├─ BodyPhysics.cs           有界约束位置检查
│  └─ Arm/                    IteratorArm、NoArm、FixedArm
├─ Behavior/                  Brain、动作、状态机、行为模块、玩家感知
├─ Conversation/              控制器、不可变脚本、命令、播放状态与身体输入
├─ Graphics/                  图形组件、Profile、命名 Sprite、网格、标准部件
├─ Examples/                  PwnIteratorExample、PwnIteratorGraphics
├─ Logging/
│  └─ IteratorLogBridge.cs     内部 BepInEx 日志适配
├─ Integration/
│  ├─ IteratorHooks.cs         集中安装、回滚和卸载游戏 Hook
│  ├─ IteratorHost.cs          框架专属 Oracle 实体适配器
│  ├─ IteratorPhysicsAdapter.cs 游戏 PhysicalObject 物理调用
│  └─ IteratorGraphicsHost.cs   每相机 Sprite 与 GPU 数据适配
└─ Documentation/
```

定义数据流为 `Builder → Descriptor → Registry`。Builder 不作为注册表数据源；集合在 Descriptor 构造时复制并封装为只读集合，Registry 在完整预检后一次发布新索引快照。

实例数据流为 `Room.ReadyForAI → Descriptor.RuntimeFactory → Runtime + Context → Oracle 宿主 → 生命周期回调`。同一份定义可在不同 Room / Session 中创建不同实例；销毁实例不等于注销定义。房间与 Oracle 查询按对象身份区分，不根据同名房间或相同游戏 ID 猜测实例所有权。

框架使用本机 Rain World 的真实类型 `Oracle.OracleID`。Registry 保存静态定义，IteratorRuntimes 管理实例索引，Context 持有当前游戏引用。工厂应只捕获可跨 Session 使用的配置，避免将旧 Room、Player 或 Game 留在长期注册的定义中。

当前 API 为前六阶段开发接口。后续优先以新增接口演进；正式发布后，已有 public API 的改名或移除必须提供弃用与迁移期。`FrameworkVersion`、`ApiVersion` 和能力查询尚未实现。
